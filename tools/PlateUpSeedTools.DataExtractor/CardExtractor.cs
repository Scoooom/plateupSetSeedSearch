using System.Text.Json;

namespace PlateUpSeedTools.DataExtractor;

/// <summary>
/// Extracts starting-dish / heat-level / card-draw data (data/cards/*.json) for the seed-search
/// feature - see docs/game-domain-knowledge.md's "Starting dish / heat / card-draw RNG" section for
/// the full mechanics this mirrors, and its "Odin partial-field-read technique" note for why the
/// ModularUnlockPack details below are a hand-curated table rather than generically parsed.
/// </summary>
public static class CardExtractor
{
    // Every Unlock-derived asset (Dish + UnlockCard) shares this exact field signature - same
    // "identify by field signature, not filename or script guid" approach as RestaurantSetting
    // (see game-domain-knowledge.md's "do not trust a shared m_Script guid" note). Confirmed 261
    // matches across the whole asset tree.
    private static readonly string[] UnlockSignatureFields = ["IsUnlockable", "HasSubOptions", "UnlockGroup", "CardType"];

    /// <summary>Extracts every Unlock-derived asset (Dish + UnlockCard) to data/cards/unlocks.json.</summary>
    public static void ExtractUnlocks(GuidResolver resolver, string assetsRoot, string outputRoot)
    {
        var cardsDir = Path.Combine(outputRoot, "cards");
        Directory.CreateDirectory(cardsDir);

        var (graphRequires, graphBlockedBy, graphMenuPhases) = BuildUnlockGraphRelationships(resolver, assetsRoot);

        var unlocks = new List<object>();
        foreach (var assetPath in Directory.EnumerateFiles(assetsRoot, "*.asset", SearchOption.AllDirectories))
        {
            if (!HasUnlockSignature(assetPath))
            {
                continue;
            }

            var guid = TryReadMetaGuid(assetPath);
            if (guid is null)
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

            unlocks.Add(ExtractUnlock(resolver, reader, graphRequires, graphBlockedBy, graphMenuPhases));
        }

        var jsonOptions = new JsonSerializerOptions { WriteIndented = true };
        File.WriteAllText(Path.Combine(cardsDir, "unlocks.json"), JsonSerializer.Serialize(unlocks, jsonOptions));
        Console.WriteLine($"  Extracted {unlocks.Count} Unlock-derived asset(s) to cards/unlocks.json");
    }

    private static object ExtractUnlock(
        GuidResolver resolver, AssetYamlReader reader,
        Dictionary<int, List<int>> graphRequires, Dictionary<int, List<int>> graphBlockedBy, Dictionary<int, List<int>> graphMenuPhases)
    {
        var isDish = reader.TryScalar("Difficulty", out _);
        var hasSubOptions = reader.Bool("HasSubOptions");
        var id = reader.Int("ID");

        // Requires/BlockedBy/menu-phase-per-dish are NOT reliably on the Unlock's own asset fields -
        // HardcodedRequirements/HardcodedBlockers/ResultingMenuItems are near-always empty in practice
        // (confirmed: 2026-08-10, a user's real-gameplay report that "family-locked" cards like
        // "Burger Toppings" and DishType.Main dishes were being offered when they shouldn't be led to
        // finding the REAL source - a separate KitchenEditor.UnlockGraph node-graph asset per dish
        // family (Burger Unlock.asset, Steak Unlocks.asset, etc., all referenced from GameData.asset's
        // UnlockGraphs list), populated into Unlock.Requires/BlockedBy/Dish.UnlocksMenuItems at game
        // startup via UnlockGraph.AddData - see game-domain-knowledge.md's "Card draw RNG" section for
        // the full trace). BuildUnlockGraphRelationships extracts that graph data generically (same
        // xNode format as LayoutGraph, no hand-curation needed unlike packs.json). Union'd with the
        // plain per-asset fields since a few base dishes (e.g. those pickable at run start, not needing
        // graph-based prerequisite wiring) DO have ResultingMenuItems populated directly.
        var hardcodedRequires = reader.ReferenceList("HardcodedRequirements").Select(g => ResolveReferencedId(resolver, g));
        var hardcodedBlockedBy = reader.ReferenceList("HardcodedBlockers").Select(g => ResolveReferencedId(resolver, g));
        var plainMenuPhases = isDish ? reader.ScalarListField("ResultingMenuItems", "Phase") : [];

        var requiresIds = hardcodedRequires.Where(x => x is not null).Select(x => x!.Value).Concat(graphRequires.GetValueOrDefault(id, [])).Distinct().ToList();
        var blockedByIds = hardcodedBlockedBy.Where(x => x is not null).Select(x => x!.Value).Concat(graphBlockedBy.GetValueOrDefault(id, [])).Distinct().ToList();
        var menuPhases = plainMenuPhases.Concat(graphMenuPhases.GetValueOrDefault(id, [])).Distinct().ToList();

        return new
        {
            id,
            name = ResolveEnglishDisplayNameOrFallback(resolver, reader),
            kind = isDish ? "Dish" : "UnlockCard",
            unlockGroup = reader.Int("UnlockGroup"),
            cardType = reader.Int("CardType"),
            isUnlockable = reader.Bool("IsUnlockable"),
            hasSubOptions,
            optionCard1Id = hasSubOptions ? ResolveReferencedId(resolver, reader.Reference("OptionCard1")) : null,
            optionCard2Id = hasSubOptions ? ResolveReferencedId(resolver, reader.Reference("OptionCard2")) : null,
            minimumFranchiseTier = reader.Int("MinimumFranchiseTier"),
            isSpecificFranchiseTier = reader.Bool("IsSpecificFranchiseTier"),
            blocksAllOtherFood = reader.Bool("BlocksAllOtherFood"),
            allowedFoodIds = reader.ReferenceList("AllowedFoods").Select(g => ResolveReferencedId(resolver, g)).ToList(),
            requiresIds,
            blockedByIds,
            heatCardDisplayedNumber = reader.Int("HeatCardDisplayedNumber"),
            dishType = isDish ? reader.Int("Type") : (int?)null,
            // MenuPhase values this dish provides (FilterBasic/UnlockSelectorMenuPhase need this - e.g.
            // "is this a Starter/Side dish", "does the player already own a Main dish").
            menuPhases = isDish ? menuPhases : null,
        };
    }

    /// <summary>
    /// Walks every KitchenEditor.UnlockGraph asset referenced from GameData.asset's UnlockGraphs list
    /// (one per dish family - Burger Unlock.asset, Steak Unlocks.asset, Pie Unlocks.asset, etc., 15 as
    /// of last extraction) and reconstructs the Requires/BlockedBy/menu-phase relationships
    /// UnlockGraph.AddData builds at runtime (RestaurantMode's GameDataConstructor.BuildGameData calls
    /// `unlockGraph.AddData(this)` for each, which does `unlock.Requires.Add(...)` from
    /// UnlockReferenceNode "Prerequisites"/"Blockers" port connections, and
    /// `dish.UnlocksMenuItems.Add(...)` from AddMenuItem nodes connected via their "UnlockedBy" port).
    /// Same xNode graph format as LayoutGraph (AssetYamlReader.Ports() already handles it generically)
    /// - unlike packs.json, this needs no hand-curation, every graph is parsed the same way.
    /// </summary>
    private static (Dictionary<int, List<int>> Requires, Dictionary<int, List<int>> BlockedBy, Dictionary<int, List<int>> MenuPhases)
        BuildUnlockGraphRelationships(GuidResolver resolver, string assetsRoot)
    {
        var requires = new Dictionary<int, List<int>>();
        var blockedBy = new Dictionary<int, List<int>>();
        var menuPhases = new Dictionary<int, List<int>>();

        static void Add(Dictionary<int, List<int>> map, int key, int value)
        {
            if (!map.TryGetValue(key, out var list))
            {
                map[key] = list = [];
            }

            list.Add(value);
        }

        var gameDataPath = FindAssetByName(assetsRoot, "GameData.asset");
        var gameDataGuid = TryReadMetaGuid(gameDataPath) ?? throw new InvalidOperationException($"No guid for {gameDataPath}");
        var gameData = AssetYamlReader.Load(gameDataPath, gameDataGuid);
        var graphGuids = gameData.ReferenceList("UnlockGraphs");

        foreach (var graphGuid in graphGuids)
        {
            if (!resolver.TryResolve(graphGuid, out var graphPath))
            {
                continue;
            }

            var graph = AssetYamlReader.Load(graphPath, graphGuid);
            var nodeGuids = graph.ReferenceList("nodes");

            var nodeReaders = new Dictionary<string, AssetYamlReader>();
            var nodeToUnlockId = new Dictionary<string, int>();

            // Pass 1: load every node, and for UnlockReferenceNodes (identified by having a
            // "Prerequisites" port, per UnlockReferenceNode.cs), resolve Item -> the referenced
            // Unlock's numeric ID.
            foreach (var nodeGuid in nodeGuids)
            {
                if (!resolver.TryResolve(nodeGuid, out var nodePath))
                {
                    continue;
                }

                AssetYamlReader node;
                try
                {
                    node = AssetYamlReader.Load(nodePath, nodeGuid);
                }
                catch
                {
                    continue;
                }

                nodeReaders[nodeGuid] = node;
                var ports = node.Ports();
                if (!ports.ContainsKey("Prerequisites"))
                {
                    continue;
                }

                var itemGuid = node.Reference("Item");
                if (itemGuid is null || !resolver.TryResolve(itemGuid, out var itemPath))
                {
                    continue;
                }

                try
                {
                    nodeToUnlockId[nodeGuid] = AssetYamlReader.Load(itemPath, itemGuid).Int("ID");
                }
                catch
                {
                    // Not every ReferenceNode<T>'s Item resolves to something with a plain ID field
                    // (e.g. a broken/unset reference) - skip, matches SettingExtractor's tolerance for
                    // orphaned graph nodes elsewhere.
                }
            }

            // Pass 2: for each UnlockReferenceNode, its "Prerequisites"/"Blockers" input port
            // connections point at the OTHER UnlockReferenceNodes it requires/is blocked by.
            foreach (var (nodeGuid, unlockId) in nodeToUnlockId)
            {
                var ports = nodeReaders[nodeGuid].Ports();
                if (ports.TryGetValue("Prerequisites", out var prereqConns))
                {
                    foreach (var (_, remoteGuid) in prereqConns)
                    {
                        if (nodeToUnlockId.TryGetValue(remoteGuid, out var reqId))
                        {
                            Add(requires, unlockId, reqId);
                        }
                    }
                }

                if (ports.TryGetValue("Blockers", out var blockConns))
                {
                    foreach (var (_, remoteGuid) in blockConns)
                    {
                        if (nodeToUnlockId.TryGetValue(remoteGuid, out var blockId))
                        {
                            Add(blockedBy, unlockId, blockId);
                        }
                    }
                }
            }

            // Pass 3: AddMenuItem nodes (identified by having an "UnlockedBy" port, per
            // AddMenuItem.cs) record a MenuPhase on whichever UnlockReferenceNode's "Unlocks" output
            // feeds their "UnlockedBy" input - that's the dish this menu item (and phase) belongs to.
            foreach (var (nodeGuid, node) in nodeReaders)
            {
                var ports = node.Ports();
                if (!ports.ContainsKey("UnlockedBy") || !node.TryScalar("Phase", out var phaseStr) || !int.TryParse(phaseStr, out var phase))
                {
                    continue;
                }

                if (!ports.TryGetValue("UnlockedBy", out var sourceConns))
                {
                    continue;
                }

                foreach (var (remotePortName, remoteGuid) in sourceConns)
                {
                    if (remotePortName == "Unlocks" && nodeToUnlockId.TryGetValue(remoteGuid, out var dishUnlockId))
                    {
                        Add(menuPhases, dishUnlockId, phase);
                    }
                }
            }
        }

        return (requires, blockedBy, menuPhases);
    }

    /// <summary>Extracts the default 15 heat + 3 chill card ladder to data/cards/heat-levels.json.</summary>
    public static void ExtractHeatLevels(GuidResolver resolver, string assetsRoot, string outputRoot)
    {
        var path = FindAssetByName(assetsRoot, "Heat Levels - Default.asset");
        var guid = TryReadMetaGuid(path) ?? throw new InvalidOperationException($"No guid for {path}");
        var reader = AssetYamlReader.Load(path, guid);

        // Cards[0..14] then ChillCards[0..2], in that order, via ReferencedUnityObjects - see
        // game-domain-knowledge.md's Heat Levels table for how this index layout was confirmed.
        var refGuids = reader.ReferencedUnityObjectGuids();
        var heatCardIds = refGuids.Take(15).Select(g => ResolveReferencedId(resolver, g)).ToList();
        var chillCardIds = refGuids.Skip(15).Take(3).Select(g => ResolveReferencedId(resolver, g)).ToList();

        var data = new { heatCardIds, chillCardIds };
        var jsonOptions = new JsonSerializerOptions { WriteIndented = true };
        File.WriteAllText(Path.Combine(outputRoot, "cards", "heat-levels.json"), JsonSerializer.Serialize(data, jsonOptions));
        Console.WriteLine($"  Extracted heat ladder ({heatCardIds.Count} heat + {chillCardIds.Count} chill) to cards/heat-levels.json");
    }

    /// <summary>
    /// Writes data/cards/packs.json - the UnlockPack graph (Default Composite Pack, March Pack, and
    /// their sub-packs, plus the two known heat-bonus packs). Composite packs' Packs list is plain
    /// YAML (extracted generically below); ModularUnlockPack internals (Filter/Sorters/
    /// ConditionalOptions) live inside an Odin-serialized blob with no object references to walk, so
    /// this is a hand-curated table decoded via the Odin partial-field-read technique documented in
    /// game-domain-knowledge.md, not a generic parse - extend this table (not a generic decoder) if a
    /// new setting needs a pack not listed here. `santa-workshop`'s Christmas Pack is NOT included
    /// yet (its extra Christmas Special Card sub-pack isn't decoded) - don't wire up card-search
    /// support for santa-workshop until it is.
    /// </summary>
    public static void WritePacksFile(GuidResolver resolver, string assetsRoot, string outputRoot)
    {
        int FindId(string assetName)
        {
            var path = FindAssetByName(assetsRoot, assetName);
            var guid = TryReadMetaGuid(path) ?? throw new InvalidOperationException($"No guid for {path}");
            return AssetYamlReader.Load(path, guid).Int("ID");
        }

        var franchiseId = FindId("Franchise Cards Pack.asset");
        var normalId = FindId("Normal Cards Pack.asset");
        var themeId = FindId("Theme Cards Pack.asset");
        var marchCardsId = FindId("March Cards.asset");
        var defaultCompositeId = FindId("Default Composite Pack.asset");
        var marchPackId = FindId("March Pack.asset");
        var heat2PackId = FindId("Heat - Food Card After Day 2 Pack.asset");
        var heat10PackId = FindId("Heat - Starter Or Dessert Pack.asset");

        // Default UnlockSorterPriority config shared by the standard schedule packs (Franchise/Normal/
        // Theme): PriorityProbability=0.5, PrioritiseRequirements=true, empty Groups/DishTypes -
        // decoded via the Odin partial-read technique, PrioritiseRequirements/Groups/DishTypes assumed
        // at their class defaults pending ground-truth confirmation (see game-domain-knowledge.md).
        object StandardPriority(double probability) => new { probability, prioritiseRequirements = true, groups = Array.Empty<int>(), dishTypes = Array.Empty<int>() };

        // FilterBasic with every Ignore*/Allow* flag at its class default (all false) - assumed
        // identical across every currently-known pack (all visibly use the same field list in their
        // Odin blob dump); flag for ground-truth correction if a pack's real behaviour implies
        // otherwise (e.g. base dishes appearing as card draws would mean AllowBaseDishes=true somewhere).
        object DefaultFilter() => new
        {
            ignoreUnlockability = false,
            ignoreFranchiseTier = false,
            ignoreDuplicateFilter = false,
            ignoreRequirements = false,
            allowBaseDishes = false,
            ignoreBlocksOtherFood = false,
            ignoreFilterByHasMain = false,
        };

        var packs = new Dictionary<string, object>
        {
            [defaultCompositeId.ToString()] = new { name = "Default Composite Pack", type = "composite", subPackIds = new[] { franchiseId, normalId, themeId } },
            [marchPackId.ToString()] = new { name = "March Pack", type = "composite", subPackIds = new[] { franchiseId, themeId, marchCardsId } },
            [franchiseId.ToString()] = new
            {
                name = "Franchise Cards Pack", type = "modular",
                selector = new { kind = "groupChoice", group1 = 5, group2 = 5 },
                condition = new { kind = "once", day = 15 },
                sorterPriority = StandardPriority(0.5),
                filter = DefaultFilter(),
            },
            [normalId.ToString()] = new
            {
                name = "Normal Cards Pack", type = "modular",
                selector = new { kind = "groupChoice", group1 = 1, group2 = 0 },
                condition = new { kind = "regular", dayInterval = 3, dayOffset = 0, dayMin = 1, dayMax = -1, tierRequired = -1 },
                sorterPriority = StandardPriority(0.5),
                filter = DefaultFilter(),
            },
            [themeId.ToString()] = new
            {
                name = "Theme Cards Pack", type = "modular",
                selector = new { kind = "groupChoice", group1 = 3, group2 = 3 },
                condition = new { kind = "guarantee", minDay = 5, minTier = -1, guaranteedGroup = 3 },
                sorterPriority = StandardPriority(0.5),
                filter = DefaultFilter(),
            },
            [marchCardsId.ToString()] = new
            {
                name = "March Cards", type = "modular",
                selector = new { kind = "groupChoice", group1 = 1, group2 = 0 },
                condition = new { kind = "regular", dayInterval = 1, dayOffset = 0, dayMin = 1, dayMax = -1, tierRequired = -1 },
                sorterPriority = StandardPriority(0.10000000149011612),
                filter = DefaultFilter(),
            },
            [heat2PackId.ToString()] = new
            {
                name = "Heat - Food Card After Day 2 Pack", type = "modular",
                selector = new { kind = "anyFromGroup", groups = new[] { 1 } },
                condition = new { kind = "once", day = 2 },
                sorterPriority = StandardPriority(0.8999999761581421),
                filter = DefaultFilter(),
            },
            [heat10PackId.ToString()] = new
            {
                name = "Heat - Starter Or Dessert Pack", type = "modular",
                selector = new { kind = "menuPhase", phase1 = 0, phase2 = 2 },
                condition = new { kind = "once", day = 1 },
                sorterPriority = StandardPriority(0.8999999761581421),
                filter = DefaultFilter(),
            },
        };

        var jsonOptions = new JsonSerializerOptions { WriteIndented = true };
        File.WriteAllText(Path.Combine(outputRoot, "cards", "packs.json"), JsonSerializer.Serialize(packs, jsonOptions));
        Console.WriteLine($"  Wrote {packs.Count} pack(s) to cards/packs.json");
    }

    private static bool HasUnlockSignature(string assetPath)
    {
        // Cheap pre-filter before a full YAML parse: every Unlock-derived asset has all four fields
        // as plain top-level (2-space-indented) scalars.
        string text;
        try
        {
            text = File.ReadAllText(assetPath);
        }
        catch
        {
            return false;
        }

        return UnlockSignatureFields.All(f => text.Contains($"\n  {f}:"));
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
