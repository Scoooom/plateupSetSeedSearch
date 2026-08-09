namespace PlateUpSeedTools.Engine.Layouts.Modules;

/// <summary>
/// Shared row/column duplication logic used by RandomExpand, SplitLine, and SplitRooms - all three
/// port the exact same PerformSplit local function from the original decompiled source.
/// </summary>
internal static class LayoutSplitHelper
{
    public static void PerformSplit(LayoutBlueprint blueprint, bool isRow, int index)
    {
        LayoutPosition Remap(LayoutPosition prev)
        {
            if (isRow && prev.Y >= index)
            {
                return new LayoutPosition(prev.X, prev.Y + 1);
            }

            if (!isRow && prev.X >= index)
            {
                return new LayoutPosition(prev.X + 1, prev.Y);
            }

            return prev;
        }

        var remapped = new Dictionary<LayoutPosition, Room>();
        foreach (var tile in blueprint.Tiles)
        {
            remapped[Remap(tile.Key)] = tile.Value;
            if (isRow && tile.Key.Y == index)
            {
                remapped[tile.Key] = tile.Value;
            }

            if (!isRow && tile.Key.X == index)
            {
                remapped[tile.Key] = tile.Value;
            }
        }

        blueprint.Tiles = remapped;
    }
}
