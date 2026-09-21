using System.Collections.Concurrent;
using PlateUpSeedTools.Engine.Data;
using PlateUpSeedTools.Engine.Seeding;
using PlateUpSeedTools.Engine.Shop;

namespace PlateUpSeedTools.Engine.Cards;

/// <summary>Everything a seed-space brute-force search needs, independent of caller (CLI vs web job).</summary>
public sealed record SeedSearchRequest(
    int SettingPackId,
    ResolvedRunSetup Setup,
    IReadOnlyDictionary<int, int> DayTargets,
    int Tier,
    string Alphabet,
    int Length,
    long TotalToCheck,
    IReadOnlySet<string>? ExcludedSeeds = null,
    // Cards the caller wants obtained by FlexibleHorizon but doesn't care which day - mixed freely
    // with DayTargets in the same search (e.g. "Ice Cream on day 3, Individual Dining any day").
    // Empty/null means "fixed-day targets only", reproducing pre-existing behavior exactly.
    IReadOnlySet<int>? FlexibleTargetIds = null,
    int FlexibleHorizon = 15,
    // Restaurant-setting slug (NOT SettingPackId, the card-draw pack - can differ, see the "card
    // schedule" override feature) - only needed to resolve a layout when OtFriendly is set, since
    // OtFriendlyChecker needs the setting's RequiredApplianceIds for its starting-kitchen state.
    string? SettingSlug = null,
    // Additional AND-condition, layered onto whatever DayTargets/FlexibleTargetIds are already
    // configured - see OtFriendlyChecker. Requires SettingSlug to be set (Search throws otherwise).
    bool OtFriendly = false,
    // Models the Bay Leaves plant's "1 reroll per night" mechanic: on any day where the base offer
    // doesn't contain something still wanted (a fixed DayTargets entry or a remaining
    // FlexibleTargetIds card), also try that night's reroll offer (CardDrawSimulator.PeekOffer's
    // skipChoices - a deterministic re-index into the same pre-shuffled pool, not fresh RNG, see
    // UnlockPackEvaluator/UnlockSelector.GetOffsetChoice) and credit a seed as matching if the
    // reroll would have landed it.
    //
    // CORRECTED after a real ground-truth capture (2026-08-20, GroundTruthLogger v1.15.0): the
    // skip_choices value a reroll uses is NOT always 1. RestaurantMode/Kitchen/FindBonusUnlocks.cs's
    // skip_choices = SCardActionDetails.RerollIndex + 1, and RerollIndex is a RUN-WIDE cumulative
    // counter that does NOT reset per night - a real capture with rerolls on both day 3 and day 5
    // showed day 3 at skipChoices=1 but day 5 at skipChoices=2 (not 1), because day 3's reroll usage
    // carried forward. Walk/Backtrack both now thread a running rerollsUsedSoFar count through the
    // day-by-day simulation and use rerollsUsedSoFar+1 for each successive reroll in the same run,
    // not a hardcoded 1 - a search that only ever needs at most one reroll in the whole run is
    // unaffected, but multi-reroll runs previously silently mispredicted every reroll after the
    // first.
    bool AllowRerollPerDay = false);

/// <summary>
/// Result of a <see cref="SeedSearcher.Search"/> call. <see cref="MatchCapReached"/> distinguishes
/// "stopped early because matchCap was hit" from a genuine full-space completion, since both
/// otherwise look like the loop just finishing. <see cref="EquivalentSeeds"/> maps a reported
/// match's seed string to the OTHER seed strings the search found that hash to the same
/// <see cref="Engine.Seeding.Seed.IntValue"/> - and are therefore byte-identical games (every
/// simulation system consumes only the 32-bit seed hash, so a hash collision is total game-state
/// identity, see FixedString32Hash). Only representatives with at least one such twin appear.
/// Best-effort: a cancelled or match-cap-truncated search hasn't scanned the whole space, so the
/// counts/samples are "found so far". Matches found via the IntValue-space search mode (see
/// <see cref="SeedSearcher.ShouldUseIntValueSpace"/>) never report equivalents even though other
/// equally-valid seed strings may exist for the same game - that mode visits each IntValue
/// exactly once by construction, so there's no natural "found a second string for this hash"
/// signal the way string-space enumeration's redundancy produces one.
/// </summary>
public readonly record struct SeedSearchResult(
    bool MatchCapReached,
    IReadOnlyDictionary<string, SeedEquivalents> EquivalentSeeds);

/// <summary>
/// Other seed strings that produce a byte-identical game to a given match. <see cref="Count"/> is
/// the true total found; <see cref="Sample"/> is capped (see SeedSearcher's MaxEquivalentSample) so
/// a pathologically collision-dense search space can't grow the result without bound.
/// </summary>
public readonly record struct SeedEquivalents(int Count, IReadOnlyList<string> Sample);

/// <summary>
/// Brute-forces the seed space defined by <see cref="SeedSearchRequest.Alphabet"/>/<see
/// cref="SeedSearchRequest.Length"/>, reporting matches (a seed where every target day's card was
/// offered) and periodic progress via callbacks. Shared by the CLI `search` verb and the web
/// search job manager - CLI passes an unbounded degree of parallelism (matching its original
/// single-user-machine behavior), the web host passes a capped value so multiple concurrent
/// visitor searches share the machine instead of each claiming every core (see
/// PlateUpSeedTools.Web.Services.SearchJobManager).
/// </summary>
public static class SeedSearcher
{
    // Reporting progress on every single seed would make the callback overhead dominate the hot
    // loop - this interval keeps progress reporting cheap while still updating multiple times a
    // second at the ~2,900 seeds/sec the CLI measured.
    private const long ProgressReportInterval = 2_000;

    // Upper bound on how many equivalent seed strings we retain per match. Real collision groups are
    // tiny (a handful), but the search alphabet has a structural collision family that grows with
    // seed length, so cap the retained sample and just count the rest.
    private const int MaxEquivalentSample = 64;

    // One match "slot": the first seed string found for a given seed hash, plus any later strings
    // that hash to the same value (identical game). DuplicateSample is guarded by locking on itself.
    private sealed class SeedGroup
    {
        public required string Representative { get; init; }
        public int DuplicateCount;
        public readonly List<string> DuplicateSample = new();
    }

    // Inclusive bounds for a user-supplied match cap - the web UI's "Max results" field and the CLI's
    // --max-results flag. A genuine product rule per the project plan, not a web-only limit, so it
    // lives here rather than being redefined in each caller. Enforcement differs by caller: the web
    // layer rejects an out-of-range value (loud), the CLI silently clamps into range (quiet).
    public const long MinMatchCap = 1;
    public const long MaxMatchCap = 500;

    // Match cap used when the caller doesn't supply one - also the maximum a caller may request.
    public const long DefaultMatchCap = MaxMatchCap;

    // The fixed size of the Seed.IntValue space (2^32). Expressed as `1L << 32` (long) rather than
    // `1 << 32` (int, which silently overflows to 0) since it's compared against/used as a bound
    // with Parallel.For's long-bound overload.
    private const long IntValueSpaceSize = 1L << 32;

    /// <summary>
    /// Whether searching the fixed 2^32-size Seed.IntValue space directly is cheaper than
    /// enumerating request.Alphabet/request.Length seed strings - true whenever the requested
    /// string space is larger than the hash space it collapses onto (e.g. the product's default
    /// 8-character/19-letter alphabet: 19^8 ≈ 17 billion strings onto only ~4.3 billion possible
    /// hashes). A winning IntValue is turned back into a displayable seed string of the requested
    /// length/alphabet via SeedHashInverter only once a match is found, not per candidate. See
    /// CLAUDE.md's project docs / the "IntValue-space seed search" plan for the full derivation.
    /// </summary>
    internal static bool ShouldUseIntValueSpace(SeedSearchRequest request) => request.TotalToCheck > IntValueSpaceSize;

    /// <param name="matchCap">
    /// Stop once this many <em>distinct</em> matches have been found - matches are de-duplicated by
    /// seed hash (see <see cref="SeedSearchResult.EquivalentSeeds"/>), so two seed strings that
    /// resolve to the same game count as one and only the first fires <paramref name="onMatch"/> and
    /// advances the cap. Both the web UI's 500 cap and its "stop after first match" option (matchCap=1)
    /// and the CLI's --max-results override funnel through here - see SearchCommand.cs/SearchJobManager.cs.
    /// Required, not optional, so every caller has to make a deliberate choice - pass long.MaxValue for
    /// effectively unbounded. Best-effort: a few in-flight parallel iterations may complete just
    /// after the cap is hit, so slightly more than matchCap onMatch calls are possible; callers
    /// that need a hard display limit (e.g. the web UI's match-list rendering) should still cap
    /// independently rather than relying on this being exact.
    /// </param>
    public static SeedSearchResult Search(
        GameDataRepository repo,
        SeedSearchRequest request,
        int maxDegreeOfParallelism,
        long matchCap,
        Action<string, bool> onMatch,
        Action<long> onProgress,
        CancellationToken cancellationToken)
    {
        if (request.OtFriendly && string.IsNullOrEmpty(request.SettingSlug))
        {
            throw new ArgumentException("SettingSlug is required when OtFriendly is set - OtFriendlyChecker needs it to resolve the starting-kitchen layout.", nameof(request));
        }

        var hasFlexibleTargets = request.FlexibleTargetIds is { Count: > 0 };
        var maxDay = Math.Max(
            request.DayTargets.Count == 0 ? 0 : request.DayTargets.Keys.Max(),
            hasFlexibleTargets ? request.FlexibleHorizon : 0);
        var flexibleTargetIds = hasFlexibleTargets ? request.FlexibleTargetIds! : (IReadOnlySet<int>)new HashSet<int>();
        long checkedCount = 0;
        long matchCount = 0;
        var matchCapReached = false;

        // De-dup by seed hash: two seed strings with the same Seed.IntValue are the same game in
        // every respect (every RNG stream derives solely from the 32-bit hash - see
        // FixedString32Hash / FixedSeedContext), and the search alphabet has abundant collisions
        // ("v4" and "46" both hash to 5861743). First string found for a hash is the reported
        // representative; later collisions are recorded as its equivalents.
        var groups = new ConcurrentDictionary<int, SeedGroup>();

        // Excluded seeds are matched by hash too, for the same reason - excluding one string
        // excludes every string that resolves to the same game, which is what "don't re-find seeds
        // I've already seen" actually means.
        var excludedHashes = request.ExcludedSeeds is { Count: > 0 }
            ? new HashSet<int>(request.ExcludedSeeds.Select(s => new Seed(s).IntValue))
            : null;

        // A linked CTS lets the cap trigger its own internal cancellation without disturbing the
        // caller's own token - Parallel.For can't distinguish "stop because the cap was hit" from
        // "stop because the caller cancelled" any other way, since both surface as the same
        // OperationCanceledException.
        using var linkedCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        var parallelOptions = new ParallelOptions
        {
            CancellationToken = linkedCts.Token,
            MaxDegreeOfParallelism = maxDegreeOfParallelism,
        };

        void ReportProgress()
        {
            var count = Interlocked.Increment(ref checkedCount);
            if (count % ProgressReportInterval == 0)
            {
                onProgress(count);
            }
        }

        try
        {
            if (ShouldUseIntValueSpace(request))
            {
                // Search the fixed 2^32 IntValue space directly instead of enumerating seed
                // strings - see ShouldUseIntValueSpace's doc comment. Every IntValue is visited
                // exactly once by construction, so a "duplicate" (another string hashing to the
                // same value) can never occur within this loop - groups.TryAdd always succeeds on
                // first encounter, and there is no equivalents-tracking else-branch to mirror the
                // string-mode loop below.
                Parallel.For(0L, IntValueSpaceSize, parallelOptions, i =>
                {
                    var seed = Seed.FromIntValue(unchecked((int)(uint)i));
                    if (excludedHashes?.Contains(seed.IntValue) != true)
                    {
                        var (isMatch, otFriendly) = Matches(
                            repo, seed, request.SettingPackId, request.Setup, request.DayTargets,
                            flexibleTargetIds, maxDay, request.Tier, request.OtFriendly, request.SettingSlug,
                            request.AllowRerollPerDay);
                        if (isMatch)
                        {
                            // Resolve a displayable seed string only now, not per-candidate. A null
                            // result means a real matching game exists but no seed string of the
                            // requested length/alphabet reaches it - not actionable (nothing a user
                            // could type), so skip it without counting it as a match.
                            var seedString = SeedHashInverter.FindPreimage(seed.IntValue, request.Length, request.Alphabet);
                            if (seedString is not null && groups.TryAdd(seed.IntValue, new SeedGroup { Representative = seedString }))
                            {
                                onMatch(seedString, otFriendly);
                                if (Interlocked.Increment(ref matchCount) >= matchCap)
                                {
                                    matchCapReached = true;
                                    linkedCts.Cancel();
                                }
                            }
                        }
                    }

                    ReportProgress();
                });
            }
            else
            {
                Parallel.For(0L, request.TotalToCheck, parallelOptions, i =>
                {
                    var seedString = IndexToSeedString(i, request.Alphabet, request.Length);
                    var seed = new Seed(seedString);
                    if (excludedHashes?.Contains(seed.IntValue) != true)
                    {
                        var (isMatch, otFriendly) = Matches(
                            repo, seed, request.SettingPackId, request.Setup, request.DayTargets,
                            flexibleTargetIds, maxDay, request.Tier, request.OtFriendly, request.SettingSlug,
                            request.AllowRerollPerDay);
                        if (isMatch)
                        {
                            if (groups.TryAdd(seed.IntValue, new SeedGroup { Representative = seedString }))
                            {
                                onMatch(seedString, otFriendly);
                                if (Interlocked.Increment(ref matchCount) >= matchCap)
                                {
                                    matchCapReached = true;
                                    linkedCts.Cancel();
                                }
                            }
                            else
                            {
                                var group = groups[seed.IntValue];
                                lock (group.DuplicateSample)
                                {
                                    group.DuplicateCount++;
                                    if (group.DuplicateSample.Count < MaxEquivalentSample)
                                    {
                                        group.DuplicateSample.Add(seedString);
                                    }
                                }
                            }
                        }
                    }

                    ReportProgress();
                });
            }
        }
        catch (OperationCanceledException) when (matchCapReached && !cancellationToken.IsCancellationRequested)
        {
            // Our own cap-triggered cancellation, not the caller's - swallow it and report via the
            // return value instead of surfacing it as a cancellation to the caller.
        }
        finally
        {
            // Always report the final tally, even on cancellation, so callers don't need to
            // separately track "how far did we actually get" themselves.
            onProgress(Interlocked.Read(ref checkedCount));
        }

        var equivalents = new Dictionary<string, SeedEquivalents>();
        foreach (var group in groups.Values)
        {
            if (group.DuplicateCount > 0)
            {
                lock (group.DuplicateSample)
                {
                    equivalents[group.Representative] = new SeedEquivalents(
                        group.DuplicateCount,
                        group.DuplicateSample.OrderBy(s => s, StringComparer.Ordinal).ToList());
                }
            }
        }

        return new SeedSearchResult(matchCapReached, equivalents);
    }

    /// <summary>
    /// Returns whether the seed matches, plus its OtFriendly status - computed for every genuine
    /// match (once the cheaper card-target filters below have already been passed) regardless of
    /// whether <paramref name="otFriendly"/> gating was requested, so callers can tag every match
    /// with it rather than only seeds that were explicitly searched for it. IsMatch is false (and
    /// OtFriendly meaningless/false) for anything that fails the card-target filters.
    /// </summary>
    private static (bool IsMatch, bool OtFriendly) Matches(
        GameDataRepository repo, Seed seed, int settingPackId, ResolvedRunSetup setup,
        IReadOnlyDictionary<int, int> dayTargets, IReadOnlySet<int> flexibleTargetIds, int maxDay, int tier,
        bool otFriendly, string? settingSlug, bool allowRerollPerDay)
    {
        // Mutated in place by Walk as flexible targets get picked up - a strict subset check at the
        // end (rather than a running "found day X" flag) since order among flexible targets doesn't
        // matter, only that all of them end up owned by maxDay.
        var remainingFlexible = new HashSet<int>(flexibleTargetIds);
        foreach (var (_, targetId, actual, _) in Walk(repo, seed, settingPackId, setup, dayTargets, remainingFlexible, maxDay, tier, allowRerollPerDay))
        {
            if (targetId is int t && actual.Unlock1Id != t && actual.Unlock2Id != t)
            {
                return (false, false);
            }
        }

        if (remainingFlexible.Count != 0)
        {
            return (false, false);
        }

        // Computed for every survivor of the cheaper filters above, not just otFriendly-gated
        // searches - cheap enough (≤3 ShopDaySimulator.SimulateDay calls) to always tag, and lets
        // callers show an OT-Friendly indicator on every match regardless of what was searched for.
        var isOtFriendly = !string.IsNullOrEmpty(settingSlug) && OtFriendlyChecker.Matches(repo, seed, settingSlug!);
        if (otFriendly && !isOtFriendly)
        {
            return (false, false);
        }

        return (true, isOtFriendly);
    }

    // Safety valve for ResolveCardPaths' backtracking search - bounds worst-case work (many filler
    // days where most alternate branches dead-end before reaching a later target) so a single
    // dialog-open can't run away. The guaranteed-valid canonical path (Unlock1Id at every filler
    // day, matching Matches' own policy) is always explored first, so this only ever costs "fewer
    // than maxPaths alternates found," never "no path at all."
    private const int MaxPathSearchNodes = 20_000;

    /// <summary>
    /// Finds up to <paramref name="maxPaths"/> distinct complete day-by-day card-pick sequences that
    /// all reproduce this seed's match - not just the single filler-policy path <see cref="Matches"/>
    /// itself used. Branches only on "filler" days (no explicit target) that offer two cards: which
    /// one is picked doesn't matter for that day, but CAN change what's offered/eligible on LATER
    /// days (Requires/BlockedBy - see UnlockPackEvaluator), so not every branch reaches a full valid
    /// completion - this backtracks and discards dead branches rather than assuming both options are
    /// always interchangeable. Depth-first, Unlock1Id tried before Unlock2Id at every branch point,
    /// so the first result is always the same canonical path Matches confirmed the seed with; extra
    /// results are alternate ways to reach the same targets. See SearchEndpoints' POST /api/search/path.
    /// </summary>
    public static IReadOnlyList<IReadOnlyList<(int Day, int? CardId, bool Rerolled)>> ResolveCardPaths(
        GameDataRepository repo, Seed seed, int settingPackId, ResolvedRunSetup setup,
        IReadOnlyDictionary<int, int> dayTargets, IReadOnlySet<int> flexibleTargetIds, int maxDay, int tier,
        bool allowRerollPerDay, int maxPaths)
    {
        var results = new List<IReadOnlyList<(int Day, int? CardId, bool Rerolled)>>();
        var current = new List<(int Day, int? CardId, bool Rerolled)>();
        var nodesVisited = 0;
        Backtrack(
            repo, seed, settingPackId, setup, dayTargets, maxDay, tier, allowRerollPerDay,
            day: 0, ownedIds: new HashSet<int>(setup.InitiallyOwnedIds), remainingFlexible: new HashSet<int>(flexibleTargetIds),
            rerollsUsedSoFar: 0, current, results, maxPaths, ref nodesVisited);
        return results;
    }

    private static void Backtrack(
        GameDataRepository repo, Seed seed, int settingPackId, ResolvedRunSetup setup,
        IReadOnlyDictionary<int, int> dayTargets, int maxDay, int tier, bool allowRerollPerDay, int day,
        HashSet<int> ownedIds, HashSet<int> remainingFlexible, int rerollsUsedSoFar,
        List<(int Day, int? CardId, bool Rerolled)> current, List<IReadOnlyList<(int Day, int? CardId, bool Rerolled)>> results,
        int maxPaths, ref int nodesVisited)
    {
        if (results.Count >= maxPaths || nodesVisited >= MaxPathSearchNodes)
        {
            return;
        }

        if (day > maxDay)
        {
            // A complete day-by-day trace only counts as a real path if every flexible target was
            // actually picked up somewhere along the way, not just "we reached the horizon" - a
            // trace that never offered one of them is a dead end for this purpose, same as any
            // other branch that fails to reproduce the match.
            if (remainingFlexible.Count == 0)
            {
                results.Add(current.ToList());
            }

            return;
        }

        nodesVisited++;

        // A fresh, single-use simulator just to read this day's offer for the current branch's
        // owned-set - PeekOffer never mutates anything we keep.
        var peekSetup = new ResolvedRunSetup(new HashSet<int>(ownedIds), setup.BonusPackIds);
        var sim = new CardDrawSimulator(repo, seed, settingPackId, peekSetup, tier);
        var targetId = dayTargets.TryGetValue(day, out var target) ? target : (int?)null;

        var offered = sim.PeekOffer(day, skipChoices: 0).Options;
        var rerolled = false;
        // See Walk's matching comment: RerollIndex is a run-wide cumulative counter, not reset per
        // night - rerollsUsedSoFar carries forward through the recursion so a later day's reroll
        // uses the correct (possibly >1) skip_choices if an earlier day in this same branch also
        // rerolled.
        var nextRerollsUsedSoFar = rerollsUsedSoFar;
        if (allowRerollPerDay && !OfferSatisfiesWanted(offered, targetId, remainingFlexible))
        {
            var rerollOffer = sim.PeekOffer(day, rerollsUsedSoFar + 1).Options;
            if (OfferSatisfiesWanted(rerollOffer, targetId, remainingFlexible))
            {
                offered = rerollOffer;
                rerolled = true;
                nextRerollsUsedSoFar = rerollsUsedSoFar + 1;
            }
        }

        if (offered.Unlock1Id is null)
        {
            current.Add((day, null, rerolled));
            Backtrack(repo, seed, settingPackId, setup, dayTargets, maxDay, tier, allowRerollPerDay, day + 1, ownedIds, remainingFlexible, nextRerollsUsedSoFar, current, results, maxPaths, ref nodesVisited);
            current.RemoveAt(current.Count - 1);
            return;
        }

        List<int> candidates;
        if (targetId is int target2)
        {
            if (offered.Unlock1Id != target2 && offered.Unlock2Id != target2)
            {
                return; // Dead end: this branch's owned-set no longer offers the target on this day.
            }

            candidates = [target2];
        }
        else
        {
            var opts = offered.Unlock2Id is int u2 && u2 != offered.Unlock1Id
                ? new List<int> { offered.Unlock1Id.Value, u2 }
                : new List<int> { offered.Unlock1Id.Value };

            // Prefer whichever offered option satisfies a still-remaining flexible target first
            // (mirrors Matches'/Walk's own greedy accept-if-remaining policy, so the canonical first
            // result stays consistent with what Matches actually confirmed) - but still explore the
            // "leave it as filler" branch too, same as today's multi-option filler days, since a
            // flexible target left for a later day is just as valid a path as picking it now.
            candidates = opts.OrderByDescending(remainingFlexible.Contains).ToList();
        }

        foreach (var candidate in candidates)
        {
            if (results.Count >= maxPaths || nodesVisited >= MaxPathSearchNodes)
            {
                return;
            }

            current.Add((day, candidate, rerolled));
            var nextOwned = new HashSet<int>(ownedIds) { candidate };
            var nextRemainingFlexible = remainingFlexible.Contains(candidate)
                ? new HashSet<int>(remainingFlexible.Where(id => id != candidate))
                : remainingFlexible;
            Backtrack(repo, seed, settingPackId, setup, dayTargets, maxDay, tier, allowRerollPerDay, day + 1, nextOwned, nextRemainingFlexible, nextRerollsUsedSoFar, current, results, maxPaths, ref nodesVisited);
            current.RemoveAt(current.Count - 1);
        }
    }

    /// <summary>Whether an offer contains something still wanted - a fixed day target, or (when
    /// targetId is null, i.e. a filler day) any card still in remainingFlexible.</summary>
    private static bool OfferSatisfiesWanted(UnlockOptions offered, int? targetId, HashSet<int> remainingFlexible)
    {
        if (targetId is int t)
        {
            return offered.Unlock1Id == t || offered.Unlock2Id == t;
        }

        return (offered.Unlock1Id is int u1 && remainingFlexible.Contains(u1)) ||
               (offered.Unlock2Id is int u2 && remainingFlexible.Contains(u2));
    }

    private static IEnumerable<(int Day, int? TargetId, CardDayResult Actual, bool Rerolled)> Walk(
        GameDataRepository repo, Seed seed, int settingPackId, ResolvedRunSetup setup,
        IReadOnlyDictionary<int, int> dayTargets, HashSet<int> remainingFlexible, int maxDay, int tier,
        bool allowRerollPerDay)
    {
        // ResolvedRunSetup.InitiallyOwnedIds gets mutated per-day by the simulator, so each candidate
        // seed needs its own copy - dish/heat/franchise-carryover resolution itself doesn't depend on
        // the seed.
        var freshSetup = new ResolvedRunSetup(new HashSet<int>(setup.InitiallyOwnedIds), setup.BonusPackIds);
        var sim = new CardDrawSimulator(repo, seed, settingPackId, freshSetup, tier);

        // RestaurantMode/Kitchen/FindBonusUnlocks.cs's skip_choices = SCardActionDetails.RerollIndex + 1
        // - RerollIndex is a RUN-WIDE cumulative counter, NOT reset per night (ground-truth-confirmed
        // 2026-08-20: a real capture with rerolls on both day 3 and day 5 showed day 3's reroll at
        // skipChoices=1 but day 5's at skipChoices=2, not 1 - the day-3 reroll's usage carried over,
        // and re-passing 1 every time was a real bug, not just a naming quirk). So every reroll used
        // on an earlier day in this same walk must bump the value used on a later day's reroll.
        var rerollsUsedSoFar = 0;

        // Day 0 is the prep day before the first real night - only relevant for packs with a
        // Day=0 UnlockConditionOnce (currently just santa-workshop's Christmas Special Card), but
        // harmless to always include: no other decoded condition can ever match day 0.
        for (var day = 0; day <= maxDay; day++)
        {
            var targetId = dayTargets.TryGetValue(day, out var t) ? t : (int?)null;

            // Only worth probing a reroll when something is actually wanted this day - filler days
            // with nothing left to chase don't care what's offered, so skip the extra PeekOffer cost.
            var skipChoices = 0;
            var rerolled = false;
            if (allowRerollPerDay && (targetId is not null || remainingFlexible.Count > 0))
            {
                var baseOffer = sim.PeekOffer(day, skipChoices: 0).Options;
                if (!OfferSatisfiesWanted(baseOffer, targetId, remainingFlexible))
                {
                    var candidateSkipChoices = rerollsUsedSoFar + 1;
                    var rerollOffer = sim.PeekOffer(day, candidateSkipChoices).Options;
                    if (OfferSatisfiesWanted(rerollOffer, targetId, remainingFlexible))
                    {
                        skipChoices = candidateSkipChoices;
                        rerolled = true;
                        rerollsUsedSoFar++;
                    }
                }
            }

            var result = sim.SimulateDay(day, offered =>
            {
                if (targetId is int tid)
                {
                    return offered.Unlock1Id == tid || offered.Unlock2Id == tid ? tid : offered.Unlock1Id;
                }

                // No fixed target this day - greedily accept whichever offered slot still needs
                // picking up (Unlock1Id preferred, matching the existing filler convention below),
                // else fall back to the same "accept Unlock1Id as filler" policy as always.
                if (offered.Unlock1Id is int u1 && remainingFlexible.Contains(u1))
                {
                    remainingFlexible.Remove(u1);
                    return u1;
                }

                if (offered.Unlock2Id is int u2 && remainingFlexible.Contains(u2))
                {
                    remainingFlexible.Remove(u2);
                    return u2;
                }

                return offered.Unlock1Id;
            }, skipChoices);

            yield return (day, targetId, result, rerolled);
        }
    }

    private static string IndexToSeedString(long index, string alphabet, int length)
    {
        var chars = new char[length];
        var radix = alphabet.Length;
        for (var pos = length - 1; pos >= 0; pos--)
        {
            chars[pos] = alphabet[(int)(index % radix)];
            index /= radix;
        }

        return new string(chars);
    }
}
