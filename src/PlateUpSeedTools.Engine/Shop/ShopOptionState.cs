namespace PlateUpSeedTools.Engine.Shop;

/// <summary>
/// Port of Kitchen.ShopBuilder.CShopBuilderOption - one candidate appliance for a given day's shop
/// rebuild. Staple/IsRemoved are mutated in place by ShopOptionEvaluator's filter pipeline, mirroring
/// how the real ECS component is mutated in place by each ShopBuilderFilter system.
/// </summary>
public sealed class ShopOptionState
{
    public required int ApplianceId { get; init; }

    public required ShoppingTags Tags { get; init; }

    public required bool SellAsUpgrade { get; init; }

    public required bool StapleWhenMissing { get; init; }

    public required bool SellOnlyAsDuplicate { get; init; }

    public required bool SellOnlyAsUnique { get; init; }

    public required IReadOnlyList<int> RequiresForShopIds { get; init; }

    /// <summary>Step 5 input (FilterByRequiredCourse) - gates Plates/Dish Rack/Enchanted Plates on whether today's menu needs a plate. Fully implemented.</summary>
    public required bool SellOnlyIfPlatesNeeded { get; init; }

    /// <summary>Step 5 input (FilterByRequiredCourse) - MenuPhase values this option requires be on the menu. Fully implemented (currently 0/451 appliances use it, so this path is dormant but correct).</summary>
    public required IReadOnlyList<int> RequiresPhaseForShopIds { get; init; }

    /// <summary>
    /// Step 6 input (FilterByRequiredProcessOrIngredient) - NOT YET WIRED: the real game's dynamic side
    /// (which processes a dish provides, Dish.RequiredProcesses/BeneficialIngredients) is a HashSet&lt;T&gt;
    /// field Unity doesn't natively serialize, confirmed Odin-serialized (zero plain-text hits for
    /// "RequiredProcesses" across every .asset in PlateUpSourceFiles) - same class of problem as
    /// packs.json's hand-curated Odin decode, not solvable by just reading this field. Carried here so a
    /// future decode only needs to populate the dynamic side, not touch this pipeline again.
    /// </summary>
    public required IReadOnlyList<int> RequiresProcessForShopIds { get; init; }

    /// <summary>Step 6 input - same Odin-blocked status as <see cref="RequiresProcessForShopIds"/>.</summary>
    public required IReadOnlyList<int> RequiresIngredientForShopIds { get; init; }

    /// <summary>
    /// Step 7 input (FilterByRequirements) - NOT YET WIRED: the dynamic side (whether a placed appliance
    /// "AllowRefreshes", Kitchen.CItemProvider) is a runtime ECS component, confirmed not present as
    /// plain text in any .asset (zero hits for "AllowRefreshes") - likely an Odin-serialized per-appliance
    /// property blob. Only 1/451 appliances (Supplies) uses this, so low priority to unblock.
    /// </summary>
    public required int ShopRequirementFilter { get; init; }

    /// <summary>
    /// Step 9 input (FilterByTheme) - NOT YET WIRED: the dynamic side (which owned unlocks grant which
    /// theme, KitchenData.ThemeAddEffect.AddsTheme) lives inside an Unlock's Odin-serialized Effects
    /// list, confirmed not present as plain text in any .asset (zero hits for "AddsTheme") - same class
    /// of problem as ShopEffect.AddStaple (step 3), needs dedicated decode work, not a quick field read.
    /// </summary>
    public required int ThemeRequired { get; init; }

    public bool IsRemoved { get; set; }

    public ShopStapleType Staple { get; set; } = ShopStapleType.NonStaple;

    /// <summary>Port of CShopBuilderOption.TakesStapleSlot - byte-verified.</summary>
    public bool TakesStapleSlot => !IsRemoved && Staple != ShopStapleType.NonStaple;

    /// <summary>Port of CShopBuilderOption.MatchesRequestTags - byte-verified against source.</summary>
    public bool MatchesRequestTags(ShoppingTags requestTags)
    {
        if ((requestTags & ShoppingTags.Basic) != 0)
        {
            return Staple != ShopStapleType.NonStaple;
        }

        if ((requestTags & Tags) != 0)
        {
            return Staple == ShopStapleType.NonStaple;
        }

        return false;
    }
}
