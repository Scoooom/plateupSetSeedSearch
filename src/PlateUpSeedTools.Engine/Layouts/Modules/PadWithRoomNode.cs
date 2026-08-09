using PlateUpSeedTools.Engine.Random;

namespace PlateUpSeedTools.Engine.Layouts.Modules;

/// <summary>Port of Kitchen.Layouts.Modules.PadWithRoom.</summary>
public sealed class PadWithRoomNode : SingleInputLayoutModuleNode
{
    public required RoomType Type { get; init; }

    public required int Above { get; init; }

    public required int Left { get; init; }

    public required int Right { get; init; }

    public required int Below { get; init; }

    protected override void ActOn(LayoutBlueprint blueprint, UnityRandom rng)
    {
        var bounds = blueprint.GetBounds();
        var padRoom = Room.Create(Type, rng);
        var tiles = new Dictionary<LayoutPosition, Room>();
        for (var x = bounds.MinX - Left; x <= bounds.MaxX + Right; x++)
        {
            for (var y = bounds.MinY - Below; y <= bounds.MaxY + Above; y++)
            {
                var pos = new LayoutPosition(x, y);
                tiles[pos] = x >= bounds.MinX && x <= bounds.MaxX && y >= bounds.MinY && y <= bounds.MaxY
                    ? blueprint[pos]
                    : padRoom;
            }
        }

        blueprint.Tiles = tiles;
    }
}
