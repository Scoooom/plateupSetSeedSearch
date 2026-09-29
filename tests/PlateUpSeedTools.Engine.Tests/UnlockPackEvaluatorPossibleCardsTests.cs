using PlateUpSeedTools.Engine.Cards;
using PlateUpSeedTools.Engine.Data;
using Xunit;

namespace PlateUpSeedTools.Engine.Tests;

/// <summary>
/// Exercises UnlockPackEvaluator.GetPossibleCardIds - the deterministic "what could possibly be
/// offered on day N" query added for the search UI's day-target dropdown filtering. Uses the same
/// Normal-setting/Pies-dish/Heat-0 setup as CardDrawSimulatorGroundTruthTests, and the exact
/// Requires/BlockedBy pair the user's own feature request used as an example: "Large Groups"
/// requires "Medium Groups", and "Medium Groups"/"Individual Dining" mutually block each other.
/// </summary>
public class UnlockPackEvaluatorPossibleCardsTests
{
    private const int PiesId = -133939790;
    private const int MediumGroupsId = -1183014556;
    private const int LargeGroupsId = -523195599;
    private const int IndividualDiningId = -1747821833;

    // "Advertising" (Customer/UnlockGroup 0, no requirements) and "Cheese Board" (Food/UnlockGroup
    // 1, no requirements) - real card IDs from data/settings/cards/unlocks.json, used to exercise
    // the Autumn Pack composite try-order fix below.
    private const int AdvertisingId = 1765310572;
    private const int CheeseBoardId = 312770813;

    // data/settings/cards/packs.json: Autumn Pack's subPackIds are [Franchise Cards Pack, Autumn
    // Dish Cards, Theme Cards Pack, Normal Cards Pack]. Autumn Dish Cards and Normal Cards Pack
    // share an IDENTICAL "regular, dayInterval 3, dayMin 1" condition object, so they're eligible
    // on exactly the same days (3, 6, 9, 12, 15) - Autumn Dish Cards is tried first and normally
    // pre-empts Normal Cards Pack on all of them.
    private const int AutumnPackId = 698670060;
    private const int AutumnIntervalDay = 3;

    [Fact]
    public void RequiresIds_CardOnlyPossibleOnceTheRequirementIsOwned()
    {
        var repo = new GameDataRepository(Path.Combine(AppContext.BaseDirectory, "data", "settings"));
        var settingPackId = repo.ResolveUnlockPackId("normal");
        var setup = RunSetup.Resolve(repo, PiesId, subOptionChoice: null, heatLevel: 0, startingUnlockId: null);
        List<int> activePackIds = [settingPackId, .. setup.BonusPackIds];

        var withoutRequirement = UnlockPackEvaluator.GetPossibleCardIds(repo, activePackIds, setup.InitiallyOwnedIds, day: 3, tier: 0);
        Assert.DoesNotContain(LargeGroupsId, withoutRequirement);

        var withRequirement = new HashSet<int>(setup.InitiallyOwnedIds) { MediumGroupsId };
        var withRequirementResult = UnlockPackEvaluator.GetPossibleCardIds(repo, activePackIds, withRequirement, day: 3, tier: 0);
        Assert.Contains(LargeGroupsId, withRequirementResult);
    }

    [Fact]
    public void BlockedByIds_CardNoLongerPossibleOnceTheBlockerIsOwned()
    {
        var repo = new GameDataRepository(Path.Combine(AppContext.BaseDirectory, "data", "settings"));
        var settingPackId = repo.ResolveUnlockPackId("normal");
        var setup = RunSetup.Resolve(repo, PiesId, subOptionChoice: null, heatLevel: 0, startingUnlockId: null);
        List<int> activePackIds = [settingPackId, .. setup.BonusPackIds];

        var withoutBlocker = UnlockPackEvaluator.GetPossibleCardIds(repo, activePackIds, setup.InitiallyOwnedIds, day: 3, tier: 0);
        Assert.Contains(IndividualDiningId, withoutBlocker);

        var withBlocker = new HashSet<int>(setup.InitiallyOwnedIds) { MediumGroupsId };
        var withBlockerResult = UnlockPackEvaluator.GetPossibleCardIds(repo, activePackIds, withBlocker, day: 3, tier: 0);
        Assert.DoesNotContain(IndividualDiningId, withBlockerResult);
    }

    [Fact]
    public void ExactIdDedup_OwnedCardIsNeverOfferedAgain()
    {
        var repo = new GameDataRepository(Path.Combine(AppContext.BaseDirectory, "data", "settings"));
        var settingPackId = repo.ResolveUnlockPackId("normal");
        var setup = RunSetup.Resolve(repo, PiesId, subOptionChoice: null, heatLevel: 0, startingUnlockId: null);
        List<int> activePackIds = [settingPackId, .. setup.BonusPackIds];

        var owned = new HashSet<int>(setup.InitiallyOwnedIds) { MediumGroupsId };
        var possible = UnlockPackEvaluator.GetPossibleCardIds(repo, activePackIds, owned, day: 3, tier: 0);
        Assert.DoesNotContain(MediumGroupsId, possible);
    }

    [Fact]
    public void HasBlockedByConflict_DetectsDirectConflict()
    {
        var repo = new GameDataRepository(Path.Combine(AppContext.BaseDirectory, "data", "settings"));

        // Reuses the same real, data-verified mutual-BlockedBy pair as the tests above -
        // "Medium Groups" and "Individual Dining" can never coexist.
        var targetIds = new HashSet<int> { MediumGroupsId, IndividualDiningId };
        var found = UnlockPackEvaluator.HasBlockedByConflict(repo, targetIds, out var conflict);

        Assert.True(found);
        Assert.Equal(targetIds, new HashSet<int> { conflict.CardId, conflict.BlockedById });
    }

    [Fact]
    public void HasBlockedByConflict_ReturnsFalseForIndependentCards()
    {
        var repo = new GameDataRepository(Path.Combine(AppContext.BaseDirectory, "data", "settings"));

        var targetIds = new HashSet<int> { MediumGroupsId, LargeGroupsId };
        var found = UnlockPackEvaluator.HasBlockedByConflict(repo, targetIds, out _);

        Assert.False(found);
    }

    // Regression coverage for the composite try-order exclusivity bug: on Autumn's interval days,
    // Autumn Dish Cards (Food-only, tried first) normally pre-empts Normal Cards Pack (Food-or-
    // Customer, tried later) entirely - so a Customer card like "Advertising" was wrongly listed
    // as reachable by the pre-fix code, which unioned every condition-eligible sub-pack instead of
    // stopping at the first that actually fires (matching GetCompositeOptions' real per-night
    // resolution). Food cards remain correctly reachable via Autumn Dish Cards itself.
    [Fact]
    public void Autumn_IntervalDay_ExcludesCustomerCards_ButIncludesFoodCards()
    {
        var repo = new GameDataRepository(Path.Combine(AppContext.BaseDirectory, "data", "settings"));
        var setup = RunSetup.Resolve(repo, PiesId, subOptionChoice: null, heatLevel: 0, startingUnlockId: null);
        List<int> activePackIds = [AutumnPackId, .. setup.BonusPackIds];

        var possible = UnlockPackEvaluator.GetPossibleCardIds(repo, activePackIds, setup.InitiallyOwnedIds, day: AutumnIntervalDay, tier: 0);

        Assert.DoesNotContain(AdvertisingId, possible);
        Assert.Contains(CheeseBoardId, possible);
    }

    // Answers a real user question: is a Customer card EVER reachable on an Autumn interval day,
    // or is the exclusion a blanket ban regardless of game state? It's state-aware, not blanket -
    // if Autumn Dish Cards' entire Food candidate pool is exhausted (e.g. a heavy franchise-
    // carryover run that already owns every Food unlock), the real composite resolver falls
    // through to Normal Cards Pack, and this fix must reflect that rather than hiding it.
    [Fact]
    public void Autumn_IntervalDay_IncludesCustomerCards_WhenFoodPoolIsExhausted()
    {
        var repo = new GameDataRepository(Path.Combine(AppContext.BaseDirectory, "data", "settings"));
        var setup = RunSetup.Resolve(repo, PiesId, subOptionChoice: null, heatLevel: 0, startingUnlockId: null);
        List<int> activePackIds = [AutumnPackId, .. setup.BonusPackIds];

        var ownedIds = new HashSet<int>(setup.InitiallyOwnedIds);
        foreach (var unlock in repo.AllUnlocksOrderedById)
        {
            if (unlock.UnlockGroup == 1) // Food
            {
                ownedIds.Add(unlock.Id);
            }
        }

        var possible = UnlockPackEvaluator.GetPossibleCardIds(repo, activePackIds, ownedIds, day: AutumnIntervalDay, tier: 0);

        Assert.Contains(AdvertisingId, possible);
    }

    // Sanity check that the fix doesn't overreach: the default "normal" schedule has only one
    // Food-or-Customer sub-pack (no separate food-only sub-pack ahead of it to pre-empt it), so
    // Customer cards are unaffected by the composite try-order fix there.
    [Fact]
    public void NormalSchedule_UnaffectedByAutumnFix_StillIncludesCustomerCards()
    {
        var repo = new GameDataRepository(Path.Combine(AppContext.BaseDirectory, "data", "settings"));
        var settingPackId = repo.ResolveUnlockPackId("normal");
        var setup = RunSetup.Resolve(repo, PiesId, subOptionChoice: null, heatLevel: 0, startingUnlockId: null);
        List<int> activePackIds = [settingPackId, .. setup.BonusPackIds];

        var possible = UnlockPackEvaluator.GetPossibleCardIds(repo, activePackIds, setup.InitiallyOwnedIds, day: AutumnIntervalDay, tier: 0);

        Assert.Contains(AdvertisingId, possible);
    }

    // GetPossibleDishTypes reachability - powers the class-target search UI's dropdown filtering
    // (e.g. "Any Base" shouldn't be selectable for a schedule that can never reach DishType 0).
    // Empirically verified (not assumed): on the normal schedule's interval days (3/6/9/12), the
    // reachable DishType set is exactly {Main=1, Side=3, Starter=4, Dessert=5} - Base=0 dishes (the
    // generic placeholder dishes like "Pies"/"Salad" themselves, as opposed to their named
    // Main-tier variants like "Vegetable Pies") are never offered as day-to-day card draws under
    // the normal schedule. Autumn's own interval days additionally reach Base=0 via its
    // cross-family "Autumn Dish Cards" sub-pack - this is the real differentiator the user's "Any
    // Main" example was gesturing at (Main IS reachable under normal too, via your own starting
    // dish's variants - Base is the one DishType normal genuinely never reaches).
    [Fact]
    public void GetPossibleDishTypes_NormalSchedule_NeverReachesBaseDishType()
    {
        var repo = new GameDataRepository(Path.Combine(AppContext.BaseDirectory, "data", "settings"));
        var settingPackId = repo.ResolveUnlockPackId("normal");
        var setup = RunSetup.Resolve(repo, PiesId, subOptionChoice: null, heatLevel: 0, startingUnlockId: null);
        List<int> activePackIds = [settingPackId, .. setup.BonusPackIds];

        for (var day = 0; day <= 15; day++)
        {
            var dishTypes = UnlockPackEvaluator.GetPossibleDishTypes(repo, activePackIds, setup.InitiallyOwnedIds, day, tier: 0);
            Assert.DoesNotContain(0, dishTypes);
        }
    }

    [Fact]
    public void GetPossibleDishTypes_AutumnIntervalDay_ReachesBaseDishType()
    {
        var repo = new GameDataRepository(Path.Combine(AppContext.BaseDirectory, "data", "settings"));
        var setup = RunSetup.Resolve(repo, PiesId, subOptionChoice: null, heatLevel: 0, startingUnlockId: null);
        List<int> activePackIds = [AutumnPackId, .. setup.BonusPackIds];

        var dishTypes = UnlockPackEvaluator.GetPossibleDishTypes(repo, activePackIds, setup.InitiallyOwnedIds, AutumnIntervalDay, tier: 0);

        Assert.Contains(0, dishTypes);
    }

    // DishTypeCatalog.NullValue (6) is defensively excluded even though no real Dish record
    // currently has it (see DishTypeCatalog's doc comment) - guard against a future data change
    // silently leaking it into the class-target UI unnoticed.
    [Fact]
    public void GetPossibleDishTypes_NeverReturnsNullValue_EvenIfSyntheticallyPresent()
    {
        var repo = new GameDataRepository(Path.Combine(AppContext.BaseDirectory, "data", "settings"));
        var settingPackId = repo.ResolveUnlockPackId("normal");
        var setup = RunSetup.Resolve(repo, PiesId, subOptionChoice: null, heatLevel: 0, startingUnlockId: null);
        List<int> activePackIds = [settingPackId, .. setup.BonusPackIds];

        for (var day = 0; day <= 15; day++)
        {
            var dishTypes = UnlockPackEvaluator.GetPossibleDishTypes(repo, activePackIds, setup.InitiallyOwnedIds, day, tier: 0);
            Assert.DoesNotContain(DishTypeCatalog.NullValue, dishTypes);
        }
    }
}
