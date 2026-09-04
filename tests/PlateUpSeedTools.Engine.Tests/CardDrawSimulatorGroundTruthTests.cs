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

    /// <summary>
    /// Pins PeekOffer's skip_choices semantics against a third real capture (2026-08-20,
    /// GroundTruthLogger v1.15.0's skip_choices logging): banquet-hall, seed "bbbbvw7", starting dish
    /// Pies, Heat 1, tier 0, a real reroll used on day 3 AND day 5 in the same run. The critical,
    /// previously-unknown fact this proves: RestaurantMode/Kitchen/FindBonusUnlocks.cs's
    /// skip_choices = SCardActionDetails.RerollIndex + 1 is a RUN-WIDE cumulative counter, not reset
    /// per night - day 3's reroll really used skip_choices=1 (the day's own base offer differs from
    /// its reroll), but day 5's reroll really used skip_choices=2, NOT 1, because day 3's reroll
    /// usage carried forward. Day 5 happens to land back on the exact same pair as its base offer at
    /// skip_choices=2 - NOT because its eligible pool is down to 2 candidates (banquet-hall has 4
    /// live PrimaryTheme unlocks total - Affordable/Charming/Exclusive/Formal, all still eligible
    /// here since Theme Cards Pack's UnlockConditionGuarantee is a one-shot-per-run draw, gone for
    /// good the moment any PrimaryTheme card is owned, so there's no "claimed some already, pool
    /// shrank" story on this pack). Rather, UnlockSelectorGroupChoice's Group1==Group2 branch picks
    /// candidates[skip_choices*2 % N]/[skip_choices*2+1 % N] - with N=4 that's exactly 2 reachable
    /// pairs total (indices 0,1 vs 2,3), toggling by skip_choices' parity forever, regardless of how
    /// large skip_choices grows. skip_choices=1 gives the OTHER (genuinely different, and WRONG if
    /// assumed to be what a single reroll always produces) pair. See SeedSearcher's AllowRerollPerDay
    /// doc comment for the fix this pins.
    /// </summary>
    [Fact]
    public void MatchesRealMultiRerollCapture()
    {
        var repo = new GameDataRepository(Path.Combine(AppContext.BaseDirectory, "data", "settings"));
        const int allYouCanEatId = -347199069;
        var setup = RunSetup.Resolve(repo, PiesId, subOptionChoice: null, heatLevel: 1, startingUnlockId: 1989028383);
        var sim = new CardDrawSimulator(repo, new Seed("bbbbvw7"), repo.ResolveUnlockPackId("banquet-hall"), setup);

        // Days 0-2: no offer. Day 3: base offer differs from its (first, skip_choices=1) reroll -
        // accept the reroll's Unlock2 ("All You Can Eat") to match the real capture's owned-set at
        // day 5.
        for (var day = 0; day <= 2; day++)
        {
            sim.SimulateDay(day, _ => null);
        }

        var day3Base = sim.PeekOffer(3, skipChoices: 0).Options;
        Assert.Equal((-1992316049, -957080051), (day3Base.Unlock1Id, day3Base.Unlock2Id)); // Mushroom Pies / High Standards
        var day3Reroll = sim.PeekOffer(3, skipChoices: 1).Options;
        Assert.Equal((-1802123036, allYouCanEatId), (day3Reroll.Unlock1Id, day3Reroll.Unlock2Id)); // Vegetable Pies / All You Can Eat

        sim.SimulateDay(3, _ => allYouCanEatId, skipChoices: 1);
        sim.SimulateDay(4, _ => null);

        var day5Base = sim.PeekOffer(5, skipChoices: 0).Options;
        Assert.Equal((-1857686620, 1293847744), (day5Base.Unlock1Id, day5Base.Unlock2Id)); // Affordable / Charming

        // The WRONG value a hardcoded skipChoices: 1 would have produced - genuinely different from
        // the base offer, which is exactly what made this bug easy to miss without a real capture.
        var day5WrongReroll = sim.PeekOffer(5, skipChoices: 1).Options;
        Assert.Equal((-1323758054, -1641333859), (day5WrongReroll.Unlock1Id, day5WrongReroll.Unlock2Id)); // Exclusive / Formal

        // The REAL, ground-truth-confirmed reroll (skip_choices=2, since one reroll was already used
        // on day 3) - identical to the base offer.
        var day5RealReroll = sim.PeekOffer(5, skipChoices: 2).Options;
        Assert.Equal(day5Base, day5RealReroll);
    }
}
