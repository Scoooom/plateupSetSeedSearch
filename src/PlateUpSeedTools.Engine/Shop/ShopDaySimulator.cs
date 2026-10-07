using PlateUpSeedTools.Engine.Data;
using PlateUpSeedTools.Engine.Random;
using PlateUpSeedTools.Engine.Seeding;

namespace PlateUpSeedTools.Engine.Shop;

/// <summary>One requested shop slot for the day, and which appliance (if any) filled it.</summary>
public sealed record ShopSlotResult(ShoppingTags RequestedTags, int? ApplianceId);

/// <summary>One night's simulated shop offers - the day%5 branch's 6 "decor shop" slots are never represented here, see ShopDifficultyHelpers.GetSlotRequests.</summary>
public sealed record ShopDayResult(int Day, IReadOnlyList<ShopSlotResult> Slots);

/// <summary>
/// Port of RestaurantMode/Kitchen/HandleNewShop.cs's per-night shop-offer resolution - see
/// docs/game-domain-knowledge.md's "Shop / appliance purchase RNG" section for the full mechanics.
///
/// Unlike CardDrawSimulator, this does NOT auto-mutate <see cref="KitchenApplianceState"/> when a
/// slot resolves - a shop offer and a player buying it are different events in the real game
/// (letters sit unopened over multiple days), so PlaceAppliance/TossAppliance on the kitchen state
/// are the caller's explicit next actions on this call's result, not baked into SimulateDay itself.
/// Don't "fix" this to auto-place like the card system - that's a deliberate divergence, not an
/// oversight (see KitchenApplianceState's doc comment for why a future search phase needs this).
///
/// v1 simplifications (see docs/game-domain-knowledge.md for the full list, and
/// ShopOptionEvaluator's class doc for exactly which filter-pipeline steps remain stubbed and why):
/// BaseUpgradedShopChance ignores CUpgradedShopChance/CGrantsShopDiscount/January-red-envelope
/// bonuses (none modeled, assumed inactive); Season defaults to Normal; CRemovesShopBlueprint
/// slot-count reduction is unmodeled (0); SellOnlyIfPlatesNeeded (menuDishIds) is implemented but
/// approximates "the menu" as the caller's full owned-dish set rather than modeling an explicit
/// menu/upcoming-unlock-queue distinction. bonusStapleApplianceIds/grantedThemes (steps 3/9) ARE
/// fully implemented now - callers resolve them from the Cards walk's owned-unlock set via
/// ShopOptionEvaluator.ResolveBonusStapleApplianceIds/ResolveGrantedThemes.
/// </summary>
public sealed class ShopDaySimulator
{
    private const int ShopCategorySeed = 823828;

    private readonly GameDataRepository _repo;
    private readonly Seed _seed;
    private readonly KitchenApplianceState _kitchen;
    private readonly IReadOnlySet<int>? _bonusStapleApplianceIds;
    private readonly int _currentSeason;
    private readonly IReadOnlySet<int>? _menuDishIds;
    private readonly int _grantedThemes;

    public ShopDaySimulator(
        GameDataRepository repo, Seed seed, KitchenApplianceState kitchen,
        IReadOnlySet<int>? bonusStapleApplianceIds = null, int currentSeason = 0,
        IReadOnlySet<int>? menuDishIds = null, int grantedThemes = 0)
    {
        _repo = repo;
        _seed = seed;
        _kitchen = kitchen;
        _bonusStapleApplianceIds = bonusStapleApplianceIds;
        _currentSeason = currentSeason;
        _menuDishIds = menuDishIds;
        _grantedThemes = grantedThemes;
    }

    /// <summary>
    /// Simulates one day's shop offers against the CURRENT KitchenApplianceState (read at call time,
    /// not snapshotted) - callers must call PlaceAppliance/TossAppliance between days in the same
    /// order the player would take those actions, since staple/duplicate eligibility depends on it.
    /// </summary>
    public ShopDayResult SimulateDay(int day)
    {
        var rng = new FixedSeedContext(_seed, unchecked(ShopCategorySeed * 1231231 + day)).CreateRandom();
        return SimulateRound(day, rng);
    }

    /// <summary>
    /// Simulates a day's shop offers, then re-runs the SAME resolution rerollCount more times,
    /// CONTINUING the same per-day RNG stream rather than resetting it - matches HandleShopReroll's
    /// real behavior: a reroll recreates every existing shop slot and re-runs the identical
    /// appliance-selection logic against whatever PRNG state the day's stream has reached. Position
    /// never changes on a reroll (user-confirmed from real gameplay - held or sitting on the ground,
    /// the new letter lands in the exact same spot as the old one; see
    /// Engine.Shop.ShopSpawnLocation's doc comment), only the appliance inside it does, which is why
    /// ShopSpawnLocationEvaluator is called once per day rather than once per round. Source-confirmed
    /// this has NO dependency on prior days' reroll activity or the real game's escalating gold-cost
    /// counter (SRerollCost) - appliance selection is seeded purely from (seed, day), see class doc and
    /// game-domain-knowledge.md's reroll section for the full trace. Result[0] is the day's initial
    /// offer (identical to calling SimulateDay(day)); Result[i] for i&gt;0 is the state after the i-th
    /// reroll that same day.
    /// </summary>
    public IReadOnlyList<ShopDayResult> SimulateDayWithRerolls(int day, int rerollCount)
    {
        if (rerollCount < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(rerollCount), "Reroll count cannot be negative.");
        }

        var rng = new FixedSeedContext(_seed, unchecked(ShopCategorySeed * 1231231 + day)).CreateRandom();
        var results = new List<ShopDayResult>(rerollCount + 1);

        for (var round = 0; round <= rerollCount; round++)
        {
            results.Add(SimulateRound(day, rng));
        }

        return results;
    }

    /// <summary>
    /// One round of shop-offer resolution (the day's initial build, or one reroll's redraw) - rebuilds
    /// the candidate pool fresh each call (matching the real game's CreateShopOptions resetting
    /// Staple/IsRemoved every frame) but consumes RNG from the caller-supplied, possibly-already-
    /// advanced stream rather than reseeding.
    /// </summary>
    private ShopDayResult SimulateRound(int day, UnityRandom rng)
    {
        // Step 1-5/8/9 of the filter pipeline - matches the real game's ShopOptionGroup pass
        // completing before HandleNewShop.OnUpdate reads the (already-tagged) CShopBuilderOptions
        // (HandleNewShop has no OrderFirst, so it runs after both OrderFirst groups within
        // EndOfDayProgressionGroup).
        var options = ShopOptionEvaluator.BuildDailyOptions(_repo, _kitchen, _bonusStapleApplianceIds, _currentSeason, _menuDishIds, _grantedThemes);
        var availableStapleCount = options.Count(o => o.TakesStapleSlot);
        var requestedSlots = ShopDifficultyHelpers.GetSlotRequests(day, availableStapleCount);
        var upgradedChance = ShopDifficultyHelpers.BaseUpgradedShopChance(day);

        var tmpOffered = new HashSet<int>();
        var slots = new List<ShopSlotResult>();

        foreach (var requestedTags in requestedSlots)
        {
            var upgradedRoll = rng.Value < upgradedChance;
            ShuffleInPlace(options, rng);

            ShopOptionState? pick = null;
            foreach (var option in options)
            {
                if (!option.IsRemoved && !tmpOffered.Contains(option.ApplianceId) && option.MatchesRequestTags(requestedTags)
                    && (upgradedRoll || option.Staple != ShopStapleType.NonStaple || !option.SellAsUpgrade))
                {
                    pick = option;
                    break;
                }
            }

            if (pick is not null && requestedTags != ShoppingTags.Decoration)
            {
                tmpOffered.Add(pick.ApplianceId);
            }

            slots.Add(new ShopSlotResult(requestedTags, pick?.ApplianceId));
        }

        return new ShopDayResult(day, slots);
    }

    /// <summary>
    /// Port of RandomExtensions.ShuffleInPlace&lt;T&gt;(this IList&lt;T&gt;) - a true Fisher-Yates
    /// using integer Range draws, byte-verified against source. NOT the same algorithm as the
    /// card-draw system's shuffle (UnlockPackEvaluator ports RandomExtensions.Shuffle&lt;T&gt;, an
    /// OrderBy(Random.value) sort-by-key with different RNG consumption) - HandleNewShop.cs calls
    /// both methods for different lists in the same method, confirming they're genuinely distinct,
    /// not one simplified from the other.
    /// </summary>
    private static void ShuffleInPlace<T>(IList<T> list, UnityRandom rng)
    {
        var n = list.Count;
        while (n > 1)
        {
            n--;
            var k = rng.Range(0, n + 1);
            (list[n], list[k]) = (list[k], list[n]);
        }
    }
}
