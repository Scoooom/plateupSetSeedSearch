using PlateUpSeedTools.Engine.Random;

namespace PlateUpSeedTools.Engine.Layouts.Modules;

/// <summary>Port of Kitchen.Layouts.Modules.MergeRoomsByType.</summary>
public sealed class MergeRoomsByTypeNode : SingleInputLayoutModuleNode
{
    protected override void ActOn(LayoutBlueprint blueprint, UnityRandom rng)
    {
        foreach (var room in blueprint.Rooms())
        {
            foreach (var neighbour in blueprint.AdjacentRooms(room))
            {
                if (neighbour.Type == room.Type)
                {
                    blueprint.SetRoom(neighbour, room);
                }
            }
        }
    }
}
