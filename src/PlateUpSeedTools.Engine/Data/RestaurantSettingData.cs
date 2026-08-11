namespace PlateUpSeedTools.Engine.Data;

/// <summary>JSON shape for data/settings/&lt;slug&gt;/setting.json - mirrors the fields the layout previewer needs from KitchenData.RestaurantSetting.</summary>
public sealed class RestaurantSettingData
{
    public required string Slug { get; init; }

    public required string DisplayName { get; init; }

    public required int Id { get; init; }

    /// <summary>Null means: pick a layout profile at seed-random from AssetReference.FixedRunLayout (see LayoutSeed).</summary>
    public int? ForceLayoutId { get; init; }

    /// <summary>Null means: falls back to AssetReference.DefaultUnlockPack (Default Composite Pack) - see game-domain-knowledge.md's "Card draw RNG" section.</summary>
    public int? UnlockPackId { get; init; }

    /// <summary>The setting's bonus starting card (e.g. Banquet Hall's "Banquet Dining"), granted immediately at run start - null if none.</summary>
    public int? StartingUnlockId { get; init; }

    /// <summary>Forces the starting dish with no player choice - null means the player picks from the office pedestal (not modeled here, the search feature takes the dish as direct input instead).</summary>
    public int? FixedDishId { get; init; }
}

/// <summary>JSON shape for data/settings/index.json.</summary>
public sealed record SettingIndexEntry(string Slug, string DisplayName, int Id);
