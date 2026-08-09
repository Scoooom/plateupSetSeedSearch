using System.Text.Json;

namespace PlateUpSeedTools.DataExtractor;

public static class SettingExtractor
{
    // Field names (beyond Input/AppendFrom/FixSeed, handled generically via ports) each module type
    // needs read as plain scalars. Matches the constructors in Engine's LayoutGraphFactory exactly.
    private static readonly Dictionary<string, string[]> ModuleParamFields = new()
    {
        ["Room Grid"] = ["Width", "Height", "Type", "SetType"],
        ["Output"] = [],
        ["Pad With Room"] = ["Type", "Above", "Left", "Right", "Below"],
        ["Create Front Door"] = ["Type", "ForceFirstHalf"],
        ["Append Features"] = [],
        ["Find All Features"] = ["Feature"],
        ["Filter By Room"] = ["RemoveMode", "Type1", "FilterSecond", "Type2"],
        ["Recentre Layout"] = [],
        ["Create Double Doors"] = ["OffsetY"],
        ["Mirror"] = ["IsVertical"],
        ["Random Expand"] = ["MinX", "MinY", "MaxX", "MaxY"],
        ["Require Feature Count Even"] = ["Type", "RequireEven"],
        ["Set Room"] = ["X", "Y", "Type"],
        ["Split Line"] = ["Position", "Count", "IsRow"],
        ["Split Rooms"] = ["UniformX", "UniformY", "RandomX", "RandomY"],
        ["Filter By Free Space"] = [],
        ["Filter Opposite Pair"] = [],
        ["Filter Select Count"] = ["Count"],
        ["Swap Room Type"] = ["X", "Y", "Type"],
        ["Move Feature In Direction"] = ["OffsetX", "OffsetY", "MaxSteps"],
        ["Insert Random Room"] = ["Type"],
        ["Merge Rooms By Type"] = [],
        ["Create Room By Joins"] = ["StartX", "StartY", "Joins", "Type", "Required"],
        ["Filter One Per Pair"] = [],
        ["Filter Adjacent Pair"] = [],
        ["Switch Features"] = ["SetToFeature"],
        ["Require Accessible"] = ["AllowGardens"],
        ["Require Features"] = ["Type", "Minimum"],
    };

    public static (string Slug, string DisplayName, int Id) Extract(
        string assetsRoot, GuidResolver resolver, string settingAssetName, string slug, string outputRoot)
    {
        var settingPath = FindAssetByName(assetsRoot, settingAssetName);
        var settingGuid = ReadMetaGuid(settingPath);
        var setting = AssetYamlReader.Load(settingPath, settingGuid);

        var settingId = setting.Int("ID");
        var displayName = setting.GetName();
        var forceLayoutGuid = setting.Reference("ForceLayout");

        var outDir = Path.Combine(outputRoot, slug);
        Directory.CreateDirectory(outDir);

        var jsonOptions = new JsonSerializerOptions { WriteIndented = true };

        if (forceLayoutGuid is not null)
        {
            var profileId = ExtractProfileAndGraph(resolver, forceLayoutGuid, outDir);
            var settingData = new { slug, displayName, id = settingId, forceLayoutId = (int?)profileId };
            File.WriteAllText(Path.Combine(outDir, "setting.json"), JsonSerializer.Serialize(settingData, jsonOptions));
        }
        else
        {
            // No forced layout: at request time, the web layer picks one of the shared/standard
            // layout profiles at random-by-seed (LayoutSeed.LayoutIdFromSeed) - see ExtractStandardLayouts.
            var settingData = new { slug, displayName, id = settingId, forceLayoutId = (int?)null };
            File.WriteAllText(Path.Combine(outDir, "setting.json"), JsonSerializer.Serialize(settingData, jsonOptions));
        }

        return (slug, displayName, settingId);
    }

    /// <summary>
    /// Extracts the fixed pool of "standard" layout profiles (AssetReference.FixedRunLayout in the
    /// decompiled source: Basic/Diner/Extended/Huge/Medium Layout) into a shared location, keyed by
    /// their numeric LayoutProfile ID, for settings that don't force a specific layout.
    /// </summary>
    public static void ExtractStandardLayouts(GuidResolver resolver, string assetsRoot, string outputRoot)
    {
        string[] standardLayoutAssetNames =
        [
            "Basic Layout.asset", "Diner Layout.asset", "Extended Layout.asset",
            "Huge Layout.asset", "Medium Layout.asset",
        ];

        var sharedDir = Path.Combine(outputRoot, "_shared-layouts");
        foreach (var assetName in standardLayoutAssetNames)
        {
            var path = FindAssetByName(assetsRoot, assetName);
            var guid = ReadMetaGuid(path);
            var id = ExtractProfileAndGraph(resolver, guid, outDir: null, idOnlyDirName: sharedDir);
            Console.WriteLine($"  Extracted shared layout '{assetName}' (id {id})");
        }
    }

    /// <summary>Extracts a LayoutProfile + its graph to outDir/profile.json and outDir/graph.json. Returns the profile's ID.</summary>
    private static int ExtractProfileAndGraph(GuidResolver resolver, string layoutProfileGuid, string? outDir, string? idOnlyDirName = null)
    {
        var profilePath = resolver.Resolve(layoutProfileGuid);
        var profile = AssetYamlReader.Load(profilePath, layoutProfileGuid);
        var profileId = profile.Int("ID");

        // idOnlyDirName lets callers (the shared standard-layout pool) key the output directory by
        // the profile's own ID, which isn't known until after loading the asset.
        outDir = idOnlyDirName is not null ? Path.Combine(idOnlyDirName, profileId.ToString()) : outDir!;
        Directory.CreateDirectory(outDir);
        var jsonOptions = new JsonSerializerOptions { WriteIndented = true };

        var tableGuid = profile.Reference("Table") ?? throw new InvalidOperationException("LayoutProfile has no Table");
        var counterGuid = profile.Reference("Counter") ?? throw new InvalidOperationException("LayoutProfile has no Counter");
        var requiredGuids = profile.ReferenceList("RequiredAppliances");

        var profileData = new
        {
            id = profileId,
            maximumTables = profile.Int("MaximumTables"),
            tableApplianceId = ResolveApplianceId(resolver, tableGuid),
            counterApplianceId = ResolveApplianceId(resolver, counterGuid),
            requiredApplianceIds = requiredGuids.Select(g => ResolveApplianceId(resolver, g)).ToList(),
        };
        File.WriteAllText(Path.Combine(outDir, "profile.json"), JsonSerializer.Serialize(profileData, jsonOptions));

        var graphGuid = profile.Reference("Graph") ?? throw new InvalidOperationException("LayoutProfile has no Graph");
        var graphData = ExtractGraph(resolver, graphGuid);
        File.WriteAllText(Path.Combine(outDir, "graph.json"), JsonSerializer.Serialize(graphData, jsonOptions));

        return profileId;
    }

    private static int ResolveApplianceId(GuidResolver resolver, string guid)
    {
        var path = resolver.Resolve(guid);
        var reader = AssetYamlReader.Load(path, guid);
        return reader.Int("ID");
    }

    private static object ExtractGraph(GuidResolver resolver, string graphGuid)
    {
        var graphPath = resolver.Resolve(graphGuid);
        var graphReader = AssetYamlReader.Load(graphPath, graphGuid);
        var nodeGuids = graphReader.ReferenceList("nodes");

        var nodes = new List<object>();
        string? outputNodeId = null;

        foreach (var nodeGuid in nodeGuids)
        {
            if (!resolver.TryResolve(nodeGuid, out var nodePath))
            {
                Console.WriteLine($"  WARNING: node guid {nodeGuid} has no resolvable asset, skipping.");
                continue;
            }

            var node = AssetYamlReader.Load(nodePath, nodeGuid);
            string typeName;
            try
            {
                typeName = node.GetName();
            }
            catch
            {
                Console.WriteLine($"  WARNING: node {nodeGuid} has no m_Name (likely a broken/orphaned node), skipping.");
                continue;
            }

            if (!ModuleParamFields.ContainsKey(typeName))
            {
                Console.WriteLine($"  WARNING: unrecognised module type '{typeName}' (node {nodeGuid}), skipping.");
                continue;
            }

            // A node with a missing/broken script reference (m_Script: {fileID: 0}) still has
            // m_Name but none of its actual fields - detectable because even a functional node with
            // zero params (Output, Recentre Layout) always has FixSeed from the LayoutModule base.
            if (!node.TryScalar("FixSeed", out _))
            {
                Console.WriteLine($"  WARNING: node {nodeGuid} ('{typeName}') has no FixSeed field - likely an orphaned/broken node with a missing script reference, skipping.");
                continue;
            }

            var ports = node.Ports();
            string? inputId = ports.TryGetValue("Input", out var inputConns) && inputConns.Count > 0 ? inputConns[0].NodeGuid : null;
            List<string>? appendFrom = ports.TryGetValue("AppendFrom", out var appendConns)
                ? appendConns.Select(c => c.NodeGuid).ToList()
                : null;

            var fixSeed = node.TryScalar("FixSeed", out var fixSeedStr) ? int.Parse(fixSeedStr) : 0;

            var paramValues = new Dictionary<string, int>();
            foreach (var field in ModuleParamFields[typeName])
            {
                paramValues[field] = node.Int(field);
            }

            nodes.Add(new
            {
                id = nodeGuid,
                type = typeName,
                input = inputId,
                appendFrom,
                fixSeed,
                @params = paramValues,
            });

            if (typeName == "Output")
            {
                outputNodeId = nodeGuid;
            }
        }

        return new { outputNodeId, nodes };
    }

    private static string FindAssetByName(string assetsRoot, string assetName)
    {
        var matches = Directory.GetFiles(assetsRoot, assetName, SearchOption.AllDirectories);
        return matches.Length switch
        {
            0 => throw new FileNotFoundException($"Could not find asset '{assetName}' under {assetsRoot}"),
            1 => matches[0],
            _ => throw new InvalidOperationException($"Multiple assets named '{assetName}' found: {string.Join(", ", matches)}"),
        };
    }

    private static string ReadMetaGuid(string assetPath)
    {
        var metaText = File.ReadAllText(assetPath + ".meta");
        var match = System.Text.RegularExpressions.Regex.Match(metaText, @"guid:\s*([0-9a-f]{32})");
        return match.Success ? match.Groups[1].Value : throw new InvalidOperationException($"No guid found in {assetPath}.meta");
    }
}
