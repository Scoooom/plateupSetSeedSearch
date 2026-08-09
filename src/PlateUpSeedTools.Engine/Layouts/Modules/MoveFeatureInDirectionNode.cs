using PlateUpSeedTools.Engine.Random;

namespace PlateUpSeedTools.Engine.Layouts.Modules;

/// <summary>Port of Kitchen.Layouts.Modules.MoveFeatureInDirection.</summary>
public sealed class MoveFeatureInDirectionNode : SingleInputLayoutModuleNode
{
    public required int OffsetX { get; init; }

    public required int OffsetY { get; init; }

    public required int MaxSteps { get; init; }

    protected override void ActOn(LayoutBlueprint blueprint, UnityRandom rng)
    {
        var offset = new LayoutPosition(OffsetX, OffsetY);
        foreach (var feature in blueprint.Features)
        {
            var tile1 = feature.Tile1;
            var tile2 = feature.Tile2;
            var id1 = blueprint[feature.Tile1].Id;
            var id2 = blueprint[feature.Tile2].Id;

            for (var i = 0; i < MaxSteps; i++)
            {
                tile1 += offset;
                tile2 += offset;
                if (blueprint[tile1].Id != id1 || blueprint[tile2].Id != id2)
                {
                    break;
                }

                feature.Tile1 = tile1;
                feature.Tile2 = tile2;
            }
        }
    }
}
