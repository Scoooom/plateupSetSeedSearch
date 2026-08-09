namespace PlateUpSeedTools.Engine.Layouts.Decorators;

public sealed record AppliancePlacement(LayoutPosition Position, int ApplianceId, int RotationDegrees);

/// <summary>
/// The subset of KitchenData.LayoutProfile/RestaurantSetting fields the decorators need. Appliance
/// references are plain int IDs here (matching AssetReference-style IDs) rather than the game's
/// GameDataObject references, since the Engine doesn't load full appliance definitions.
/// </summary>
public sealed class LayoutProfileInfo
{
    public required int TableApplianceId { get; init; }

    public required int CounterApplianceId { get; init; }

    public required int MaximumTables { get; init; }

    public required IReadOnlyList<int> RequiredApplianceIds { get; init; }
}
