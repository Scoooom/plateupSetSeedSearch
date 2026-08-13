using PlateUpSeedTools.Engine.Cards;
using PlateUpSeedTools.Engine.Data;
using PlateUpSeedTools.Engine.Seeding;
using Xunit;

namespace PlateUpSeedTools.Engine.Tests;

/// <summary>
/// Exercises PlateUpSeedTools.Engine.Cards.SeedSearcher - the brute-force seed-space loop extracted
/// out of the CLI's SearchCommand.cs so the web search job manager can reuse it - independently of
/// the CLI. Uses the same GameDataRepository/RunSetup/CardDrawSimulator setup as
/// CardDrawSimulatorGroundTruthTests (Normal setting, starting dish Pies, Heat 0), but over a small
/// synthetic search space (8 candidate seeds) rather than the real ~19^8 game seed space, since
/// exhaustively brute-forcing that isn't practical inside a unit test. Cross-checks SeedSearcher's
/// parallel loop against an independent straightforward sequential simulation of the same space -
/// this is what actually matters for trusting the extraction preserved SearchCommand's original
/// behavior (no seeds silently skipped/double-counted, matches reported for every seed that
/// actually offers the target card, final checked-count accurate).
/// </summary>
public class SeedSearcherTests
{
    private const int PiesId = -133939790;

    // Ground-truth-pinned by CardDrawSimulatorGroundTruthTests: seed "bdghjmqr", day 3, Unlock1Id
    // is Vegetable Pies. Reused here as a real card ID to search for (any valid target ID would do
    // for this test's purposes; this one is convenient because it's already known-good).
    private const int TargetCardId = -1802123036;
    private const int TargetDay = 3;

    [Fact]
    public void FindsExactlyTheSameMatchesAsAnIndependentSequentialSimulation()
    {
        var repo = new GameDataRepository(Path.Combine(AppContext.BaseDirectory, "data", "settings"));
        var settingPackId = repo.ResolveUnlockPackId("normal");
        var setup = RunSetup.Resolve(repo, PiesId, subOptionChoice: null, heatLevel: 0, startingUnlockId: null);

        const string alphabet = "bd";
        const int length = 3;
        var totalToCheck = (long)Math.Pow(alphabet.Length, length);

        var expectedMatches = ComputeMatchesSequentially(repo, settingPackId, setup, alphabet, length, totalToCheck);

        var request = new SeedSearchRequest(settingPackId, setup, new Dictionary<int, int> { [TargetDay] = TargetCardId }, Tier: 0, alphabet, length, totalToCheck);

        var actualMatches = new HashSet<string>();
        var matchLock = new object();
        var finalCheckedCount = 0L;
        var progressCalls = 0;

        var result = SeedSearcher.Search(
            repo,
            request,
            maxDegreeOfParallelism: -1,
            matchCap: long.MaxValue,
            onMatch: seed =>
            {
                lock (matchLock)
                {
                    actualMatches.Add(seed);
                }
            },
            onProgress: count =>
            {
                finalCheckedCount = count;
                progressCalls++;
            },
            cancellationToken: CancellationToken.None);

        Assert.Equal(expectedMatches, actualMatches);
        Assert.Equal(totalToCheck, finalCheckedCount);
        // Search always reports a final tally even for a space too small to hit the periodic
        // progress interval, so onProgress must have fired at least once.
        Assert.True(progressCalls >= 1);
        Assert.False(result.MatchCapReached);
    }

    // Uses an empty DayTargets dictionary - Matches() trivially returns true for every seed when
    // there are no day targets (the day loop just never runs), which makes the match count exactly
    // controllable without depending on real card-draw outcomes. maxDegreeOfParallelism: 1 (rather
    // than the CLI/web's usual unbounded/capped values) makes the cap deterministic - no in-flight
    // parallel iterations can complete just after the cap is hit, so the counts below are exact,
    // not just "close to."
    [Fact]
    public void StopsEarlyOnceMatchCapIsReached()
    {
        var repo = new GameDataRepository(Path.Combine(AppContext.BaseDirectory, "data", "settings"));
        var settingPackId = repo.ResolveUnlockPackId("normal");
        var setup = RunSetup.Resolve(repo, PiesId, subOptionChoice: null, heatLevel: 0, startingUnlockId: null);

        const string alphabet = "bdgh";
        const int length = 4;
        var totalToCheck = (long)Math.Pow(alphabet.Length, length); // 256
        const long matchCap = 10;

        var request = new SeedSearchRequest(settingPackId, setup, new Dictionary<int, int>(), Tier: 0, alphabet, length, totalToCheck);

        var matchCount = 0;
        var finalCheckedCount = 0L;

        var result = SeedSearcher.Search(
            repo,
            request,
            maxDegreeOfParallelism: 1,
            matchCap: matchCap,
            onMatch: _ => matchCount++,
            onProgress: count => finalCheckedCount = count,
            cancellationToken: CancellationToken.None);

        Assert.True(result.MatchCapReached);
        Assert.Equal(matchCap, matchCount);
        Assert.True(finalCheckedCount < totalToCheck);
    }

    [Fact]
    public void DoesNotReportMatchCapReachedWhenTheCapIsNeverHit()
    {
        var repo = new GameDataRepository(Path.Combine(AppContext.BaseDirectory, "data", "settings"));
        var settingPackId = repo.ResolveUnlockPackId("normal");
        var setup = RunSetup.Resolve(repo, PiesId, subOptionChoice: null, heatLevel: 0, startingUnlockId: null);

        const string alphabet = "bdgh";
        const int length = 4;
        var totalToCheck = (long)Math.Pow(alphabet.Length, length); // 256

        var request = new SeedSearchRequest(settingPackId, setup, new Dictionary<int, int>(), Tier: 0, alphabet, length, totalToCheck);

        var matchCount = 0;
        var finalCheckedCount = 0L;

        var result = SeedSearcher.Search(
            repo,
            request,
            maxDegreeOfParallelism: 1,
            matchCap: 1000, // above totalToCheck, so every trivially-matching seed fits under it
            onMatch: _ => matchCount++,
            onProgress: count => finalCheckedCount = count,
            cancellationToken: CancellationToken.None);

        Assert.False(result.MatchCapReached);
        Assert.Equal(totalToCheck, matchCount);
        Assert.Equal(totalToCheck, finalCheckedCount);
    }

    [Fact]
    public void EmptyFlexibleTargetsReproducesFixedDayOnlyBehavior()
    {
        var repo = new GameDataRepository(Path.Combine(AppContext.BaseDirectory, "data", "settings"));
        var settingPackId = repo.ResolveUnlockPackId("normal");
        var setup = RunSetup.Resolve(repo, PiesId, subOptionChoice: null, heatLevel: 0, startingUnlockId: null);

        const string alphabet = "bd";
        const int length = 3;
        var totalToCheck = (long)Math.Pow(alphabet.Length, length);

        var expectedMatches = ComputeMatchesSequentially(repo, settingPackId, setup, alphabet, length, totalToCheck);

        // FlexibleTargetIds explicitly set to an empty set (rather than left null) - should be a
        // total no-op, reproducing exactly today's fixed-day-only results.
        var request = new SeedSearchRequest(
            settingPackId, setup, new Dictionary<int, int> { [TargetDay] = TargetCardId }, Tier: 0, alphabet, length, totalToCheck,
            FlexibleTargetIds: new HashSet<int>());

        var actualMatches = RunSearch(repo, request);

        Assert.Equal(expectedMatches, actualMatches);
    }

    // "Mushroom Pies" - ground-truth-pinned by CardDrawSimulatorGroundTruthTests.
    // MatchesRealCapturedCardSequence as seed "bdghjmqr"'s real day-6 Unlock1Id under this exact
    // setup (Normal/Pies/Heat 0) - reused here as a second real card ID distinct from TargetCardId.
    private const int SecondCardId = -1992316049;

    [Fact]
    public void FlexibleTargetMatchesEveryDayItCouldBeObtainedByTheHorizon()
    {
        var repo = new GameDataRepository(Path.Combine(AppContext.BaseDirectory, "data", "settings"));
        var settingPackId = repo.ResolveUnlockPackId("normal");
        var setup = RunSetup.Resolve(repo, PiesId, subOptionChoice: null, heatLevel: 0, startingUnlockId: null);

        const string alphabet = "bd";
        const int length = 3;
        var totalToCheck = (long)Math.Pow(alphabet.Length, length);
        const int horizon = TargetDay;

        var expectedMatches = ComputeFlexibleMatchesSequentially(
            repo, settingPackId, setup, alphabet, length, totalToCheck,
            new Dictionary<int, int>(), new HashSet<int> { TargetCardId }, horizon);

        var request = new SeedSearchRequest(
            settingPackId, setup, new Dictionary<int, int>(), Tier: 0, alphabet, length, totalToCheck,
            FlexibleTargetIds: new HashSet<int> { TargetCardId }, FlexibleHorizon: horizon);

        var actualMatches = RunSearch(repo, request);

        Assert.Equal(expectedMatches, actualMatches);

        // Every seed that matches the fixed-day-3 form of the same target must also match the
        // flexible form with a horizon >= 3 - obtaining it on day 3 certainly counts as "obtained
        // by day 3".
        var fixedMatches = ComputeMatchesSequentially(repo, settingPackId, setup, alphabet, length, totalToCheck);
        Assert.Subset(actualMatches, fixedMatches);
    }

    [Fact]
    public void MixesAFixedDayTargetAndAFlexibleTargetInTheSameSearch()
    {
        var repo = new GameDataRepository(Path.Combine(AppContext.BaseDirectory, "data", "settings"));
        var settingPackId = repo.ResolveUnlockPackId("normal");
        var setup = RunSetup.Resolve(repo, PiesId, subOptionChoice: null, heatLevel: 0, startingUnlockId: null);

        const string alphabet = "bd";
        const int length = 3;
        var totalToCheck = (long)Math.Pow(alphabet.Length, length);
        const int horizon = 6; // covers SecondCardId's real day-6 offer for the ground-truth seed

        var dayTargets = new Dictionary<int, int> { [TargetDay] = TargetCardId };
        var flexibleTargetIds = new HashSet<int> { SecondCardId };

        var expectedMatches = ComputeFlexibleMatchesSequentially(
            repo, settingPackId, setup, alphabet, length, totalToCheck, dayTargets, flexibleTargetIds, horizon);

        var request = new SeedSearchRequest(
            settingPackId, setup, dayTargets, Tier: 0, alphabet, length, totalToCheck,
            FlexibleTargetIds: flexibleTargetIds, FlexibleHorizon: horizon);

        var actualMatches = RunSearch(repo, request);

        Assert.Equal(expectedMatches, actualMatches);
    }

    [Fact]
    public void ResolveCardPathsReportsTheDayAFlexibleTargetActuallyLandedOn()
    {
        var repo = new GameDataRepository(Path.Combine(AppContext.BaseDirectory, "data", "settings"));
        var settingPackId = repo.ResolveUnlockPackId("normal");
        var setup = RunSetup.Resolve(repo, PiesId, subOptionChoice: null, heatLevel: 0, startingUnlockId: null);

        var flexibleTargetIds = new HashSet<int> { TargetCardId };
        const int horizon = TargetDay;

        // "bdghjmqr" is the ground-truth-pinned seed itself, not a member of the tiny brute-force
        // alphabet used elsewhere in this file - real Unlock1Id/Unlock2Id on day 3 is Vegetable
        // Pies/Health and Safety (CardDrawSimulatorGroundTruthTests.CapturedOffers), so a flexible
        // search for Vegetable Pies with a day-3 horizon must resolve to exactly day 3.
        var paths = SeedSearcher.ResolveCardPaths(
            repo, new Seed("bdghjmqr"), settingPackId, setup, new Dictionary<int, int>(), flexibleTargetIds, horizon, tier: 0, maxPaths: 3);

        Assert.NotEmpty(paths);
        var path = paths[0];
        var targetEntry = Assert.Single(path, entry => entry.CardId == TargetCardId);
        Assert.Equal(TargetDay, targetEntry.Day);
    }

    private static HashSet<string> RunSearch(GameDataRepository repo, SeedSearchRequest request)
    {
        var actualMatches = new HashSet<string>();
        var matchLock = new object();

        SeedSearcher.Search(
            repo,
            request,
            maxDegreeOfParallelism: -1,
            matchCap: long.MaxValue,
            onMatch: seed =>
            {
                lock (matchLock)
                {
                    actualMatches.Add(seed);
                }
            },
            onProgress: _ => { },
            cancellationToken: CancellationToken.None);

        return actualMatches;
    }

    // Mirrors SeedSearcher's private Walk/Matches greedy flexible-target policy independently (days
    // 0..horizon, fixed targets take priority, then whichever offered slot still needs picking up -
    // Unlock1Id preferred - else the same Unlock1Id filler fallback) - pins the intended matching
    // behavior explicitly so a future refactor of the engine's own implementation can't silently
    // drift without a test noticing.
    private static HashSet<string> ComputeFlexibleMatchesSequentially(
        GameDataRepository repo, int settingPackId, ResolvedRunSetup setup, string alphabet, int length, long totalToCheck,
        IReadOnlyDictionary<int, int> dayTargets, IReadOnlySet<int> flexibleTargetIds, int horizon)
    {
        var matches = new HashSet<string>();
        for (var i = 0L; i < totalToCheck; i++)
        {
            var seedString = IndexToSeedString(i, alphabet, length);
            var freshSetup = new ResolvedRunSetup(new HashSet<int>(setup.InitiallyOwnedIds), setup.BonusPackIds);
            var sim = new CardDrawSimulator(repo, new Seed(seedString), settingPackId, freshSetup);
            var remaining = new HashSet<int>(flexibleTargetIds);
            var fixedMismatch = false;

            for (var day = 0; day <= horizon; day++)
            {
                var fixedTarget = dayTargets.TryGetValue(day, out var t) ? t : (int?)null;
                var result = sim.SimulateDay(day, offered =>
                {
                    if (fixedTarget is int ft)
                    {
                        return offered.Unlock1Id == ft || offered.Unlock2Id == ft ? ft : offered.Unlock1Id;
                    }

                    if (offered.Unlock1Id is int u1 && remaining.Contains(u1))
                    {
                        remaining.Remove(u1);
                        return u1;
                    }

                    if (offered.Unlock2Id is int u2 && remaining.Contains(u2))
                    {
                        remaining.Remove(u2);
                        return u2;
                    }

                    return offered.Unlock1Id;
                });

                if (fixedTarget is int f && result.Unlock1Id != f && result.Unlock2Id != f)
                {
                    fixedMismatch = true;
                    break;
                }
            }

            if (!fixedMismatch && remaining.Count == 0)
            {
                matches.Add(seedString);
            }
        }

        return matches;
    }

    private static HashSet<string> ComputeMatchesSequentially(
        GameDataRepository repo, int settingPackId, ResolvedRunSetup setup, string alphabet, int length, long totalToCheck)
    {
        var matches = new HashSet<string>();
        for (var i = 0L; i < totalToCheck; i++)
        {
            var seedString = IndexToSeedString(i, alphabet, length);
            var freshSetup = new ResolvedRunSetup(new HashSet<int>(setup.InitiallyOwnedIds), setup.BonusPackIds);
            var sim = new CardDrawSimulator(repo, new Seed(seedString), settingPackId, freshSetup);

            CardDayResult? targetResult = null;
            for (var day = 1; day <= TargetDay; day++)
            {
                var result = sim.SimulateDay(day, offered =>
                    day == TargetDay && (offered.Unlock1Id == TargetCardId || offered.Unlock2Id == TargetCardId)
                        ? TargetCardId
                        : offered.Unlock1Id);
                if (day == TargetDay)
                {
                    targetResult = result;
                }
            }

            if (targetResult is { } r && (r.Unlock1Id == TargetCardId || r.Unlock2Id == TargetCardId))
            {
                matches.Add(seedString);
            }
        }

        return matches;
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
