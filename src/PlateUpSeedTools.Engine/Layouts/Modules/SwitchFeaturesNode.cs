using PlateUpSeedTools.Engine.Random;

namespace PlateUpSeedTools.Engine.Layouts.Modules;

/// <summary>Port of Kitchen.Layouts.Modules.SwitchFeatures.</summary>
public sealed class SwitchFeaturesNode : SingleInputLayoutModuleNode
{
    public required FeatureType SetToFeature { get; init; }

    protected override void ActOn(LayoutBlueprint blueprint, UnityRandom rng)
    {
        foreach (var feature in blueprint.Features)
        {
            feature.Type = SetToFeature;
        }
    }
}
