using PlateUpSeedTools.Engine.Data;
using PlateUpSeedTools.Engine.Seeding;

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
    IReadOnlySet<string>? ExcludedSeeds = null);

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
        var maxDay = request.DayTargets.Count == 0 ? 0 : request.DayTargets.Keys.Max();
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
                    Matches(repo, new Seed(seedString), request.SettingPackId, request.Setup, request.DayTargets, maxDay, request.Tier))
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
        IReadOnlyDictionary<int, int> dayTargets, int maxDay, int tier)
    {
        foreach (var (_, targetId, actual) in Walk(repo, seed, settingPackId, setup, dayTargets, maxDay, tier))
        {
            if (targetId is int t && actual.Unlock1Id != t && actual.Unlock2Id != t)
            {
                return false;
            }
        }

        return true;
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
        IReadOnlyDictionary<int, int> dayTargets, int maxDay, int tier, int maxPaths)
    {
        var results = new List<IReadOnlyList<(int Day, int? CardId)>>();
        var current = new List<(int Day, int? CardId)>();
        var nodesVisited = 0;
        Backtrack(
            repo, seed, settingPackId, setup, dayTargets, maxDay, tier,
            day: 1, ownedIds: new HashSet<int>(setup.InitiallyOwnedIds),
            current, results, maxPaths, ref nodesVisited);
        return results;
    }

    private static void Backtrack(
        GameDataRepository repo, Seed seed, int settingPackId, ResolvedRunSetup setup,
        IReadOnlyDictionary<int, int> dayTargets, int maxDay, int tier, int day, HashSet<int> ownedIds,
        List<(int Day, int? CardId)> current, List<IReadOnlyList<(int Day, int? CardId)>> results,
        int maxPaths, ref int nodesVisited)
    {
        if (results.Count >= maxPaths || nodesVisited >= MaxPathSearchNodes)
        {
            return;
        }

        if (day > maxDay)
        {
            results.Add(current.ToList());
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
            Backtrack(repo, seed, settingPackId, setup, dayTargets, maxDay, tier, day + 1, ownedIds, current, results, maxPaths, ref nodesVisited);
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
            candidates = offered.Unlock2Id is int u2 && u2 != offered.Unlock1Id
                ? [offered.Unlock1Id.Value, u2]
                : [offered.Unlock1Id.Value];
        }

        foreach (var candidate in candidates)
        {
            if (results.Count >= maxPaths || nodesVisited >= MaxPathSearchNodes)
            {
                return;
            }

            current.Add((day, candidate));
            var nextOwned = new HashSet<int>(ownedIds) { candidate };
            Backtrack(repo, seed, settingPackId, setup, dayTargets, maxDay, tier, day + 1, nextOwned, current, results, maxPaths, ref nodesVisited);
            current.RemoveAt(current.Count - 1);
        }
    }

    private static IEnumerable<(int Day, int? TargetId, CardDayResult Actual)> Walk(
        GameDataRepository repo, Seed seed, int settingPackId, ResolvedRunSetup setup,
        IReadOnlyDictionary<int, int> dayTargets, int maxDay, int tier)
    {
        // ResolvedRunSetup.InitiallyOwnedIds gets mutated per-day by the simulator, so each candidate
        // seed needs its own copy - dish/heat/franchise-carryover resolution itself doesn't depend on
        // the seed.
        var freshSetup = new ResolvedRunSetup(new HashSet<int>(setup.InitiallyOwnedIds), setup.BonusPackIds);
        var sim = new CardDrawSimulator(repo, seed, settingPackId, freshSetup, tier);

        for (var day = 1; day <= maxDay; day++)
        {
            var targetId = dayTargets.TryGetValue(day, out var t) ? t : (int?)null;
            var result = sim.SimulateDay(day, offered =>
                targetId is int tid && (offered.Unlock1Id == tid || offered.Unlock2Id == tid) ? tid : offered.Unlock1Id);

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
