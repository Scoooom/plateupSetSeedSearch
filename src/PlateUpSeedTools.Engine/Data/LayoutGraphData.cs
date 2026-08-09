using System.Text.Json;

namespace PlateUpSeedTools.Engine.Data;

/// <summary>JSON shape for data/settings/&lt;slug&gt;/graph.json, produced by the DataExtractor tool.</summary>
public sealed class LayoutGraphData
{
    public required string OutputNodeId { get; init; }

    public required List<LayoutNodeData> Nodes { get; init; }
}

/// <summary>
/// One node in the graph. `Type` is the module's editor display name (e.g. "Room Grid",
/// "Create Front Door") as it appears in the asset's `m_Name` field - see
/// LayoutGraphFactory.ModuleTypesByName for the mapping to a concrete LayoutModuleNode.
/// `Params` holds each module's own scalar fields (Width/Height/Type/... for Room Grid, etc.),
/// keyed by their exact field name in the decompiled source.
/// </summary>
public sealed class LayoutNodeData
{
    public required string Id { get; init; }

    public required string Type { get; init; }

    public string? Input { get; init; }

    public List<string>? AppendFrom { get; init; }

    public int FixSeed { get; init; }

    public Dictionary<string, JsonElement> Params { get; init; } = [];
}
