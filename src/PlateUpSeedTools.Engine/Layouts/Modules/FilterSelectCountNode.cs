using PlateUpSeedTools.Engine.Random;

namespace PlateUpSeedTools.Engine.Layouts.Modules;

/// <summary>Port of Kitchen.Layouts.Modules.FilterSelectCount.</summary>
public sealed class FilterSelectCountNode : SingleInputLayoutModuleNode
{
    public required int Count { get; init; }

    protected override void ActOn(LayoutBlueprint blueprint, UnityRandom rng)
    {
        blueprint.Features = blueprint.Features.OrderBy(_ => rng.Value).Take(Count).ToList();
    }
}
