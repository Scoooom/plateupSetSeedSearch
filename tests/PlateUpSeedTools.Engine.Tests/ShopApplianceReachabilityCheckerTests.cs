using PlateUpSeedTools.Engine.Data;
using PlateUpSeedTools.Engine.Seeding;
using PlateUpSeedTools.Engine.Shop;
using Xunit;

namespace PlateUpSeedTools.Engine.Tests;

/// <summary>
/// Backs the Search page's achievement presets (see AchievementPreset) - these aren't ground-truth
/// captures (unlike ShopDaySimulatorGroundTruthTests), just regression pins against this engine's
/// own ShopDaySimulator output for a few sample seeds, confirming AllReachableByDay actually finds
/// both a reachable and an unreachable case rather than being vacuously always-false (a real risk
/// for an "AND over N independent per-day checks" method - a typo in an appliance id, or an
/// off-by-one in the day range, would silently make every seed look unreachable).
/// </summary>
public class ShopApplianceReachabilityCheckerTests
{
    private static readonly GameDataRepository Repo = new(Path.Combine(AppContext.BaseDirectory, "data", "settings"));

    [Theory]
    [InlineData("ofdz", "safety-last", true)]
    [InlineData("6i74", "charcoal-factory", true)]
    [InlineData("vqrj", "safety-last", false)]
    [InlineData("vqrj", "charcoal-factory", false)]
    public void MatchesExpectedReachability(string seedString, string presetSlug, bool expected)
    {
        var seed = new Seed(seedString);
        var preset = AchievementPreset.TryResolve(presetSlug)!;

        var actual = ShopApplianceReachabilityChecker.AllReachableByDay(Repo, seed, "normal", preset.ApplianceIds, 15);

        Assert.Equal(expected, actual);
    }

    [Fact]
    public void NotReachableWithinZeroDays()
    {
        // Day range floor is 1 (day 0's shop isn't ground-truth-verified) - a maxDay below that
        // should never find anything regardless of seed.
        var seed = new Seed("ofdz");
        var preset = AchievementPreset.TryResolve("safety-last")!;

        Assert.False(ShopApplianceReachabilityChecker.AllReachableByDay(Repo, seed, "normal", preset.ApplianceIds, 0));
    }
}
