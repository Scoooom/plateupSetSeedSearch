using PlateUpSeedTools.Engine.Random;

namespace PlateUpSeedTools.Engine.Layouts;

/// <summary>
/// Port of Kitchen.Layouts.LayoutBlueprint - the room/tile grid plus door/hatch/etc. features.
/// Only the members actually used by the currently-ported modules/decorators are included; port
/// more from the original (DecompiledSource/Kitchen.Layouts/LayoutBlueprint.cs) as new modules need them.
/// </summary>
public sealed class LayoutBlueprint
{
    public int Id { get; set; }

    public Dictionary<LayoutPosition, Room> Tiles { get; set; } = new();

    public List<Feature> Features { get; set; } = [];

    public static LayoutBlueprint New(UnityRandom rng) => new() { Id = rng.Range(int.MinValue, int.MaxValue) };

    public LayoutBlueprint()
    {
    }

    /// <summary>Deep-copies tiles/features and draws a fresh random ID, like the original copy constructor.</summary>
    public LayoutBlueprint(LayoutBlueprint source, UnityRandom rng)
    {
        Id = rng.Range(int.MinValue, int.MaxValue);
        Tiles = new Dictionary<LayoutPosition, Room>(source.Tiles);
        Features = source.Features.Select(f => new Feature(f)).ToList();
    }

    public Room this[int x, int y]
    {
        get => this[new LayoutPosition(x, y)];
        set => Tiles[new LayoutPosition(x, y)] = value;
    }

    public Room this[LayoutPosition pos]
    {
        get => Tiles.GetValueOrDefault(pos, Room.Null);
        set => Tiles[pos] = value;
    }

    public Bounds GetBounds()
    {
        if (Tiles.Count == 0)
        {
            return new Bounds(0, 0, 0, 0);
        }

        var minX = int.MaxValue;
        var maxX = int.MinValue;
        var minY = int.MaxValue;
        var maxY = int.MinValue;
        foreach (var pos in Tiles.Keys)
        {
            minX = Math.Min(minX, pos.X);
            maxX = Math.Max(maxX, pos.X);
            minY = Math.Min(minY, pos.Y);
            maxY = Math.Max(maxY, pos.Y);
        }

        return new Bounds(minX, maxX, minY, maxY);
    }

    public HashSet<Room> Rooms() => [.. Tiles.Values];

    public HashSet<LayoutPosition> TilesOfRoom(Room room) =>
        [.. Tiles.Where(t => t.Value.Id == room.Id).Select(t => t.Key)];

    public bool IsTileOpenSpace(LayoutPosition tile)
    {
        var room = this[tile].Id;
        return LayoutHelpers.AllNearby.All(n => this[tile + n].Id == room);
    }

    public bool IsTileFlatWall(LayoutPosition tile)
    {
        var room = this[tile].Id;
        return LayoutHelpers.Directions.Count(n => this[tile + n].Id == room) == 3;
    }

    public bool IsTileAccessible(LayoutPosition tile) =>
        LayoutHelpers.Directions.Any(n => IsTileOpenSpace(tile + n));

    public bool HasFeature(LayoutPosition tile, FeatureType type = FeatureType.Generic) =>
        GetFeatures(tile, type).Count > 0;

    public bool HasFeature(LayoutPosition tile1, LayoutPosition tile2) =>
        GetFeatures(tile1).Any(f => f.Tile1 == tile2 || f.Tile2 == tile2);

    public List<Feature> GetFeatures(LayoutPosition tile, FeatureType type = FeatureType.Generic) =>
        Features.Where(f => (f.Tile1 == tile || f.Tile2 == tile) && (type == FeatureType.Generic || f.Type == type)).ToList();

    public List<LayoutPosition> GetWalls(LayoutPosition tile)
    {
        var room = this[tile].Id;
        return LayoutHelpers.Directions.Where(d => this[tile + d].Id != room).Select(d => d + tile).ToList();
    }

    public List<LayoutPosition> AdjacentInRoom(LayoutPosition tile)
    {
        var room = this[tile].Id;
        return LayoutHelpers.Directions.Where(d => this[tile + d].Id == room).Select(d => tile + d).ToList();
    }

    public HashSet<Room> AdjacentRooms(Room start)
    {
        var result = new HashSet<Room>();
        foreach (var tile in Tiles)
        {
            if (tile.Value.Id != start.Id)
            {
                continue;
            }

            foreach (var direction in LayoutHelpers.Directions)
            {
                var neighbour = this[direction.X + tile.Key.X, direction.Y + tile.Key.Y];
                if (neighbour.Id != Room.Null.Id && neighbour.Id != start.Id)
                {
                    result.Add(neighbour);
                }
            }
        }

        return result;
    }

    public HashSet<Room> ConnectedRooms(Room start)
    {
        var result = new HashSet<Room>();
        foreach (var feature in Features)
        {
            if (!feature.Type.IsDoor())
            {
                continue;
            }

            if (this[feature.Tile1].Id == start.Id)
            {
                result.Add(this[feature.Tile2]);
            }

            if (this[feature.Tile2].Id == start.Id)
            {
                result.Add(this[feature.Tile1]);
            }
        }

        return result;
    }

    public void SetRoom(Room current, Room next)
    {
        foreach (var key in Tiles.Keys.ToList())
        {
            if (Tiles[key].Id == current.Id)
            {
                Tiles[key] = next;
            }
        }
    }
}

public readonly record struct Bounds(int MinX, int MaxX, int MinY, int MaxY)
{
    // Float division on purpose - the original computes bounds.center as a Vector3 with `/2f`, and
    // comparisons like `tile.x < bounds.center.x` behave differently for odd (min+max) than integer
    // division would (e.g. min=0,max=9 -> center 4.5, so x<4.5 includes x=4; integer 9/2=4 would not).
    public double CenterX => (MinX + MaxX) / 2.0;

    public double CenterY => (MinY + MaxY) / 2.0;
}
