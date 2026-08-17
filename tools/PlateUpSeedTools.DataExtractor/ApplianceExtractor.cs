using System.Text.Json;

namespace PlateUpSeedTools.DataExtractor;

/// <summary>
/// Extracts the full purchasable-Appliance table (data/settings/shop/appliances.json) for the
/// shop/blueprint-offer simulator - see docs/game-domain-knowledge.md's "Shop / appliance purchase
/// RNG" section for the mechanics these fields drive.
///
/// Deliberately a SEPARATE file/dataset from the existing data/settings/appliances.json (written by
/// SettingExtractor.WriteAppliancesFile) - that one is a small id-&gt;name lookup for the ~7
/// appliance IDs the layout-preview renderer needs icons for, unrelated purpose. Do not merge these.
/// </summary>
public static class ApplianceExtractor
{
    // Every purchasable-Appliance asset shares this exact field signature - same "identify by field
    // signature, not filename or script guid" approach CardExtractor uses for Unlocks. Confirmed 44
    // matches across the whole asset tree.
    private static readonly string[] ApplianceSignatureFields = ["PurchaseCost", "IsPurchasable", "ShoppingTags", "ShopRequirementFilter", "StapleWhenMissing"];

    /// <summary>
    /// Extracts every Appliance asset to data/settings/shop/appliances.json, in
    /// <c>GameData.asset</c>'s own <c>GameDataObjects</c> list order - NOT filesystem-enumeration
    /// order. This matters: unlike Unlocks (where the real game explicitly re-sorts candidates by ID
    /// before use, see GameDataRepository.AllUnlocksOrderedById), CreateShopOptions.CreateOptions
    /// iterates `base.Data.Get&lt;Appliance&gt;()` with no sort at all - the candidate list's initial
    /// order (before HandleNewShop's per-slot Fisher-Yates ShuffleInPlace) is whatever order
    /// GameData.asset registers appliances in. A ground-truth capture (seed `vqrj`, 2026-08-16)
    /// confirmed the shuffle ALGORITHM/RNG stream was byte-correct but produced different final
    /// picks than the real game specifically because this extractor's original filesystem-order
    /// list didn't match - Fisher-Yates permutes POSITIONS deterministically given the RNG stream,
    /// but which candidate starts in which position still depends on this initial order.
    /// </summary>
    public static void ExtractShopAppliances(GuidResolver resolver, string assetsRoot, string outputRoot)
    {
        var shopDir = Path.Combine(outputRoot, "shop");
        Directory.CreateDirectory(shopDir);

        var appliances = new List<object>();
        var nonEmptyCounts = new Dictionary<string, int>
        {
            ["RequiresForShop"] = 0,
            ["RequiresProcessForShop"] = 0,
            ["RequiresIngredientForShop"] = 0,
            ["RequiresPhaseForShop"] = 0,
            ["ThemeRequired"] = 0,
            ["ShopRequirementFilter"] = 0,
        };

        var gameDataPath = FindAssetByName(assetsRoot, "GameData.asset");
        var gameDataGuid = TryReadMetaGuid(gameDataPath) ?? throw new InvalidOperationException($"No guid for {gameDataPath}");
        var gameData = AssetYamlReader.Load(gameDataPath, gameDataGuid);

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

            appliances.Add(ExtractAppliance(resolver, reader, nonEmptyCounts));
        }

        var jsonOptions = new JsonSerializerOptions { WriteIndented = true };
        File.WriteAllText(Path.Combine(shopDir, "appliances.json"), JsonSerializer.Serialize(appliances, jsonOptions));
        Console.WriteLine($"  Extracted {appliances.Count} shop appliance(s) to shop/appliances.json");

        // Throwaway diagnostic (not shipped as a feature): feeds the engine port's stub-vs-implement
        // decision for FilterByRequiredCourse/FilterByRequiredProcessOrIngredient/FilterByRequirements/
        // FilterByTheme - if these are all near-zero across the real dataset, stubbing those four
        // filters to always-pass is safe for v1; if not, that stub needs revisiting before it ships.
        Console.WriteLine("  Non-empty field counts (informs which shop filters need full implementation):");
        foreach (var (field, count) in nonEmptyCounts)
        {
            Console.WriteLine($"    {field}: {count}/{appliances.Count}");
        }
    }

    private static object ExtractAppliance(GuidResolver resolver, AssetYamlReader reader, Dictionary<string, int> nonEmptyCounts)
    {
        var requiresForShopIds = reader.ReferenceList("RequiresForShop").Select(g => ResolveReferencedId(resolver, g)).Where(x => x is not null).Select(x => x!.Value).ToList();
        var requiresProcessForShopIds = reader.ReferenceList("RequiresProcessForShop").Select(g => ResolveReferencedId(resolver, g)).Where(x => x is not null).Select(x => x!.Value).ToList();
        var requiresIngredientForShopIds = reader.ReferenceList("RequiresIngredientForShop").Select(g => ResolveReferencedId(resolver, g)).Where(x => x is not null).Select(x => x!.Value).ToList();
        var requiresPhaseForShopIds = reader.IntList("RequiresPhaseForShop");
        var themeRequired = reader.Int("ThemeRequired");
        var shopRequirementFilter = reader.Int("ShopRequirementFilter");

        if (requiresForShopIds.Count > 0) nonEmptyCounts["RequiresForShop"]++;
        if (requiresProcessForShopIds.Count > 0) nonEmptyCounts["RequiresProcessForShop"]++;
        if (requiresIngredientForShopIds.Count > 0) nonEmptyCounts["RequiresIngredientForShop"]++;
        if (requiresPhaseForShopIds.Count > 0) nonEmptyCounts["RequiresPhaseForShop"]++;
        if (themeRequired != 0) nonEmptyCounts["ThemeRequired"]++;
        if (shopRequirementFilter != 0) nonEmptyCounts["ShopRequirementFilter"]++;

        return new
        {
            id = reader.Int("ID"),
            name = ResolveEnglishDisplayNameOrFallback(resolver, reader),
            purchaseCost = reader.Int("PurchaseCost"),
            isPurchasable = reader.Bool("IsPurchasable"),
            isPurchasableAsUpgrade = reader.Bool("IsPurchasableAsUpgrade"),
            restrictedToSeason = reader.Int("RestrictedToSeason"),
            themeRequired,
            shoppingTags = reader.Int("ShoppingTags"),
            shopRequirementFilter,
            requiresForShopIds,
            requiresProcessForShopIds,
            requiresIngredientForShopIds,
            requiresPhaseForShopIds,
            stapleWhenMissing = reader.Bool("StapleWhenMissing"),
            sellOnlyAsDuplicate = reader.Bool("SellOnlyAsDuplicate"),
            sellOnlyAsUnique = reader.Bool("SellOnlyAsUnique"),
            sellOnlyIfPlatesNeeded = reader.Bool("SellOnlyIfPlatesNeeded"),
            preventSale = reader.Bool("PreventSale"),
        };
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

    private static int? ResolveReferencedId(GuidResolver resolver, string? guid)
    {
        if (guid is null || !resolver.TryResolve(guid, out var path))
        {
            return null;
        }

        try
        {
            return AssetYamlReader.Load(path, guid).Int("ID");
        }
        catch
        {
            return null;
        }
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

        var match = System.Text.RegularExpressions.Regex.Match(File.ReadAllText(metaPath), @"guid:\s*([0-9a-f]{32})");
        return match.Success ? match.Groups[1].Value : null;
    }
}
