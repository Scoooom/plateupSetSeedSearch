using PlateUpSeedTools.Engine.Random;

namespace PlateUpSeedTools.Engine.Layouts.Modules;

/// <summary>Port of Kitchen.Layouts.Modules.FilterOnePerPair.</summary>
public sealed class FilterOnePerPairNode : SingleInputLayoutModuleNode
{
    protected override void ActOn(LayoutBlueprint blueprint, UnityRandom rng)
    {
        var rooms = blueprint.Rooms().ToList();
        var pairs = rooms
            .SelectMany(r => rooms.Where(room => r.Id < room.Id).Select(room => (r.Id, room.Id)))
            .ToList();

        var ordered = blueprint.Features.OrderBy(_ => rng.Value).ToList();
        ordered.RemoveAll(f =>
        {
            var id1 = blueprint[f.Tile1].Id;
            var id2 = blueprint[f.Tile2].Id;
            var pair = (Math.Min(id1, id2), Math.Max(id1, id2));
            if (pairs.Contains(pair))
            {
                pairs.Remove(pair);
                return false;
            }

            return true;
        });

        blueprint.Features = ordered;
    }
}
