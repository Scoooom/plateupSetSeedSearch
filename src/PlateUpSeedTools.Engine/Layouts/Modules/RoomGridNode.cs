using PlateUpSeedTools.Engine.Random;

namespace PlateUpSeedTools.Engine.Layouts.Modules;

/// <summary>Port of Kitchen.Layouts.Modules.RoomGrid - the graph's root node (no input).</summary>
public sealed class RoomGridNode : LayoutModuleNode
{
    public required int Width { get; init; }

    public required int Height { get; init; }

    public required RoomType Type { get; init; }

    public required bool SetType { get; init; }

    public override IEnumerable<string> ParentIds => [];

    public override LayoutBlueprint Execute(IReadOnlyDictionary<string, LayoutBlueprint> results, UnityRandom rng)
    {
        var blueprint = LayoutBlueprint.New(rng);
        var fixedRoom = SetType ? Room.Create(Type, rng) : default;
        for (var x = 0; x < Width; x++)
        {
            for (var y = 0; y < Height; y++)
            {
                blueprint[x, y] = SetType ? fixedRoom : Room.New(rng);
            }
        }

        return blueprint;
    }
}
