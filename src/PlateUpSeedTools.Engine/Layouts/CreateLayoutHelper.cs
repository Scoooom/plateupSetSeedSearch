using PlateUpSeedTools.Engine.Layouts.Decorators;
using PlateUpSeedTools.Engine.Random;

namespace PlateUpSeedTools.Engine.Layouts;

public sealed record ConstructedLayout(LayoutBlueprint Blueprint, IReadOnlyList<AppliancePlacement> Decorations);

/// <summary>
/// Port of Kitchen.CreateLayoutHelper.ConstructLayout - retries graph build + decoration up to 100
/// times with seed+i on LayoutFailureException, same as the original. No EntityManager/ECS: the
/// original creates ECS entities/components as its "return value", here it's just the plain data.
/// Graph building and decoration deliberately share one UnityRandom instance across the whole
/// attempt - see LayoutGraphExecutor's doc comment and the RNG-stream-continuity note in
/// technical_findings memory for why that's required for fidelity.
/// </summary>
public static class CreateLayoutHelper
{
    public const int MapAttemptsMax = 100;

    public static ConstructedLayout ConstructLayout(LayoutGraphDefinition graph, LayoutProfileInfo profile, int seed)
    {
        var rng = new UnityRandom(seed);
        for (var i = 0; i < MapAttemptsMax; i++)
        {
            try
            {
                var blueprint = LayoutGraphExecutor.Build(graph, seed + i, rng);
                var decorations = LayoutDecorator.AttemptDecoration(blueprint, profile, rng);
                return new ConstructedLayout(blueprint, decorations);
            }
            catch (LayoutFailureException)
            {
            }
        }

        throw new LayoutFailureException($"Giving up after {MapAttemptsMax} attempts");
    }
}
