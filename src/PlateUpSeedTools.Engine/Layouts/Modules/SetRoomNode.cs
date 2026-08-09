using PlateUpSeedTools.Engine.Random;

namespace PlateUpSeedTools.Engine.Layouts.Modules;

/// <summary>Port of Kitchen.Layouts.Modules.SetRoom.</summary>
public sealed class SetRoomNode : SingleInputLayoutModuleNode
{
    public required int X { get; init; }

    public required int Y { get; init; }

    public required RoomType Type { get; init; }

    protected override void ActOn(LayoutBlueprint blueprint, UnityRandom rng)
    {
        blueprint[X, Y] = Room.Create(Type, rng);
    }
}
