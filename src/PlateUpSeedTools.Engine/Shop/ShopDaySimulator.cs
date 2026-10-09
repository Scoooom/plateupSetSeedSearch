using PlateUpSeedTools.Engine.Data;
using PlateUpSeedTools.Engine.Random;
using PlateUpSeedTools.Engine.Seeding;

namespace PlateUpSeedTools.Engine.Shop;

/// <summary>
/// One requested shop slot for the day, and which appliance (if any) filled it. SlotIndex is the
/// slot's position in the day's ORIGINAL (round-0) template - stable across rounds even as slots
/// disappear (see ShopDayRoundState.RemoveSlot), so a caller can keep "Slot 3" meaning the same
/// physical slot across every round shown, not a position that reindexes as earlier slots vanish.
/// </summary>
public sealed record ShopSlotResult(int SlotIndex, ShoppingTags RequestedTags, int? ApplianceId);

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
    public ShopDayResult SimulateDay(int day) => BeginDayRounds(day).SimulateNextRound();

    /// <summary>
    /// Simulates a day's shop offers, then re-runs the resolution rerollCount more times, CONTINUING
    /// the same per-day RNG stream rather than resetting it. Thin wrapper around
    /// <see cref="BeginDayRounds"/>/<see cref="ShopDayRoundState.SimulateNextRound"/> - a caller that
    /// never calls <see cref="ShopDayRoundState.RemoveSlot"/> or mutates the kitchen mid-loop (this
    /// method's own contract) gets byte-identical behavior to before this type existed; every round
    /// still sees the full, never-shrinking slot set.
    ///
    /// CORRECTION (superseding an earlier, incomplete port): a reroll does NOT re-request each slot's
    /// ORIGINAL tag. Source-confirmed (HandleShopReroll.cs): the real reroll handler computes ONE
    /// shared tag for the whole reroll - `Day % 5 != 0 ? DefaultShoppingTag : Decoration` - and
    /// requests EVERY surviving letter with that SAME tag, regardless of what tag that letter's slot
    /// was originally created with. Per ShopOptionState.MatchesRequestTags, neither DefaultShoppingTag
    /// nor Decoration ever matches a staple-flagged option (only a bare `Basic` request can) - so a
    /// reroll can NEVER produce a staple appliance, full stop, independent of ownership or any same-
    /// day purchase. This was previously missed entirely: this class used to re-request every round
    /// (including rerolls) with the INITIAL offer's own per-slot tag template
    /// (ShopDifficultyHelpers.GetSlotRequests' output), which incorrectly let staples reappear in
    /// every reroll. See ShopDayRoundState.SimulateNextRound for where this is now applied - round 0
    /// (the initial offer) still uses the per-slot template; every round after that uses the single
    /// reroll-wide tag for every still-active slot.
    ///
    /// Position is NOT day-constant either (unrelated to the reroll-tag correction above, documented
    /// separately): per HandleShopReroll.cs and a real controlled gameplay test, whether a reroll's
    /// letter gets a fresh placement depends on whether it was held by a player at that exact reroll -
    /// held means a fresh ShopSpawnLocationEvaluator.Evaluate call against the then-live setting and
    /// the then-live someone-standing-inside fact; unheld means the previous round's location carries
    /// over unchanged. This method does NOT compute location at all (appliance selection and position
    /// are source-confirmed independent via GetPostTiles' separate UseSubcontext(1) draw, so it
    /// doesn't perturb this method's RNG stream) - callers must track held/unheld per round themselves
    /// and call ShopSpawnLocationEvaluator.Evaluate only for rounds marked held, carrying the prior
    /// round's label forward otherwise.
    /// </summary>
    public IReadOnlyList<ShopDayResult> SimulateDayWithRerolls(int day, int rerollCount)
    {
        if (rerollCount < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(rerollCount), "Reroll count cannot be negative.");
        }

        var state = BeginDayRounds(day);
        var results = new List<ShopDayResult>(rerollCount + 1);

        for (var round = 0; round <= rerollCount; round++)
        {
            results.Add(state.SimulateNextRound());
        }

        return results;
    }

    /// <summary>
    /// Starts a new, stateful walk through one day's shop rounds - the primary entry point for a
    /// caller that needs to interleave buy/toss actions BETWEEN rounds (same-day purchases affecting
    /// later rerolls the same day - see ShopDayRoundState's doc comment for the full mechanic this
    /// exists to model). SimulateDay/SimulateDayWithRerolls above are thin wrappers over this for the
    /// common no-mid-day-action case.
    ///
    /// Computes the day's RNG stream and slot-tag template ONCE here, not per round: source-confirmed
    /// (CreateShopRequests.cs) the real game's slot-COUNT/template decision genuinely only runs once,
    /// at the day's first frame - a reroll never re-derives it, it only ever reuses or drops existing
    /// slots (see ShopDayRoundState.RemoveSlot). Recomputing it fresh every round (this class's
    /// behavior before this method existed) was harmless only by coincidence, because nothing used to
    /// mutate kitchen state mid-day; now that mutation is possible, recomputing fresh would risk
    /// silently drifting from the real one-time-per-day decision (e.g. a changed available-staple-
    /// count clamp mid-day, which the real game never does).
    /// </summary>
    public ShopDayRoundState BeginDayRounds(int day)
    {
        var rng = new FixedSeedContext(_seed, unchecked(ShopCategorySeed * 1231231 + day)).CreateRandom();
        var options = ShopOptionEvaluator.BuildDailyOptions(_repo, _kitchen, _bonusStapleApplianceIds, _currentSeason, _menuDishIds, _grantedThemes);
        var availableStapleCount = options.Count(o => o.TakesStapleSlot);
        var requestedSlots = ShopDifficultyHelpers.GetSlotRequests(day, availableStapleCount);
        return new ShopDayRoundState(this, day, rng, requestedSlots);
    }

    /// <summary>
    /// One round of shop-offer resolution. Rebuilds the candidate pool fresh each call (matching the
    /// real game's CreateShopOptions resetting Staple/IsRemoved every frame - this means a kitchen
    /// mutation applied between calls, e.g. a same-day buy/toss, is correctly picked up here) but
    /// consumes RNG from the caller-supplied, possibly-already-advanced stream rather than reseeding.
    ///
    /// isReroll selects which tag each active slot is requested with - see
    /// SimulateDayWithRerolls' doc comment for the full reroll-tag correction this encodes: false
    /// (round 0 only) uses each slot's own entry from requestedSlots (the day's real per-slot
    /// template); true (every round after) uses ONE shared tag for every still-active slot,
    /// `day % 5 != 0 ? DefaultShoppingTag : Decoration` - deliberately NOT reusing
    /// ShopDifficultyHelpers.GetSlotRequests' own day%5 branch (which additionally guards `day > 0`,
    /// since day 0's INITIAL offer is treated as a normal day - see that method's doc) because
    /// HandleShopReroll.cs's real ternary has no such guard; day 0's reroll (only reachable if Jasmine
    /// grants it a shop at all) genuinely uses the Decoration tag like day 5/10/15, even though day
    /// 0's own initial offer does not. Preserve this asymmetry - it is a real, source-confirmed
    /// mechanic, not an inconsistency to "fix" by sharing one branch.
    /// </summary>
    internal ShopDayResult SimulateRoundCore(int day, UnityRandom rng, IReadOnlyList<ShoppingTags> requestedSlots, IReadOnlyList<int> activeSlotIndexes, bool isReroll)
    {
        var options = ShopOptionEvaluator.BuildDailyOptions(_repo, _kitchen, _bonusStapleApplianceIds, _currentSeason, _menuDishIds, _grantedThemes);
        var upgradedChance = ShopDifficultyHelpers.BaseUpgradedShopChance(day);
        var rerollTag = day % 5 != 0 ? ShoppingTagsExtensions.DefaultShoppingTag : ShoppingTags.Decoration;

        var tmpOffered = new HashSet<int>();
        var slots = new List<ShopSlotResult>();

        foreach (var slotIndex in activeSlotIndexes)
        {
            var requestedTags = isReroll ? rerollTag : requestedSlots[slotIndex];
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

            slots.Add(new ShopSlotResult(slotIndex, requestedTags, pick?.ApplianceId));
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
    internal static void ShuffleInPlace<T>(IList<T> list, UnityRandom rng)
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

/// <summary>
/// A stateful walk through one day's shop rounds, returned by <see cref="ShopDaySimulator.BeginDayRounds"/>.
/// Exists to model a real mechanic this project missed in its first port: buying a shop letter
/// doesn't just change kitchen ownership for staple re-evaluation purposes - it PERMANENTLY removes
/// that specific slot from the day's shop, with no backfill. Source-confirmed
/// (RestaurantMode/Kitchen/PurchaseAfterDuration.cs removes CApplianceBlueprint from the purchased
/// entity rather than destroying-and-recreating it; HandleShopReroll.cs's Blueprints query, re-run
/// fresh at reroll time, simply never matches that entity again since it no longer carries
/// CLetterBlueprint/CApplianceBlueprint; CreateShopRequests.cs - the day's slot-COUNT decision - only
/// ever runs once, at the day's start, with no per-slot backfill counter). A day with 5 slots where
/// slot 2 gets bought produces a 4-slot reroll afterward, not a 5-slot reroll with slot 2 replaced.
///
/// Call <see cref="SimulateNextRound"/> to advance one round at a time (round 0 = the initial offer);
/// between calls, apply whatever the caller observed (a buy via <see cref="RemoveSlot"/> PLUS the
/// caller's own KitchenApplianceState.PlaceAppliance/TossAppliance call - this type does not own the
/// kitchen reference, the owning ShopDaySimulator does) before calling SimulateNextRound again, so
/// the next round correctly reflects it.
/// </summary>
public sealed class ShopDayRoundState
{
    private readonly ShopDaySimulator _simulator;
    private readonly int _day;
    private readonly UnityRandom _rng;
    private readonly IReadOnlyList<ShoppingTags> _requestedSlots;
    private readonly List<int> _activeSlotIndexes;
    private int _roundsSimulated;

    internal ShopDayRoundState(ShopDaySimulator simulator, int day, UnityRandom rng, IReadOnlyList<ShoppingTags> requestedSlots)
    {
        _simulator = simulator;
        _day = day;
        _rng = rng;
        _requestedSlots = requestedSlots;
        _activeSlotIndexes = Enumerable.Range(0, requestedSlots.Count).ToList();
    }

    /// <summary>
    /// Permanently removes slotIndex from every round simulated from now on - call after recording a
    /// buy for the round just simulated, before calling SimulateNextRound again. A toss does NOT call
    /// this (see this type's doc comment) - it only needs the caller's own kitchen mutation, which
    /// SimulateNextRound already picks up fresh via ShopOptionEvaluator.BuildDailyOptions. No-ops
    /// (via List.Remove's normal semantics) if slotIndex was already removed or is out of range.
    /// </summary>
    public void RemoveSlot(int slotIndex) => _activeSlotIndexes.Remove(slotIndex);

    /// <summary>Simulates the next round (0 = initial offer, 1 = first reroll, ...) and advances past it.</summary>
    public ShopDayResult SimulateNextRound()
    {
        var isReroll = _roundsSimulated > 0;
        _roundsSimulated++;
        return _simulator.SimulateRoundCore(_day, _rng, _requestedSlots, _activeSlotIndexes, isReroll);
    }
}
