namespace PlateUpSeedTools.Engine.Data;

/// <summary>JSON shape for data/settings/&lt;slug&gt;/setting.json - mirrors the fields the layout previewer needs from KitchenData.RestaurantSetting.</summary>
public sealed class RestaurantSettingData
{
    public required string Slug { get; init; }

    public required string DisplayName { get; init; }

    public required int Id { get; init; }

    /// <summary>Null means: pick a layout profile at seed-random from AssetReference.FixedRunLayout (see LayoutSeed).</summary>
    public int? ForceLayoutId { get; init; }
}

/// <summary>JSON shape for data/settings/index.json.</summary>
public sealed record SettingIndexEntry(string Slug, string DisplayName, int Id);
