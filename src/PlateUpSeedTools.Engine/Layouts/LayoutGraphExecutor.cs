using PlateUpSeedTools.Engine.Layouts.Modules;
using PlateUpSeedTools.Engine.Random;

namespace PlateUpSeedTools.Engine.Layouts;

/// <summary>
/// Executes a fixed, pre-built layout graph. Ports LayoutGraph.Build(int)/Update() exactly
/// (DecompiledSource/Kitchen.Layouts/LayoutGraph.cs) - same requeue-until-parents-are-done queue
/// algorithm, same node execution order - rather than a "smarter" topological sort, because a
/// differently-ordered (even if still technically valid) execution would consume the shared RNG
/// stream in a different order and silently diverge from the real game's output.
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

        return results[output.Id];
    }
}
