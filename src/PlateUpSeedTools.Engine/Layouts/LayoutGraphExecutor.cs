using PlateUpSeedTools.Engine.Layouts.Modules;
using PlateUpSeedTools.Engine.Random;

namespace PlateUpSeedTools.Engine.Layouts;

/// <summary>
/// Executes a fixed, pre-built layout graph. Ports LayoutGraph.Build(int)/Update() exactly
/// (DecompiledSource/Kitchen.Layouts/LayoutGraph.cs) - same requeue-until-parents-are-done queue
/// algorithm, same node execution order - rather than a "smarter" topological sort, because a
/// differently-ordered (even if still technically valid) execution would consume the shared RNG
/// stream in a different order and silently diverge from the real game's output.
///
/// CRITICAL, easy-to-miss detail (found via ground-truth capture against the real game, session 3):
/// the original `LayoutGraph.Build(seed)` calls `Update()` (running the whole graph once), then
/// finds the Output node and calls `output.GetResult()` - which calls `UpdateAll()` FIRST, which
/// calls `Update()` a SECOND time, before finally returning `Result`. The second `Update()` does
/// NOT re-seed - it continues the SAME RNG stream from wherever the first pass left it. So the
/// real, returned layout is the result of a second, independent full graph execution, continuing
/// (not resetting) the stream - the first pass's computed rooms/features are entirely discarded,
/// consumed only for their RNG side effects. Confirmed by capturing the real game live: the FIRST
/// observed pass matched a naive single-pass port exactly, byte for byte, while the layout actually
/// shipped to the player matched a LATER pass instead. Both passes must run on every Build() call
/// (mirrored below via RunPass called twice on the same rng) or every layout this engine has ever
/// produced is wrong in the same way, even though each individual module's own logic is faithful.
/// </summary>
public static class LayoutGraphExecutor
{
    /// <summary>
    /// Builds the graph using the given rng, reseeded to `seed` first. The caller keeps the same
    /// rng instance afterward - callers that need to continue the same RNG stream into decoration
    /// (see CreateLayoutHelper) rely on this rather than this method creating its own instance.
    /// </summary>
    public static LayoutBlueprint Build(LayoutGraphDefinition graph, int seed, UnityRandom rng) =>
        Build(graph.Nodes, graph.Output, seed, rng);

    public static LayoutBlueprint Build(IReadOnlyList<LayoutModuleNode> nodes, OutputNode output, int seed, UnityRandom rng)
    {
        rng.InitState(seed);

        // First pass: matches the original's direct `Update()` call. Its result is entirely
        // discarded (see class doc comment) - only run for its RNG-stream side effect, exactly like
        // the real game.
        RunPass(nodes, rng);

        // Second pass: matches `output.GetResult()` -> `UpdateAll()` -> `Update()`. This is the one
        // whose result actually gets returned/used.
        return RunPass(nodes, rng)[output.Id];
    }

    private static Dictionary<string, LayoutBlueprint> RunPass(IReadOnlyList<LayoutModuleNode> nodes, UnityRandom rng)
    {
        var results = new Dictionary<string, LayoutBlueprint>();

        var queue = new Queue<LayoutModuleNode>(nodes);
        var budget = queue.Count * queue.Count;
        while (queue.Count > 0 && budget-- >= 0)
        {
            var node = queue.Dequeue();
            var stillQueued = queue.Select(n => n.Id).ToHashSet();
            if (node.ParentIds.Any(stillQueued.Contains))
            {
                queue.Enqueue(node);
                continue;
            }

            var result = node.Execute(results, rng)
                ?? throw new LayoutFailureException($"Node '{node.Id}' produced no result");
            results[node.Id] = result;
        }

        if (budget <= 0)
        {
            throw new LayoutFailureException("Failed to update graph");
        }

        return results;
    }
}
