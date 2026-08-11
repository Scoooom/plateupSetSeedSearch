using PlateUpSeedTools.Engine.Cards;
using PlateUpSeedTools.Engine.Data;
using PlateUpSeedTools.Engine.Seeding;
using Xunit;

namespace PlateUpSeedTools.Engine.Tests;

/// <summary>
/// Internal-consistency tests for the card-draw simulator (Engine/Cards/) - determinism and
/// structural sanity against the real extracted data/settings/cards/*.json. No ground-truth-captured
/// reference values exist yet (see game-domain-knowledge.md's "Ground-truth verification still
/// needed" note) - these tests catch obvious breakage but do NOT prove fidelity to the real game.
/// </summary>
public class CardDrawSimulatorTests
{
    // Real IDs from data/settings/cards/unlocks.json (see game-domain-knowledge.md).
    private const int SpaghettiBaseId = 1764920765;
    private const int SpaghettiTraditionalId = 1411587046;
    private const int SpaghettiExtraStarchId = -1974675533;
    private const int Heat2CardId = 1382681650; // "Second Bite"

    private static GameDataRepository CreateRepository() =>
        new(Path.Combine(AppContext.BaseDirectory, "data", "settings"));

    private static CardDrawSimulator CreateSimulator(GameDataRepository repo, Seed seed, int heatLevel = 0) =>
        new(repo, seed, repo.ResolveUnlockPackId("normal"),
            RunSetup.Resolve(repo, SpaghettiBaseId, subOptionChoice: 1, heatLevel, startingUnlockId: null));

    [Fact]
    public void SameSeedProducesIdenticalOfferForSameDay()
    {
        var repo = CreateRepository();
        var seed = new Seed("cuddle");

        var sim1 = CreateSimulator(repo, seed);
        var sim2 = CreateSimulator(repo, seed);

        // Simulate up to day 3 on both, always declining (policy returns null) so the owned set
        // (and therefore later-day filtering) stays identical between the two runs.
        CardDayResult? last1 = null, last2 = null;
        for (var day = 1; day <= 3; day++)
        {
            last1 = sim1.SimulateDay(day, _ => null);
            last2 = sim2.SimulateDay(day, _ => null);
        }

        Assert.Equal(last1, last2);
    }

    [Fact]
    public void NormalScheduleOffersNothingOnDaysOneAndTwo()
    {
        // Default Composite Pack (Franchise day15/Normal interval3/Theme day>=5) has no condition
        // that can fire on day 1 or 2 at heat 0 - see game-domain-knowledge.md's schedule table.
        var repo = CreateRepository();
        var sim = CreateSimulator(repo, new Seed("cuddle"));

        var day1 = sim.SimulateDay(1, _ => null);
        var day2 = sim.SimulateDay(2, _ => null);

        Assert.Null(day1.Unlock1Id);
        Assert.Null(day2.Unlock1Id);
    }

    [Fact]
    public void NormalScheduleOffersACardOnDayThree()
    {
        var repo = CreateRepository();
        var sim = CreateSimulator(repo, new Seed("cuddle"));

        sim.SimulateDay(1, _ => null);
        sim.SimulateDay(2, _ => null);
        var day3 = sim.SimulateDay(3, _ => null);

        Assert.NotNull(day3.Unlock1Id);
    }

    [Fact]
    public void Heat2GrantsACardOfferOnDayTwo()
    {
        // Heat 2's card ("Second Bite") injects a one-time day-2 bonus pack offering a Dish card -
        // see game-domain-knowledge.md's "Heat draw bonus packs" table.
        var repo = CreateRepository();
        var sim = CreateSimulator(repo, new Seed("cuddle"), heatLevel: 2);

        sim.SimulateDay(1, _ => null);
        var day2 = sim.SimulateDay(2, _ => null);

        Assert.NotNull(day2.Unlock1Id);
        var offered = repo.GetUnlock(day2.Unlock1Id!.Value);
        Assert.Equal("Dish", offered.Kind);
    }

    [Fact]
    public void HeatCardsAreGrantedCumulatively()
    {
        var repo = CreateRepository();
        var ids = HeatSelection.ResolveGrantedCardIds(repo.HeatLevels, 3);

        Assert.Equal(3, ids.Count);
        Assert.Contains(repo.HeatLevels.GetCardId(1)!.Value, ids);
        Assert.Contains(repo.HeatLevels.GetCardId(2)!.Value, ids);
        Assert.Contains(repo.HeatLevels.GetCardId(3)!.Value, ids);
    }

    [Fact]
    public void RunSetupOwnsBothBaseDishAndChosenSubOption()
    {
        var repo = CreateRepository();
        var setup = RunSetup.Resolve(repo, SpaghettiBaseId, subOptionChoice: 2, heatLevel: 0, startingUnlockId: null);

        Assert.Contains(SpaghettiBaseId, setup.InitiallyOwnedIds);
        Assert.Contains(SpaghettiExtraStarchId, setup.InitiallyOwnedIds);
        Assert.DoesNotContain(SpaghettiTraditionalId, setup.InitiallyOwnedIds);
    }

    [Fact]
    public void RunSetupThrowsWithoutSubOptionChoiceForSubOptionDish()
    {
        var repo = CreateRepository();

        Assert.Throws<ArgumentException>(() =>
            RunSetup.Resolve(repo, SpaghettiBaseId, subOptionChoice: null, heatLevel: 0, startingUnlockId: null));
    }

    [Fact]
    public void Heat2BonusPackIsResolvedFromGrantedHeatCards()
    {
        var repo = CreateRepository();
        var setup = RunSetup.Resolve(repo, SpaghettiBaseId, subOptionChoice: 1, heatLevel: 2, startingUnlockId: null);

        Assert.Contains(Heat2CardId, setup.InitiallyOwnedIds);
        Assert.Single(setup.BonusPackIds);
    }

    [Fact]
    public void AcceptingAnOfferedCardBlocksItFromReappearing()
    {
        var repo = CreateRepository();
        var sim = CreateSimulator(repo, new Seed("bdghjmqr"));

        sim.SimulateDay(1, _ => null);
        sim.SimulateDay(2, _ => null);
        var day3 = sim.SimulateDay(3, opts => opts.Unlock1Id);

        Assert.Equal(day3.Unlock1Id, day3.SelectedId);
    }
}
