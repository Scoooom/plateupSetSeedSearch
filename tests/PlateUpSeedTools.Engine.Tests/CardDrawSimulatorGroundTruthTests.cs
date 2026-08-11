using PlateUpSeedTools.Engine.Cards;
using PlateUpSeedTools.Engine.Data;
using PlateUpSeedTools.Engine.Seeding;
using Xunit;

namespace PlateUpSeedTools.Engine.Tests;

/// <summary>
/// Pins Engine.Cards.CardDrawSimulator against real values captured live from the running game via
/// PlateUpSourceFiles/GroundTruthLogger (v1.12.0's ModularUnlockPack/CompositeUnlockPack.GetOptions
/// patch) - same standard as FixedString32HashTests/UnityRandomTests/LayoutSeedTests, and the first
/// ground-truth confirmation of the card-draw port specifically (see game-domain-knowledge.md's
/// "Ground-truth verification" section for the full capture, including confirmation that the
/// composite pack's Franchise-&gt;Normal-&gt;Theme try-in-order short-circuit behaves exactly as
/// ported - Theme Cards Pack genuinely never gets a GetOptions call on days where Normal Cards Pack
/// already succeeded, visible directly in the captured log).
///
/// Setup: Normal setting, seed "bdghjmqr", starting dish Pies (no sub-options), Heat 0, no setting
/// StartingUnlock. Player policy during capture: always accept whichever card is offered in the
/// first slot (Unlock1) - see CardDrawSimulator's SimulateDay selectPolicy parameter.
/// </summary>
public class CardDrawSimulatorGroundTruthTests
{
    private const int PiesId = -133939790;

    // (Day, Unlock1Id, Unlock2Id) for every day 1-15, captured 2026-08-10 - null means no offer.
    private static readonly (int Day, int? Unlock1Id, int? Unlock2Id)[] CapturedOffers =
    [
        (1, null, null),
        (2, null, null),
        (3, -1802123036, -1737101581), // Vegetable Pies / Health and Safety
        (4, null, null),
        (5, 1293847744, -1857686620), // Charming / Affordable
        (6, -1992316049, 1103452725), // Mushroom Pies / High Quality
        (7, null, null),
        (8, null, null),
        (9, 1393945424, 233335391), // Pumpkin Seed / Personalised Waiting
        (10, null, null),
        (11, null, null),
        (12, -1745179096, 1642577536), // Onion Rings / Herd Mentality
        (13, null, null),
        (14, null, null),
        (15, 1618418478, 1173444265), // Bootstrapping / Wash Basin
    ];

    [Fact]
    public void MatchesRealCapturedCardSequence()
    {
        var repo = new GameDataRepository(Path.Combine(AppContext.BaseDirectory, "data", "settings"));
        var setup = RunSetup.Resolve(repo, PiesId, subOptionChoice: null, heatLevel: 0, startingUnlockId: null);
        var sim = new CardDrawSimulator(repo, new Seed("bdghjmqr"), repo.ResolveUnlockPackId("normal"), setup);

        foreach (var (day, expectedUnlock1, expectedUnlock2) in CapturedOffers)
        {
            var result = sim.SimulateDay(day, offered => offered.Unlock1Id);

            Assert.Equal(expectedUnlock1, result.Unlock1Id);
            Assert.Equal(expectedUnlock2, result.Unlock2Id);
        }
    }

    /// <summary>
    /// Pins RunSetup's franchiseCarryoverIds modeling against a second real capture: same seed
    /// ("cuddle" - confirmed via the ConstructLayoutGroundTruthPatch's CaptureGate only firing for
    /// its hardcoded TargetConstructionSeed, which really did gate open during this capture),
    /// starting dish Pies, Heat 2, but this run was continued via "Franchise" off a completed prior
    /// run (tier=1) rather than started fresh. Captured 2026-08-10 via GroundTruthLogger v1.13.0's
    /// current_cards field: real day=0/tier=1 owned set was exactly {Pies, Heat1 card, Heat2 card}
    /// plus 5 extra IDs (Mushroom Pies, Affordable, Roast Potato, Careful Accounting, Herd
    /// Mentality) - real cards earned during the prior completed run, carried over via
    /// BeginRestaurant.cs's CFranchiseItem.Cards copy. Player policy: accept slot 1 every night
    /// EXCEPT day 3 and day 12 (reconstructed from the day=15 owned-set snapshot, which contained
    /// Dinner Rush/Victorian Standards - day 3/12's slot-2 offers - instead of Apple Pies/Ice Cream,
    /// their slot-1 offers). Every day 0-15 matched bit-for-bit, including the two divergent
    /// mid-sequence picks - see game-domain-knowledge.md's "Franchise tier / carryover cards"
    /// section, now confirmed rather than inferred.
    /// </summary>
    [Fact]
    public void MatchesRealFranchiseCapture()
    {
        var repo = new GameDataRepository(Path.Combine(AppContext.BaseDirectory, "data", "settings"));
        int[] franchiseCarryoverIds = [-1992316049, -1857686620, -1289628495, -1205203705, 1642577536];
        var setup = RunSetup.Resolve(repo, PiesId, subOptionChoice: null, heatLevel: 2, startingUnlockId: null, franchiseCarryoverIds);
        var sim = new CardDrawSimulator(repo, new Seed("cuddle"), repo.ResolveUnlockPackId("normal"), setup, tier: 1);

        // (Day, Unlock1Id, Unlock2Id) for every day 0-15 - null means no offer.
        (int Day, int? Unlock1Id, int? Unlock2Id)[] capturedOffers =
        [
            (0, null, null),
            (1, null, null),
            (2, -1802123036, -1015158173), // Vegetable Pies / Broccoli Cheese Soup
            (3, -211821608, -37551439), // Apple Pies / Dinner Rush
            (4, null, null),
            (5, null, null),
            (6, -1745179096, -287956430), // Onion Rings / Leisurely Eating
            (7, null, null),
            (8, null, null),
            (9, -233806503, 1765310572), // Tomato Soup / Advertising
            (10, null, null),
            (11, null, null),
            (12, 373996608, -913531466), // Ice Cream / Victorian Standards
            (13, null, null),
            (14, null, null),
            (15, -401283600, 868780472), // Preparation Time / Catalogue
        ];
        var slot2Days = new HashSet<int> { 3, 12 };

        foreach (var (day, expectedUnlock1, expectedUnlock2) in capturedOffers)
        {
            var result = sim.SimulateDay(day, offered => slot2Days.Contains(day) ? offered.Unlock2Id ?? offered.Unlock1Id : offered.Unlock1Id);

            Assert.Equal(expectedUnlock1, result.Unlock1Id);
            Assert.Equal(expectedUnlock2, result.Unlock2Id);
        }
    }
}
