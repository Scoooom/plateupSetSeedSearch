namespace PlateUpSeedTools.Engine.Shop;

/// <summary>
/// Named achievement presets for the Search page's achievement filter - each maps to the shop
/// appliance(s) that must be reachable (see <see cref="ShopApplianceReachabilityChecker"/>) for
/// that achievement to even be possible this run. Only two of PlateUp's 15+ named achievements
/// depend on a specific shop-purchasable appliance at all - a full survey of every
/// `Achievement*.cs` class in the decompiled source found the rest are pure player-behavior
/// counters (fires, mess, serve counts, timing, day survival) with no RNG-determined item
/// dependency, and a third (LEARNING_BY_DOING) references a specific item but it's tutorial-only,
/// never present in the real shop RNG, so it's excluded here. This only checks that the enabling
/// appliances are obtainable - it can't verify the in-game achievement action itself (holding a
/// knife while wearing trainers; cooking on a Danger Hob under an active Gas Override).
/// </summary>
public static class AchievementPreset
{
    public sealed record Entry(string Slug, string DisplayName, string Explanation, IReadOnlySet<int> ApplianceIds);

    public static readonly IReadOnlyList<Entry> All = new[]
    {
        new Entry(
            "safety-last",
            "Safety Last (Sharp Knife + Trainers)",
            "Checks that Sharp Knife and Trainers are each offered in the shop by day 15 (not necessarily the same day, since appliances are kept once bought) - the two items the real achievement requires holding/wearing at once.",
            new HashSet<int> { 2023704259, 723626409 }),
        new Entry(
            "charcoal-factory",
            "Charcoal Factory (Danger Hob + Gas Override)",
            "Checks that Danger Hob and Gas Override are each offered in the shop by day 15 (not necessarily the same day) - the two items the real achievement requires using together (cooking on a Danger Hob under an active Gas Override effect).",
            new HashSet<int> { -1448690107, -770041014 }),
    };

    public static Entry? TryResolve(string? slug) =>
        string.IsNullOrEmpty(slug) ? null : All.FirstOrDefault(e => e.Slug.Equals(slug, StringComparison.OrdinalIgnoreCase));
}
