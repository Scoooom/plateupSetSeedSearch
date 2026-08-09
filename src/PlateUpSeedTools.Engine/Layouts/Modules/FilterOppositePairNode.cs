using PlateUpSeedTools.Engine.Random;

namespace PlateUpSeedTools.Engine.Layouts.Modules;

/// <summary>Port of Kitchen.Layouts.Modules.FilterOppositePair.</summary>
public sealed class FilterOppositePairNode : SingleInputLayoutModuleNode
{
    protected override void ActOn(LayoutBlueprint blueprint, UnityRandom rng)
    {
        foreach (var item in blueprint.Features.OrderBy(_ => rng.Value).ToList())
        {
            foreach (var feature in blueprint.Features)
            {
                var id1 = blueprint[item.Tile1.X, item.Tile1.Y].Id;
                var id2 = blueprint[item.Tile2.X, item.Tile2.Y].Id;
                var id3 = blueprint[feature.Tile1.X, feature.Tile1.Y].Id;
                var id4 = blueprint[feature.Tile2.X, feature.Tile2.Y].Id;

                if (id1 != Room.Null.Id && id2 != Room.Null.Id && id3 != Room.Null.Id && id4 != Room.Null.Id
                    && (id1 != id3 || id2 != id4) && (id1 == id3 || id1 == id4 || id2 == id3 || id2 == id4)
                    && item.Tile2.X - item.Tile1.X == feature.Tile2.X - feature.Tile1.X
                    && item.Tile2.Y - item.Tile1.Y == feature.Tile2.Y - feature.Tile1.Y
                    && (item.Tile1.X == feature.Tile1.X || item.Tile1.Y == feature.Tile1.Y))
                {
                    blueprint.Features = [item, feature];
                    return;
                }
            }
        }
    }
}
