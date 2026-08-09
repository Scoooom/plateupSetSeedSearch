using PlateUpSeedTools.Engine.Random;

namespace PlateUpSeedTools.Engine.Layouts.Modules;

/// <summary>Port of Kitchen.Layouts.Modules.InsertRandomRoom.</summary>
public sealed class InsertRandomRoomNode : SingleInputLayoutModuleNode
{
    public required RoomType Type { get; init; }

    protected override void ActOn(LayoutBlueprint blueprint, UnityRandom rng)
    {
        if (blueprint.Tiles.Count == 0)
        {
            return;
        }

        var positions = blueprint.Tiles.Keys.OrderBy(t => (t.X * 1000) + t.Y).ToList();
        var pos = positions[rng.Range(0, positions.Count)];
        blueprint[pos] = Room.Create(Type, rng);
    }
}
