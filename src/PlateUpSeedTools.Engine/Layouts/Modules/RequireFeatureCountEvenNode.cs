using PlateUpSeedTools.Engine.Random;

namespace PlateUpSeedTools.Engine.Layouts.Modules;

/// <summary>Port of Kitchen.Layouts.Modules.RequireFeatureCountEven (a LayoutCondition subclass).</summary>
public sealed class RequireFeatureCountEvenNode : SingleInputLayoutModuleNode
{
    public required FeatureType Type { get; init; }

    public required bool RequireEven { get; init; }

    protected override void ActOn(LayoutBlueprint blueprint, UnityRandom rng)
    {
        var count = blueprint.Features.Count(f => f.Type == Type);
        if (count % 2 == 0 != RequireEven)
        {
            throw new LayoutFailureException($"Found {count} wanted even {RequireEven} required {Type}");
        }
    }
}
