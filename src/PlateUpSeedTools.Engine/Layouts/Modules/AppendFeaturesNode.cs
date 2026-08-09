using PlateUpSeedTools.Engine.Random;

namespace PlateUpSeedTools.Engine.Layouts.Modules;

/// <summary>
/// Port of Kitchen.Layouts.Modules.AppendFeatures. Not a SingleInputLayoutModuleNode - it has a
/// second, multi-connection "AppendFrom" port alongside the standard single "Input" port.
/// </summary>
public sealed class AppendFeaturesNode : LayoutModuleNode
{
    public required string InputId { get; init; }

    public required IReadOnlyList<string> AppendFromIds { get; init; }

    public int FixSeed { get; init; }

    public override IEnumerable<string> ParentIds => AppendFromIds.Prepend(InputId);

    public override LayoutBlueprint? Execute(IReadOnlyDictionary<string, LayoutBlueprint> results, UnityRandom rng)
    {
        if (!results.TryGetValue(InputId, out var input))
        {
            return null;
        }

        var clone = new LayoutBlueprint(input, rng);
        RunWithOptionalFixSeed(rng, FixSeed, () =>
        {
            foreach (var id in AppendFromIds)
            {
                if (!results.TryGetValue(id, out var from))
                {
                    continue;
                }

                clone.Features.AddRange(from.Features.Where(f => !clone.HasFeature(f.Tile1, f.Tile2)));
            }
        });
        return clone;
    }
}
