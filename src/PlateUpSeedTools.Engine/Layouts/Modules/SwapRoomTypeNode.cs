using PlateUpSeedTools.Engine.Random;

namespace PlateUpSeedTools.Engine.Layouts.Modules;

/// <summary>Port of Kitchen.Layouts.Modules.SwapRoomType.</summary>
public sealed class SwapRoomTypeNode : SingleInputLayoutModuleNode
{
    public required int X { get; init; }

    public required int Y { get; init; }

    public required RoomType Type { get; init; }

    protected override void ActOn(LayoutBlueprint blueprint, UnityRandom rng)
    {
        var room = blueprint[X, Y];
        var replacement = Room.Create(Type, rng);
        foreach (var item in blueprint.Tiles.ToList())
        {
            if (item.Value.Id == room.Id)
            {
                blueprint[item.Key] = replacement;
            }
        }
    }
}
