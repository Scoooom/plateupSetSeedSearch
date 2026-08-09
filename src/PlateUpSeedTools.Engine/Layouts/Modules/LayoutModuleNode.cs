using PlateUpSeedTools.Engine.Random;

namespace PlateUpSeedTools.Engine.Layouts.Modules;

/// <summary>
/// A node in a layout graph. Ports the relevant slice of xNode's Node/Module base classes
/// (DecompiledSource/Kitchen.Layouts.Modules/Module.cs, LayoutModule.cs) - not the generic node
/// editor plumbing (ports, connections, live-graph updates), just what's needed to execute a fixed,
/// already-built graph: identity, which other nodes feed it, and how to produce its LayoutBlueprint
/// result given its parents' already-computed results.
/// </summary>
public abstract class LayoutModuleNode
{
    public required string Id { get; init; }

    /// <summary>IDs of every node connected to any of this node's input ports (xNode's Module.Parents).</summary>
    public abstract IEnumerable<string> ParentIds { get; }

    public abstract LayoutBlueprint? Execute(IReadOnlyDictionary<string, LayoutBlueprint> results, UnityRandom rng);

    /// <summary>
    /// Port of the FixSeed handling in LayoutModule.Generate(): temporarily swap to a fresh
    /// FixSeed-derived stream for this node's RNG usage, then restore the caller's stream exactly
    /// where it left off, so a fixed node doesn't perturb the rest of the graph's RNG sequence.
    /// </summary>
    protected static void RunWithOptionalFixSeed(UnityRandom rng, int fixSeed, Action action)
    {
        if (fixSeed == 0)
        {
            action();
            return;
        }

        var saved = rng.GetState();
        rng.InitState(fixSeed);
        action();
        rng.SetState(saved);
    }
}

/// <summary>Shared behavior for the common case: one "Input" port, clone it, mutate the clone.</summary>
public abstract class SingleInputLayoutModuleNode : LayoutModuleNode
{
    public required string InputId { get; init; }

    public int FixSeed { get; init; }

    public override IEnumerable<string> ParentIds => [InputId];

    protected abstract void ActOn(LayoutBlueprint blueprint, UnityRandom rng);

    public override LayoutBlueprint? Execute(IReadOnlyDictionary<string, LayoutBlueprint> results, UnityRandom rng)
    {
        if (!results.TryGetValue(InputId, out var input))
        {
            return null;
        }

        var clone = new LayoutBlueprint(input, rng);
        RunWithOptionalFixSeed(rng, FixSeed, () => ActOn(clone, rng));
        return clone;
    }
}
