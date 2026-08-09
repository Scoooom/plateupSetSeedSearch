using PlateUpSeedTools.Engine.Random;

namespace PlateUpSeedTools.Engine.Layouts.Modules;

/// <summary>Port of Kitchen.Layouts.Modules.FilterByRoom.</summary>
public sealed class FilterByRoomNode : SingleInputLayoutModuleNode
{
    public required bool RemoveMode { get; init; }

    public required RoomType Type1 { get; init; }

    public required bool FilterSecond { get; init; }

    public required RoomType Type2 { get; init; }

    protected override void ActOn(LayoutBlueprint blueprint, UnityRandom rng)
    {
        blueprint.Features.RemoveAll(f => RemoveMode != (
            (blueprint[f.Tile1].Type != Type1 || (blueprint[f.Tile2].Type != Type2 && FilterSecond))
            && ((blueprint[f.Tile1].Type != Type2 && FilterSecond) || blueprint[f.Tile2].Type != Type1)));
    }
}
