using System.Text.Json;
using PlateUpSeedTools.Engine.Layouts;
using PlateUpSeedTools.Engine.Layouts.Decorators;
using PlateUpSeedTools.Engine.Seeding;

namespace PlateUpSeedTools.Engine.Data;

/// <summary>Loads the extracted data/settings/&lt;slug&gt;/*.json files and hands back ready-to-use Engine objects.</summary>
public sealed class GameDataRepository
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    private readonly string _root;
    private readonly Dictionary<string, RestaurantSettingData> _settingsBySlug;

    public GameDataRepository(string dataRoot)
    {
        _root = dataRoot;
        var indexPath = Path.Combine(_root, "index.json");
        var index = JsonSerializer.Deserialize<List<SettingIndexEntry>>(File.ReadAllText(indexPath), JsonOptions) ?? [];
        _settingsBySlug = index.ToDictionary(
            e => e.Slug,
            e => JsonSerializer.Deserialize<RestaurantSettingData>(File.ReadAllText(Path.Combine(_root, e.Slug, "setting.json")), JsonOptions)!);
    }

    public IReadOnlyList<SettingIndexEntry> ListSettings() =>
        _settingsBySlug.Values.Select(s => new SettingIndexEntry(s.Slug, s.DisplayName, s.Id)).ToList();

    public RestaurantSettingData GetSetting(string slug) =>
        _settingsBySlug.TryGetValue(slug, out var setting) ? setting : throw new KeyNotFoundException($"Unknown restaurant setting slug '{slug}'");

    /// <summary>
    /// Resolves which LayoutProfile a run uses for this setting and seed - the setting's ForceLayout
    /// always wins when set, otherwise it's the seed-random pick from the shared standard-layout pool
    /// (LayoutSeed.LayoutIdFromSeed, port of AssetReference.FixedRunLayout resolution) - and loads it.
    /// </summary>
    public (LayoutProfileInfo Profile, LayoutGraphDefinition Graph) ResolveLayout(string settingSlug, Seed seed)
    {
        var setting = GetSetting(settingSlug);
        var dir = setting.ForceLayoutId is not null
            ? Path.Combine(_root, settingSlug)
            : Path.Combine(_root, "_shared-layouts", LayoutSeed.LayoutIdFromSeed(seed).ToString());

        var profileData = JsonSerializer.Deserialize<LayoutProfileData>(File.ReadAllText(Path.Combine(dir, "profile.json")), JsonOptions)!;
        var graphData = JsonSerializer.Deserialize<LayoutGraphData>(File.ReadAllText(Path.Combine(dir, "graph.json")), JsonOptions)!;

        var profile = new LayoutProfileInfo
        {
            TableApplianceId = profileData.TableApplianceId,
            CounterApplianceId = profileData.CounterApplianceId,
            MaximumTables = profileData.MaximumTables,
            RequiredApplianceIds = profileData.RequiredApplianceIds,
        };

        return (profile, LayoutGraphFactory.Create(graphData));
    }
}
