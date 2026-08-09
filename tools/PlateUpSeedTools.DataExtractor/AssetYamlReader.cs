using YamlDotNet.RepresentationModel;

namespace PlateUpSeedTools.DataExtractor;

/// <summary>
/// Thin wrapper around a Unity .asset file's single YAML document (the MonoBehaviour mapping).
/// Unity's custom `!u!114 &amp;id` tags are left alone - YamlDotNet's low-level representation
/// model (YamlStream/YamlMappingNode) parses the structure generically without trying to resolve
/// them to a .NET type, which is all we need for reading plain field values.
/// </summary>
public sealed class AssetYamlReader
{
    private readonly YamlMappingNode _root;

    public string Guid { get; }

    private AssetYamlReader(YamlMappingNode root, string guid)
    {
        _root = root;
        Guid = guid;
    }

    public static AssetYamlReader Load(string assetPath, string guid)
    {
        var stream = new YamlStream();
        using var reader = new StreamReader(assetPath);
        stream.Load(reader);
        var doc = stream.Documents.Single();
        var monoBehaviour = (YamlMappingNode)((YamlMappingNode)doc.RootNode)["MonoBehaviour"];
        return new AssetYamlReader(monoBehaviour, guid);
    }

    public string GetName() => Scalar("m_Name");

    public string Scalar(string key) => ((YamlScalarNode)_root[key]).Value ?? "";

    public bool TryScalar(string key, out string value)
    {
        if (_root.Children.TryGetValue(new YamlScalarNode(key), out var node) && node is YamlScalarNode scalar)
        {
            value = scalar.Value ?? "";
            return true;
        }

        value = "";
        return false;
    }

    public int Int(string key) => int.Parse(Scalar(key));

    public bool Bool(string key) => Scalar(key) != "0";

    /// <summary>Reads a Unity object reference field ({fileID, guid, type}) and returns its guid, or null if unset (fileID: 0).</summary>
    public string? Reference(string key)
    {
        var node = (YamlMappingNode)_root[key];
        if (node.Children.TryGetValue(new YamlScalarNode("guid"), out var guidNode))
        {
            return ((YamlScalarNode)guidNode).Value;
        }

        return null;
    }

    /// <summary>Reads a list of Unity object references, e.g. RequiredAppliances.</summary>
    public List<string> ReferenceList(string key)
    {
        var seq = (YamlSequenceNode)_root[key];
        return seq.Children
            .Select(c => (YamlMappingNode)c)
            .Select(m => m.Children.TryGetValue(new YamlScalarNode("guid"), out var g) ? ((YamlScalarNode)g).Value : null)
            .Where(g => !string.IsNullOrEmpty(g))
            .Select(g => g!)
            .ToList();
    }

    /// <summary>
    /// Reads an xNode `ports` map, returning, per port field name, the list of (sourcePortName, sourceNodeGuid)
    /// it's connected to (i.e. this node's inputs on that port).
    /// </summary>
    public Dictionary<string, List<(string PortName, string NodeGuid)>> Ports()
    {
        var result = new Dictionary<string, List<(string, string)>>();
        if (!_root.Children.TryGetValue(new YamlScalarNode("ports"), out var portsNode))
        {
            return result;
        }

        var ports = (YamlMappingNode)portsNode;
        var keys = ((YamlSequenceNode)ports["keys"]).Children.Select(k => ((YamlScalarNode)k).Value!).ToList();
        var values = ((YamlSequenceNode)ports["values"]).Children.Cast<YamlMappingNode>().ToList();

        for (var i = 0; i < keys.Count && i < values.Count; i++)
        {
            var connections = new List<(string, string)>();
            if (values[i].Children.TryGetValue(new YamlScalarNode("connections"), out var connNode) && connNode is YamlSequenceNode connSeq)
            {
                foreach (var c in connSeq.Children.Cast<YamlMappingNode>())
                {
                    var fieldName = ((YamlScalarNode)c["fieldName"]).Value!;
                    var nodeRef = (YamlMappingNode)c["node"];
                    if (nodeRef.Children.TryGetValue(new YamlScalarNode("guid"), out var g))
                    {
                        connections.Add((fieldName, ((YamlScalarNode)g).Value!));
                    }
                }
            }

            result[keys[i]] = connections;
        }

        return result;
    }
}
