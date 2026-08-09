using PlateUpSeedTools.Engine.Random;

namespace PlateUpSeedTools.Engine.Layouts.Modules;

/// <summary>Port of Kitchen.Layouts.Modules.Output - the graph's terminal node, a pure passthrough.</summary>
public sealed class OutputNode : SingleInputLayoutModuleNode
{
    protected override void ActOn(LayoutBlueprint blueprint, UnityRandom rng)
    {
    }
}
