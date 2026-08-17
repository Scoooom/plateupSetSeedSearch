using PlateUpSeedTools.Engine.Data;
using PlateUpSeedTools.Engine.Seeding;

namespace PlateUpSeedTools.Engine.Shop;

/// <summary>
/// One hardcoded strategy check for the web/CLI search's "OT Friendly" filter: does this seed's shop
/// offer Research Desk on day 1, again on day 2, and again on day 4 (decline day 1 -&gt; buy+place
/// day 2 -&gt; toss day 3 in between) - ground-truth-confirmed against seed "vqrj", see
/// ShopDaySimulatorGroundTruthTests. Deliberately not generalized to other appliances/day patterns -
/// this is a single named strategy, not a general search tool (see project plan).
/// </summary>
public static class OtFriendlyChecker
{
    public const int ResearchDeskId = 1139247360;

    public static bool Matches(GameDataRepository repo, Seed seed, string settingSlug)
    {
        var profile = repo.ResolveLayoutProfile(settingSlug, seed);
        var kitchen = new KitchenApplianceState(profile.RequiredApplianceIds);
        var sim = new ShopDaySimulator(repo, seed, kitchen);

        if (!OffersResearchDesk(sim, 1))
        {
            return false;
        }

        if (!OffersResearchDesk(sim, 2))
        {
            return false;
        }

        kitchen.PlaceAppliance(ResearchDeskId);
        kitchen.TossAppliance(ResearchDeskId);

        return OffersResearchDesk(sim, 4);
    }

    private static bool OffersResearchDesk(ShopDaySimulator sim, int day) =>
        sim.SimulateDay(day).Slots.Any(s => s.ApplianceId == ResearchDeskId);
}
