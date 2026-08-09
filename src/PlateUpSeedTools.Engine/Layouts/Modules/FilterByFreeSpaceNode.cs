using PlateUpSeedTools.Engine.Random;

namespace PlateUpSeedTools.Engine.Layouts.Modules;

/// <summary>Port of Kitchen.Layouts.Modules.FilterByFreeSpace.</summary>
public sealed class FilterByFreeSpaceNode : SingleInputLayoutModuleNode
{
    protected override void ActOn(LayoutBlueprint blueprint, UnityRandom rng)
    {
        blueprint.Features.RemoveAll(f => !blueprint.IsTileFlatWall(f.Tile1) || !blueprint.IsTileFlatWall(f.Tile2));
    }
}
