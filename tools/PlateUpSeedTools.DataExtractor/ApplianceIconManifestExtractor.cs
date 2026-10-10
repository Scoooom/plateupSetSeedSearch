using System.Text.Json;
using System.Text.RegularExpressions;
using YamlDotNet.RepresentationModel;

namespace PlateUpSeedTools.DataExtractor;

/// <summary>
/// Extracts a manifest (data/settings/shop/appliance-icon-manifest.json) that drives the offline
/// Blender icon-render pipeline (tools/IconRenderer/render_icons.py) - see
/// docs/data-pipeline.md's icon-render section.
///
/// Appliance assets carry no 2D icon/sprite field at all (confirmed by direct inspection) - only a
/// `Prefab` reference to a 3D GameObject hierarchy (mesh + material, no SpriteRenderer). So instead
/// of extracting an image, this extractor resolves, per appliance: which prefab file to render
/// (by name - AssetRipper's FBX re-export is expected to preserve the same `&lt;ObjectName&gt;.fbx`
/// naming it already uses for `.prefab`/`.asset` in the existing YAML export), and a per-material
/// color override table, since PlateUp's custom shaders (`_Color0`/`_Overlay` etc.) won't translate
/// through Unity's standard FBX material export the way a Standard/PBR shader would - the render
/// script applies these colors directly rather than trusting whatever Blender's FBX importer guesses
/// from the embedded material.
/// </summary>
public static partial class ApplianceIconManifestExtractor
{
    // Same signature-based identification ApplianceExtractor.cs uses - keeps this manifest's
    // appliance set identical to shop/appliances.json's 451 records (which already supersets the
    // 7-entry layout appliances.json set - confirmed by direct comparison).
    private static readonly string[] ApplianceSignatureFields = ["PurchaseCost", "IsPurchasable", "ShoppingTags", "ShopRequirementFilter", "StapleWhenMissing"];

    // Unity's sentinel guid for built-in engine primitives (default Plane/Cube/Capsule/etc. meshes
    // that ship inside Unity itself, not as a project asset) - these never resolve via GuidResolver
    // since there's no .meta file for them. Appliances reference one occasionally for simple
    // decorative sub-parts (e.g. Hob's disabled collider-visualization plane) - flagged as
    // "builtin:&lt;fileID&gt;" in the manifest rather than silently dropped, so the render script can
    // decide whether to substitute a primitive or skip the part.
    private const string BuiltinMeshGuid = "0000000000000000e000000000000000";

    // Color fields that describe the material's actual surface appearance, in priority order. Most
    // materials use "_Color0"; a few (e.g. liquids like "Sink Water") use plain "_Color" instead.
    private static readonly string[] PreferredColorFields = ["_Color0", "_Color", "_BaseColor", "_MainColor"];

    public static void ExtractIconManifest(GuidResolver resolver, string assetsRoot, string outputRoot, string? modelsOutputDir = null)
    {
        var shopDir = Path.Combine(outputRoot, "shop");
        Directory.CreateDirectory(shopDir);

        var gameDataPath = FindAssetByName(assetsRoot, "GameData.asset");
        var gameDataGuid = TryReadMetaGuid(gameDataPath) ?? throw new InvalidOperationException($"No guid for {gameDataPath}");
        var gameData = AssetYamlReader.Load(gameDataPath, gameDataGuid);

        var entries = new List<ManifestEntry>();
        var unresolvedPrefabs = new List<string>();

        foreach (var guid in gameData.ReferenceList("GameDataObjects"))
        {
            if (!resolver.TryResolve(guid, out var assetPath) || !HasApplianceSignature(assetPath))
            {
                continue;
            }

            AssetYamlReader reader;
            try
            {
                reader = AssetYamlReader.Load(assetPath, guid);
            }
            catch
            {
                continue;
            }

            var id = reader.Int("ID");
            var name = ResolveEnglishDisplayNameOrFallback(resolver, reader);
            var slug = Slugify(name);

            string? prefabName = null;
            string? prefabPath = null;
            var materials = new List<object>();

            var prefabGuid = reader.Reference("Prefab");
            if (prefabGuid is not null && resolver.TryResolve(prefabGuid, out var resolvedPrefabPath))
            {
                prefabPath = resolvedPrefabPath;
                prefabName = Path.GetFileNameWithoutExtension(prefabPath);

                foreach (var materialGuid in TryGetDistinctMaterialGuids(prefabPath))
                {
                    if (materialGuid == BuiltinMeshGuid || !resolver.TryResolve(materialGuid, out var materialPath))
                    {
                        continue;
                    }

                    var info = TryExtractMaterialInfo(materialPath, resolver);
                    if (info is not null)
                    {
                        materials.Add(new { info.Value.Name, color = info.Value.Color, info.Value.TexturePath });
                    }
                }
            }
            else
            {
                unresolvedPrefabs.Add(name);
            }

            entries.Add(new ManifestEntry(id, name, slug, prefabName, prefabPath, materials));
        }

        // 19 distinct English display names are shared by more than one appliance ID in the real
        // data (e.g. "Counter" is both the real physical counter AND a logic-only, invisible
        // "Item Return When Lost" entity with an empty prefab - confirmed by direct inspection, not
        // an extractor bug). Every consumer of these icons (LayoutImageRenderer, explorer-shop.js)
        // resolves purely by slugified name with no ID, so two colliding entries would silently
        // race to overwrite the same output PNG - keep only the richest entry per slug (most
        // materials = most likely to be the real visible model) so a logic-only duplicate can never
        // clobber a real one.
        var deduped = entries
            .GroupBy(e => e.Slug)
            .Select(g => g.OrderByDescending(e => e.Materials.Count).ThenBy(e => e.Id).First())
            .OrderBy(e => e.Name, StringComparer.Ordinal)
            .ToList();

        // Model (OBJ/MTL) writing happens AFTER dedup, keyed only by the surviving entry per slug -
        // doing this during the first pass would let a losing duplicate's write race the winner's
        // and clobber it on disk even though the manifest JSON correctly only lists the winner.
        var modelsWritten = 0;
        if (modelsOutputDir is not null)
        {
            foreach (var entry in deduped)
            {
                if (entry.PrefabPath is null)
                {
                    continue;
                }

                var materialsByGuid = new Dictionary<string, (string Name, float[] Color, string? TexturePath)>();
                foreach (var materialGuid in TryGetDistinctMaterialGuids(entry.PrefabPath))
                {
                    if (materialGuid == BuiltinMeshGuid || !resolver.TryResolve(materialGuid, out var materialPath))
                    {
                        continue;
                    }

                    var info = TryExtractMaterialInfo(materialPath, resolver);
                    if (info is not null)
                    {
                        materialsByGuid[materialGuid] = info.Value;
                    }
                }

                if (ApplianceModelExtractor.TryWriteObjModel(entry.PrefabPath, resolver, materialsByGuid, modelsOutputDir, entry.Slug))
                {
                    modelsWritten++;
                }
            }
        }

        var manifest = deduped.Select(e => new
        {
            e.Id,
            e.Name,
            e.Slug,
            e.PrefabName,
            e.Materials,
        });

        var jsonOptions = new JsonSerializerOptions { WriteIndented = true, PropertyNamingPolicy = JsonNamingPolicy.CamelCase };
        File.WriteAllText(Path.Combine(shopDir, "appliance-icon-manifest.json"), JsonSerializer.Serialize(manifest, jsonOptions));
        Console.WriteLine($"  Wrote icon-render manifest for {deduped.Count} appliance(s) to shop/appliance-icon-manifest.json ({entries.Count - deduped.Count} name collision(s) deduped)");
        if (modelsOutputDir is not null)
        {
            Console.WriteLine($"  Wrote {modelsWritten}/{deduped.Count} OBJ model(s) to {modelsOutputDir}");
        }

        if (unresolvedPrefabs.Count > 0)
        {
            Console.WriteLine($"  {unresolvedPrefabs.Count} appliance(s) have no resolvable Prefab (will be skipped by the render script): {string.Join(", ", unresolvedPrefabs)}");
        }
    }

    private sealed record ManifestEntry(int Id, string Name, string Slug, string? PrefabName, string? PrefabPath, List<object> Materials);

    private static List<string> TryGetDistinctMaterialGuids(string prefabPath)
    {
        try
        {
            return ApplianceModelExtractor.ParsePrefabMeshParts(prefabPath).SelectMany(p => p.MaterialGuids).Distinct().ToList();
        }
        catch
        {
            return [];
        }
    }

    /// <summary>
    /// Reads a Material asset's base color and optional overlay texture. Material files are
    /// single-document but root-keyed "Material" (not "MonoBehaviour"), so again this doesn't go
    /// through AssetYamlReader. Alpha is always reported as 1.0 regardless of the source value - this
    /// custom shader's `a` channel doesn't mean literal opacity for most appliances (commonly `a: 0`
    /// on fully-opaque solid appliances), and the one real exception (e.g. "Sink Water"'s translucency)
    /// isn't worth the risk of an icon silently rendering invisible for appliances where `a` turns out
    /// to mean something else entirely - a deliberate simplification for icon-recognizability, not an
    /// attempt at shader-accurate rendering.
    /// </summary>
    private static (string Name, float[] Color, string? TexturePath)? TryExtractMaterialInfo(string materialPath, GuidResolver resolver)
    {
        var stream = new YamlStream();
        using (var fileReader = new StreamReader(materialPath))
        {
            stream.Load(fileReader);
        }

        var doc = stream.Documents.SingleOrDefault();
        if (doc?.RootNode is not YamlMappingNode root || !root.Children.TryGetValue(new YamlScalarNode("Material"), out var materialNode)
            || materialNode is not YamlMappingNode material)
        {
            return null;
        }

        var name = material.Children.TryGetValue(new YamlScalarNode("m_Name"), out var nameNode) && nameNode is YamlScalarNode nameScalar
            ? nameScalar.Value ?? Path.GetFileNameWithoutExtension(materialPath)
            : Path.GetFileNameWithoutExtension(materialPath);

        float r = 0.8f, g = 0.8f, b = 0.8f;
        if (material.Children.TryGetValue(new YamlScalarNode("m_SavedProperties"), out var savedPropsNode) && savedPropsNode is YamlMappingNode savedProps
            && savedProps.Children.TryGetValue(new YamlScalarNode("m_Colors"), out var colorsNode) && colorsNode is YamlMappingNode colors)
        {
            // Preferred fields first; otherwise fall back to the first field that both (a) isn't a
            // known non-appearance utility field (emission/overlay-math/UI-clip-rect/mask-coord -
            // some materials, e.g. TextMeshPro font atlases pulled in incidentally by a decorative
            // text sub-mesh, have NO real color field at all, only these) and (b) actually looks like
            // a color (every component in [0,1] - UI shaders reuse the Color4 struct to smuggle
            // pixel-space rects/matrices like `{r: -32767, g: -32767, b: 32767, a: 32767}`, which
            // would otherwise read back as a nonsense "color").
            var colorField = PreferredColorFields.FirstOrDefault(f => colors.Children.ContainsKey(new YamlScalarNode(f)))
                ?? colors.Children
                    .Where(kv => kv.Key is YamlScalarNode k && !NonAppearanceColorFieldNameRegex().IsMatch(k.Value ?? "")
                        && kv.Value is YamlMappingNode m && LooksLikeColor(m))
                    .Select(kv => ((YamlScalarNode)kv.Key).Value)
                    .FirstOrDefault();

            if (colorField is not null && colors.Children.TryGetValue(new YamlScalarNode(colorField), out var colorNode) && colorNode is YamlMappingNode colorMap)
            {
                r = ReadFloat(colorMap, "r", r);
                g = ReadFloat(colorMap, "g", g);
                b = ReadFloat(colorMap, "b", b);
            }
        }

        string? texturePath = null;
        if (material.Children.TryGetValue(new YamlScalarNode("m_SavedProperties"), out var savedProps2Node) && savedProps2Node is YamlMappingNode savedProps2
            && savedProps2.Children.TryGetValue(new YamlScalarNode("m_TexEnvs"), out var texEnvsNode) && texEnvsNode is YamlMappingNode texEnvs
            && texEnvs.Children.TryGetValue(new YamlScalarNode("_Overlay"), out var overlayNode) && overlayNode is YamlMappingNode overlay
            && overlay.Children.TryGetValue(new YamlScalarNode("m_Texture"), out var textureRefNode) && textureRefNode is YamlMappingNode textureRef
            && textureRef.Children.TryGetValue(new YamlScalarNode("guid"), out var textureGuidNode) && textureGuidNode is YamlScalarNode textureGuidScalar
            && !string.IsNullOrEmpty(textureGuidScalar.Value)
            && resolver.TryResolve(textureGuidScalar.Value, out var resolvedTexturePath))
        {
            texturePath = resolvedTexturePath;
        }

        // A preferred field (`_Color0`/`_Color`) is trusted unconditionally above (unlike the
        // fallback search, which requires in-range values) - some are legitimately HDR/emissive
        // (e.g. "Fairy Light - Green" at r/g/b > 1 for bloom). Clamp to [0,1] here since this feeds
        // a plain non-HDR Blender base-color input - saturating a glowing decoration to its brightest
        // non-HDR equivalent reads fine in a small icon; passing >1 through would misrender.
        return (name, new[] { Math.Clamp(r, 0f, 1f), Math.Clamp(g, 0f, 1f), Math.Clamp(b, 0f, 1f), 1.0f }, texturePath);
    }

    [GeneratedRegex("Emission|Overlay|Clip|Mask", RegexOptions.IgnoreCase)]
    private static partial Regex NonAppearanceColorFieldNameRegex();

    private static bool LooksLikeColor(YamlMappingNode colorMap) =>
        new[] { "r", "g", "b" }.All(c => ReadFloat(colorMap, c, float.NaN) is >= 0f and <= 1f);

    private static float ReadFloat(YamlMappingNode map, string key, float fallback) =>
        map.Children.TryGetValue(new YamlScalarNode(key), out var node) && node is YamlScalarNode scalar
            && float.TryParse(scalar.Value, System.Globalization.CultureInfo.InvariantCulture, out var value)
            ? value
            : fallback;

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

    private static bool HasApplianceSignature(string assetPath)
    {
        string text;
        try
        {
            text = File.ReadAllText(assetPath);
        }
        catch
        {
            return false;
        }

        return ApplianceSignatureFields.All(f => text.Contains($"\n  {f}:"));
    }

    private static string ResolveEnglishDisplayNameOrFallback(GuidResolver resolver, AssetYamlReader reader)
    {
        const int englishLocale = 1;
        foreach (var guid in reader.ReferencedUnityObjectGuids())
        {
            if (!resolver.TryResolve(guid, out var path))
            {
                continue;
            }

            AssetYamlReader child;
            try
            {
                child = AssetYamlReader.Load(path, guid);
            }
            catch
            {
                continue;
            }

            if (child.TryScalar("Locale", out var localeStr)
                && int.TryParse(localeStr, out var locale)
                && locale == englishLocale
                && child.TryScalar("Name", out var name)
                && !string.IsNullOrWhiteSpace(name))
            {
                return name;
            }
        }

        return reader.GetName();
    }

    private static string? TryReadMetaGuid(string assetPath)
    {
        var metaPath = assetPath + ".meta";
        if (!File.Exists(metaPath))
        {
            return null;
        }

        var match = Regex.Match(File.ReadAllText(metaPath), @"guid:\s*([0-9a-f]{32})");
        return match.Success ? match.Groups[1].Value : null;
    }

    // Port of LayoutImageRenderer.Slugify / explorer-shop.js's slugifyApplianceName - MUST stay
    // byte-identical to both, since the render script names output files by this slug and both the
    // server-side renderer and the Blueprint Explorer frontend resolve icons purely by
    // File.Exists-on-slugged-filename / <img> onerror fallback, with no other lookup table consulted.
    [GeneratedRegex("[^a-z0-9]+")]
    private static partial Regex SlugRegex();

    private static string Slugify(string name) => SlugRegex().Replace(name.ToLowerInvariant(), "-").Trim('-');
}
