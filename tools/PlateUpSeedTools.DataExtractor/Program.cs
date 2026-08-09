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

    // Discovered after an initial filename-based search ("*Setting*.asset") missed every setting
    // whose asset isn't literally named "<Something> Setting.asset" - these are the base game's
    // default/seasonal locations. Found by scanning every .asset for the exact RestaurantSetting
    // field signature (WeatherMode/ForceLayout/UnlockPack/AlwaysLight/FixedRunSeason/StartingUnlock)
    // rather than trusting filenames or a script GUID (AssetRipper maps many unrelated MonoBehaviour
    // types to the same guid for this stripped assembly, so guid-matching alone isn't reliable either).
    ("City.asset", "city"),
    ("Alpine.asset", "alpine"),
    ("Country.asset", "country"),
    ("Autumn.asset", "autumn"),
    ("Halloween.asset", "halloween"),
    ("Halloween - With Cards.asset", "halloween-with-cards"),
    ("2310 Witch Hut.asset", "witch-hut"),
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

SettingExtractor.WriteAppliancesFile(outputRoot);

Console.WriteLine($"Done. Wrote index with {indexEntries.Count} setting(s) to {indexPath}");
Console.WriteLine($"Wrote {SettingExtractor.KnownAppliances.Count} known appliance(s) to {Path.Combine(outputRoot, "appliances.json")}");
return 0;
