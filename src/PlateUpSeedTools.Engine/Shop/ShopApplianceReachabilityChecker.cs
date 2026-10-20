using PlateUpSeedTools.Engine.Data;
using PlateUpSeedTools.Engine.Seeding;

namespace PlateUpSeedTools.Engine.Shop;

/// <summary>
/// Generic "does the shop ever offer each of these appliances by day N" reachability check -
/// backs the Search page's achievement presets (e.g. Safety Last needs Sharp Knife + Trainers
/// obtainable; Charcoal Factory needs Danger Hob + Gas Override). Deliberately independent of any
/// buy/toss sequence (unlike <see cref="OtFriendlyChecker"/>, which encodes one specific
/// multi-day strategy): an achievement preset only needs each enabling appliance to be
/// *obtainable* at some point in the run, since appliances are kept once bought, so every day is
/// simulated against the same starting KitchenApplianceState (the layout's required appliances)
/// with nothing purchased in between. This only tells you the appliances are reachable - it can't
/// verify the in-game achievement action itself (e.g. holding a knife while wearing trainers),
/// since that's player behavior, not seed state.
///
/// Days start at 1, not 0 - day 0's shop isn't ground-truth-verified on this branch (see
/// ShopDaySimulatorGroundTruthTests), matching OtFriendlyChecker's own day range.
/// </summary>
public static class ShopApplianceReachabilityChecker
{
    public static bool AllReachableByDay(
        GameDataRepository repo, Seed seed, string settingSlug, IReadOnlyCollection<int> requiredApplianceIds, int maxDay)
    {
        var profile = repo.ResolveLayoutProfile(settingSlug, seed);
        var kitchen = new KitchenApplianceState(profile.RequiredApplianceIds);
        var sim = new ShopDaySimulator(repo, seed, kitchen);

        var remaining = new HashSet<int>(requiredApplianceIds);
        for (var day = 1; day <= maxDay && remaining.Count > 0; day++)
        {
            var offeredThisDay = sim.SimulateDay(day).Slots;
            remaining.RemoveWhere(id => offeredThisDay.Any(s => s.ApplianceId == id));
        }

        return remaining.Count == 0;
    }
}
