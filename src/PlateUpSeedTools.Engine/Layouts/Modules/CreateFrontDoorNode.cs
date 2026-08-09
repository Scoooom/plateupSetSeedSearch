using PlateUpSeedTools.Engine.Random;

namespace PlateUpSeedTools.Engine.Layouts.Modules;

/// <summary>Port of Kitchen.Layouts.Modules.CreateFrontDoor.</summary>
public sealed class CreateFrontDoorNode : SingleInputLayoutModuleNode
{
    public required RoomType Type { get; init; }

    public required bool ForceFirstHalf { get; init; }

    protected override void ActOn(LayoutBlueprint blueprint, UnityRandom rng)
    {
        if (blueprint.Tiles.Count == 0)
        {
            return;
        }

        var bounds = blueprint.GetBounds();
        var candidates = blueprint.Tiles
            .Where(t => t.Key.Y == bounds.MinY && t.Value.Type == Type && (!ForceFirstHalf || t.Key.X < bounds.CenterX))
            .ToList();

        if (candidates.Count == 0)
        {
            throw new LayoutFailureException("No suitable front door");
        }

        var key = candidates[rng.Range(0, candidates.Count)].Key;
        var tile = new LayoutPosition(key.X, key.Y - 1);
        blueprint.Features.Add(new Feature(key, tile, FeatureType.FrontDoor));
    }
}
