using PlateUpSeedTools.Engine.Data;
using PlateUpSeedTools.Engine.Shop;
using Xunit;

namespace PlateUpSeedTools.Engine.Tests;

/// <summary>
/// Formula/pure-function coverage for the shop simulator foundation (Engine/Shop/) that doesn't need
/// a live ground-truth capture - ShopDifficultyHelpers' formulas, ShopOptionState.MatchesRequestTags'
/// truth table, KitchenApplianceState's place/toss bookkeeping, and ShopOptionEvaluator's staple/
/// duplicate-cap pipeline against the real extracted Research Desk / Enchanting Desk data (the two
/// BlueprintUpgrader-tagged appliances in the current dataset). See ShopDaySimulatorGroundTruthTests
/// for the RNG-dependent, gameplay-verified coverage.
/// </summary>
public class ShopOptionEvaluatorTests
{
    // Real IDs from data/settings/shop/appliances.json.
    private const int ResearchDeskId = 1139247360; // "Research Desk" (Blueprint Upgrade Desk) - StapleWhenMissing=true
    private const int EnchantingDeskId = -292467039; // "Enchanting Desk" - also BlueprintUpgrader-tagged, StapleWhenMissing=false

    private static GameDataRepository LoadRepo() => new(Path.Combine(AppContext.BaseDirectory, "data", "settings"));

    [Theory]
    [InlineData(0, 4)]
    [InlineData(4, 4)]
    [InlineData(5, 3)]
    [InlineData(9, 3)]
    [InlineData(10, 2)]
    [InlineData(20, 2)] // clamped floor - never drops below 2
    public void StapleCountFollowsFiveDayCurve(int day, int expected)
    {
        Assert.Equal(expected, ShopDifficultyHelpers.StapleCount(day));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(100)]
    public void TotalShopCountIsAlwaysFive(int day)
    {
        Assert.Equal(5, ShopDifficultyHelpers.TotalShopCount(day));
    }

    [Theory]
    [InlineData(0, 0f)]
    [InlineData(4, 0f)]
    [InlineData(5, 0.1f)]
    [InlineData(10, 0.2f)]
    public void BaseUpgradedShopChanceFollowsFiveDayCurve(int day, float expected)
    {
        Assert.Equal(expected, ShopDifficultyHelpers.BaseUpgradedShopChance(day), precision: 5);
    }

    [Fact]
    public void GetSlotRequests_Day5Branch_IsEightDecorationTwoSpecialEvent()
    {
        var requests = ShopDifficultyHelpers.GetSlotRequests(day: 5, availableStapleTaggedOptionCount: 99);

        Assert.Equal(10, requests.Count);
        Assert.Equal(8, requests.Count(t => t == ShoppingTags.Decoration));
        Assert.Equal(2, requests.Count(t => t == ShoppingTags.SpecialEvent));
    }

    [Fact]
    public void GetSlotRequests_NormalDay_ClampsStapleSlotsToAvailableOptions()
    {
        // Day 1: StapleCount(1) = 4, but only 1 staple-tagged option currently exists - the request
        // must clamp down to what's actually available, not request more staple slots than exist.
        var requests = ShopDifficultyHelpers.GetSlotRequests(day: 1, availableStapleTaggedOptionCount: 1);

        Assert.Equal(5, requests.Count);
        Assert.Equal(1, requests.Count(t => t == ShoppingTags.Basic));
        Assert.Equal(4, requests.Count(t => t == ShoppingTagsExtensions.DefaultShoppingTag));
    }

    [Fact]
    public void GetSlotRequests_NormalDay_UnclampedUsesFullStapleCount()
    {
        var requests = ShopDifficultyHelpers.GetSlotRequests(day: 1, availableStapleTaggedOptionCount: 99);

        Assert.Equal(5, requests.Count);
        Assert.Equal(4, requests.Count(t => t == ShoppingTags.Basic));
        Assert.Equal(1, requests.Count(t => t == ShoppingTagsExtensions.DefaultShoppingTag));
    }

    [Theory]
    [InlineData(ShopStapleType.NonStaple, false)]
    [InlineData(ShopStapleType.FixedStaple, true)]
    [InlineData(ShopStapleType.BonusStaple, true)]
    [InlineData(ShopStapleType.WhenMissing, true)]
    public void MatchesRequestTags_BasicRequest_OnlyMatchesStaples(ShopStapleType staple, bool expectedMatch)
    {
        var option = new ShopOptionState
        {
            ApplianceId = 1,
            Tags = ShoppingTags.Office,
            SellAsUpgrade = false,
            StapleWhenMissing = false,
            SellOnlyAsDuplicate = false,
            SellOnlyAsUnique = false,
            RequiresForShopIds = [],
            Staple = staple,
        };

        Assert.Equal(expectedMatch, option.MatchesRequestTags(ShoppingTags.Basic));
    }

    [Fact]
    public void MatchesRequestTags_NonBasicRequest_OnlyMatchesNonStaplesWithOverlappingTags()
    {
        var nonStapleMatchingTag = new ShopOptionState
        {
            ApplianceId = 1, Tags = ShoppingTags.Office, SellAsUpgrade = false, StapleWhenMissing = false,
            SellOnlyAsDuplicate = false, SellOnlyAsUnique = false, RequiresForShopIds = [], Staple = ShopStapleType.NonStaple,
        };
        var stapleMatchingTag = new ShopOptionState
        {
            ApplianceId = 2, Tags = ShoppingTags.Office, SellAsUpgrade = false, StapleWhenMissing = false,
            SellOnlyAsDuplicate = false, SellOnlyAsUnique = false, RequiresForShopIds = [], Staple = ShopStapleType.WhenMissing,
        };
        var nonStapleNoOverlap = new ShopOptionState
        {
            ApplianceId = 3, Tags = ShoppingTags.Cooking, SellAsUpgrade = false, StapleWhenMissing = false,
            SellOnlyAsDuplicate = false, SellOnlyAsUnique = false, RequiresForShopIds = [], Staple = ShopStapleType.NonStaple,
        };

        Assert.True(nonStapleMatchingTag.MatchesRequestTags(ShoppingTags.Office));
        Assert.False(stapleMatchingTag.MatchesRequestTags(ShoppingTags.Office)); // already staple - shouldn't double-fill a random slot
        Assert.False(nonStapleNoOverlap.MatchesRequestTags(ShoppingTags.Office));
    }

    [Fact]
    public void KitchenApplianceState_PlaceThenToss_RoundTripsIsPlaced()
    {
        var kitchen = new KitchenApplianceState();
        Assert.False(kitchen.IsPlaced(ResearchDeskId));

        kitchen.PlaceAppliance(ResearchDeskId);
        Assert.True(kitchen.IsPlaced(ResearchDeskId));
        Assert.Contains(ResearchDeskId, kitchen.PlacedApplianceIds);

        kitchen.TossAppliance(ResearchDeskId);
        Assert.False(kitchen.IsPlaced(ResearchDeskId));
        Assert.DoesNotContain(ResearchDeskId, kitchen.PlacedApplianceIds);
    }

    [Fact]
    public void KitchenApplianceState_TossWithoutPlacing_Throws()
    {
        var kitchen = new KitchenApplianceState();
        Assert.Throws<InvalidOperationException>(() => kitchen.TossAppliance(ResearchDeskId));
    }

    [Fact]
    public void KitchenApplianceState_InitiallyPlacedIds_AreRespected()
    {
        var kitchen = new KitchenApplianceState([ResearchDeskId]);
        Assert.True(kitchen.IsPlaced(ResearchDeskId));
    }

    [Fact]
    public void BuildDailyOptions_ResearchDesk_IsWhenMissingStapleWhenNotPlaced()
    {
        var repo = LoadRepo();
        var kitchen = new KitchenApplianceState();

        var options = ShopOptionEvaluator.BuildDailyOptions(repo, kitchen);
        var researchDesk = options.Single(o => o.ApplianceId == ResearchDeskId);

        Assert.Equal(ShopStapleType.WhenMissing, researchDesk.Staple);
        Assert.False(researchDesk.IsRemoved);
    }

    [Fact]
    public void BuildDailyOptions_ResearchDesk_StopsBeingStapleOncePlaced()
    {
        var repo = LoadRepo();
        var kitchen = new KitchenApplianceState([ResearchDeskId]);

        var options = ShopOptionEvaluator.BuildDailyOptions(repo, kitchen);
        var researchDesk = options.Single(o => o.ApplianceId == ResearchDeskId);

        Assert.Equal(ShopStapleType.NonStaple, researchDesk.Staple);
    }

    [Fact]
    public void BuildDailyOptions_ResearchDesk_RemovedWhenAnotherBlueprintUpgraderIsPlaced()
    {
        // FilterBlueprintUpgraders isn't staple-gated - Research Desk should be filtered out even
        // while it would otherwise be a WhenMissing staple, once a DIFFERENT BlueprintUpgrader-tagged
        // appliance (Enchanting Desk) is already placed.
        var repo = LoadRepo();
        var kitchen = new KitchenApplianceState([EnchantingDeskId]);

        var options = ShopOptionEvaluator.BuildDailyOptions(repo, kitchen);
        var researchDesk = options.Single(o => o.ApplianceId == ResearchDeskId);

        Assert.True(researchDesk.IsRemoved);
    }

    [Fact]
    public void BuildDailyOptions_ResearchDesk_ReappearsAsWhenMissingAfterTossingBothUpgraders()
    {
        // The toss-then-restaple mechanic the user's real strategy depends on - source-verified here,
        // but see ShopDaySimulatorGroundTruthTests for the ground-truth-verified version once captured.
        var repo = LoadRepo();
        var kitchen = new KitchenApplianceState([ResearchDeskId]);
        kitchen.TossAppliance(ResearchDeskId);

        var options = ShopOptionEvaluator.BuildDailyOptions(repo, kitchen);
        var researchDesk = options.Single(o => o.ApplianceId == ResearchDeskId);

        Assert.Equal(ShopStapleType.WhenMissing, researchDesk.Staple);
        Assert.False(researchDesk.IsRemoved);
    }
}
