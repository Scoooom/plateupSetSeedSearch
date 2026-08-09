using System.Text.RegularExpressions;

namespace PlateUpSeedTools.DataExtractor;

/// <summary>Resolves a Unity asset GUID to its file path by scanning `**/*.meta` files for `guid: &lt;value&gt;`.</summary>
public sealed partial class GuidResolver
{
    private readonly Dictionary<string, string> _guidToAssetPath = [];

    public static GuidResolver BuildFrom(string assetsRoot)
    {
        var resolver = new GuidResolver();
        foreach (var metaPath in Directory.EnumerateFiles(assetsRoot, "*.meta", SearchOption.AllDirectories))
        {
            var text = File.ReadAllText(metaPath);
            var match = GuidLineRegex().Match(text);
            if (!match.Success)
            {
                continue;
            }

            var assetPath = metaPath[..^".meta".Length];
            if (File.Exists(assetPath))
            {
                resolver._guidToAssetPath[match.Groups[1].Value] = assetPath;
            }
        }

        return resolver;
    }

    public string Resolve(string guid) =>
        _guidToAssetPath.TryGetValue(guid, out var path)
            ? path
            : throw new KeyNotFoundException($"No asset found for guid '{guid}'");

    public bool TryResolve(string guid, out string path) => _guidToAssetPath.TryGetValue(guid, out path!);

    [GeneratedRegex(@"guid:\s*([0-9a-f]{32})")]
    private static partial Regex GuidLineRegex();
}
