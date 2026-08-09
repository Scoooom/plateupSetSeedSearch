using PlateUpSeedTools.Engine.Random;

namespace PlateUpSeedTools.Engine.Layouts.Modules;

/// <summary>Port of Kitchen.Layouts.Modules.FindAllFeatures ("Find All Connections" in the editor).</summary>
public sealed class FindAllFeaturesNode : SingleInputLayoutModuleNode
{
    public required FeatureType Feature { get; init; }

    protected override void ActOn(LayoutBlueprint blueprint, UnityRandom rng)
    {
        var found = blueprint.Tiles
            .SelectMany(t => LayoutHelpers.Directions
                .Select(d => new LayoutPosition(d.X + t.Key.X, d.Y + t.Key.Y))
                .Where(d => blueprint[d].Id != t.Value.Id)
                .Select(d => new Feature(t.Key, d, FeatureType.Generic)))
            .OrderBy(f => (f.Tile1.X * 1000) + f.Tile2.Y)
            .ToList();

        foreach (var item in found)
        {
            blueprint.Features.Add(new Feature(item.Tile1, item.Tile2, Feature));
        }
    }
}
