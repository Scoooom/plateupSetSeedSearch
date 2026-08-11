using PlateUpSeedTools.Engine.Data;
using PlateUpSeedTools.Engine.Seeding;

namespace PlateUpSeedTools.Engine.Cards;

/// <summary>One night's simulated card offer. Unlock1Id/Unlock2Id are null if nothing was offered that night.</summary>
public sealed record CardDayResult(int Day, int? Unlock1Id, int? Unlock2Id, int? SelectedId);

/// <summary>
/// Port of RestaurantMode/Kitchen/FindBonusUnlocks.cs's per-night card-draw logic - see
/// game-domain-knowledge.md's "Card draw RNG" section for the full mechanics. Assumes
/// Pref.SeedsAffectEverything is true (the default) - see that section's point 4.
///
/// Simplification (documented, pending ground-truth verification): when more than one active pack
/// would independently yield a card the same night, this always prefers the setting's own pack over
/// bonus packs (construction order below), matching a plausible entity-creation-order priority - see
/// game-domain-knowledge.md point 5. Not yet observed to matter for any currently-supported
/// setting/heat combination (Heat 2's day-2 and Heat 10's day-1 bonuses don't collide with the
/// default schedule's earliest day-3 slot).
/// </summary>
public sealed class CardDrawSimulator
{
    private const int CardDrawCategorySeed = 84902921;

    private readonly GameDataRepository _repo;
    private readonly Seed _seed;
    private readonly List<int> _activePackIds;
    private readonly HashSet<int> _ownedIds;
    private readonly int _tier;

    public CardDrawSimulator(GameDataRepository repo, Seed seed, int settingPackId, ResolvedRunSetup setup, int tier = 0)
    {
        _repo = repo;
        _seed = seed;
        _activePackIds = [settingPackId, .. setup.BonusPackIds];
        _ownedIds = new HashSet<int>(setup.InitiallyOwnedIds);
        _tier = tier;
    }

    /// <summary>
    /// Simulates one night. <paramref name="selectPolicy"/> decides which of the (up to two) offered
    /// cards the simulated player accepts - the accepted card is added to the owned set and affects
    /// every subsequent night's filtering (dedup/Requires/BlockedBy/BlocksAllOtherFood - see
    /// game-domain-knowledge.md point 6, "current_cards = only accepted unlocks"). Pass a policy that
    /// returns null to simulate declining (no trace left, matching the real game).
    /// Days must be simulated in increasing order (each night's owned-set mutation feeds the next).
    /// </summary>
    public CardDayResult SimulateDay(int day, Func<UnlockOptions, int?> selectPolicy)
    {
        var request = new UnlockRequest(day, _tier);
        var offered = default(UnlockOptions);

        foreach (var packId in _activePackIds)
        {
            // Fresh UnityRandom per pack try - the real game reseeds fresh per top-level pack entity
            // via FixedSeedContext(seed, 84902921*1231231+day).UseSubcontext(1), not once per night;
            // see game-domain-knowledge.md point 4-5 for why trying multiple packs is RNG-safe.
            var rng = new FixedSeedContext(_seed, unchecked(CardDrawCategorySeed * 1231231 + day)).UseSubcontext(1).CreateRandom();
            var options = UnlockPackEvaluator.GetOptions(_repo, packId, _ownedIds, request, rng);
            if (options.Unlock1Id is not null)
            {
                offered = options;
                break;
            }
        }

        var selectedId = offered.Unlock1Id is not null ? selectPolicy(offered) : null;
        if (selectedId is int id)
        {
            _ownedIds.Add(id);
        }

        return new CardDayResult(day, offered.Unlock1Id, offered.Unlock2Id, selectedId);
    }
}
