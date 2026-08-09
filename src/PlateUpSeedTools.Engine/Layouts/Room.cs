using PlateUpSeedTools.Engine.Random;

namespace PlateUpSeedTools.Engine.Layouts;

/// <summary>
/// Port of Kitchen.Layouts.Room. The original draws its ID from the ambient global
/// UnityEngine.Random when constructed (Room.New / new Room(RoomType)) - here that's an explicit
/// UnityRandom parameter instead (see UnityRandom's doc comment for why), so every place that
/// creates a "new random room" in the original must pass the SAME shared rng instance used for the
/// rest of that layout-build pass, in the same call order, to stay bit-exact.
/// </summary>
public readonly struct Room
{
    public int Id { get; }

    public RoomType Type { get; }

    public Room(int id, RoomType type)
    {
        Id = id;
        Type = type;
    }

    public static Room New(UnityRandom rng) => new(rng.Range(int.MinValue, int.MaxValue), RoomType.Unassigned);

    public static Room Create(RoomType type, UnityRandom rng) => new(rng.Range(int.MinValue, int.MaxValue), type);

    public static Room Null { get; } = new(0, RoomType.NoRoom);
}
