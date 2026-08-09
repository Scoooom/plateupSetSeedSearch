using PlateUpSeedTools.Engine.Random;

namespace PlateUpSeedTools.Engine.Layouts.Decorators;

/// <summary>
/// Port of Kitchen.DiningDecorator - places dining tables in a room, up to Profile.MaximumTables,
/// keeping tables spaced apart. Returns false (no decorations committed) if it couldn't place enough.
/// </summary>
public static class DiningDecorator
{
    public static bool Decorate(Room room, LayoutBlueprint blueprint, LayoutProfileInfo profile, UnityRandom rng, List<AppliancePlacement> decorations)
    {
        var pending = new List<AppliancePlacement>();
        var blocked = new HashSet<LayoutPosition>();

        // OrderBy(_ => rng.Value) matches DiningDecorator.Decorate's `orderby Random.value` - consumes
        // one Random.value draw per tile, in the room's tile enumeration order.
        var tiles = blueprint.TilesOfRoom(room).OrderBy(_ => rng.Value).ToList();

        var placed = 0;
        foreach (var tile in tiles)
        {
            if (!blueprint.IsTileOpenSpace(tile))
            {
                continue;
            }

            var blockedHere = false;
            foreach (var direction in LayoutHelpers.Directions)
            {
                var pos = direction + tile;
                if (blocked.Contains(pos) || blueprint.HasFeature(pos))
                {
                    blockedHere = true;
                    break;
                }
            }

            if (blockedHere)
            {
                continue;
            }

            pending.Add(new AppliancePlacement(tile, profile.TableApplianceId, 0));
            placed++;
            if (placed >= profile.MaximumTables)
            {
                break;
            }

            foreach (var near in LayoutHelpers.AllNearby)
            {
                blocked.Add(near + tile);
            }
        }

        if (placed != profile.MaximumTables)
        {
            return false;
        }

        decorations.AddRange(pending);
        return true;
    }
}
