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
    long TotalToCheck);

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

    public static void Search(
        GameDataRepository repo,
        SeedSearchRequest request,
        int maxDegreeOfParallelism,
        Action<string> onMatch,
        Action<long> onProgress,
        CancellationToken cancellationToken)
    {
        var maxDay = request.DayTargets.Count == 0 ? 0 : request.DayTargets.Keys.Max();
        long checkedCount = 0;

        var parallelOptions = new ParallelOptions
        {
            CancellationToken = cancellationToken,
            MaxDegreeOfParallelism = maxDegreeOfParallelism,
        };

        try
        {
            Parallel.For(0L, request.TotalToCheck, parallelOptions, i =>
            {
                var seedString = IndexToSeedString(i, request.Alphabet, request.Length);
                if (Matches(repo, new Seed(seedString), request.SettingPackId, request.Setup, request.DayTargets, maxDay, request.Tier))
                {
                    onMatch(seedString);
                }

                var count = Interlocked.Increment(ref checkedCount);
                if (count % ProgressReportInterval == 0)
                {
                    onProgress(count);
                }
            });
        }
        finally
        {
            // Always report the final tally, even on cancellation, so callers don't need to
            // separately track "how far did we actually get" themselves.
            onProgress(Interlocked.Read(ref checkedCount));
        }
    }

    private static bool Matches(
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

            if (targetId is int expected && result.Unlock1Id != expected && result.Unlock2Id != expected)
            {
                return false;
            }
        }

        return true;
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
