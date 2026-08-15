namespace PlateUpSeedTools.Engine.Data;

/// <summary>
/// JSON shape for one entry in data/settings/cards/packs.json - a hand-curated (not generically
/// parsed) subset of the real UnlockPack graph, see CardExtractor.WritePacksFile's doc comment and
/// game-domain-knowledge.md's "Odin partial-field-read technique" note for why. Type is either
/// "composite" (KitchenData.CompositeUnlockPack, only SubPackIds set) or "modular"
/// (KitchenData.ModularUnlockPack, Selector/Condition/SorterPriority/Filter set).
/// </summary>
public sealed class UnlockPackData
{
    public required string Name { get; init; }

    public required string Type { get; init; }

    public List<int>? SubPackIds { get; init; }

    public SelectorData? Selector { get; init; }

    public ConditionData? Condition { get; init; }

    public SorterPriorityData? SorterPriority { get; init; }

    public FilterData? Filter { get; init; }
}

/// <summary>Port of UnlockSelectorGroupChoice/AnyFromGroup/MenuPhase/ForcedCard config - Kind picks which.</summary>
public sealed class SelectorData
{
    /// <summary>"groupChoice" | "anyFromGroup" | "menuPhase" | "forcedCard".</summary>
    public required string Kind { get; init; }

    public int? Group1 { get; init; }

    public int? Group2 { get; init; }

    public List<int>? Groups { get; init; }

    public int? Phase1 { get; init; }

    public int? Phase2 { get; init; }

    /// <summary>
    /// "forcedCard" only - the pack's Sets (KitchenData.UnlockSetFixed.Unlocks), a literal list of
    /// specific card IDs rather than a UnlockGroup-based candidate pool. Every other selector kind
    /// draws candidates from every Unlock (filtered by group membership at select time); this is the
    /// one case the port actually needs an explicit candidate SOURCE for - see
    /// UnlockPackEvaluator's doc comment on why Sets/IUnlockSet isn't modeled generically otherwise.
    /// </summary>
    public List<int>? FixedCardIds { get; init; }
}

/// <summary>Port of UnlockConditionRegular/Guarantee/Once config - Kind picks which.</summary>
public sealed class ConditionData
{
    /// <summary>"regular" | "guarantee" | "once".</summary>
    public required string Kind { get; init; }

    public int? DayInterval { get; init; }

    public int? DayOffset { get; init; }

    public int? DayMin { get; init; }

    public int? DayMax { get; init; }

    public int? TierRequired { get; init; }

    public int? MinDay { get; init; }

    public int? MinTier { get; init; }

    public int? GuaranteedGroup { get; init; }

    public int? Day { get; init; }
}

/// <summary>Port of UnlockSorterPriority's per-pack config.</summary>
public sealed class SorterPriorityData
{
    public required double Probability { get; init; }

    public required bool PrioritiseRequirements { get; init; }

    public required List<int> Groups { get; init; }

    public required List<int> DishTypes { get; init; }
}

/// <summary>Port of FilterBasic's Ignore*/Allow* flags.</summary>
public sealed class FilterData
{
    public required bool IgnoreUnlockability { get; init; }

    public required bool IgnoreFranchiseTier { get; init; }

    public required bool IgnoreDuplicateFilter { get; init; }

    public required bool IgnoreRequirements { get; init; }

    public required bool AllowBaseDishes { get; init; }

    public required bool IgnoreBlocksOtherFood { get; init; }

    public required bool IgnoreFilterByHasMain { get; init; }

    /// <summary>
    /// True: an IsSpecificFranchiseTier candidate's franchise-tier check is skipped entirely (never
    /// blocks on tier) - default false (unset) so the exact-equality check the fresh-draw path relies
    /// on is preserved for every pack deserialized from packs.json. Not `required` for exactly that
    /// reason: adding a required field here would break deserializing every existing pack's Filter
    /// blob, which never sets this. Only UnlockPackEvaluator's CarryoverFilter (hand-constructed in
    /// code, not JSON) sets this true - a repeatable franchise-carryover card's MinimumFranchiseTier
    /// is an occurrence index ("the Nth time you've taken this card"), not the franchise tier you
    /// were on when you took it, so it has no relationship to the declared tier to compare against -
    /// see IsValidCarryoverPick's doc comment for the full explanation.
    /// </summary>
    public bool SkipSpecificFranchiseTierCheck { get; init; }
}
