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
    bool OtFriendly = false);

/// <summary>Result of a <see cref="SeedSearcher.Search"/> call - <see cref="MatchCapReached"/> distinguishes "stopped early because matchCap was hit" from a genuine full-space completion, since both otherwise look like the loop just finishing.</summary>
public readonly record struct SeedSearchResult(bool MatchCapReached);

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

    /// <param name="matchCap">
    /// Stop once this many matches have been found (both the web UI's hard 500 cap and its
    /// "stop after first match" option, which is just matchCap=1, and the CLI's --max-results
    /// override all funnel through here - see SearchCommand.cs/SearchJobManager.cs). Required, not
    /// optional, so every caller has to make a deliberate choice - pass long.MaxValue for
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
        Action<string> onMatch,
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

        try
        {
            Parallel.For(0L, request.TotalToCheck, parallelOptions, i =>
            {
                var seedString = IndexToSeedString(i, request.Alphabet, request.Length);
                if (request.ExcludedSeeds?.Contains(seedString) != true &&
                    Matches(repo, new Seed(seedString), request.SettingPackId, request.Setup, request.DayTargets, flexibleTargetIds, maxDay, request.Tier, request.OtFriendly, request.SettingSlug))
                {
                    onMatch(seedString);
                    if (Interlocked.Increment(ref matchCount) >= matchCap)
                    {
                        matchCapReached = true;
                        linkedCts.Cancel();
                    }
                }

                var count = Interlocked.Increment(ref checkedCount);
                if (count % ProgressReportInterval == 0)
                {
                    onProgress(count);
                }
            });
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

        return new SeedSearchResult(matchCapReached);
    }

    private static bool Matches(
        GameDataRepository repo, Seed seed, int settingPackId, ResolvedRunSetup setup,
        IReadOnlyDictionary<int, int> dayTargets, IReadOnlySet<int> flexibleTargetIds, int maxDay, int tier,
        bool otFriendly, string? settingSlug)
    {
        // Mutated in place by Walk as flexible targets get picked up - a strict subset check at the
        // end (rather than a running "found day X" flag) since order among flexible targets doesn't
        // matter, only that all of them end up owned by maxDay.
        var remainingFlexible = new HashSet<int>(flexibleTargetIds);
        foreach (var (_, targetId, actual) in Walk(repo, seed, settingPackId, setup, dayTargets, remainingFlexible, maxDay, tier))
        {
            if (targetId is int t && actual.Unlock1Id != t && actual.Unlock2Id != t)
            {
                return false;
            }
        }

        if (remainingFlexible.Count != 0)
        {
            return false;
        }

        // Checked last - cards-target rejection above is cheap and rejects most seeds fast; the shop
        // check does real work (a KitchenApplianceState + up to 3 ShopDaySimulator.SimulateDay calls),
        // only worth paying once a seed has already survived the cheaper filter.
        return !otFriendly || OtFriendlyChecker.Matches(repo, seed, settingSlug!);
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
    public static IReadOnlyList<IReadOnlyList<(int Day, int? CardId)>> ResolveCardPaths(
        GameDataRepository repo, Seed seed, int settingPackId, ResolvedRunSetup setup,
        IReadOnlyDictionary<int, int> dayTargets, IReadOnlySet<int> flexibleTargetIds, int maxDay, int tier, int maxPaths)
    {
        var results = new List<IReadOnlyList<(int Day, int? CardId)>>();
        var current = new List<(int Day, int? CardId)>();
        var nodesVisited = 0;
        Backtrack(
            repo, seed, settingPackId, setup, dayTargets, maxDay, tier,
            day: 0, ownedIds: new HashSet<int>(setup.InitiallyOwnedIds), remainingFlexible: new HashSet<int>(flexibleTargetIds),
            current, results, maxPaths, ref nodesVisited);
        return results;
    }

    private static void Backtrack(
        GameDataRepository repo, Seed seed, int settingPackId, ResolvedRunSetup setup,
        IReadOnlyDictionary<int, int> dayTargets, int maxDay, int tier, int day, HashSet<int> ownedIds, HashSet<int> remainingFlexible,
        List<(int Day, int? CardId)> current, List<IReadOnlyList<(int Day, int? CardId)>> results,
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
        // owned-set - the decline policy (returns null) means it never mutates anything we keep.
        var peekSetup = new ResolvedRunSetup(new HashSet<int>(ownedIds), setup.BonusPackIds);
        var offered = default(UnlockOptions);
        new CardDrawSimulator(repo, seed, settingPackId, peekSetup, tier).SimulateDay(day, o =>
        {
            offered = o;
            return null;
        });

        if (offered.Unlock1Id is null)
        {
            current.Add((day, null));
            Backtrack(repo, seed, settingPackId, setup, dayTargets, maxDay, tier, day + 1, ownedIds, remainingFlexible, current, results, maxPaths, ref nodesVisited);
            current.RemoveAt(current.Count - 1);
            return;
        }

        List<int> candidates;
        if (dayTargets.TryGetValue(day, out var target))
        {
            if (offered.Unlock1Id != target && offered.Unlock2Id != target)
            {
                return; // Dead end: this branch's owned-set no longer offers the target on this day.
            }

            candidates = [target];
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

            current.Add((day, candidate));
            var nextOwned = new HashSet<int>(ownedIds) { candidate };
            var nextRemainingFlexible = remainingFlexible.Contains(candidate)
                ? new HashSet<int>(remainingFlexible.Where(id => id != candidate))
                : remainingFlexible;
            Backtrack(repo, seed, settingPackId, setup, dayTargets, maxDay, tier, day + 1, nextOwned, nextRemainingFlexible, current, results, maxPaths, ref nodesVisited);
            current.RemoveAt(current.Count - 1);
        }
    }

    private static IEnumerable<(int Day, int? TargetId, CardDayResult Actual)> Walk(
        GameDataRepository repo, Seed seed, int settingPackId, ResolvedRunSetup setup,
        IReadOnlyDictionary<int, int> dayTargets, HashSet<int> remainingFlexible, int maxDay, int tier)
    {
        // ResolvedRunSetup.InitiallyOwnedIds gets mutated per-day by the simulator, so each candidate
        // seed needs its own copy - dish/heat/franchise-carryover resolution itself doesn't depend on
        // the seed.
        var freshSetup = new ResolvedRunSetup(new HashSet<int>(setup.InitiallyOwnedIds), setup.BonusPackIds);
        var sim = new CardDrawSimulator(repo, seed, settingPackId, freshSetup, tier);

        // Day 0 is the prep day before the first real night - only relevant for packs with a
        // Day=0 UnlockConditionOnce (currently just santa-workshop's Christmas Special Card), but
        // harmless to always include: no other decoded condition can ever match day 0.
        for (var day = 0; day <= maxDay; day++)
        {
            var targetId = dayTargets.TryGetValue(day, out var t) ? t : (int?)null;
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
            });

            yield return (day, targetId, result);
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
