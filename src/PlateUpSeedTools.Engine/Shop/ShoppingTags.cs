namespace PlateUpSeedTools.Engine.Shop;

/// <summary>Port of KitchenData.ShoppingTags - byte-verified against source.</summary>
[Flags]
public enum ShoppingTags
{
    None = 0,
    Basic = 1,
    Decoration = 2,
    Technology = 4,
    FrontOfHouse = 8,
    Plumbing = 0x10,
    Cooking = 0x20,
    Automation = 0x40,
    Christmas = 0x80,
    Misc = 0x100,
    Office = 0x200,
    BlueprintUpgrader = 0x400,
    BlueprintStore = 0x800,
    Halloween = 0x1000,
    SpecialEvent = 0x2000,
}

/// <summary>Port of KitchenData.ShoppingTagsExtensions - a fixed game constant (not seed/setting-dependent), used to request the "random" (non-staple) shop slots.</summary>
public static class ShoppingTagsExtensions
{
    public const ShoppingTags DefaultShoppingTag =
        ShoppingTags.Technology | ShoppingTags.FrontOfHouse | ShoppingTags.Plumbing |
        ShoppingTags.Cooking | ShoppingTags.Automation | ShoppingTags.Misc | ShoppingTags.Office;
}
