using PlateUpSeedTools.Engine.Random;

namespace PlateUpSeedTools.Engine.Layouts.Modules;

/// <summary>Port of Kitchen.Layouts.Modules.RandomExpand.</summary>
public sealed class RandomExpandNode : SingleInputLayoutModuleNode
{
    public required int MinX { get; init; }

    public required int MinY { get; init; }

    public required int MaxX { get; init; }

    public required int MaxY { get; init; }

    protected override void ActOn(LayoutBlueprint blueprint, UnityRandom rng)
    {
        var bounds = blueprint.GetBounds();
        var rowSplits = rng.Range(MinX, MaxX + 1);
        var colSplits = rng.Range(MinY, MaxY + 1);

        for (var i = 0; i < rowSplits; i++)
        {
            LayoutSplitHelper.PerformSplit(blueprint, isRow: true, rng.Range(bounds.MinY, bounds.MaxY + i + 1));
        }

        for (var j = 0; j < colSplits; j++)
        {
            LayoutSplitHelper.PerformSplit(blueprint, isRow: false, rng.Range(bounds.MinX, bounds.MaxX + j + 1));
        }
    }
}
