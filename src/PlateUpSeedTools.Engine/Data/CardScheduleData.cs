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

    /// <summary>
    /// The bonus starting card the schedule's *original* setting grants (RestaurantSettingData's own
    /// StartingUnlockId equivalent), or null if it doesn't grant one. Needed because the session-6
    /// layout-only consolidation flattened every folded-in setting's StartingUnlockId to null on the
    /// synthetic "normal" setting - a card schedule selection changes which pack drives draws but,
    /// without this field, silently lost the setting's own granted card too (e.g. Autumn's "Community",
    /// unlock id 319209671 - ground-truth confirmed 2026-09-23, a real franchise-continuation capture's
    /// day-0 owned set included it and the port had no way to reproduce it).
    /// </summary>
    public int? StartingUnlockId { get; init; }
}
