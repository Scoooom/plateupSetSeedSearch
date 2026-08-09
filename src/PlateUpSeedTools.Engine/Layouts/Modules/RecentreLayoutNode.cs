using PlateUpSeedTools.Engine.Random;

namespace PlateUpSeedTools.Engine.Layouts.Modules;

/// <summary>Port of Kitchen.Layouts.Modules.RecentreLayout.</summary>
public sealed class RecentreLayoutNode : SingleInputLayoutModuleNode
{
    protected override void ActOn(LayoutBlueprint blueprint, UnityRandom rng)
    {
        if (blueprint.Tiles.Count == 0)
        {
            return;
        }

        var minX = blueprint.Tiles.Keys.Min(k => k.X);
        var minY = blueprint.Tiles.Keys.Min(k => k.Y);
        var maxX = blueprint.Tiles.Keys.Max(k => k.X);
        var maxY = blueprint.Tiles.Keys.Max(k => k.Y);

        var newMinX = (int)Math.Floor(-(maxX - minX) / 2.0);
        var newMinY = (int)Math.Floor(-(maxY - minY) / 2.0);

        LayoutPosition Translate(LayoutPosition p) => new(p.X - minX + newMinX, p.Y - minY + newMinY);

        blueprint.Tiles = blueprint.Tiles.ToDictionary(r => Translate(r.Key), r => r.Value);
        blueprint.Features = blueprint.Features.Select(f => new Feature(Translate(f.Tile1), Translate(f.Tile2), f.Type)).ToList();
    }
}
