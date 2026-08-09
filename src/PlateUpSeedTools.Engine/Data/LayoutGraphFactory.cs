using System.Text.Json;
using PlateUpSeedTools.Engine.Layouts;
using PlateUpSeedTools.Engine.Layouts.Modules;

namespace PlateUpSeedTools.Engine.Data;

/// <summary>Converts extracted LayoutGraphData into an executable LayoutGraphDefinition.</summary>
public static class LayoutGraphFactory
{
    public static LayoutGraphDefinition Create(LayoutGraphData data)
    {
        var byId = data.Nodes.ToDictionary(n => n.Id, CreateNode);
        var output = byId.Values.OfType<OutputNode>().FirstOrDefault(n => n.Id == data.OutputNodeId)
            ?? throw new InvalidOperationException($"Graph has no Output node with id '{data.OutputNodeId}'");
        return new LayoutGraphDefinition(byId.Values.ToList(), output);
    }

    private static LayoutModuleNode CreateNode(LayoutNodeData n)
    {
        var p = n.Params;
        return n.Type switch
        {
            "Room Grid" => new RoomGridNode
            {
                Id = n.Id,
                Width = GetInt(p, "Width"),
                Height = GetInt(p, "Height"),
                Type = (RoomType)GetInt(p, "Type"),
                SetType = GetBool(p, "SetType"),
            },
            "Output" => new OutputNode { Id = n.Id, InputId = RequireInput(n) },
            "Pad With Room" => new PadWithRoomNode
            {
                Id = n.Id,
                InputId = RequireInput(n),
                FixSeed = n.FixSeed,
                Type = (RoomType)GetInt(p, "Type"),
                Above = GetInt(p, "Above"),
                Left = GetInt(p, "Left"),
                Right = GetInt(p, "Right"),
                Below = GetInt(p, "Below"),
            },
            "Create Front Door" => new CreateFrontDoorNode
            {
                Id = n.Id,
                InputId = RequireInput(n),
                FixSeed = n.FixSeed,
                Type = (RoomType)GetInt(p, "Type"),
                ForceFirstHalf = GetBool(p, "ForceFirstHalf"),
            },
            "Append Features" => new AppendFeaturesNode
            {
                Id = n.Id,
                InputId = RequireInput(n),
                FixSeed = n.FixSeed,
                AppendFromIds = n.AppendFrom ?? [],
            },
            "Find All Features" => new FindAllFeaturesNode
            {
                Id = n.Id,
                InputId = RequireInput(n),
                FixSeed = n.FixSeed,
                Feature = (FeatureType)GetInt(p, "Feature"),
            },
            "Filter By Room" => new FilterByRoomNode
            {
                Id = n.Id,
                InputId = RequireInput(n),
                FixSeed = n.FixSeed,
                RemoveMode = GetBool(p, "RemoveMode"),
                Type1 = (RoomType)GetInt(p, "Type1"),
                FilterSecond = GetBool(p, "FilterSecond"),
                Type2 = (RoomType)GetInt(p, "Type2"),
            },
            "Recentre Layout" => new RecentreLayoutNode { Id = n.Id, InputId = RequireInput(n), FixSeed = n.FixSeed },
            "Create Double Doors" => new CreateDoubleDoorsNode
            {
                Id = n.Id,
                InputId = RequireInput(n),
                FixSeed = n.FixSeed,
                OffsetY = GetInt(p, "OffsetY"),
            },
            "Mirror" => new MirrorNode
            {
                Id = n.Id,
                InputId = RequireInput(n),
                FixSeed = n.FixSeed,
                IsVertical = GetBool(p, "IsVertical"),
            },
            "Random Expand" => new RandomExpandNode
            {
                Id = n.Id,
                InputId = RequireInput(n),
                FixSeed = n.FixSeed,
                MinX = GetInt(p, "MinX"),
                MinY = GetInt(p, "MinY"),
                MaxX = GetInt(p, "MaxX"),
                MaxY = GetInt(p, "MaxY"),
            },
            "Require Feature Count Even" => new RequireFeatureCountEvenNode
            {
                Id = n.Id,
                InputId = RequireInput(n),
                FixSeed = n.FixSeed,
                Type = (FeatureType)GetInt(p, "Type"),
                RequireEven = GetBool(p, "RequireEven"),
            },
            "Set Room" => new SetRoomNode
            {
                Id = n.Id,
                InputId = RequireInput(n),
                FixSeed = n.FixSeed,
                X = GetInt(p, "X"),
                Y = GetInt(p, "Y"),
                Type = (RoomType)GetInt(p, "Type"),
            },
            "Split Line" => new SplitLineNode
            {
                Id = n.Id,
                InputId = RequireInput(n),
                FixSeed = n.FixSeed,
                Position = GetInt(p, "Position"),
                Count = GetInt(p, "Count"),
                IsRow = GetBool(p, "IsRow"),
            },
            "Split Rooms" => new SplitRoomsNode
            {
                Id = n.Id,
                InputId = RequireInput(n),
                FixSeed = n.FixSeed,
                UniformX = GetInt(p, "UniformX"),
                UniformY = GetInt(p, "UniformY"),
                RandomX = GetInt(p, "RandomX"),
                RandomY = GetInt(p, "RandomY"),
            },
            "Filter By Free Space" => new FilterByFreeSpaceNode { Id = n.Id, InputId = RequireInput(n), FixSeed = n.FixSeed },
            "Filter Opposite Pair" => new FilterOppositePairNode { Id = n.Id, InputId = RequireInput(n), FixSeed = n.FixSeed },
            "Filter Select Count" => new FilterSelectCountNode
            {
                Id = n.Id,
                InputId = RequireInput(n),
                FixSeed = n.FixSeed,
                Count = GetInt(p, "Count"),
            },
            "Swap Room Type" => new SwapRoomTypeNode
            {
                Id = n.Id,
                InputId = RequireInput(n),
                FixSeed = n.FixSeed,
                X = GetInt(p, "X"),
                Y = GetInt(p, "Y"),
                Type = (RoomType)GetInt(p, "Type"),
            },
            "Move Feature In Direction" => new MoveFeatureInDirectionNode
            {
                Id = n.Id,
                InputId = RequireInput(n),
                FixSeed = n.FixSeed,
                OffsetX = GetInt(p, "OffsetX"),
                OffsetY = GetInt(p, "OffsetY"),
                MaxSteps = GetInt(p, "MaxSteps"),
            },
            "Insert Random Room" => new InsertRandomRoomNode
            {
                Id = n.Id,
                InputId = RequireInput(n),
                FixSeed = n.FixSeed,
                Type = (RoomType)GetInt(p, "Type"),
            },
            "Merge Rooms By Type" => new MergeRoomsByTypeNode { Id = n.Id, InputId = RequireInput(n), FixSeed = n.FixSeed },
            "Create Room By Joins" => new CreateRoomByJoinsNode
            {
                Id = n.Id,
                InputId = RequireInput(n),
                FixSeed = n.FixSeed,
                StartX = GetInt(p, "StartX"),
                StartY = GetInt(p, "StartY"),
                Joins = GetInt(p, "Joins"),
                Type = (RoomType)GetInt(p, "Type"),
                RequiredJoins = GetBool(p, "Required"),
            },
            "Filter One Per Pair" => new FilterOnePerPairNode { Id = n.Id, InputId = RequireInput(n), FixSeed = n.FixSeed },
            "Filter Adjacent Pair" => new FilterAdjacentPairNode { Id = n.Id, InputId = RequireInput(n), FixSeed = n.FixSeed },
            "Switch Features" => new SwitchFeaturesNode
            {
                Id = n.Id,
                InputId = RequireInput(n),
                FixSeed = n.FixSeed,
                SetToFeature = (FeatureType)GetInt(p, "SetToFeature"),
            },
            "Require Accessible" => new RequireAccessibleNode
            {
                Id = n.Id,
                InputId = RequireInput(n),
                FixSeed = n.FixSeed,
                AllowGardens = GetBool(p, "AllowGardens"),
            },
            "Require Features" => new RequireFeaturesNode
            {
                Id = n.Id,
                InputId = RequireInput(n),
                FixSeed = n.FixSeed,
                Type = (FeatureType)GetInt(p, "Type"),
                Minimum = GetInt(p, "Minimum"),
            },
            _ => throw new NotSupportedException($"Unknown layout graph node type '{n.Type}' (node {n.Id})"),
        };
    }

    private static string RequireInput(LayoutNodeData n) =>
        n.Input ?? throw new InvalidOperationException($"Node '{n.Id}' ({n.Type}) has no Input connection");

    private static int GetInt(Dictionary<string, JsonElement> p, string key) => p[key].GetInt32();

    private static bool GetBool(Dictionary<string, JsonElement> p, string key) => p[key].ValueKind switch
    {
        JsonValueKind.True => true,
        JsonValueKind.False => false,
        JsonValueKind.Number => p[key].GetInt32() != 0, // Unity YAML bools are often serialized as 0/1
        _ => throw new InvalidOperationException($"'{key}' is not a boolean-like value"),
    };
}
