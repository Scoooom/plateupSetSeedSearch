namespace PlateUpSeedTools.Engine.Data;

/// <summary>JSON shape for data/settings/&lt;slug&gt;/profile.json - mirrors KitchenData.LayoutProfile's fields.</summary>
public sealed class LayoutProfileData
{
    public required int Id { get; init; }

    public required int MaximumTables { get; init; }

    public required int TableApplianceId { get; init; }

    public required int CounterApplianceId { get; init; }

    public required List<int> RequiredApplianceIds { get; init; }
}
