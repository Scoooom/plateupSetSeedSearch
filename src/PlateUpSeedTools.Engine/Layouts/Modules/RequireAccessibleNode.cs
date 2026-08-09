using PlateUpSeedTools.Engine.Random;

namespace PlateUpSeedTools.Engine.Layouts.Modules;

/// <summary>Port of Kitchen.Layouts.Modules.RequireAccessible (a LayoutCondition subclass).</summary>
public sealed class RequireAccessibleNode : SingleInputLayoutModuleNode
{
    public required bool AllowGardens { get; init; }

    protected override void ActOn(LayoutBlueprint blueprint, UnityRandom rng)
    {
        var frontDoors = blueprint.Features
            .Where(f => f.Type == FeatureType.FrontDoor && (blueprint[f.Tile1].Type == RoomType.NoRoom || blueprint[f.Tile2].Type == RoomType.NoRoom))
            .ToList();

        if (frontDoors.Count == 0)
        {
            throw new LayoutFailureException("No front door");
        }

        var accessibleRooms = new HashSet<Room>();

        void Process(Room room)
        {
            if (!AllowGardens && room.Type == RoomType.Garden)
            {
                return;
            }

            accessibleRooms.Add(room);
            foreach (var connected in blueprint.ConnectedRooms(room))
            {
                if (!accessibleRooms.Contains(connected))
                {
                    Process(connected);
                }
            }
        }

        foreach (var feature in frontDoors)
        {
            if (blueprint[feature.Tile1].Type != RoomType.NoRoom)
            {
                Process(blueprint[feature.Tile1]);
            }

            if (blueprint[feature.Tile2].Type != RoomType.NoRoom)
            {
                Process(blueprint[feature.Tile2]);
            }
        }

        foreach (var room in blueprint.Rooms())
        {
            if (room.Type == RoomType.NoRoom || (!AllowGardens && room.Type == RoomType.Garden))
            {
                continue;
            }

            if (!accessibleRooms.Contains(room))
            {
                throw new LayoutFailureException($"Unable to find path to {room.Type} ({room.Id})");
            }

            if (IsDisjoint(blueprint.TilesOfRoom(room)))
            {
                throw new LayoutFailureException($"Room {room.Type} ({room.Id}) is disjoint");
            }
        }
    }

    private static bool IsDisjoint(HashSet<LayoutPosition> tiles)
    {
        if (tiles.Count == 0)
        {
            return false;
        }

        var visited = new HashSet<LayoutPosition>();
        var stack = new Stack<LayoutPosition>();
        stack.Push(tiles.First());

        while (stack.Count != 0)
        {
            var pos = stack.Pop();
            foreach (var direction in LayoutHelpers.Directions)
            {
                var neighbour = pos + direction;
                if (tiles.Contains(neighbour) && visited.Add(neighbour))
                {
                    stack.Push(neighbour);
                }
            }
        }

        return visited.Count != tiles.Count;
    }
}
