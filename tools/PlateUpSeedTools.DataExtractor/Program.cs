using System.Text.Json;
using PlateUpSeedTools.DataExtractor;

if (args.Length < 2)
{
    Console.WriteLine("Usage: DataExtractor <PlateUpSourceFiles path> <output data/settings path>");
    return 1;
}

var sourceRoot = args[0];
var outputRoot = args[1];
var assetsRoot = Path.Combine(sourceRoot, "AssetRipper", "ExportedProject", "Assets");

Console.WriteLine("Indexing asset GUIDs...");
var resolver = GuidResolver.BuildFrom(assetsRoot);

Console.WriteLine("Extracting shared/standard layout pool (AssetReference.FixedRunLayout)...");
SettingExtractor.ExtractStandardLayouts(resolver, assetsRoot, outputRoot);

// Only the settings with a genuinely distinct layout get their own extracted asset. Several other
// real RestaurantSetting assets (Bakery/February/January/Coffeeshop/Turbo/City/Alpine/Country/
// Autumn/Halloween, plus the "Christmas Banquet"/"Halloween - With Cards" reskins of Banquet Hall
// and Witch Hut) all had ForceLayout unset or pointed at a layout byte-identical to one of these -
// i.e. they produced the exact same generated layout as each other for any given seed. Consolidated
// into the single synthetic "normal" entry below (for the shared-pool group) and by simply dropping
// the redundant reskins (for the two ForceLayout duplicate pairs) - see project-history.md for the
// investigation and rationale.
var settingsToExtract = new (string SettingAssetName, string Slug)[]
{
    ("Banquet Setting.asset", "banquet-hall"),
    ("Santa Workshop Setting.asset", "santa-workshop"),
    ("2310 Witch Hut.asset", "witch-hut"),
};

var indexEntries = new List<(string Slug, string DisplayName, int Id)>();

foreach (var (settingAssetName, slug) in settingsToExtract)
{
    Console.WriteLine($"Extracting '{settingAssetName}' -> {slug}");
    var entry = SettingExtractor.Extract(assetsRoot, resolver, settingAssetName, slug, outputRoot);
    indexEntries.Add(entry);
}

Console.WriteLine("Writing synthetic 'normal' setting (replaces the byte-identical shared-pool settings)...");
indexEntries.Add(SettingExtractor.WriteSynthetic(outputRoot, "normal", "Normal", id: 0));

var indexPath = Path.Combine(outputRoot, "index.json");
var indexJson = JsonSerializer.Serialize(
    indexEntries.Select(e => new { slug = e.Slug, displayName = e.DisplayName, id = e.Id }),
    new JsonSerializerOptions { WriteIndented = true });
File.WriteAllText(indexPath, indexJson);

SettingExtractor.WriteAppliancesFile(outputRoot);

Console.WriteLine($"Done. Wrote index with {indexEntries.Count} setting(s) to {indexPath}");
Console.WriteLine($"Wrote {SettingExtractor.KnownAppliances.Count} known appliance(s) to {Path.Combine(outputRoot, "appliances.json")}");
return 0;
