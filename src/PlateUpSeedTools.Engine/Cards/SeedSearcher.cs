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

    /// <summary>
    /// Replays the same day-by-day simulation <see cref="Matches"/> used to confirm a match, but
    /// returns what was actually picked each day instead of discarding it - lets a caller show the
    /// user a concrete card to pick for every day, including "filler" days that had no explicit
    /// target (which matters for schedules like "turbo" that draw every day, not just every 3rd) -
    /// see SearchEndpoints' POST /api/search/path. Uses the exact same filler policy (prefer the
    /// day's target if offered, otherwise Unlock1Id) as Matches so the reconstructed path is
    /// guaranteed consistent with why this seed matched in the first place.
    /// </summary>
    public static IReadOnlyList<(int Day, int? CardId)> ResolveCardPath(
        GameDataRepository repo, Seed seed, int settingPackId, ResolvedRunSetup setup,
        IReadOnlyDictionary<int, int> dayTargets, int maxDay, int tier)
    {
        var path = new List<(int Day, int? CardId)>();
        foreach (var (day, _, actual) in Walk(repo, seed, settingPackId, setup, dayTargets, maxDay, tier))
        {
            path.Add((day, actual.SelectedId));
        }

        return path;
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
