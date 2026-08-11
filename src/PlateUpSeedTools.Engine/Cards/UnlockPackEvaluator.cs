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
    ///
    /// KNOWN GAP (see docs/project-history.md's "Known gaps" section, flagged urgent): santa-workshop's
    /// Christmas Pack has an undecoded sub-pack (Christmas Special Card) - this throws if the pack
    /// graph reaches it, same limitation CardDrawSimulator already has for that one setting.
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
        var candidates = repo.AllUnlocksOrderedById
            .Where(c => !ShouldBlockCard(repo, c, currentCards, request, filter))
            .ToList();

        var selector = pack.Selector ?? throw new InvalidOperationException($"Modular pack '{pack.Name}' has no Selector");
        foreach (var id in GetReachableIds(selector, candidates))
        {
            result.Add(id);
        }
    }

    // --- UnlockSelector*.GetOptions, membership-only variants (no RNG index pick) ---

    private static IEnumerable<int> GetReachableIds(SelectorData selector, List<UnlockData> candidates) => selector.Kind switch
    {
        "groupChoice" => ReachableGroupChoice(selector, candidates),
        "anyFromGroup" => candidates.Where(c => selector.Groups!.Contains(c.UnlockGroup)).Select(c => c.Id),
        "menuPhase" => candidates.Where(c => c.Kind == "Dish" && (c.ProvidesPhase(selector.Phase1!.Value) || c.ProvidesPhase(selector.Phase2!.Value))).Select(c => c.Id),
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
        var candidates = repo.AllUnlocksOrderedById
            .Where(c => !ShouldBlockCard(repo, c, currentCards, request, filter))
            .ToList();

        // Sorters: always [Shuffle, Priority] for every currently-decoded pack - unconditional, runs
        // even if the pack's condition below turns out false (see game-domain-knowledge.md's
        // "shuffle-then-discard on a non-match night" fidelity note).
        candidates = candidates.OrderBy(_ => rng.Value).ToList();
        var sorterPriority = pack.SorterPriority ?? throw new InvalidOperationException($"Modular pack '{pack.Name}' has no SorterPriority");
        var isPriority = rng.Value < sorterPriority.Probability;
        candidates = candidates.OrderByDescending(c => isPriority && IsPriority(c, sorterPriority) ? 1 : 0).ToList();

        var condition = pack.Condition ?? throw new InvalidOperationException($"Modular pack '{pack.Name}' has no Condition");
        var selector = pack.Selector ?? throw new InvalidOperationException($"Modular pack '{pack.Name}' has no Selector");
        if (!ShouldProvide(repo, condition, currentCards, request))
        {
            return default;
        }

        return SelectOptions(selector, candidates, skipChoices);
    }

    /// <summary>
    /// Whether <paramref name="candidate"/> is a legal pick for the given <paramref name="filter"/>
    /// given the cards already owned/selected so far - reuses <see cref="ShouldBlockCard"/> (the
    /// single source of truth for unlockability/franchise-tier/duplicate/Requires/BlockedBy
    /// eligibility) rather than reimplementing it. Intended for validating a manually-chosen card
    /// (e.g. a franchise-carryover pick in the search UI), not a simulated draw - Day is irrelevant
    /// to every check ShouldBlockCard performs (only Tier is consulted), so 0 is a safe placeholder.
    /// </summary>
    public static bool IsEligible(GameDataRepository repo, UnlockData candidate, HashSet<int> currentCards, int tier, FilterData filter) =>
        !ShouldBlockCard(repo, candidate, currentCards, new UnlockRequest(Day: 0, tier), filter);

    // --- FilterBasic.ShouldBlockCard ---

    private static bool ShouldBlockCard(GameDataRepository repo, UnlockData candidate, HashSet<int> currentCards, UnlockRequest request, FilterData filter)
    {
        if (!filter.IgnoreUnlockability && !candidate.IsUnlockable)
        {
            return true;
        }

        var franchiseBlock = !filter.IgnoreFranchiseTier && candidate.IsSpecificFranchiseTier
            ? candidate.MinimumFranchiseTier != request.Tier
            : candidate.MinimumFranchiseTier > request.Tier;
        if (franchiseBlock)
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
