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

    // The root MonoBehaviour's own local fileID within this one asset file (Unity's `&11400000`
    // anchor on the `--- !u!114 &11400000` document-start line - confirmed universal across every
    // single-MonoBehaviour-root ScriptableObject .asset in this game's export, always 11400000, but
    // read from the actual document rather than hardcoded in case that ever isn't true). A
    // guid-less `{fileID: <this value>}` reference is Unity's "local/same-file reference" form -
    // i.e. this field points at the asset's OWN object, not a different asset - see Reference's doc
    // comment for why this matters and the real bug this fixes.
    private readonly string _ownFileId;

    public string Guid { get; }

    private AssetYamlReader(YamlMappingNode root, string guid, string ownFileId)
    {
        _root = root;
        Guid = guid;
        _ownFileId = ownFileId;
    }

    public static AssetYamlReader Load(string assetPath, string guid)
    {
        var stream = new YamlStream();
        using var reader = new StreamReader(assetPath);
        stream.Load(reader);
        var doc = stream.Documents.Single();
        var rootNode = (YamlMappingNode)doc.RootNode;
        var monoBehaviour = (YamlMappingNode)rootNode["MonoBehaviour"];
        var ownFileId = rootNode.Anchor.Value ?? "";
        return new AssetYamlReader(monoBehaviour, guid, ownFileId);
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

    /// <summary>
    /// Reads a Unity object reference field ({fileID, guid, type}) and returns its guid, or null if
    /// unset (fileID: 0). A reference with NO `guid:` key is NOT automatically "unset" - Unity omits
    /// `guid`/`type` entirely for a "local" reference (one pointing at another object inside the
    /// SAME .asset file, by local fileID) - confirmed real: "Serving Board Stack.asset"'s
    /// `RequiresForShop` self-references its own root object this way (`{fileID: 11400000}`, no
    /// guid), which a prior version of this method silently treated as unset, producing an empty
    /// `requiresForShopIds` for an appliance that actually DOES require one of itself already owned
    /// - found from a real ground-truth divergence (day-1 5th shop slot: real game picks "Wellies",
    /// port predicted "Serving Boards" - both looked field-identical in extracted data until this
    /// was found). The only local fileID this code path ever needs to resolve is the asset's OWN
    /// root object (every Appliance .asset here is single-MonoBehaviour-root, so a self-reference
    /// is the only local reference shape that occurs) - resolved to this asset's own guid, which
    /// GuidResolver then naturally maps back to this same appliance's own ID downstream, same as any
    /// other reference.
    /// </summary>
    public string? Reference(string key)
    {
        var node = (YamlMappingNode)_root[key];
        if (node.Children.TryGetValue(new YamlScalarNode("guid"), out var guidNode))
        {
            return ((YamlScalarNode)guidNode).Value;
        }

        if (node.Children.TryGetValue(new YamlScalarNode("fileID"), out var fileIdNode)
            && ((YamlScalarNode)fileIdNode).Value == _ownFileId && _ownFileId != "0")
        {
            return Guid;
        }

        return null;
    }

    /// <summary>Reads a list of Unity object references, e.g. RequiredAppliances. See Reference's doc comment for the local/self-reference case this also handles.</summary>
    public List<string> ReferenceList(string key)
    {
        var seq = (YamlSequenceNode)_root[key];
        return seq.Children
            .Select(c => (YamlMappingNode)c)
            .Select(ResolveReferenceGuid)
            .Where(g => !string.IsNullOrEmpty(g))
            .Select(g => g!)
            .ToList();
    }

    private string? ResolveReferenceGuid(YamlMappingNode entry)
    {
        if (entry.Children.TryGetValue(new YamlScalarNode("guid"), out var guidNode))
        {
            return ((YamlScalarNode)guidNode).Value;
        }

        if (entry.Children.TryGetValue(new YamlScalarNode("fileID"), out var fileIdNode)
            && ((YamlScalarNode)fileIdNode).Value == _ownFileId && _ownFileId != "0")
        {
            return Guid;
        }

        return null;
    }

    /// <summary>
    /// Reads serializationData.ReferencedUnityObjects - the guids of Unity objects referenced from
    /// within the (possibly undecoded) Odin-serialized SerializedBytes blob. Used to find a
    /// GenericLocalisation-derived asset's per-locale "BasicInfo" child assets (each is its own
    /// plain-YAML asset with a Locale/Name/Description field - see GuidResolver-based lookup in
    /// SettingExtractor.ResolveEnglishDisplayName) without needing to decode the Odin blob itself.
    /// </summary>
    public List<string> ReferencedUnityObjectGuids()
    {
        if (!_root.Children.TryGetValue(new YamlScalarNode("serializationData"), out var sdNode)
            || sdNode is not YamlMappingNode sd
            || !sd.Children.TryGetValue(new YamlScalarNode("ReferencedUnityObjects"), out var listNode)
            || listNode is not YamlSequenceNode seq)
        {
            return [];
        }

        return seq.Children
            .Select(c => (YamlMappingNode)c)
            .Select(m => m.Children.TryGetValue(new YamlScalarNode("guid"), out var g) ? ((YamlScalarNode)g).Value : null)
            .Where(g => !string.IsNullOrEmpty(g))
            .Select(g => g!)
            .ToList();
    }

    /// <summary>
    /// Reads a plain list of scalar ints/enum values (e.g. Appliance.RequiresPhaseForShop, a
    /// List&lt;MenuPhase&gt;) - unlike <see cref="ScalarListField"/>, each entry here is the scalar
    /// itself, not a mapping with a named sub-field. An unset/empty field serializes as either `Key:`
    /// (no children) or `Key: []` depending on Unity's emitter - both read back as an empty list.
    /// </summary>
    public List<int> IntList(string key)
    {
        if (!_root.Children.TryGetValue(new YamlScalarNode(key), out var node) || node is not YamlSequenceNode seq)
        {
            return [];
        }

        return seq.Children.Cast<YamlScalarNode>().Select(s => int.Parse(s.Value!)).ToList();
    }

    /// <summary>
    /// Reads a list-of-mappings field (e.g. Dish.ResultingMenuItems) and pulls a named int sub-field
    /// out of each entry - e.g. ResultingMenuItems[].Phase, to determine which MenuPhase values a
    /// dish provides (needed for FilterBasic/UnlockSelectorMenuPhase card-draw filtering).
    /// </summary>
    public List<int> ScalarListField(string key, string subField)
    {
        if (!_root.Children.TryGetValue(new YamlScalarNode(key), out var node) || node is not YamlSequenceNode seq)
        {
            return [];
        }

        return seq.Children
            .Cast<YamlMappingNode>()
            .Where(m => m.Children.ContainsKey(new YamlScalarNode(subField)))
            .Select(m => int.Parse(((YamlScalarNode)m[subField]).Value!))
            .ToList();
    }

    /// <summary>
    /// Reads serializationData.SerializedBytes (the Odin-serialized binary blob, hex-encoded) as an
    /// OdinFieldReader, or null if absent/empty - see OdinFieldReader's class doc for what it can and
    /// can't decode from this blob.
    /// </summary>
    public OdinFieldReader? OdinBlob()
    {
        if (!_root.Children.TryGetValue(new YamlScalarNode("serializationData"), out var sdNode)
            || sdNode is not YamlMappingNode sd
            || !sd.Children.TryGetValue(new YamlScalarNode("SerializedBytes"), out var bytesNode)
            || bytesNode is not YamlScalarNode bytesScalar)
        {
            return null;
        }

        return OdinFieldReader.FromHex(bytesScalar.Value);
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
