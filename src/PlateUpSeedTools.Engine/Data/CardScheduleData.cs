namespace PlateUpSeedTools.Engine.Data;

/// <summary>
/// JSON shape for one entry in data/settings/cards/card-schedules.json - a selectable card-draw
/// pack, independent of restaurant setting/layout (see GameDataRepository.ResolveCardSchedulePackId's
/// doc comment for why this is a separate axis: several settings folded into the "normal" synthetic
/// setting during the layout-only consolidation turned out to have their own distinct card pack).
/// </summary>
public sealed class CardScheduleData
{
    public required string Slug { get; init; }

    public required string DisplayName { get; init; }

    public required int PackId { get; init; }
}
