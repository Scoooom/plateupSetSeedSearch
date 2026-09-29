using PlateUpSeedTools.Engine.Cards;
using PlateUpSeedTools.Engine.Data;
using PlateUpSeedTools.Engine.Seeding;
using PlateUpSeedTools.Engine.Shop;
using Xunit;

namespace PlateUpSeedTools.Engine.Tests;

public class OtFriendlyCheckerTests
{
    private static GameDataRepository LoadRepo() => new(Path.Combine(AppContext.BaseDirectory, "data", "settings"));

    [Fact]
    public void SeedVqrj_UnderNormalSetting_IsOtFriendly()
    {
        // The ground-truth-confirmed positive case - see ShopDaySimulatorGroundTruthTests.
        var repo = LoadRepo();
        Assert.True(OtFriendlyChecker.Matches(repo, new Seed("vqrj"), "normal"));
    }

    [Fact]
    public void SeedGggg_UnderNormalSetting_IsNotOtFriendly()
    {
        // Research Desk is offered day 1 but NOT day 2 for this seed (checked directly against
        // ShopDaySimulator) - a genuine negative case, not just "some seed that happens to fail".
        var repo = LoadRepo();
        Assert.False(OtFriendlyChecker.Matches(repo, new Seed("gggg"), "normal"));
    }

    [Fact]
    public void Search_OtFriendlyWithoutSettingSlug_Throws()
    {
        var repo = LoadRepo();
        var setup = RunSetup.Resolve(repo, -133939790, subOptionChoice: null, heatLevel: 0, startingUnlockId: null);
        var request = new SeedSearchRequest(
            repo.ResolveUnlockPackId("normal"), setup, new Dictionary<int, TargetMatcher>(), Tier: 0,
            Alphabet: "vqrj", Length: 1, TotalToCheck: 4,
            OtFriendly: true, SettingSlug: null);

        Assert.Throws<ArgumentException>(() => SeedSearcher.Search(
            repo, request, maxDegreeOfParallelism: 1, matchCap: 1,
            onMatch: (_, _) => { }, onProgress: _ => { }, cancellationToken: CancellationToken.None));
    }
}
