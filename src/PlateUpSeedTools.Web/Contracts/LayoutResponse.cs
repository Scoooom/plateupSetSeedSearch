namespace PlateUpSeedTools.Web.Contracts;

public sealed record LayoutResponse(
    string Seed,
    string SettingSlug,
    string SettingName,
    int LayoutProfileId,
    int Width,
    int Height,
    IReadOnlyList<TileDto> Tiles,
    IReadOnlyList<ApplianceDto> Appliances);

public readonly record struct TileDto(int X, int Y, string RoomType);

public readonly record struct ApplianceDto(int X, int Y, int ApplianceId, int RotationDegrees);
