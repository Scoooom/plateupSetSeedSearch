namespace PlateUpSeedTools.Engine.Layouts;

/// <summary>Port of Kitchen.Layouts.LayoutHelpers.</summary>
public static class LayoutHelpers
{
    public static readonly IReadOnlyList<LayoutPosition> Directions =
    [
        new LayoutPosition(0, 1),
        new LayoutPosition(0, -1),
        new LayoutPosition(-1, 0),
        new LayoutPosition(1, 0),
    ];

    public static readonly IReadOnlyList<LayoutPosition> AllNearby =
    [
        new LayoutPosition(1, 0),
        new LayoutPosition(0, 1),
        new LayoutPosition(-1, 0),
        new LayoutPosition(0, -1),
        new LayoutPosition(1, 1),
        new LayoutPosition(-1, 1),
        new LayoutPosition(1, -1),
        new LayoutPosition(-1, -1),
    ];

    public static bool IsInside(RoomType type, bool allowGarden = false) => type switch
    {
        RoomType.NoRoom => false,
        RoomType.Garden => allowGarden,
        RoomType.FrontEntrance => false,
        _ => true,
    };

    public static bool IsInside(Room room) => IsInside(room.Type);
}
