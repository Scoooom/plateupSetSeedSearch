using PlateUpSeedTools.Engine.Random;

namespace PlateUpSeedTools.Engine.Layouts.Modules;

/// <summary>Port of Kitchen.Layouts.Modules.SplitLine.</summary>
public sealed class SplitLineNode : SingleInputLayoutModuleNode
{
    public required int Position { get; init; }

    public required int Count { get; init; }

    public required bool IsRow { get; init; }

    protected override void ActOn(LayoutBlueprint blueprint, UnityRandom rng)
    {
        for (var i = 0; i <= Count; i++)
        {
            LayoutSplitHelper.PerformSplit(blueprint, IsRow, Position);
        }
    }
}
