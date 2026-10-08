using PlateUpSeedTools.Engine.Data;
using PlateUpSeedTools.Engine.Seeding;
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
    private const int PlatesId = 1313469794; // "Plates" - StapleWhenMissing=true AND SellOnlyIfPlatesNeeded=true
    private const int PiesId = -133939790; // "Pies" dish - IsMainThatDoesNotNeedPlates=false, MenuPhases=[Main]
    private const int FlowerPotsCardId = 1491215862; // "Flower Pots" card - ShopEffect.AddStaple grants Flower Pot
    private const int FlowerPotApplianceId = 221442949; // "Flower Pot" - the appliance that card grants
    private const int ExclusiveCardId = -1323758054; // "Exclusive" card - ThemeAddEffect.AddsTheme grants DecorationType.Exclusive (1)
    private const int CandelabraId = -13481890; // "Candelabra" - ThemeRequired=1 (Exclusive)

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
            SellOnlyIfPlatesNeeded = false,
            RequiresPhaseForShopIds = [],
            RequiresProcessForShopIds = [],
            RequiresIngredientForShopIds = [],
            ShopRequirementFilter = 0,
            ThemeRequired = 0,
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
            SellOnlyAsDuplicate = false, SellOnlyAsUnique = false, RequiresForShopIds = [],
            SellOnlyIfPlatesNeeded = false, RequiresPhaseForShopIds = [], RequiresProcessForShopIds = [],
            RequiresIngredientForShopIds = [], ShopRequirementFilter = 0, ThemeRequired = 0,
            Staple = ShopStapleType.NonStaple,
        };
        var stapleMatchingTag = new ShopOptionState
        {
            ApplianceId = 2, Tags = ShoppingTags.Office, SellAsUpgrade = false, StapleWhenMissing = false,
            SellOnlyAsDuplicate = false, SellOnlyAsUnique = false, RequiresForShopIds = [],
            SellOnlyIfPlatesNeeded = false, RequiresPhaseForShopIds = [], RequiresProcessForShopIds = [],
            RequiresIngredientForShopIds = [], ShopRequirementFilter = 0, ThemeRequired = 0,
            Staple = ShopStapleType.WhenMissing,
        };
        var nonStapleNoOverlap = new ShopOptionState
        {
            ApplianceId = 3, Tags = ShoppingTags.Cooking, SellAsUpgrade = false, StapleWhenMissing = false,
            SellOnlyAsDuplicate = false, SellOnlyAsUnique = false, RequiresForShopIds = [],
            SellOnlyIfPlatesNeeded = false, RequiresPhaseForShopIds = [], RequiresProcessForShopIds = [],
            RequiresIngredientForShopIds = [], ShopRequirementFilter = 0, ThemeRequired = 0,
            Staple = ShopStapleType.NonStaple,
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

    [Fact]
    public void BuildDailyOptions_Plates_RemovedWhenMenuHasNoPlateNeedingMainDish()
    {
        var repo = LoadRepo();
        var kitchen = new KitchenApplianceState();

        var options = ShopOptionEvaluator.BuildDailyOptions(repo, kitchen, menuDishIds: new HashSet<int>());
        var plates = options.Single(o => o.ApplianceId == PlatesId);

        // Step 2 still force-tags it WhenMissing (not currently placed) - step 5's menu check removes
        // it anyway, demonstrating WhenMissing does NOT exempt an option from FilterByRequiredCourse
        // (unlike FilterByRequiredAppliances/step 4, which does exempt WhenMissing).
        Assert.Equal(ShopStapleType.WhenMissing, plates.Staple);
        Assert.True(plates.IsRemoved);
    }

    [Fact]
    public void BuildDailyOptions_Plates_StaysEligibleWhenMenuHasPlateNeedingMainDish()
    {
        var repo = LoadRepo();
        var kitchen = new KitchenApplianceState();

        var options = ShopOptionEvaluator.BuildDailyOptions(repo, kitchen, menuDishIds: new HashSet<int> { PiesId });
        var plates = options.Single(o => o.ApplianceId == PlatesId);

        Assert.Equal(ShopStapleType.WhenMissing, plates.Staple);
        Assert.False(plates.IsRemoved);
    }

    [Fact]
    public void BuildDailyOptions_Plates_DefaultsToRemovedWhenNoMenuDishIdsSupplied()
    {
        // menuDishIds omitted entirely (not just empty) - same result as an explicit empty set, since
        // ShopDaySimulator/BuildDailyOptions callers that don't track Cards-walk state (e.g.
        // OtFriendlyChecker) shouldn't silently assume a plate-needing menu.
        var repo = LoadRepo();
        var kitchen = new KitchenApplianceState();

        var options = ShopOptionEvaluator.BuildDailyOptions(repo, kitchen);
        var plates = options.Single(o => o.ApplianceId == PlatesId);

        Assert.True(plates.IsRemoved);
    }

    [Fact]
    public void ResolveBonusStapleApplianceIds_OwnedFlowerPotsCard_ResolvesToFlowerPotAppliance()
    {
        var repo = LoadRepo();

        var result = ShopOptionEvaluator.ResolveBonusStapleApplianceIds(repo, new HashSet<int> { FlowerPotsCardId });

        Assert.Equal(new HashSet<int> { FlowerPotApplianceId }, result);
    }

    [Fact]
    public void BuildDailyOptions_FlowerPot_BecomesBonusStapleWhenGrantingCardOwned()
    {
        var repo = LoadRepo();
        var kitchen = new KitchenApplianceState();
        var bonusStaples = ShopOptionEvaluator.ResolveBonusStapleApplianceIds(repo, new HashSet<int> { FlowerPotsCardId });

        var options = ShopOptionEvaluator.BuildDailyOptions(repo, kitchen, bonusStapleApplianceIds: bonusStaples);
        var flowerPot = options.Single(o => o.ApplianceId == FlowerPotApplianceId);

        Assert.Equal(ShopStapleType.BonusStaple, flowerPot.Staple);
        Assert.False(flowerPot.IsRemoved);
    }

    [Fact]
    public void ResolveGrantedThemes_OwnedExclusiveCard_ResolvesToExclusiveFlag()
    {
        var repo = LoadRepo();

        var result = ShopOptionEvaluator.ResolveGrantedThemes(repo, new HashSet<int> { ExclusiveCardId });

        Assert.Equal(1, result); // DecorationType.Exclusive
    }

    [Fact]
    public void BuildDailyOptions_Candelabra_RemovedUntilExclusiveThemeGranted()
    {
        var repo = LoadRepo();
        var kitchen = new KitchenApplianceState();

        var withoutTheme = ShopOptionEvaluator.BuildDailyOptions(repo, kitchen);
        Assert.True(withoutTheme.Single(o => o.ApplianceId == CandelabraId).IsRemoved);

        var grantedThemes = ShopOptionEvaluator.ResolveGrantedThemes(repo, new HashSet<int> { ExclusiveCardId });
        var withTheme = ShopOptionEvaluator.BuildDailyOptions(repo, kitchen, grantedThemes: grantedThemes);
        Assert.False(withTheme.Single(o => o.ApplianceId == CandelabraId).IsRemoved);
    }

    [Fact]
    public void SimulateDayWithRerolls_ZeroRerolls_MatchesPlainSimulateDay()
    {
        var repo = LoadRepo();
        var seed = new Seed("vqrj");
        var (profile, _) = repo.ResolveLayout("normal", seed);

        var plain = new ShopDaySimulator(repo, seed, new KitchenApplianceState(profile.RequiredApplianceIds)).SimulateDay(2);
        var rerolled = new ShopDaySimulator(repo, seed, new KitchenApplianceState(profile.RequiredApplianceIds)).SimulateDayWithRerolls(2, rerollCount: 0);

        Assert.Single(rerolled);
        Assert.Equal(plain.Slots.Select(s => s.ApplianceId), rerolled[0].Slots.Select(s => s.ApplianceId));
    }

    [Fact]
    public void SimulateDayWithRerolls_PrefixOfRoundsIsStableRegardlessOfTotalRerollCount()
    {
        // A UI that doesn't know the final reroll count upfront (shows "next reroll" incrementally)
        // needs round N's result to be identical whether it was computed as part of a 1-reroll or a
        // 3-reroll request - this is the real continuing-stream property, not just "doesn't throw."
        var repo = LoadRepo();
        var seed = new Seed("vqrj");
        var (profile, _) = repo.ResolveLayout("normal", seed);

        var oneReroll = new ShopDaySimulator(repo, seed, new KitchenApplianceState(profile.RequiredApplianceIds)).SimulateDayWithRerolls(2, rerollCount: 1);
        var threeRerolls = new ShopDaySimulator(repo, seed, new KitchenApplianceState(profile.RequiredApplianceIds)).SimulateDayWithRerolls(2, rerollCount: 3);

        Assert.Equal(2, oneReroll.Count);
        Assert.Equal(4, threeRerolls.Count);
        for (var i = 0; i < oneReroll.Count; i++)
        {
            Assert.Equal(oneReroll[i].Slots.Select(s => s.ApplianceId), threeRerolls[i].Slots.Select(s => s.ApplianceId));
        }
    }

    [Fact]
    public void SimulateDayWithRerolls_NegativeRerollCount_Throws()
    {
        var repo = LoadRepo();
        var seed = new Seed("vqrj");
        var sim = new ShopDaySimulator(repo, seed, new KitchenApplianceState());

        Assert.Throws<ArgumentOutOfRangeException>(() => sim.SimulateDayWithRerolls(1, rerollCount: -1));
    }

    [Theory]
    [InlineData(ShopSpawnSetting.Outside, false, ShopSpawnLocation.NearFrontDoor)]
    [InlineData(ShopSpawnSetting.Outside, true, ShopSpawnLocation.NearFrontDoor)]
    [InlineData(ShopSpawnSetting.Inside, true, ShopSpawnLocation.NearPlayer)]
    [InlineData(ShopSpawnSetting.Inside, false, ShopSpawnLocation.RandomInsideTile)]
    public void ShopSpawnLocationEvaluator_MatchesRealGameplay_PlayerPositionMattersOnlyWhenInside(
        ShopSpawnSetting setting, bool someoneStandingInside, ShopSpawnLocation expected)
    {
        // Source-confirmed (GetPostTiles): Outside mode is pure front-door geometry, player position
        // never consulted. Inside mode genuinely branches on it - near-player candidates come first
        // in the shuffled list if someone's standing inside, else it falls through to a random inside
        // tile - see ShopSpawnLocation's doc comment for the full mechanic.
        Assert.Equal(expected, ShopSpawnLocationEvaluator.Evaluate(setting, someoneStandingInside));
    }
}
