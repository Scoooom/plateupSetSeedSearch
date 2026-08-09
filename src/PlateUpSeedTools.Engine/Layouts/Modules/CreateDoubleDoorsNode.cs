using PlateUpSeedTools.Engine.Random;

namespace PlateUpSeedTools.Engine.Layouts.Modules;

/// <summary>Port of Kitchen.Layouts.Modules.CreateDoubleDoors.</summary>
public sealed class CreateDoubleDoorsNode : SingleInputLayoutModuleNode
{
    public required int OffsetY { get; init; }

    protected override void ActOn(LayoutBlueprint blueprint, UnityRandom rng)
    {
        if (blueprint.Tiles.Count == 0)
        {
            return;
        }

        foreach (var feature in blueprint.Features)
        {
            if (feature.Type != FeatureType.Door || feature.Tile1.Y != OffsetY)
            {
                continue;
            }

            foreach (var feature2 in blueprint.Features)
            {
                var sameColumn = (feature.Tile1.X == feature2.Tile1.X && feature.Tile2.X == feature2.Tile2.X)
                    || (feature.Tile2.X == feature2.Tile1.X && feature.Tile1.X == feature2.Tile2.X);
                if (sameColumn && Math.Abs(feature.Tile1.Y - feature2.Tile2.Y) <= 1)
                {
                    blueprint.Features =
                    [
                        new Feature(feature.Tile1, feature.Tile2, FeatureType.Door),
                        new Feature(feature2.Tile1, feature2.Tile2, FeatureType.DoorReversed),
                    ];
                    return;
                }
            }
        }
    }
}
