using PlateUpSeedTools.Engine.Random;

namespace PlateUpSeedTools.Engine.Layouts.Modules;

/// <summary>Port of Kitchen.Layouts.Modules.Mirror.</summary>
public sealed class MirrorNode : SingleInputLayoutModuleNode
{
    public required bool IsVertical { get; init; }

    protected override void ActOn(LayoutBlueprint blueprint, UnityRandom rng)
    {
        var bounds = blueprint.GetBounds();
        var max = IsVertical ? bounds.MaxY : bounds.MaxX;

        LayoutPosition Mirror(LayoutPosition p) => IsVertical
            ? new LayoutPosition(p.X, max + (max + 1 - p.Y))
            : new LayoutPosition(max + (max + 1 - p.X), p.Y);

        foreach (var item in blueprint.Tiles.ToList())
        {
            blueprint[Mirror(item.Key)] = item.Value;
        }

        for (var i = blueprint.Features.Count - 1; i >= 0; i--)
        {
            var feature = blueprint.Features[i];
            if (feature.Type != FeatureType.FrontDoor)
            {
                blueprint.Features.Add(new Feature(Mirror(feature.Tile1), Mirror(feature.Tile2), feature.Type));
            }
        }
    }
}
