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
            onMatch: (seed, _) =>
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
            onMatch: (_, _) => matchCount++,
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
            onMatch: (_, _) => matchCount++,
            onProgress: count => finalCheckedCount = count,
            cancellationToken: CancellationToken.None);

        Assert.False(result.MatchCapReached);
        Assert.Equal(totalToCheck, matchCount);
        Assert.Equal(totalToCheck, finalCheckedCount);
    }

    // "v4" and "46" both hash to Seed.IntValue 5861743 - and every RNG stream derives solely from
    // that 32-bit hash, so they are the byte-identical game. The searcher must report only one of
    // them and record the other as an equivalent. Alphabet "v46" length 2 is 9 candidate strings,
    // exactly one colliding pair, so 8 distinct games. maxDegreeOfParallelism: 1 makes the
    // first-seen representative deterministic ("v4" is enumerated before "46").
    [Fact]
    public void CollapsesSeedStringsThatHashToTheSameValue()
    {
        Assert.Equal(new Seed("v4").IntValue, new Seed("46").IntValue); // guard: the collision this test relies on

        var repo = new GameDataRepository(Path.Combine(AppContext.BaseDirectory, "data", "settings"));
        var settingPackId = repo.ResolveUnlockPackId("normal");
        var setup = RunSetup.Resolve(repo, PiesId, subOptionChoice: null, heatLevel: 0, startingUnlockId: null);

        const string alphabet = "v46";
        const int length = 2;
        var totalToCheck = (long)Math.Pow(alphabet.Length, length); // 9

        var request = new SeedSearchRequest(settingPackId, setup, new Dictionary<int, int>(), Tier: 0, alphabet, length, totalToCheck);

        var reported = new List<string>();
        var result = SeedSearcher.Search(
            repo, request, maxDegreeOfParallelism: 1, matchCap: long.MaxValue,
            onMatch: (seed, _) => reported.Add(seed),
            onProgress: _ => { },
            cancellationToken: CancellationToken.None);

        Assert.Equal(8, reported.Count);          // 9 strings, one colliding pair collapsed
        Assert.Contains("v4", reported);          // first-seen wins
        Assert.DoesNotContain("46", reported);

        var equivalent = Assert.Single(result.EquivalentSeeds);
        Assert.Equal("v4", equivalent.Key);
        Assert.Equal(1, equivalent.Value.Count);
        Assert.Equal(new[] { "46" }, equivalent.Value.Sample);
    }

    // With 9 candidate strings but 8 distinct games, a cap of 9 is never reached (proving the cap
    // counts distinct matches, not raw matching strings), while a cap of 8 is reached exactly and a
    // cap of 7 stops early.
    [Theory]
    [InlineData(9, false)]
    [InlineData(8, true)]
    [InlineData(7, true)]
    public void MatchCapCountsDistinctSeeds(long matchCap, bool expectedCapReached)
    {
        var repo = new GameDataRepository(Path.Combine(AppContext.BaseDirectory, "data", "settings"));
        var settingPackId = repo.ResolveUnlockPackId("normal");
        var setup = RunSetup.Resolve(repo, PiesId, subOptionChoice: null, heatLevel: 0, startingUnlockId: null);

        var request = new SeedSearchRequest(settingPackId, setup, new Dictionary<int, int>(), Tier: 0, "v46", 2, 9);

        var matchCount = 0;
        var result = SeedSearcher.Search(
            repo, request, maxDegreeOfParallelism: 1, matchCap: matchCap,
            onMatch: (_, _) => matchCount++,
            onProgress: _ => { },
            cancellationToken: CancellationToken.None);

        Assert.Equal(expectedCapReached, result.MatchCapReached);
        Assert.Equal(Math.Min(matchCap, 8), matchCount);
    }

    // Excluded seeds are matched by hash, so excluding "v4" also drops its identical twin "46".
    [Fact]
    public void ExcludedSeedIsMatchedByHash()
    {
        var repo = new GameDataRepository(Path.Combine(AppContext.BaseDirectory, "data", "settings"));
        var settingPackId = repo.ResolveUnlockPackId("normal");
        var setup = RunSetup.Resolve(repo, PiesId, subOptionChoice: null, heatLevel: 0, startingUnlockId: null);

        var request = new SeedSearchRequest(
            settingPackId, setup, new Dictionary<int, int>(), Tier: 0, "v46", 2, 9,
            ExcludedSeeds: new HashSet<string> { "v4" });

        var reported = new List<string>();
        SeedSearcher.Search(
            repo, request, maxDegreeOfParallelism: 1, matchCap: long.MaxValue,
            onMatch: (seed, _) => reported.Add(seed),
            onProgress: _ => { },
            cancellationToken: CancellationToken.None);

        Assert.DoesNotContain("v4", reported);
        Assert.DoesNotContain("46", reported); // same hash as the excluded "v4"
        Assert.Equal(7, reported.Count);        // 9 strings - 1 colliding pair (both excluded)
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
            repo, new Seed("bdghjmqr"), settingPackId, setup, new Dictionary<int, int>(), flexibleTargetIds, horizon,
            tier: 0, allowRerollPerDay: false, maxPaths: 3);

        Assert.NotEmpty(paths);
        var path = paths[0];
        var targetEntry = Assert.Single(path, entry => entry.CardId == TargetCardId);
        Assert.Equal(TargetDay, targetEntry.Day);
    }

    /// <summary>
    /// Proves OtFriendly is a real AND-condition, not silently ignored - a search with no day
    /// targets and OtFriendly:false trivially matches every seed in the space (same as
    /// DoesNotReportMatchCapReachedWhenTheCapIsNeverHit), while the exact same space with
    /// OtFriendly:true narrows to a strict subset that still includes the one seed
    /// (OtFriendlyCheckerTests confirms "vqrj" is ground-truth-confirmed OtFriendly) known to satisfy it.
    /// </summary>
    [Fact]
    public void OtFriendlyNarrowsMatchesToASubsetIncludingAKnownGoodSeed()
    {
        var repo = new GameDataRepository(Path.Combine(AppContext.BaseDirectory, "data", "settings"));
        var settingPackId = repo.ResolveUnlockPackId("normal");
        var setup = RunSetup.Resolve(repo, PiesId, subOptionChoice: null, heatLevel: 0, startingUnlockId: null);

        const string alphabet = "vqrj";
        const int length = 4;
        var totalToCheck = (long)Math.Pow(alphabet.Length, length); // 256, includes "vqrj" itself

        var withoutFilter = new SeedSearchRequest(settingPackId, setup, new Dictionary<int, int>(), Tier: 0, alphabet, length, totalToCheck);
        var withFilter = withoutFilter with { OtFriendly = true, SettingSlug = "normal" };

        var allMatches = RunSearch(repo, withoutFilter);
        var otFriendlyMatches = RunSearch(repo, withFilter);

        Assert.Equal(totalToCheck, allMatches.Count); // no day targets - everyone matches trivially
        Assert.True(otFriendlyMatches.Count < allMatches.Count);
        Assert.Subset(allMatches, otFriendlyMatches);
        Assert.Contains("vqrj", otFriendlyMatches);
    }

    /// <summary>
    /// Proves OtFriendly is tagged on every match regardless of gating, not just when the search
    /// itself was OtFriendly-gated (OtFriendlyNarrowsMatchesToASubsetIncludingAKnownGoodSeed proves
    /// the gating side separately) - same "vqrj"-alphabet ground truth (OtFriendlyCheckerTests
    /// confirms "vqrj" is OtFriendly, "gggg" is not, both members of this alphabet).
    /// </summary>
    [Fact]
    public void OtFriendlyIsTaggedOnEveryMatchRegardlessOfGating()
    {
        var repo = new GameDataRepository(Path.Combine(AppContext.BaseDirectory, "data", "settings"));
        var settingPackId = repo.ResolveUnlockPackId("normal");
        var setup = RunSetup.Resolve(repo, PiesId, subOptionChoice: null, heatLevel: 0, startingUnlockId: null);

        const string alphabet = "vqrjg";
        const int length = 4;
        var totalToCheck = (long)Math.Pow(alphabet.Length, length);

        // No OtFriendly gating - every seed in the space matches trivially (no day targets).
        var request = new SeedSearchRequest(
            settingPackId, setup, new Dictionary<int, int>(), Tier: 0, alphabet, length, totalToCheck,
            SettingSlug: "normal", OtFriendly: false);

        var tags = RunSearchWithOtFriendlyTags(repo, request);

        Assert.Equal(totalToCheck, tags.Count);
        Assert.True(tags["vqrj"]);
        Assert.False(tags["gggg"]);
    }

    /// <summary>
    /// Proves AllowRerollPerDay is a real superset-widening AND-condition: seed "bbbb" only offers
    /// "Roast Potato" on day 3 via a reroll (PeekOffer(day: 3, skipChoices: 1)), not the base offer -
    /// discovered by brute-force probing PeekOffer against this exact Normal/Pies/Heat 0 setup, not
    /// hand-picked. Without the flag it must NOT match; with it, it must.
    /// </summary>
    [Fact]
    public void AllowRerollPerDayFindsAMatchOnlyReachableByRerolling()
    {
        var repo = new GameDataRepository(Path.Combine(AppContext.BaseDirectory, "data", "settings"));
        var settingPackId = repo.ResolveUnlockPackId("normal");
        var setup = RunSetup.Resolve(repo, PiesId, subOptionChoice: null, heatLevel: 0, startingUnlockId: null);
        const int rerollDay = 3;
        const int rerollOnlyCardId = -1289628495; // "Roast Potato"

        var requestNoReroll = new SeedSearchRequest(
            settingPackId, setup, new Dictionary<int, int> { [rerollDay] = rerollOnlyCardId }, Tier: 0,
            Alphabet: "b", Length: 4, TotalToCheck: 1);
        var requestWithReroll = requestNoReroll with { AllowRerollPerDay = true };

        Assert.Empty(RunSearch(repo, requestNoReroll));
        Assert.Equal(new HashSet<string> { "bbbb" }, RunSearch(repo, requestWithReroll));
    }

    /// <summary>
    /// ResolveCardPaths' equivalent of the test above - the reroll day comes back Rerolled: true and
    /// every other day in the path Rerolled: false.
    /// </summary>
    [Fact]
    public void ResolveCardPathsReportsWhichDayWasRerolled()
    {
        var repo = new GameDataRepository(Path.Combine(AppContext.BaseDirectory, "data", "settings"));
        var settingPackId = repo.ResolveUnlockPackId("normal");
        var setup = RunSetup.Resolve(repo, PiesId, subOptionChoice: null, heatLevel: 0, startingUnlockId: null);
        const int rerollDay = 3;
        const int rerollOnlyCardId = -1289628495; // "Roast Potato"

        var paths = SeedSearcher.ResolveCardPaths(
            repo, new Seed("bbbb"), settingPackId, setup, new Dictionary<int, int> { [rerollDay] = rerollOnlyCardId },
            new HashSet<int>(), rerollDay, tier: 0, allowRerollPerDay: true, maxPaths: 3);

        Assert.NotEmpty(paths);
        var path = paths[0];
        var rerollEntry = Assert.Single(path, entry => entry.CardId == rerollOnlyCardId);
        Assert.Equal(rerollDay, rerollEntry.Day);
        Assert.True(rerollEntry.Rerolled);
        Assert.All(path.Where(e => e.Day != rerollDay), entry => Assert.False(entry.Rerolled));
    }

    /// <summary>
    /// Regression test for the run-wide-cumulative-RerollIndex bug (see
    /// CardDrawSimulatorGroundTruthTests.MatchesRealMultiRerollCapture and SeedSearchRequest's
    /// AllowRerollPerDay doc comment): before the fix, Walk/Backtrack hardcoded skipChoices: 1 for
    /// every reroll, so a search for a card only offered by the WRONG skip_choices=1 on day 5 (real
    /// day-5 reroll is skip_choices=2, since seed "bbbbvw7" banquet-hall/Pies/Heat1 already used a
    /// reroll on day 3) would have false-positive matched. "Exclusive" is exactly that card - real
    /// skip_choices=2 offers Affordable/Charming (same as the base offer) on day 5, never Exclusive.
    /// </summary>
    [Fact]
    public void MultiRerollDoesNotFalsePositiveOnStaleSkipChoices()
    {
        var repo = new GameDataRepository(Path.Combine(AppContext.BaseDirectory, "data", "settings"));
        var settingPackId = repo.ResolveUnlockPackId("banquet-hall");
        var setup = RunSetup.Resolve(repo, PiesId, subOptionChoice: null, heatLevel: 1, startingUnlockId: 1989028383);
        const int allYouCanEatId = -347199069;
        const int exclusiveId = -1323758054; // only offered by the WRONG (pre-fix) skip_choices=1
        const int charmingId = 1293847744; // genuinely offered on day 5 (base offer, and real reroll)

        var falsePositiveTargets = new Dictionary<int, int> { [3] = allYouCanEatId, [5] = exclusiveId };
        var realTargets = new Dictionary<int, int> { [3] = allYouCanEatId, [5] = charmingId };

        var falsePositivePaths = SeedSearcher.ResolveCardPaths(
            repo, new Seed("bbbbvw7"), settingPackId, setup, falsePositiveTargets,
            new HashSet<int>(), maxDay: 5, tier: 0, allowRerollPerDay: true, maxPaths: 3);
        Assert.Empty(falsePositivePaths);

        var realPaths = SeedSearcher.ResolveCardPaths(
            repo, new Seed("bbbbvw7"), settingPackId, setup, realTargets,
            new HashSet<int>(), maxDay: 5, tier: 0, allowRerollPerDay: true, maxPaths: 3);
        Assert.NotEmpty(realPaths);
    }

    // Guards the exact int/long overflow pitfall IntValue-space search depends on: `1 << 32` (int)
    // silently wraps to 0, so the crossover threshold must be computed as a `long`.
    [Theory]
    [InlineData(4294967295L, false)] // 2^32 - 1: string space still cheaper (or equal)
    [InlineData(4294967296L, false)] // exactly 2^32: equal cost, not strictly cheaper - stays in string mode
    [InlineData(4294967297L, true)]  // 2^32 + 1: string space is now the larger one
    public void ShouldUseIntValueSpace_CrossesOverExactlyAtTheFixedIntValueSpaceSize(long totalToCheck, bool expected)
    {
        var repo = new GameDataRepository(Path.Combine(AppContext.BaseDirectory, "data", "settings"));
        var settingPackId = repo.ResolveUnlockPackId("normal");
        var setup = RunSetup.Resolve(repo, PiesId, subOptionChoice: null, heatLevel: 0, startingUnlockId: null);
        var request = new SeedSearchRequest(settingPackId, setup, new Dictionary<int, int>(), Tier: 0, Seed.FriendlyCharacters, Length: 8, totalToCheck);

        Assert.Equal(expected, SeedSearcher.ShouldUseIntValueSpace(request));
    }

    // The actual risk IntValue-space search mode introduces: does a Seed built via the raw-int fast
    // path (Seed.FromIntValue, used inside the IntValue-mode sweep) behave identically, for every
    // downstream consumer, to an ordinary string-backed Seed with the same hash? Everything in the
    // engine only ever reads .IntValue off a Seed (confirmed by inspection), so this exercises the
    // real consumer (CardDrawSimulator) across a full multi-day walk rather than just comparing the
    // two Seeds' IntValue properties in isolation.
    [Fact]
    public void SeedFromIntValue_ProducesIdenticalCardDrawsToAStringBackedSeedWithTheSameHash()
    {
        var repo = new GameDataRepository(Path.Combine(AppContext.BaseDirectory, "data", "settings"));
        var settingPackId = repo.ResolveUnlockPackId("normal");
        var setup = RunSetup.Resolve(repo, PiesId, subOptionChoice: null, heatLevel: 0, startingUnlockId: null);

        var stringSeed = new Seed("bdghjmqr");
        var intSeed = Seed.FromIntValue(stringSeed.IntValue);
        Assert.Equal(stringSeed.IntValue, intSeed.IntValue);

        var stringSim = new CardDrawSimulator(repo, stringSeed, settingPackId, new ResolvedRunSetup(new HashSet<int>(setup.InitiallyOwnedIds), setup.BonusPackIds));
        var intSim = new CardDrawSimulator(repo, intSeed, settingPackId, new ResolvedRunSetup(new HashSet<int>(setup.InitiallyOwnedIds), setup.BonusPackIds));

        for (var day = 1; day <= 15; day++)
        {
            var fromString = stringSim.SimulateDay(day, offered => offered.Unlock1Id);
            var fromInt = intSim.SimulateDay(day, offered => offered.Unlock1Id);
            Assert.Equal(fromString.Unlock1Id, fromInt.Unlock1Id);
            Assert.Equal(fromString.Unlock2Id, fromInt.Unlock2Id);
        }
    }

    private static HashSet<string> RunSearch(GameDataRepository repo, SeedSearchRequest request) =>
        RunSearchWithOtFriendlyTags(repo, request).Keys.ToHashSet();

    private static Dictionary<string, bool> RunSearchWithOtFriendlyTags(GameDataRepository repo, SeedSearchRequest request)
    {
        var actualMatches = new Dictionary<string, bool>();
        var matchLock = new object();

        SeedSearcher.Search(
            repo,
            request,
            maxDegreeOfParallelism: -1,
            matchCap: long.MaxValue,
            onMatch: (seed, otFriendly) =>
            {
                lock (matchLock)
                {
                    actualMatches[seed] = otFriendly;
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
