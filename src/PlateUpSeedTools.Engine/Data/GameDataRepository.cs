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
    private readonly Dictionary<int, string> _applianceNames;
    private readonly Dictionary<int, UnlockData> _unlocksById;
    private readonly HeatLevelsData _heatLevels;
    private readonly Dictionary<int, UnlockPackData> _packsById;
    private readonly Dictionary<string, CardScheduleData> _cardSchedulesBySlug;
    private readonly Dictionary<int, ShopApplianceData> _shopAppliancesById;
    private readonly List<ShopApplianceData> _shopAppliancesInGameDataOrder;

    public GameDataRepository(string dataRoot)
    {
        _root = dataRoot;
        var indexPath = Path.Combine(_root, "index.json");
        var index = JsonSerializer.Deserialize<List<SettingIndexEntry>>(File.ReadAllText(indexPath), JsonOptions) ?? [];
        _settingsBySlug = index.ToDictionary(
            e => e.Slug,
            e => JsonSerializer.Deserialize<RestaurantSettingData>(File.ReadAllText(Path.Combine(_root, e.Slug, "setting.json")), JsonOptions)!);

        var appliancesPath = Path.Combine(_root, "appliances.json");
        _applianceNames = JsonSerializer.Deserialize<Dictionary<int, string>>(File.ReadAllText(appliancesPath), JsonOptions) ?? [];

        var cardsDir = Path.Combine(_root, "cards");
        var unlocks = JsonSerializer.Deserialize<List<UnlockData>>(File.ReadAllText(Path.Combine(cardsDir, "unlocks.json")), JsonOptions) ?? [];
        _unlocksById = unlocks.ToDictionary(u => u.Id);
        _heatLevels = JsonSerializer.Deserialize<HeatLevelsData>(File.ReadAllText(Path.Combine(cardsDir, "heat-levels.json")), JsonOptions)!;
        var packsRaw = JsonSerializer.Deserialize<Dictionary<string, UnlockPackData>>(File.ReadAllText(Path.Combine(cardsDir, "packs.json")), JsonOptions) ?? [];
        _packsById = packsRaw.ToDictionary(kv => int.Parse(kv.Key), kv => kv.Value);

        var schedules = JsonSerializer.Deserialize<List<CardScheduleData>>(File.ReadAllText(Path.Combine(cardsDir, "card-schedules.json")), JsonOptions) ?? [];
        _cardSchedulesBySlug = schedules.ToDictionary(s => s.Slug);

        var shopDir = Path.Combine(_root, "shop");
        var shopAppliances = JsonSerializer.Deserialize<List<ShopApplianceData>>(File.ReadAllText(Path.Combine(shopDir, "appliances.json")), JsonOptions) ?? [];
        _shopAppliancesById = shopAppliances.ToDictionary(a => a.Id);
        // Deliberately a separate ordered List, not _shopAppliancesById.Values.ToList() - Dictionary
        // enumeration order isn't a documented guarantee, and this order is load-bearing (see
        // AllShopAppliances' doc comment): it must exactly match shop/appliances.json's own array
        // order, which ApplianceExtractor writes in GameData.asset's GameDataObjects order.
        _shopAppliancesInGameDataOrder = shopAppliances;
    }

    /// <summary>
    /// Resolves an appliance's real display name (from data/settings/appliances.json, produced by
    /// the DataExtractor's ResolveAppliance). Falls back to a synthesized label for any ID missing
    /// from that file (shouldn't happen once extraction is re-run, but keeps a stale data/settings
    /// from crashing the app rather than just showing an unlabeled appliance).
    /// </summary>
    public string GetApplianceName(int applianceId) =>
        _applianceNames.TryGetValue(applianceId, out var name) ? name : $"Appliance {applianceId}";

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

    /// <summary>Every Unlock-derived asset (Dish + UnlockCard), ordered by ID - matches UnlockSetAutomatic.GetCardSet's OrderBy(u => u.ID).</summary>
    public IReadOnlyList<UnlockData> AllUnlocksOrderedById => _unlocksById.Values.OrderBy(u => u.Id).ToList();

    public UnlockData GetUnlock(int id) =>
        _unlocksById.TryGetValue(id, out var unlock) ? unlock : throw new KeyNotFoundException($"Unknown unlock ID {id}");

    public bool TryGetUnlock(int id, out UnlockData unlock) => _unlocksById.TryGetValue(id, out unlock!);

    public HeatLevelsData HeatLevels => _heatLevels;

    public UnlockPackData GetPack(int id) =>
        _packsById.TryGetValue(id, out var pack) ? pack : throw new KeyNotFoundException($"Unknown unlock pack ID {id}");

    /// <summary>AssetReference.DefaultUnlockPack ("Default Composite Pack") - used when a setting's UnlockPack field is unset.</summary>
    public const int DefaultUnlockPackId = -2020288853;

    /// <summary>A setting's card-draw pack, falling back to DefaultUnlockPackId if the setting doesn't override it - see game-domain-knowledge.md's "Card draw RNG" point 1.</summary>
    public int ResolveUnlockPackId(string settingSlug) => GetSetting(settingSlug).UnlockPackId ?? DefaultUnlockPackId;

    /// <summary>
    /// Every selectable "card schedule" (data/settings/cards/card-schedules.json) - a card-draw pack
    /// choice independent of restaurant setting/layout. Exists because several settings folded into
    /// the "normal" synthetic setting during session 6's layout-only consolidation (before the
    /// card-draw feature existed) turned out to have their own distinct UnlockPack (Turbo's "every
    /// day" schedule, Coffeeshop/Halloween/Autumn/January's own packs) that the merge silently made
    /// unreachable - see game-domain-knowledge.md's card-pack-coverage section. Search/Explorer let
    /// the user pick a card schedule separately from the restaurant setting they're previewing/
    /// searching, rather than reintroducing near-duplicate settings into the setting picker.
    /// </summary>
    public IReadOnlyList<CardScheduleData> ListCardSchedules() => _cardSchedulesBySlug.Values.ToList();

    /// <summary>Resolves a card-schedule slug to its pack ID, or null if unset/blank (meaning: use the setting's own ResolveUnlockPackId as before).</summary>
    public int? ResolveCardSchedulePackId(string? scheduleSlug) =>
        string.IsNullOrEmpty(scheduleSlug) ? null
        : _cardSchedulesBySlug.TryGetValue(scheduleSlug, out var schedule) ? schedule.PackId
        : throw new KeyNotFoundException($"Unknown card schedule '{scheduleSlug}'");

    /// <summary>
    /// Every Appliance asset (data/settings/shop/appliances.json) - includes non-purchasable ones,
    /// matching CreateShopOptions' own IsPurchasable/IsPurchasableAsUpgrade filter happening at
    /// candidate-build time, not at extraction time. ORDER MATTERS: this must match GameData.asset's
    /// GameDataObjects registration order, since CreateShopOptions.CreateOptions has no explicit sort
    /// (unlike Unlocks, see AllUnlocksOrderedById) - ShopDaySimulator's per-slot Fisher-Yates shuffle
    /// permutes this list's positions deterministically given the RNG stream, so a different starting
    /// order produces different final picks even with byte-correct shuffle logic (ground-truth-caught,
    /// see ApplianceExtractor.ExtractShopAppliances' doc comment).
    /// </summary>
    public IReadOnlyList<ShopApplianceData> AllShopAppliances => _shopAppliancesInGameDataOrder;

    public ShopApplianceData GetShopAppliance(int id) =>
        _shopAppliancesById.TryGetValue(id, out var appliance) ? appliance : throw new KeyNotFoundException($"Unknown shop appliance ID {id}");

    public bool TryGetShopAppliance(int id, out ShopApplianceData appliance) => _shopAppliancesById.TryGetValue(id, out appliance!);
}
