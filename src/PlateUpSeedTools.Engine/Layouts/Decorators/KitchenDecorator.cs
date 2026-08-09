using PlateUpSeedTools.Engine.Random;

namespace PlateUpSeedTools.Engine.Layouts.Decorators;

/// <summary>
/// Port of Kitchen.KitchenDecorator - places a counter at each hatch tile, then walks the room
/// placing Profile.RequiredAppliances one per accessible flat-wall tile, starting from a random tile.
/// </summary>
public static class KitchenDecorator
{
    public static void Decorate(Room room, LayoutBlueprint blueprint, LayoutProfileInfo profile, UnityRandom rng, List<AppliancePlacement> decorations)
    {
        var queue = new Queue<int>(profile.RequiredApplianceIds);
        var usedTiles = new List<LayoutPosition>(decorations.Select(d => d.Position));
        var roomTiles = blueprint.TilesOfRoom(room);

        foreach (var tile in roomTiles)
        {
            if (blueprint.HasFeature(tile, FeatureType.Hatch))
            {
                decorations.Add(new AppliancePlacement(tile, profile.CounterApplianceId, FindWallRotation(blueprint, tile)));
                usedTiles.Add(tile);
            }
        }

        LayoutPosition NextTile(LayoutPosition tile)
        {
            foreach (var candidate in blueprint.AdjacentInRoom(tile).Concat(roomTiles))
            {
                if (blueprint.IsTileAccessible(candidate) && !blueprint.HasFeature(candidate)
                    && blueprint.IsTileFlatWall(candidate) && !usedTiles.Contains(candidate))
                {
                    usedTiles.Add(candidate);
                    return candidate;
                }
            }

            throw new LayoutFailureException("Not enough spaces to place kitchen equipment");
        }

        var startCandidates = roomTiles.Where(x => !usedTiles.Contains(x)).ToList();
        var current = NextTile(startCandidates[rng.Range(0, startCandidates.Count)]);
        while (queue.Count > 0)
        {
            decorations.Add(new AppliancePlacement(current, queue.Dequeue(), FindWallRotation(blueprint, current)));
            current = NextTile(current);
        }
    }

    /// <summary>
    /// Port of Kitchen.KitchenDecorator.FindWallRotation, returning degrees instead of a Quaternion -
    /// display-only (schematic preview), so it doesn't need to bit-match Unity's Quaternion values,
    /// just consistently identify which of the 4 cardinal directions the tile faces a room boundary.
    /// </summary>
    private static int FindWallRotation(LayoutBlueprint blueprint, LayoutPosition pos)
    {
        var room = blueprint[pos];
        foreach (var direction in LayoutHelpers.Directions)
        {
            var neighbour = direction + pos;
            if (blueprint[neighbour].Id != room.Id)
            {
                return DirectionToDegrees(direction);
            }
        }

        return 0;
    }

    private static int DirectionToDegrees(LayoutPosition direction) => (direction.X, direction.Y) switch
    {
        (0, 1) => 0,
        (1, 0) => 90,
        (0, -1) => 180,
        (-1, 0) => 270,
        _ => 0,
    };
}
