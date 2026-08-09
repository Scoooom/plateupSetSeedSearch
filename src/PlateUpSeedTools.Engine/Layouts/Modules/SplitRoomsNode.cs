using PlateUpSeedTools.Engine.Random;

namespace PlateUpSeedTools.Engine.Layouts.Modules;

/// <summary>Port of Kitchen.Layouts.Modules.SplitRooms.</summary>
public sealed class SplitRoomsNode : SingleInputLayoutModuleNode
{
    public required int UniformX { get; init; }

    public required int UniformY { get; init; }

    public required int RandomX { get; init; }

    public required int RandomY { get; init; }

    protected override void ActOn(LayoutBlueprint blueprint, UnityRandom rng)
    {
        var bounds = blueprint.GetBounds();

        var x = bounds.MaxX;
        while (x >= bounds.MinX)
        {
            for (var i = 0; i < UniformX; i++)
            {
                LayoutSplitHelper.PerformSplit(blueprint, isRow: false, x);
            }

            x--;
        }

        var y = bounds.MaxY;
        while (y >= bounds.MinY)
        {
            for (var j = 0; j < UniformY; j++)
            {
                LayoutSplitHelper.PerformSplit(blueprint, isRow: true, y);
            }

            y--;
        }

        bounds = blueprint.GetBounds();
        for (var k = 0; k < RandomX; k++)
        {
            LayoutSplitHelper.PerformSplit(blueprint, isRow: true, rng.Range(bounds.MinY, bounds.MaxY + k + 1));
        }

        for (var l = 0; l < RandomY; l++)
        {
            LayoutSplitHelper.PerformSplit(blueprint, isRow: false, rng.Range(bounds.MinX, bounds.MaxX + l + 1));
        }
    }
}
