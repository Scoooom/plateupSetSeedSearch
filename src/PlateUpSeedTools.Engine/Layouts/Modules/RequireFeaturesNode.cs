using PlateUpSeedTools.Engine.Random;

namespace PlateUpSeedTools.Engine.Layouts.Modules;

/// <summary>Port of Kitchen.Layouts.Modules.RequireFeatures (a LayoutCondition subclass).</summary>
public sealed class RequireFeaturesNode : SingleInputLayoutModuleNode
{
    public required FeatureType Type { get; init; }

    public required int Minimum { get; init; }

    protected override void ActOn(LayoutBlueprint blueprint, UnityRandom rng)
    {
        var count = blueprint.Features.Count(f => f.Type == Type);
        if (count < Minimum)
        {
            throw new LayoutFailureException($"Found {count} of {Minimum} required {Type}");
        }
    }
}
