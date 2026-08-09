using PlateUpSeedTools.Engine.Layouts.Modules;

namespace PlateUpSeedTools.Engine.Layouts;

/// <summary>A fully-parsed, ready-to-execute layout graph (all nodes plus which one is the terminal Output).</summary>
public sealed record LayoutGraphDefinition(IReadOnlyList<LayoutModuleNode> Nodes, OutputNode Output);
