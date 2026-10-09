using PlateUpSeedTools.Engine.Data;
using PlateUpSeedTools.Engine.Seeding;
using PlateUpSeedTools.Engine.Shop;
using Xunit;

namespace PlateUpSeedTools.Engine.Tests;

/// <summary>
/// Covers the two real bugs found in a user report against the shipped Blueprints view (both source-
/// confirmed against the decompiled game, not assumed):
/// 1. A reroll can NEVER produce a staple-flagged appliance, period - HandleShopReroll.cs requests
///    every surviving slot with one shared non-Basic tag (DefaultShoppingTag/Decoration), and
///    ShopOptionState.MatchesRequestTags only matches staples against a bare Basic request. The
///    engine previously (incorrectly) re-requested every round, including rerolls, with the day's
///    ORIGINAL per-slot template - letting staples reappear in every reroll.
/// 2. Buying a shop letter permanently removes that SLOT from the day, with no backfill
///    (PurchaseAfterDuration.cs removes CApplianceBlueprint from the same entity rather than
///    destroying it; HandleShopReroll's Blueprints query simply never matches it again).
/// See ShopDaySimulator.cs's doc comments for the full source trace.
/// </summary>
public class ShopDayRoundStateTests
{
    private const int ResearchDeskId = 1139247360; // "Research Desk" - StapleWhenMissing=true

    private static GameDataRepository LoadRepo() => new(Path.Combine(AppContext.BaseDirectory, "data", "settings"));

    [Fact]
    public void SimulateNextRound_RerollRoundsNeverProduceAStapleTaggedAppliance()
    {
        var repo = LoadRepo();
        var seed = new Seed("vqrj");
        var (profile, _) = repo.ResolveLayout("normal", seed);
        var kitchen = new KitchenApplianceState(profile.RequiredApplianceIds);
        var sim = new ShopDaySimulator(repo, seed, kitchen);

        var state = sim.BeginDayRounds(1);
        var initial = state.SimulateNextRound();
        var reroll1 = state.SimulateNextRound();
        var reroll2 = state.SimulateNextRound();

        // The initial offer (round 0) uses the real per-slot template and DOES include staples -
        // confirms this test's setup is meaningful, not vacuously true because nothing is ever staple.
        var options = ShopOptionEvaluator.BuildDailyOptions(repo, kitchen).ToDictionary(o => o.ApplianceId);
        Assert.Contains(initial.Slots, s => s.ApplianceId is int id && options[id].Staple != ShopStapleType.NonStaple);

        foreach (var round in new[] { reroll1, reroll2 })
        {
            foreach (var slot in round.Slots)
            {
                if (slot.ApplianceId is int pickedId)
                {
                    Assert.Equal(ShopStapleType.NonStaple, options[pickedId].Staple);
                }
            }
        }
    }

    [Fact]
    public void RemoveSlot_PermanentlyDropsThatIndexFromLaterRoundsWithoutRenumbering()
    {
        var repo = LoadRepo();
        var seed = new Seed("vqrj");
        var (profile, _) = repo.ResolveLayout("normal", seed);
        var kitchen = new KitchenApplianceState(profile.RequiredApplianceIds);
        var sim = new ShopDaySimulator(repo, seed, kitchen);

        var state = sim.BeginDayRounds(1);
        var initial = state.SimulateNextRound();
        Assert.Equal(5, initial.Slots.Count);
        Assert.Equal(new[] { 0, 1, 2, 3, 4 }, initial.Slots.Select(s => s.SlotIndex));

        state.RemoveSlot(0);
        var reroll1 = state.SimulateNextRound();

        Assert.Equal(4, reroll1.Slots.Count);
        Assert.Equal(new[] { 1, 2, 3, 4 }, reroll1.Slots.Select(s => s.SlotIndex));

        state.RemoveSlot(2);
        var reroll2 = state.SimulateNextRound();

        Assert.Equal(3, reroll2.Slots.Count);
        Assert.Equal(new[] { 1, 3, 4 }, reroll2.Slots.Select(s => s.SlotIndex));
    }

    [Fact]
    public void BuyingResearchDeskMidDay_PermanentlyRemovesItsSlotFromLaterRerolls()
    {
        var repo = LoadRepo();
        var seed = new Seed("vqrj");
        var (profile, _) = repo.ResolveLayout("normal", seed);
        var kitchen = new KitchenApplianceState(profile.RequiredApplianceIds);
        var sim = new ShopDaySimulator(repo, seed, kitchen);

        var state = sim.BeginDayRounds(1);
        var initial = state.SimulateNextRound();
        var researchDeskSlot = initial.Slots.Single(s => s.ApplianceId == ResearchDeskId);

        kitchen.PlaceAppliance(ResearchDeskId);
        state.RemoveSlot(researchDeskSlot.SlotIndex);

        var reroll1 = state.SimulateNextRound();
        Assert.DoesNotContain(reroll1.Slots, s => s.SlotIndex == researchDeskSlot.SlotIndex);
        Assert.DoesNotContain(reroll1.Slots, s => s.ApplianceId == ResearchDeskId);
    }

    [Fact]
    public void SimulateDayWithRerolls_StillNeverShrinksSlotCount_WhenNothingIsBought()
    {
        // Regression lock: the old all-in-one API (no RemoveSlot calls possible) must still behave
        // exactly as before this type existed - every round has the full, unshrunk slot set.
        var repo = LoadRepo();
        var seed = new Seed("vqrj");
        var (profile, _) = repo.ResolveLayout("normal", seed);
        var kitchen = new KitchenApplianceState(profile.RequiredApplianceIds);
        var sim = new ShopDaySimulator(repo, seed, kitchen);

        var rounds = sim.SimulateDayWithRerolls(1, rerollCount: 3);

        Assert.All(rounds, r => Assert.Equal(5, r.Slots.Count));
        Assert.All(rounds, r => Assert.Equal(new[] { 0, 1, 2, 3, 4 }, r.Slots.Select(s => s.SlotIndex)));
    }
}
