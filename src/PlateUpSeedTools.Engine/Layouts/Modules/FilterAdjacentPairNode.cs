using PlateUpSeedTools.Engine.Random;

namespace PlateUpSeedTools.Engine.Layouts.Modules;

/// <summary>Port of Kitchen.Layouts.Modules.FilterAdjacentPair.</summary>
public sealed class FilterAdjacentPairNode : SingleInputLayoutModuleNode
{
    protected override void ActOn(LayoutBlueprint blueprint, UnityRandom rng)
    {
        foreach (var f1 in blueprint.Features.OrderBy(_ => rng.Value).ToList())
        {
            foreach (var f2 in blueprint.Features)
            {
                var adjacent = LayoutHelpers.Directions.Any(d =>
                    (f2.Tile1 == f1.Tile1 + d && f2.Tile2 == f1.Tile2 + d) ||
                    (f2.Tile2 == f1.Tile1 + d && f2.Tile1 == f1.Tile2 + d));

                if (adjacent)
                {
                    blueprint.Features = [f1, f2];
                    return;
                }
            }
        }
    }
}
