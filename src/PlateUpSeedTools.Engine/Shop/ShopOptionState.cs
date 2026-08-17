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
