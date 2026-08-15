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
/// Tier-laddered franchise cards (e.g. "Variety" - a distinct reward Unlock per franchise tier
/// reached, 0-5, all sharing the literal Name "Variety") need extra handling: a bare name match
/// against every Unlock in the game hits every tier's record at once. See <see
/// cref="GetTierLadderSiblings"/> for the shared "is this actually a tier ladder" detection (also
/// used by UnlockPackEvaluator.IsValidCarryoverPick's cumulative lower-tier check) and the `" (Tier
/// N)"` qualifier syntax below that lets a caller pick a specific rung explicitly.
/// </summary>
public static class UnlockNameResolver
{
    private static readonly Regex TierQualifierPattern = new(@"^(?<base>.+) \(Tier (?<tier>\d+)\)$", RegexOptions.IgnoreCase | RegexOptions.Compiled);

    /// <param name="tier">
    /// The caller's currently-declared franchise tier - used only to disambiguate a bare (unqualified)
    /// tier-laddered name, by floor-resolving to the highest rung whose MinimumFranchiseTier is &lt;=
    /// this value (same floor semantics as UnlockPackEvaluator's carryover-context tier check). Has no
    /// effect on non-ambiguous names or on an explicitly `" (Tier N)"`-qualified name.
    /// </param>
    public static UnlockNameLookup TryResolveByName(GameDataRepository repo, string name, int tier)
    {
        var all = repo.AllUnlocksOrderedById;
        var qualifierMatch = TierQualifierPattern.Match(name);
        var baseName = qualifierMatch.Success ? qualifierMatch.Groups["base"].Value : name;
        int? qualifiedTier = qualifierMatch.Success ? int.Parse(qualifierMatch.Groups["tier"].Value) : null;

        var matches = all.Where(u => string.Equals(u.Name, baseName, StringComparison.OrdinalIgnoreCase)).ToList();
        if (matches.Count == 1)
        {
            return new UnlockNameLookup(matches[0], [], []);
        }

        if (matches.Count > 1)
        {
            return ResolveAmbiguous(all, baseName, matches, qualifiedTier, tier);
        }

        var close = all.Where(u => u.Name.Contains(name, StringComparison.OrdinalIgnoreCase)).Take(5).Select(u => u.Name).ToList();
        return new UnlockNameLookup(null, [], close);
    }

    private static UnlockNameLookup ResolveAmbiguous(
        IReadOnlyList<UnlockData> all, string baseName, List<UnlockData> matches, int? qualifiedTier, int tier)
    {
        // The dead-duplicate rows (see DropInertDuplicateNames' doc comment) are never a legitimate
        // answer - drop them from consideration whenever at least one live record shares the name,
        // but don't blindly drop everything if a name is somehow only ever inert.
        matches = DropInertDuplicateNames(matches).ToList();

        if (matches.Count == 1)
        {
            return new UnlockNameLookup(matches[0], [], []);
        }

        if (IsTierLadder(matches))
        {
            if (qualifiedTier is int qt)
            {
                var exact = matches.SingleOrDefault(m => m.MinimumFranchiseTier == qt);
                return exact is not null
                    ? new UnlockNameLookup(exact, [], [])
                    : new UnlockNameLookup(null, matches, []);
            }

            var floorMatch = matches
                .Where(m => m.MinimumFranchiseTier <= tier)
                .OrderByDescending(m => m.MinimumFranchiseTier)
                .FirstOrDefault();
            if (floorMatch is not null)
            {
                return new UnlockNameLookup(floorMatch, [], []);
            }
        }

        return new UnlockNameLookup(null, matches, []);
    }

    /// <summary>
    /// True if every record in <paramref name="candidates"/> is part of the same "tier ladder" - a
    /// franchise reward granted once per tier reached, extracted as a separate Unlock record per
    /// tier (e.g. "Variety" has one live record per tier 0-5) all sharing the same literal Name.
    /// Deliberately narrow: requires every record to be IsSpecificFranchiseTier AND agree on every
    /// other disambiguating field (Kind/UnlockGroup/CardType), differing ONLY by
    /// MinimumFranchiseTier - so this can't misfire on same-name duplicates that differ for other
    /// reasons (e.g. "Simplicity"/"Flower Pots", which differ by UnlockGroup/CardType, not tier; or
    /// "Advertising"/"Fish Selection", which are genuinely identical except for raw Id - no tier
    /// signal at all, correctly NOT treated as a ladder here).
    /// </summary>
    public static bool IsTierLadder(IReadOnlyList<UnlockData> candidates) =>
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
    /// the CLI and every web endpoint that resolves a dish/card name, so the "it's a tier ladder,
    /// here's how to qualify it" hint (see <see cref="IsTierLadder"/>) only needs to be written once.
    /// </summary>
    public static string BuildAmbiguousOrUnknownErrorMessage(string label, string name, UnlockNameLookup lookup)
    {
        if (lookup.AmbiguousMatches.Count > 0)
        {
            if (IsTierLadder(lookup.AmbiguousMatches))
            {
                var qualified = lookup.AmbiguousMatches
                    .OrderBy(m => m.MinimumFranchiseTier)
                    .Select(m => $"{m.Name} (Tier {m.MinimumFranchiseTier})")
                    .ToList();
                return $"'{name}' is granted once per franchise tier reached - specify which one, e.g. \"{qualified[0]}\". Available: {string.Join(", ", qualified)}.";
            }

            return $"Ambiguous {label} name '{name}' - {lookup.AmbiguousMatches.Count} matches (IDs: {string.Join(", ", lookup.AmbiguousMatches.Select(m => m.Id))}).";
        }

        return lookup.Suggestions.Count > 0
            ? $"Unknown {label} '{name}'. Did you mean: {string.Join(", ", lookup.Suggestions)}?"
            : $"Unknown {label} '{name}'.";
    }

    /// <summary>
    /// Every live (IsUnlockable) sibling of <paramref name="candidate"/> sharing its Name and part of
    /// the same tier ladder (see <see cref="IsTierLadder"/>) - empty if <paramref name="candidate"/>
    /// isn't part of a ladder at all (including the single-record case). Used by
    /// UnlockPackEvaluator.IsValidCarryoverPick's cumulative lower-tier prerequisite check (owning a
    /// higher tier's reward implies owning every lower tier's too - franchise tiers are reached in
    /// order) and by SearchEndpoints' qualified-display-name builder.
    /// </summary>
    public static IReadOnlyList<UnlockData> GetTierLadderSiblings(GameDataRepository repo, UnlockData candidate)
    {
        var sameName = repo.AllUnlocksOrderedById
            .Where(u => u.IsUnlockable && string.Equals(u.Name, candidate.Name, StringComparison.OrdinalIgnoreCase))
            .ToList();
        return IsTierLadder(sameName) ? sameName : [];
    }
}
