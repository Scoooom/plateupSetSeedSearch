using PlateUpSeedTools.Engine.Data;
using PlateUpSeedTools.Engine.Random;

namespace PlateUpSeedTools.Engine.Cards;

/// <summary>
/// Port of KitchenData.CompositeUnlockPack/ModularUnlockPack/FilterBasic/UnlockSorterShuffle/
/// UnlockSorterPriority/UnlockSelector*/UnlockCondition* (all in DecompiledSource/GameData/
/// KitchenData/) - see game-domain-knowledge.md's "Card draw RNG" and "Corrections after directly
/// reading every relevant class" sections for the mechanics this mirrors verbatim.
///
/// One caller-supplied UnityRandom instance is threaded through a whole GetOptions call so that a
/// composite pack's sequential sub-pack tries (Franchise -&gt; Normal -&gt; Theme etc.) share one
/// continuing RNG stream, exactly like the original - the same "one continuous stream per logical
/// unit of work" rule LayoutGraphExecutor/CreateLayoutHelper already follow for layouts. Each
/// *separate* CardDrawSimulator.SimulateDay pack-try gets its own freshly-constructed UnityRandom
/// (see that class) since the real game reseeds fresh per top-level pack entity, not per night.
///
/// Simplification vs. the original: only a single Filter/Selector/Condition per modular pack is
/// modeled (the original allows lists of each) since every currently-decoded pack has exactly one of
/// each - extend UnlockPackData to a list if a future pack needs more than one.
/// </summary>
public static class UnlockPackEvaluator
{
    public static UnlockOptions GetOptions(GameDataRepository repo, int packId, HashSet<int> currentCards, UnlockRequest request, UnityRandom rng, int skipChoices = 0) =>
        GetOptionsWithSource(repo, packId, currentCards, request, rng, skipChoices).Options;

    /// <summary>
    /// Same as <see cref="GetOptions"/> but also reports which leaf modular pack actually produced
    /// the offer - a composite pack (e.g. the setting's own "Default Composite Pack") tries several
    /// sub-packs in order and returns whichever fires first, so the top-level packId alone doesn't
    /// say whether an offer came from, say, the Franchise Cards Pack vs the Theme Cards Pack. Used by
    /// CardDrawSimulator to label offers for the Seed Explorer UI; GetOptions ignores the source and
    /// stays the primary/simpler API for everything else.
    /// </summary>
    public static (UnlockOptions Options, int? SourcePackId) GetOptionsWithSource(GameDataRepository repo, int packId, HashSet<int> currentCards, UnlockRequest request, UnityRandom rng, int skipChoices = 0)
    {
        var pack = repo.GetPack(packId);
        return pack.Type switch
        {
            "composite" => GetCompositeOptions(repo, pack, currentCards, request, rng, skipChoices),
            "modular" => (GetModularOptions(repo, pack, currentCards, request, rng, skipChoices), packId),
            _ => throw new NotSupportedException($"Unknown pack type '{pack.Type}' for pack {packId}"),
        };
    }

    /// <summary>
    /// Deterministic "what could possibly be offered on day N" query, for the search UI's day-target
    /// dropdown filtering - NOT gameplay simulation. RNG only ever decides shuffle order and which
    /// single card gets picked from an already-determined nonempty slot (see <see cref="GetOptions"/>
    /// and its <see cref="ShouldBlockCard"/>/<see cref="ShouldProvide"/> helpers, reused unchanged
    /// here) - candidate *eligibility*, whether a pack fires at all that day, and which
    /// UnlockGroup/MenuPhase slot(s) a firing pack draws from are all deterministic given
    /// (currentCards, day, tier). This unions across EVERY sub-pack of a composite (not the "first
    /// non-empty wins" order <see cref="GetCompositeOptions"/> uses for the real RNG path) and every
    /// active top-level pack, since the goal is "everything reachable," not "what the actual seed
    /// would show."
    /// </summary>
    public static HashSet<int> GetPossibleCardIds(GameDataRepository repo, IReadOnlyList<int> activePackIds, HashSet<int> currentCards, int day, int tier)
    {
        var request = new UnlockRequest(day, tier);
        var result = new HashSet<int>();
        foreach (var packId in activePackIds)
        {
            CollectPossibleCardIds(repo, packId, currentCards, request, result);
        }

        return result;
    }

    private static void CollectPossibleCardIds(GameDataRepository repo, int packId, HashSet<int> currentCards, UnlockRequest request, HashSet<int> result)
    {
        var pack = repo.GetPack(packId);
        switch (pack.Type)
        {
            case "composite":
                foreach (var subPackId in pack.SubPackIds ?? [])
                {
                    CollectPossibleCardIds(repo, subPackId, currentCards, request, result);
                }

                break;
            case "modular":
                CollectPossibleModularCardIds(repo, pack, currentCards, request, result);
                break;
            default:
                throw new NotSupportedException($"Unknown pack type '{pack.Type}' for pack {packId}");
        }
    }

    private static void CollectPossibleModularCardIds(GameDataRepository repo, UnlockPackData pack, HashSet<int> currentCards, UnlockRequest request, HashSet<int> result)
    {
        var condition = pack.Condition ?? throw new InvalidOperationException($"Modular pack '{pack.Name}' has no Condition");
        if (!ShouldProvide(repo, condition, currentCards, request))
        {
            return;
        }

        var filter = pack.Filter ?? throw new InvalidOperationException($"Modular pack '{pack.Name}' has no Filter");
        var selector = pack.Selector ?? throw new InvalidOperationException($"Modular pack '{pack.Name}' has no Selector");
        var candidates = CandidateSource(repo, selector)
            .Where(c => !ShouldBlockCard(repo, c, currentCards, request, AsCumulativeCopyFilter(filter)))
            .ToList();

        foreach (var id in GetReachableIds(selector, candidates))
        {
            result.Add(id);
        }
    }

    /// <summary>
    /// Wraps a pack's real Filter (from packs.json) with TreatSpecificFranchiseTierAsCumulativeCopy
    /// forced true, for this "possible cards" reachability preview only - see that flag's doc comment
    /// on FilterData for why it can't be the pack's own default and can't be applied to the actual
    /// GetModularOptions draw path.
    /// </summary>
    private static FilterData AsCumulativeCopyFilter(FilterData filter) => new()
    {
        IgnoreUnlockability = filter.IgnoreUnlockability,
        IgnoreFranchiseTier = filter.IgnoreFranchiseTier,
        IgnoreDuplicateFilter = filter.IgnoreDuplicateFilter,
        IgnoreRequirements = filter.IgnoreRequirements,
        AllowBaseDishes = filter.AllowBaseDishes,
        IgnoreBlocksOtherFood = filter.IgnoreBlocksOtherFood,
        IgnoreFilterByHasMain = filter.IgnoreFilterByHasMain,
        TreatSpecificFranchiseTierAsCumulativeCopy = true,
    };

    /// <summary>
    /// Every currently-decoded pack except "forcedCard" ones draws from every Unlock (filtered by
    /// group membership inside the selector itself) - the real game's Sets/IUnlockSet abstraction
    /// isn't modeled generically for those, see this class's doc comment. "forcedCard" is the one
    /// case that genuinely needs an explicit candidate source: Sets is a KitchenData.UnlockSetFixed
    /// literal list of specific card IDs, not filterable by group at all.
    /// </summary>
    private static IEnumerable<UnlockData> CandidateSource(GameDataRepository repo, SelectorData selector) =>
        selector.Kind == "forcedCard"
            ? (selector.FixedCardIds ?? throw new InvalidOperationException("forcedCard selector has no FixedCardIds")).Select(repo.GetUnlock)
            : repo.AllUnlocksOrderedById;

    // --- UnlockSelector*.GetOptions, membership-only variants (no RNG index pick) ---

    private static IEnumerable<int> GetReachableIds(SelectorData selector, List<UnlockData> candidates) => selector.Kind switch
    {
        "groupChoice" => ReachableGroupChoice(selector, candidates),
        "anyFromGroup" => candidates.Where(c => selector.Groups!.Contains(c.UnlockGroup)).Select(c => c.Id),
        "menuPhase" => candidates.Where(c => c.Kind == "Dish" && (c.ProvidesPhase(selector.Phase1!.Value) || c.ProvidesPhase(selector.Phase2!.Value))).Select(c => c.Id),
        "forcedCard" => candidates.Select(c => c.Id),
        _ => throw new NotSupportedException($"Unknown selector kind '{selector.Kind}'"),
    };

    private static IEnumerable<int> ReachableGroupChoice(SelectorData s, List<UnlockData> candidates)
    {
        // Mirrors SelectGroupChoice's group1/group2 fallback-swap: if one configured group has zero
        // eligible candidates, the real selector substitutes the other group into both slots.
        var group1 = s.Group1!.Value;
        var group2 = s.Group2!.Value;
        if (candidates.All(c => c.UnlockGroup != s.Group1!.Value))
        {
            group1 = s.Group2!.Value;
        }
        else if (candidates.All(c => c.UnlockGroup != s.Group2!.Value))
        {
            group2 = s.Group1!.Value;
        }

        return candidates.Where(c => c.UnlockGroup == group1 || c.UnlockGroup == group2).Select(c => c.Id);
    }

    private static (UnlockOptions Options, int? SourcePackId) GetCompositeOptions(GameDataRepository repo, UnlockPackData pack, HashSet<int> currentCards, UnlockRequest request, UnityRandom rng, int skipChoices)
    {
        foreach (var subPackId in pack.SubPackIds ?? [])
        {
            var result = GetOptionsWithSource(repo, subPackId, currentCards, request, rng, skipChoices);
            if (result.Options.Unlock1Id is not null)
            {
                return result;
            }
        }

        return (default, null);
    }

    private static UnlockOptions GetModularOptions(GameDataRepository repo, UnlockPackData pack, HashSet<int> currentCards, UnlockRequest request, UnityRandom rng, int skipChoices)
    {
        var filter = pack.Filter ?? throw new InvalidOperationException($"Modular pack '{pack.Name}' has no Filter");
        var selector = pack.Selector ?? throw new InvalidOperationException($"Modular pack '{pack.Name}' has no Selector");
        var candidates = CandidateSource(repo, selector)
            .Where(c => !ShouldBlockCard(repo, c, currentCards, request, filter))
            .ToList();

        // Sorters: [Shuffle, Priority] for every currently-decoded pack EXCEPT "Christmas Special
        // Card" (byte-verified: its Sorters list is genuinely empty, unlike every other pack) - a
        // null SorterPriority means "this pack has no sorters," so skip both RNG draws entirely
        // rather than unconditionally consuming them. Getting this right matters beyond this one
        // pack's own night: composite sub-packs share one continuing RNG stream (see this class's
        // doc comment), so phantom draws here would desync every later sub-pack tried the same night.
        if (pack.SorterPriority is { } sorterPriority)
        {
            // Runs even if the pack's condition below turns out false (see game-domain-knowledge.md's
            // "shuffle-then-discard on a non-match night" fidelity note) - but only when sorters exist.
            candidates = candidates.OrderBy(_ => rng.Value).ToList();
            var isPriority = rng.Value < sorterPriority.Probability;
            candidates = candidates.OrderByDescending(c => isPriority && IsPriority(c, sorterPriority) ? 1 : 0).ToList();
        }

        var condition = pack.Condition ?? throw new InvalidOperationException($"Modular pack '{pack.Name}' has no Condition");
        if (!ShouldProvide(repo, condition, currentCards, request))
        {
            return default;
        }

        return SelectOptions(selector, candidates, skipChoices);
    }

    /// <summary>
    /// Whether <paramref name="candidate"/> is an internally-consistent "already owned" pick given
    /// <paramref name="currentCards"/> (other cards already owned/selected) and
    /// <paramref name="tier"/> - for validating a manually-asserted franchise-carryover card in the
    /// search UI, NOT a simulated draw. Deliberately distinct from asking "would the Franchise/Theme
    /// pack ever OFFER this" (a previous version of this check reused that pack's own Filter, which
    /// was wrong - offer-eligibility flags like unlockability/blocks-other-food/has-a-main are about
    /// the DRAW mechanism, irrelevant to whether a card could already be owned from a prior run).
    /// What DOES matter for "already owned" - and is real, data-verified, not an invented
    /// restriction (e.g. every occurrence of the "Variety" franchise bonus is genuinely `BlockedBy`
    /// ["Community", "Coffee"] in the extracted data) - is internal consistency: Requires/BlockedBy
    /// chains against the rest of the selection, no exact duplicates, and (for cards that aren't part
    /// of a repeatable family - see below) franchise-tier eligibility. Day is irrelevant to every
    /// check `ShouldBlockCard` performs (only Tier is consulted), so 0 is a safe placeholder.
    ///
    /// For a REPEATABLE franchise-carryover card (e.g. "Variety" - 6 live records,
    /// `MinimumFranchiseTier` 0-5), `ShouldBlockCard` enforces cumulative same-name ownership only
    /// (you can't claim copy 3 without copies 1 and 2) - the declared `tier` is never consulted for
    /// these, since a repeatable card's `MinimumFranchiseTier` is an occurrence index ("the Nth time
    /// you've taken this card"), not the franchise tier you were on when you took it - see
    /// `ShouldBlockCard`'s own doc comment for the full explanation and why this is enforced there
    /// (shared by carryover validation, the day-target dropdown, and the actual simulated draw
    /// alike, not just here). Non-repeatable franchise-gated cards still respect the tier floor (a
    /// genuine constraint: you couldn't already own a card gated behind franchise progress you
    /// haven't reached).
    /// </summary>
    public static bool IsValidCarryoverPick(GameDataRepository repo, UnlockData candidate, HashSet<int> currentCards, int tier) =>
        !ShouldBlockCard(repo, candidate, currentCards, new UnlockRequest(Day: 0, tier), CarryoverFilter);

    /// <summary>
    /// True if <paramref name="targetIds"/> contains two cards that can never coexist because owning
    /// one permanently prevents the other from ever being offered again (see ShouldBlockCard's
    /// BlockedByIds check - e.g. every tier of "Variety" is genuinely BlockedBy ["Community",
    /// "Coffee"] in the extracted data, see IsValidCarryoverPick's doc comment). Used by the
    /// "any day" search target mode (SeedSearcher.FlexibleTargetIds) to reject an unsatisfiable
    /// target set up front, before starting a potentially long brute-force run - no seed could ever
    /// satisfy a set containing such a pair, regardless of pick order.
    ///
    /// A single pairwise pass suffices - no transitive/graph traversal is needed, since
    /// ShouldBlockCard only ever looks one hop back at currently-owned cards
    /// (candidate.BlockedByIds.Any(currentCards.Contains)), so any two targets that can conflict do
    /// so directly, not through an intermediate card.
    /// </summary>
    public static bool HasBlockedByConflict(GameDataRepository repo, IReadOnlySet<int> targetIds, out (int CardId, int BlockedById) conflict)
    {
        foreach (var id in targetIds)
        {
            foreach (var blockerId in repo.GetUnlock(id).BlockedByIds)
            {
                if (targetIds.Contains(blockerId))
                {
                    conflict = (id, blockerId);
                    return true;
                }
            }
        }

        conflict = default;
        return false;
    }

    // Ignores every offer-specific gate (unlockability/blocks-other-food/has-a-main/base-dish) -
    // those govern whether a card would be freshly DRAWN, not whether it could already be owned.
    // Requires/BlockedBy/duplicate/franchise-tier stay enforced - see IsValidCarryoverPick's doc
    // comment for why those specifically are real constraints, not invented ones.
    private static readonly FilterData CarryoverFilter = new()
    {
        IgnoreUnlockability = true,
        IgnoreFranchiseTier = false,
        IgnoreDuplicateFilter = false,
        IgnoreRequirements = false,
        AllowBaseDishes = true,
        IgnoreBlocksOtherFood = true,
        IgnoreFilterByHasMain = true,
        TreatSpecificFranchiseTierAsCumulativeCopy = true,
    };

    // --- FilterBasic.ShouldBlockCard ---

    private static bool ShouldBlockCard(GameDataRepository repo, UnlockData candidate, HashSet<int> currentCards, UnlockRequest request, FilterData filter)
    {
        if (!filter.IgnoreUnlockability && !candidate.IsUnlockable)
        {
            return true;
        }

        // CORRECTED after a real, repeated user report: an IsSpecificFranchiseTier candidate's
        // MinimumFranchiseTier is - for every such card that actually exists in the game's data - a
        // same-name OCCURRENCE index ("the Nth time you've taken this specific repeatable card
        // across separate franchise cycles," e.g. "Variety" has 6 live records), NOT the franchise
        // tier you were on when you took it. Those are unrelated numbers, and the declared
        // request.Tier must NEVER be consulted to decide eligibility for these - not exact-match, not
        // a floor, nothing (see this class's CumulativeCopyFilter/AsCumulativeCopyFilter doc comments
        // for exactly which callers this applies to and why NOT all of them - it can't be every
        // caller: the exact-tier-match rule is real and ground-truth-verified for the ACTUAL
        // simulated card draw, changing it there measurably altered a real captured 16-day sequence,
        // since which candidates are IN the pool at all affects the RNG shuffle order for every other
        // candidate that night too, not just repeatable cards themselves).
        if (candidate.IsSpecificFranchiseTier)
        {
            if (!filter.IgnoreFranchiseTier)
            {
                var franchiseBlockedRepeatable = filter.TreatSpecificFranchiseTierAsCumulativeCopy
                    ? UnlockNameResolver.GetRepeatableCardSiblings(repo, candidate)
                        .Any(s => s.MinimumFranchiseTier < candidate.MinimumFranchiseTier && !currentCards.Contains(s.Id))
                    : candidate.MinimumFranchiseTier != request.Tier;
                if (franchiseBlockedRepeatable)
                {
                    return true;
                }
            }
        }
        else if (candidate.MinimumFranchiseTier > request.Tier)
        {
            return true;
        }

        if (!filter.IgnoreDuplicateFilter && currentCards.Contains(candidate.Id))
        {
            return true;
        }

        if (!filter.IgnoreRequirements && candidate.RequiresIds.Any(r => !currentCards.Contains(r)))
        {
            return true;
        }

        if (!filter.IgnoreRequirements && candidate.BlockedByIds.Any(currentCards.Contains))
        {
            return true;
        }

        if (!filter.AllowBaseDishes && candidate.IsBaseDish)
        {
            return true;
        }

        if (!filter.IgnoreBlocksOtherFood)
        {
            foreach (var ownedId in currentCards)
            {
                if (repo.TryGetUnlock(ownedId, out var owned) && owned.BlocksAllOtherFood
                    && candidate.Kind == "Dish" && !owned.AllowedFoodIds.Contains(candidate.Id))
                {
                    return true;
                }
            }
        }

        // MenuPhase: Starter=0, Main=1, Dessert=2, Side=3, Complete=4.
        if (!filter.IgnoreFilterByHasMain && candidate.Kind == "Dish" && (candidate.ProvidesPhase(0) || candidate.ProvidesPhase(3)))
        {
            var hasMain = currentCards.Any(id => repo.TryGetUnlock(id, out var owned) && owned.Kind == "Dish" && owned.ProvidesPhase(1));
            if (!hasMain)
            {
                return true;
            }
        }

        return false;
    }

    // --- UnlockSorterPriority.IsPriority ---

    private static bool IsPriority(UnlockData c, SorterPriorityData cfg)
    {
        if (cfg.PrioritiseRequirements && c.RequiresIds.Count > 0)
        {
            return true;
        }

        if (cfg.Groups.Contains(c.UnlockGroup))
        {
            return true;
        }

        return c.Kind == "Dish" && c.DishType is int dishType && cfg.DishTypes.Contains(dishType);
    }

    // --- UnlockCondition*.ShouldProvide ---

    private static bool ShouldProvide(GameDataRepository repo, ConditionData cond, HashSet<int> currentCards, UnlockRequest request) => cond.Kind switch
    {
        "regular" => ShouldProvideRegular(cond, request),
        "guarantee" => ShouldProvideGuarantee(repo, cond, currentCards, request),
        "once" => request.Day == cond.Day!.Value,
        _ => throw new NotSupportedException($"Unknown condition kind '{cond.Kind}'"),
    };

    private static bool ShouldProvideRegular(ConditionData cond, UnlockRequest request)
    {
        var dayMax = cond.DayMax!.Value;
        var dayMin = cond.DayMin!.Value;
        var dayInterval = cond.DayInterval!.Value;
        var tierRequired = cond.TierRequired!.Value;

        if (request.Day > dayMax && dayMax > 0)
        {
            return false;
        }

        if (request.Day < dayMin && dayMin > 0)
        {
            return false;
        }

        if (dayInterval != 0 && (request.Day - cond.DayOffset!.Value) % dayInterval != 0)
        {
            return false;
        }

        return request.Tier == tierRequired || tierRequired <= 0;
    }

    private static bool ShouldProvideGuarantee(GameDataRepository repo, ConditionData cond, HashSet<int> currentCards, UnlockRequest request)
    {
        if (request.Day < cond.MinDay!.Value || request.Tier < cond.MinTier!.Value)
        {
            return false;
        }

        return !currentCards.Any(id => repo.TryGetUnlock(id, out var owned) && owned.UnlockGroup == cond.GuaranteedGroup!.Value);
    }

    // --- UnlockSelector*.GetOptions ---

    private static UnlockOptions SelectOptions(SelectorData selector, List<UnlockData> candidates, int skipChoices) => selector.Kind switch
    {
        "groupChoice" => SelectGroupChoice(selector, candidates, skipChoices),
        "anyFromGroup" => SelectAnyFromGroup(selector, candidates, skipChoices),
        "menuPhase" => SelectMenuPhase(selector, candidates, skipChoices),
        "forcedCard" => new UnlockOptions(GetOffsetChoice(candidates, skipChoices)?.Id, null),
        _ => throw new NotSupportedException($"Unknown selector kind '{selector.Kind}'"),
    };

    private static UnlockData? GetOffsetChoice(List<UnlockData> candidates, int skips) =>
        candidates.Count == 0 ? null : candidates[skips % candidates.Count];

    private static UnlockOptions SelectGroupChoice(SelectorData s, List<UnlockData> candidates, int skipChoices)
    {
        var group1 = s.Group1!.Value;
        var group2 = s.Group2!.Value;
        if (candidates.All(c => c.UnlockGroup != s.Group1!.Value))
        {
            group1 = s.Group2!.Value;
        }
        else if (candidates.All(c => c.UnlockGroup != s.Group2!.Value))
        {
            group2 = s.Group1!.Value;
        }

        if (group1 == group2)
        {
            var opts = candidates.Where(c => c.UnlockGroup == group1).ToList();
            return new UnlockOptions(GetOffsetChoice(opts, skipChoices * 2)?.Id, GetOffsetChoice(opts, skipChoices * 2 + 1)?.Id);
        }

        var opt1s = candidates.Where(c => c.UnlockGroup == group1).ToList();
        var opt2s = candidates.Where(c => c.UnlockGroup == group2).ToList();
        return new UnlockOptions(GetOffsetChoice(opt1s, skipChoices)?.Id, GetOffsetChoice(opt2s, skipChoices)?.Id);
    }

    private static UnlockOptions SelectAnyFromGroup(SelectorData s, List<UnlockData> candidates, int skipChoices)
    {
        var opts = candidates.Where(c => s.Groups!.Contains(c.UnlockGroup)).ToList();
        return new UnlockOptions(GetOffsetChoice(opts, skipChoices * 2)?.Id, GetOffsetChoice(opts, skipChoices * 2 + 1)?.Id);
    }

    private static UnlockOptions SelectMenuPhase(SelectorData s, List<UnlockData> candidates, int skipChoices)
    {
        var opt1s = new List<UnlockData>();
        var opt2s = new List<UnlockData>();
        foreach (var c in candidates)
        {
            if (c.Kind != "Dish")
            {
                continue;
            }

            if (c.ProvidesPhase(s.Phase1!.Value))
            {
                opt1s.Add(c);
            }
            else if (c.ProvidesPhase(s.Phase2!.Value))
            {
                opt2s.Add(c);
            }
        }

        return new UnlockOptions(GetOffsetChoice(opt1s, skipChoices)?.Id, GetOffsetChoice(opt2s, skipChoices)?.Id);
    }
}
