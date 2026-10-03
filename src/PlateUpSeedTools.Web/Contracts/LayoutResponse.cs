namespace PlateUpSeedTools.Web.Contracts;

public sealed record LayoutResponse(
    string Seed,
    string SettingSlug,
    string SettingName,
    int LayoutProfileId,
    // The resolved named map size (e.g. "Diner") - null for a setting that forces its own bespoke
    // layout (ForceLayoutId is never one of the 5 shared-pool IDs LayoutSeed.TryGetSize knows about),
    // where size isn't a meaningful concept at all. See LayoutSeed.TryGetSize.
    string? MapSizeName,
    int Width,
    int Height,
    IReadOnlyList<TileDto> Tiles,
    IReadOnlyList<ApplianceDto> Appliances,
    IReadOnlyList<FeatureDto> Features,
    int TableApplianceId);

public readonly record struct TileDto(int X, int Y, string RoomType, int RoomId);

public readonly record struct ApplianceDto(int X, int Y, int ApplianceId, string ApplianceName, int RotationDegrees);

/// <summary>
/// A door/hatch/etc. on the wall segment between two adjacent tiles (Kitchen.Layouts.Features.Feature).
/// FrontDoor is the entrance/exit players spawn at and walk through each customer wave - the single
/// most speedrun-relevant coordinate in a layout, since it fixes where the run's pathing starts.
/// </summary>
public readonly record struct FeatureDto(int Tile1X, int Tile1Y, int Tile2X, int Tile2Y, string Type);
