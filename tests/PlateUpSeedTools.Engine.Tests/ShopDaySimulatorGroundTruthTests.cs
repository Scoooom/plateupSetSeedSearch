using PlateUpSeedTools.Engine.Data;
using PlateUpSeedTools.Engine.Seeding;
using PlateUpSeedTools.Engine.Shop;
using Xunit;

namespace PlateUpSeedTools.Engine.Tests;

/// <summary>
/// Pins Engine.Shop.ShopDaySimulator against real values captured live from the running game via
/// PlateUpSourceFiles/GroundTruthLogger v1.14.0 (ShopBlueprintLetterGroundTruthPatch/
/// CreateShopRequestsGroundTruthPatch). Setup: "Alpine" restaurant setting (resolves to the synthetic
/// `normal` setting/shared-layout pool), seed "vqrj". Player-confirmed actions: decline Research Desk
/// day 1, buy + place it day 2, toss it day 3 (no shop offer-check needed that day - RNG is a fresh
/// per-day FixedSeedContext, not a continuing stream, so skipping day 3 doesn't perturb day 4).
///
/// This capture caught a real bug: every simulation before this test assumed a completely empty
/// starting kitchen. The real game places every appliance in the resolved layout's
/// LayoutProfile.RequiredAppliances (LayoutProfileInfo.RequiredApplianceIds) as real, occupying
/// appliances BEFORE day 1's shop ever builds (CreateNewKitchen.cs, unconditional, independent of
/// dish/heat/player choice) - one of those, Blueprint Cabinet (-571205127), is itself
/// StapleWhenMissing=true, so an empty-kitchen assumption wrongly made it eligible every day. Fixed
/// by seeding KitchenApplianceState from GameDataRepository.ResolveLayout(...).Profile.
/// RequiredApplianceIds instead of constructing it empty - see OtFriendlyChecker for the same pattern.
///
/// Days 2 and 4 match the real capture exactly, all 5 slots. Day 1's 5th slot (the single non-staple
/// "DefaultShoppingTag" random pick) was a real, now-RESOLVED discrepancy (2026-10-09): real capture
/// showed "Wellies" (1796077718), the simulator predicted "Serving Boards" (235423916). Both looked
/// byte-identical across every extracted field - the actual root cause was in the DATA EXTRACTOR, not
/// this class: `AssetYamlReader.Reference`/`ReferenceList` (tools/PlateUpSeedTools.DataExtractor)
/// treated any Unity object reference lacking a `guid:` key as unset, but Unity omits `guid`/`type`
/// entirely for a LOCAL reference (one pointing at another object inside the SAME .asset file, by
/// local fileID) - "Serving Board Stack.asset"'s own `RequiresForShop` field self-references its own
/// root object this way (`{fileID: 11400000}`, no guid), which silently extracted as `[]` instead of
/// `[235423916]`. The real game's `FilterByRequiredAppliances.HasRequirements` (ShopOptionEvaluator.cs)
/// was already correctly ported and correctly excludes Serving Boards once this is fixed - nothing in
/// the engine itself needed changing, only the extractor (now resolves a guid-less local reference to
/// the asset's own guid) and the regenerated `data/settings/shop/appliances.json` /
/// `data/settings/cards/unlocks.json` (13 appliances' `requiresForShopIds` and a 4-card mutually-
/// exclusive tier group's `blockedByIds` were affected by the same bug - see
/// `AssetYamlReader.Reference`'s doc comment for the full list/writeup). Confirmed via a real
/// ground-truth capture of the exact post-shuffle candidate order for this slot (GroundTruthLogger
/// v1.10.0's `SHOP_SHUFFLED`) that the port's own shuffle/RNG stream was ALREADY correct before this
/// fix - walking the real post-shuffle order with the port's (then-wrong) filter state reproduced
/// "Serving Boards" exactly, proving the bug was purely in filter data, not RNG.
/// </summary>
public class ShopDaySimulatorGroundTruthTests
{
    private const int ResearchDeskId = 1139247360;
    private const int HobId = 862493270;
    private const int SinkId = 1083874952;
    private const int CounterId = -1248669347;
    private const int PlatesId = 1313469794;
    private const int DiningTableId = 209074140;
    private const int PrepStationId = 1656358740;
    private const int FlowerPotId = 221442949;
    private const int WelliesId = 1796077718;

    // Pies - this capture predates the SellOnlyIfPlatesNeeded gate (Conditional Staples work) and
    // never recorded the real run's actual starting dish, so the exact dish is unknown. Pies stands
    // in deliberately: IsMainThatDoesNotNeedPlates=false like virtually every non-hand-held Main dish,
    // and the gate is a plain boolean (plates needed: yes/no) - any ordinary plate-needing Main dish
    // produces the identical MenuRequiresPlates=true result the real capture's Plates-present slots
    // require, so which specific dish is used here doesn't affect correctness of this assertion.
    private const int PiesId = -133939790;

    [Fact]
    public void MatchesRealCapturedShopSequence()
    {
        var repo = new GameDataRepository(Path.Combine(AppContext.BaseDirectory, "data", "settings"));
        var seed = new Seed("vqrj");
        var (profile, _) = repo.ResolveLayout("normal", seed);
        var kitchen = new KitchenApplianceState(profile.RequiredApplianceIds);
        var sim = new ShopDaySimulator(repo, seed, kitchen, menuDishIds: new HashSet<int> { PiesId });

        var day1 = sim.SimulateDay(1);
        Assert.Equal(
            new int?[] { ResearchDeskId, HobId, SinkId, CounterId, WelliesId },
            day1.Slots.Select(s => s.ApplianceId).ToArray());

        var day2 = sim.SimulateDay(2);
        Assert.Equal(
            new int?[] { PlatesId, DiningTableId, CounterId, ResearchDeskId, PrepStationId },
            day2.Slots.Select(s => s.ApplianceId).ToArray());

        // Day 2: buy + place Research Desk. Day 3: toss it (no offer-check needed that day).
        kitchen.PlaceAppliance(ResearchDeskId);
        kitchen.TossAppliance(ResearchDeskId);

        var day4 = sim.SimulateDay(4);
        Assert.Equal(
            new int?[] { SinkId, ResearchDeskId, PlatesId, HobId, FlowerPotId },
            day4.Slots.Select(s => s.ApplianceId).ToArray());
    }

    private const int CombinerId = -1906799936;

    /// <summary>
    /// Pins a SEPARATE real capture (2026-10-09, same seed "vqrj"/"normal" setting, GroundTruthLogger
    /// v1.9.0-v1.13.0's Jasmine-injection toggle + SHOP_STAPLE_COUNT/SHOP_OPTION_RAW_STATE captures):
    /// day 0 (the prep day before day 1, only reachable at all with Jasmine present - see
    /// JasmineInjectionSystem in the GroundTruthLogger repo). Real root cause, found and fixed same
    /// day: step 6 (FilterByRequiredProcessOrIngredient) was entirely unwired (see
    /// ShopOptionEvaluator's class doc) - the real game's RequiredProcesses/BeneficialIngredients sets
    /// are rebuilt once per day from live CMenuItem entities, which provably don't exist yet on day 0
    /// (5 independent real captures, 100% reproducible: Hob and Sink - both with real, non-empty
    /// RequiresProcessForShopIds/RequiresIngredientForShopIds - were NEVER staple-eligible on day 0,
    /// while Counter/Dining Table, which have no such requirement, always were). Confirmed via
    /// SHOP_OPTION_RAW_STATE that Hob/Sink's Tags/Staple fields are byte-identical to Counter/Dining
    /// Table's (FixedStaple, correctly tagged) - IsRemoved is the only field that differs, and only on
    /// day 0. Fixed by modeling the one case this project can be fully confident about without the
    /// still-unsolved Dish.RequiredProcesses/BeneficialIngredients Odin-HashSet decode: before any
    /// dish is genuinely on the menu, nothing can satisfy a process/ingredient requirement, so any
    /// appliance with one is unconditionally removed (<see cref="ShopOptionEvaluator.BuildDailyOptions"/>'s
    /// new anyMenuItemActive parameter, threaded from ShopDaySimulator as `day > 0`). The general
    /// "is THIS SPECIFIC requirement satisfied once a dish IS active" case (days 1+) remains
    /// unmodeled/always-pass, same as before - this fix only ever removes an option, never adds one
    /// back, so it cannot regress any already-passing day 1/2/4 assertion above.
    ///
    /// menuDishIds is deliberately EMPTY here, matching the real production caller
    /// (ExplorerEndpoints.SimulateShopDay's `for (day = 0; day &lt; request.Day; day++)` loop never
    /// executes for request.Day==0, so CardDrawSimulator.OwnedUnlockIds is never advanced past its
    /// initial state) - NOT the PiesId placeholder the day-1/2/4 test above uses, which predates this
    /// capture and was never meant to represent day 0's actual (dish-less) state.
    /// </summary>
    [Fact]
    public void MatchesRealCapturedDay0ShopSequence()
    {
        var repo = new GameDataRepository(Path.Combine(AppContext.BaseDirectory, "data", "settings"));
        var seed = new Seed("vqrj");
        var (profile, _) = repo.ResolveLayout("normal", seed);
        var kitchen = new KitchenApplianceState(profile.RequiredApplianceIds);
        var sim = new ShopDaySimulator(repo, seed, kitchen, menuDishIds: new HashSet<int>());

        var day0 = sim.SimulateDay(0);
        Assert.Equal(
            new int?[] { ResearchDeskId, DiningTableId, CounterId, CombinerId, FlowerPotId },
            day0.Slots.Select(s => s.ApplianceId).ToArray());
    }
}
