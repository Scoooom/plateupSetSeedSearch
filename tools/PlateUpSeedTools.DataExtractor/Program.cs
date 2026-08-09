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

var settingsToExtract = new (string SettingAssetName, string Slug)[]
{
    ("Banquet Setting.asset", "banquet-hall"),
    ("Christmas Banquet Setting.asset", "christmas-banquet-hall"),
    ("Santa Workshop Setting.asset", "santa-workshop"),
    ("Bakery Setting.asset", "bakery"),
    ("February Setting.asset", "february"),
    ("January Setting.asset", "january"),
    ("June Setting - Coffeeshop.asset", "coffeeshop"),
    ("March Setting - Turbo.asset", "turbo"),
};

var indexEntries = new List<(string Slug, string DisplayName, int Id)>();

foreach (var (settingAssetName, slug) in settingsToExtract)
{
    Console.WriteLine($"Extracting '{settingAssetName}' -> {slug}");
    var entry = SettingExtractor.Extract(assetsRoot, resolver, settingAssetName, slug, outputRoot);
    indexEntries.Add(entry);
}

var indexPath = Path.Combine(outputRoot, "index.json");
var indexJson = JsonSerializer.Serialize(
    indexEntries.Select(e => new { slug = e.Slug, displayName = e.DisplayName, id = e.Id }),
    new JsonSerializerOptions { WriteIndented = true });
File.WriteAllText(indexPath, indexJson);

Console.WriteLine($"Done. Wrote index with {indexEntries.Count} setting(s) to {indexPath}");
return 0;
