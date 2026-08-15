using System.Text.RegularExpressions;

namespace PlateUpSeedTools.Engine.Data;

/// <summary>
/// Result of a fuzzy dish/card name lookup - exactly one of <see cref="Unlock"/> (unique match) or
/// <see cref="AmbiguousMatches"/> (2+ exact-name matches) or <see cref="Suggestions"/> (no exact
/// match; near-miss names, possibly empty) is populated.
/// </summary>
public readonly record struct UnlockNameLookup(UnlockData? Unlock, IReadOnlyList<UnlockData> AmbiguousMatches, IReadOnlyList<string> Suggestions);

/// <summary>
/// Console-free dish/card name resolution shared by the CLI `search` verb and the web search API -
/// both need identical unknown/ambiguous-name handling, just formatted differently (CLI prints to
/// stdout, the web endpoint turns it into a JSON error).
///
/// Repeatable franchise-carryover cards (e.g. "Variety" - a distinct Unlock record per time you've
/// taken it across separate franchise cycles, 6 live records total) need extra handling: a bare name
/// match against every Unlock in the game hits every occurrence at once. See <see
/// cref="GetRepeatableCardSiblings"/> for the shared "is this actually a repeatable-card family"
/// detection (also used by UnlockPackEvaluator.IsValidCarryoverPick's cumulative lower-copy check)
/// and the `" (copy N)"` qualifier syntax below that lets a caller pick a specific occurrence
/// explicitly. IMPORTANT, learned the hard way (a real user-reported bug): the numeric suffix here is
/// an occurrence COUNT ("this is your Nth time taking this card"), NOT the franchise tier you were on
/// when you took it - those are unrelated numbers. Don't reintroduce a comparison against a declared
/// franchise-tier value anywhere in this class.
/// </summary>
public static class UnlockNameResolver
{
    private static readonly Regex CopyQualifierPattern = new(@"^(?<base>.+) \(copy (?<copy>\d+)\)$", RegexOptions.IgnoreCase | RegexOptions.Compiled);

    public static UnlockNameLookup TryResolveByName(GameDataRepository repo, string name)
    {
        var all = repo.AllUnlocksOrderedById;
        var qualifierMatch = CopyQualifierPattern.Match(name);
        var baseName = qualifierMatch.Success ? qualifierMatch.Groups["base"].Value : name;
        // "copy N" is 1-indexed for display; the underlying MinimumFranchiseTier field is 0-indexed.
        int? qualifiedCopyIndex = qualifierMatch.Success ? int.Parse(qualifierMatch.Groups["copy"].Value) - 1 : null;

        var matches = all.Where(u => string.Equals(u.Name, baseName, StringComparison.OrdinalIgnoreCase)).ToList();
        if (matches.Count == 1)
        {
            return new UnlockNameLookup(matches[0], [], []);
        }

        if (matches.Count > 1)
        {
            return ResolveAmbiguous(matches, qualifiedCopyIndex);
        }

        var close = all.Where(u => u.Name.Contains(name, StringComparison.OrdinalIgnoreCase)).Take(5).Select(u => u.Name).ToList();
        return new UnlockNameLookup(null, [], close);
    }

    private static UnlockNameLookup ResolveAmbiguous(List<UnlockData> matches, int? qualifiedCopyIndex)
    {
        // The dead-duplicate rows (see DropInertDuplicateNames' doc comment) are never a legitimate
        // answer - drop them from consideration whenever at least one live record shares the name,
        // but don't blindly drop everything if a name is somehow only ever inert.
        matches = DropInertDuplicateNames(matches).ToList();

        if (matches.Count == 1)
        {
            return new UnlockNameLookup(matches[0], [], []);
        }

        if (IsRepeatableCardFamily(matches))
        {
            if (qualifiedCopyIndex is int copyIndex)
            {
                var exact = matches.SingleOrDefault(m => m.MinimumFranchiseTier == copyIndex);
                return exact is not null
                    ? new UnlockNameLookup(exact, [], [])
                    : new UnlockNameLookup(null, matches, []);
            }

            // No qualifier and no declared-tier signal to lean on (there never was a valid one - see
            // this class's doc comment) - a bare repeatable-card name is genuinely ambiguous among
            // its occurrences. Report it via AmbiguousMatches so BuildAmbiguousOrUnknownErrorMessage
            // can suggest the "(copy N)" qualifier. A normal web user never hits this in practice -
            // GetFranchiseCandidates always emits already-qualified names (see SearchEndpoints.cs) -
            // this only bites CLI/curl callers typing a bare name freehand.
        }

        return new UnlockNameLookup(null, matches, []);
    }

    /// <summary>
    /// True if every record in <paramref name="candidates"/> is part of the same "repeatable card
    /// family" - a franchise-carryover card you can take more than once across separate franchise
    /// cycles, extracted as a separate Unlock record per occurrence (e.g. "Variety" has 6 live
    /// records, one per time you've taken it) all sharing the same literal Name. Deliberately narrow:
    /// requires every record to be IsSpecificFranchiseTier AND agree on every other disambiguating
    /// field (Kind/UnlockGroup/CardType), differing ONLY by MinimumFranchiseTier (the occurrence
    /// index) - so this can't misfire on same-name duplicates that differ for other reasons (e.g.
    /// "Simplicity"/"Flower Pots", which differ by UnlockGroup/CardType; or "Advertising"/
    /// "Fish Selection", which are genuinely identical except for raw Id - no occurrence signal at
    /// all, correctly NOT treated as a family here).
    /// </summary>
    public static bool IsRepeatableCardFamily(IReadOnlyList<UnlockData> candidates) =>
        candidates.Count > 1 &&
        candidates.All(c => c.IsSpecificFranchiseTier) &&
        candidates.Select(c => (c.Kind, c.UnlockGroup, c.CardType)).Distinct().Count() == 1 &&
        candidates.Select(c => c.MinimumFranchiseTier).Distinct().Count() == candidates.Count;

    /// <summary>
    /// Drops the dead, IsUnlockable: false extraction-artifact duplicate records (see this class's
    /// doc comment) from a candidate list whenever a live sibling shares the same Name - same
    /// heuristic <see cref="TryResolveByName"/> applies internally, exposed here for callers that
    /// build their own candidate lists directly (e.g. SearchEndpoints.GetFranchiseCandidates, which
    /// iterates every Unlock in the game rather than going through name resolution) and would
    /// otherwise let an inert duplicate slip through - CarryoverFilter's IgnoreUnlockability is
    /// deliberately permissive for legitimately-inert-but-ownable cards, so it can't tell the
    /// difference on its own.
    /// </summary>
    public static IEnumerable<UnlockData> DropInertDuplicateNames(IEnumerable<UnlockData> unlocks) =>
        unlocks
            .GroupBy(u => u.Name, StringComparer.OrdinalIgnoreCase)
            .SelectMany(group =>
            {
                var live = group.Where(u => u.IsUnlockable).ToList();
                return live.Count > 0 ? live : group.ToList();
            });

    /// <summary>
    /// Builds a human-readable error for a failed <see cref="TryResolveByName"/> lookup - shared by
    /// the CLI and every web endpoint that resolves a dish/card name, so the "it's a repeatable card,
    /// here's how to qualify it" hint (see <see cref="IsRepeatableCardFamily"/>) only needs to be
    /// written once.
    /// </summary>
    public static string BuildAmbiguousOrUnknownErrorMessage(string label, string name, UnlockNameLookup lookup)
    {
        if (lookup.AmbiguousMatches.Count > 0)
        {
            if (IsRepeatableCardFamily(lookup.AmbiguousMatches))
            {
                var qualified = lookup.AmbiguousMatches
                    .OrderBy(m => m.MinimumFranchiseTier)
                    .Select(m => $"{m.Name} (copy {m.MinimumFranchiseTier + 1})")
                    .ToList();
                return $"'{name}' can be taken more than once across separate franchise cycles - specify which copy, e.g. \"{qualified[0]}\". Available: {string.Join(", ", qualified)}.";
            }

            return $"Ambiguous {label} name '{name}' - {lookup.AmbiguousMatches.Count} matches (IDs: {string.Join(", ", lookup.AmbiguousMatches.Select(m => m.Id))}).";
        }

        return lookup.Suggestions.Count > 0
            ? $"Unknown {label} '{name}'. Did you mean: {string.Join(", ", lookup.Suggestions)}?"
            : $"Unknown {label} '{name}'.";
    }

    /// <summary>
    /// Every live (IsUnlockable) sibling of <paramref name="candidate"/> sharing its Name and part of
    /// the same repeatable-card family (see <see cref="IsRepeatableCardFamily"/>) - empty if
    /// <paramref name="candidate"/> isn't part of a family at all (including the single-record case).
    /// Used by UnlockPackEvaluator.IsValidCarryoverPick's cumulative lower-copy prerequisite check
    /// (owning your Nth copy implies owning copies 1..N-1 too - copies are taken in order) and by
    /// SearchEndpoints' qualified-display-name builder.
    /// </summary>
    public static IReadOnlyList<UnlockData> GetRepeatableCardSiblings(GameDataRepository repo, UnlockData candidate)
    {
        var sameName = repo.AllUnlocksOrderedById
            .Where(u => u.IsUnlockable && string.Equals(u.Name, candidate.Name, StringComparison.OrdinalIgnoreCase))
            .ToList();
        return IsRepeatableCardFamily(sameName) ? sameName : [];
    }
}
