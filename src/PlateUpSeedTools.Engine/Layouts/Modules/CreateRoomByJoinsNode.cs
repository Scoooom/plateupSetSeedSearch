using PlateUpSeedTools.Engine.Random;

namespace PlateUpSeedTools.Engine.Layouts.Modules;

/// <summary>Port of Kitchen.Layouts.Modules.CreateRoomByJoins.</summary>
public sealed class CreateRoomByJoinsNode : SingleInputLayoutModuleNode
{
    public required int StartX { get; init; }

    public required int StartY { get; init; }

    public required int Joins { get; init; }

    public required RoomType Type { get; init; }

    public required bool RequiredJoins { get; init; }

    protected override void ActOn(LayoutBlueprint blueprint, UnityRandom rng)
    {
        var original = blueprint[StartX, StartY];
        var room = new Room(original.Id, Type);
        blueprint.SetRoom(original, room);

        for (var i = 0; i < Joins; i++)
        {
            var candidates = blueprint.AdjacentRooms(room).Where(r => r.Type == RoomType.Unassigned).ToArray();
            if (candidates.Length == 0)
            {
                if (RequiredJoins)
                {
                    throw new LayoutFailureException("CreateRoomByJoins: Not enough rooms to join");
                }

                break;
            }

            var current = candidates[rng.Range(0, candidates.Length)];
            blueprint.SetRoom(current, room);
        }
    }
}
