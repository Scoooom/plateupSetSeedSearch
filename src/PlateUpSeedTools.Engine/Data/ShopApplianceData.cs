namespace PlateUpSeedTools.Engine.Data;

/// <summary>
/// JSON shape for one entry in data/settings/shop/appliances.json - mirrors the KitchenData.Appliance
/// fields the shop/blueprint-offer simulator needs. See game-domain-knowledge.md's "Shop / appliance
/// purchase RNG" section for the mechanics these fields drive.
///
/// Deliberately named/rooted distinctly from data/settings/appliances.json (loaded separately, see
/// GameDataRepository.GetApplianceName) - that file is a small id-&gt;name lookup for the layout
/// preview's icons, unrelated purpose, do not conflate the two.
/// </summary>
public sealed class ShopApplianceData
{
    public required int Id { get; init; }

    public required string Name { get; init; }

    public required int PurchaseCost { get; init; }

    public required bool IsPurchasable { get; init; }

    public required bool IsPurchasableAsUpgrade { get; init; }

    /// <summary>Season enum value (0 = Normal) - real-world/wall-clock driven in the actual game, not seed-derived; the shop simulator defaults candidate-building to Normal only.</summary>
    public required int RestrictedToSeason { get; init; }

    /// <summary>DecorationType enum value (0 = None) - gates FilterByTheme, stubbed in v1 of the simulator.</summary>
    public required int ThemeRequired { get; init; }

    /// <summary>ShoppingTags [Flags] bitmask - see Engine.Shop.ShoppingTags for the value table.</summary>
    public required int ShoppingTags { get; init; }

    /// <summary>ShopRequirementFilter enum value (0 = None, 1 = RefreshableProvider) - gates FilterByRequirements, stubbed in v1 of the simulator.</summary>
    public required int ShopRequirementFilter { get; init; }

    public required List<int> RequiresForShopIds { get; init; }

    public required List<int> RequiresProcessForShopIds { get; init; }

    public required List<int> RequiresIngredientForShopIds { get; init; }

    public required List<int> RequiresPhaseForShopIds { get; init; }

    /// <summary>True if TagStapleWhenMissing should force this into the offer pool whenever the player has none currently placed - the mechanic Research Desk's real strategy depends on.</summary>
    public required bool StapleWhenMissing { get; init; }

    public required bool SellOnlyAsDuplicate { get; init; }

    public required bool SellOnlyAsUnique { get; init; }

    public required bool SellOnlyIfPlatesNeeded { get; init; }

    public required bool PreventSale { get; init; }
}
