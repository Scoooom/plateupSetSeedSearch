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
/// v1 simplifications (see docs/game-domain-knowledge.md for the full list): BaseUpgradedShopChance
/// ignores CUpgradedShopChance/CGrantsShopDiscount/January-red-envelope bonuses (none modeled,
/// assumed inactive); Season defaults to Normal; CShopStaple bonus-staple source and
/// CRemovesShopBlueprint slot-count reduction are both unmodeled (empty/0).
/// </summary>
public sealed class ShopDaySimulator
{
    private const int ShopCategorySeed = 823828;

    private readonly GameDataRepository _repo;
    private readonly Seed _seed;
    private readonly KitchenApplianceState _kitchen;
    private readonly IReadOnlySet<int>? _bonusStapleApplianceIds;
    private readonly int _currentSeason;

    public ShopDaySimulator(
        GameDataRepository repo, Seed seed, KitchenApplianceState kitchen,
        IReadOnlySet<int>? bonusStapleApplianceIds = null, int currentSeason = 0)
    {
        _repo = repo;
        _seed = seed;
        _kitchen = kitchen;
        _bonusStapleApplianceIds = bonusStapleApplianceIds;
        _currentSeason = currentSeason;
    }

    /// <summary>
    /// Simulates one day's shop offers against the CURRENT KitchenApplianceState (read at call time,
    /// not snapshotted) - callers must call PlaceAppliance/TossAppliance between days in the same
    /// order the player would take those actions, since staple/duplicate eligibility depends on it.
    /// </summary>
    public ShopDayResult SimulateDay(int day)
    {
        // Step 1-4/8 of the filter pipeline, run once for the day - matches the real game's
        // ShopOptionGroup pass completing before HandleNewShop.OnUpdate reads the (already-tagged)
        // CShopBuilderOptions (HandleNewShop has no OrderFirst, so it runs after both OrderFirst
        // groups within EndOfDayProgressionGroup).
        var options = ShopOptionEvaluator.BuildDailyOptions(_repo, _kitchen, _bonusStapleApplianceIds, _currentSeason);
        var availableStapleCount = options.Count(o => o.TakesStapleSlot);
        var requestedSlots = ShopDifficultyHelpers.GetSlotRequests(day, availableStapleCount);

        // One continuing RNG stream for the whole day - no .UseSubcontext(1) here (that's only used,
        // separately, around GetPostTiles()'s letter-position stream in the real game, irrelevant to
        // *which* appliance is picked - see class doc / game-domain-knowledge.md).
        var rng = new FixedSeedContext(_seed, unchecked(ShopCategorySeed * 1231231 + day)).CreateRandom();
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
