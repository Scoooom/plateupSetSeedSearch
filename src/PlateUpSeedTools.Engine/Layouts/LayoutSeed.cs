using PlateUpSeedTools.Engine.Seeding;

namespace PlateUpSeedTools.Engine.Layouts;

/// <summary>
/// Port of Kitchen.LayoutSeed (DecompiledSource/FranchiseMode/Kitchen/LayoutSeed.cs), covering the
/// LayoutSeed(Seed) constructor path and the relevant slice of GenerateMap - not the franchise-mode
/// "regenerate until valid_layout_ids matches" constructor overload, which doesn't apply to previewing
/// a specific already-known seed.
/// </summary>
public static class LayoutSeed
{
    /// <summary>
    /// AssetReference.FixedRunLayout (GameData/KitchenData/AssetReference.cs) - a hardcoded pool of
    /// layout profile IDs used when a RestaurantSetting doesn't force a specific layout. Copied as
    /// literal values straight from the decompiled static initializer (HugeLayout appears twice,
    /// at indices 3 and 7, matching the original).
    /// </summary>
    private static readonly int[] FixedRunLayoutIds =
    [
        222370461, -80202533, 557943155, 154938708,
        -2045800810, -80202533, 557943155, 154938708,
    ];

    /// <summary>Real in-game display name for each of the 5 distinct layouts in <see cref="FixedRunLayoutIds"/>.</summary>
    public enum LayoutSize { Basic, Diner, Medium, Huge, Extended }

    /// <summary>
    /// Maps each of the 5 distinct <see cref="FixedRunLayoutIds"/> values to its real in-game name -
    /// found by grepping `.asset` files for `^  ID: &lt;value&gt;$` (`Basic/Diner/Extended/Huge/Medium
    /// Layout.asset`), see game-domain-knowledge.md's "RestaurantSetting / LayoutProfile data model"
    /// section. A setting's own ForceLayoutId (see ResolveLayoutId) is never one of these 5 values, so
    /// TryGetSize correctly returns false for every forced-layout setting - size is only a meaningful
    /// concept for settings that draw from this shared pool.
    /// </summary>
    private static readonly IReadOnlyDictionary<int, LayoutSize> SizeByLayoutId = new Dictionary<int, LayoutSize>
    {
        [222370461] = LayoutSize.Basic,
        [-80202533] = LayoutSize.Diner,
        [557943155] = LayoutSize.Extended,
        [154938708] = LayoutSize.Huge,
        [-2045800810] = LayoutSize.Medium,
    };

    /// <summary>Resolves a layout profile ID to its named size - false for any ID outside the 5-member pool (i.e. every ForceLayoutId-based layout).</summary>
    public static bool TryGetSize(int layoutProfileId, out LayoutSize size) =>
        SizeByLayoutId.TryGetValue(layoutProfileId, out size);

    /// <summary>Port of LayoutSeed.LayoutIDFromSeed().</summary>
    public static int LayoutIdFromSeed(Seed seed)
    {
        var rng = new FixedSeedContext(seed, 5078598).UseSubcontext(0).CreateRandom();
        return FixedRunLayoutIds[rng.Range(0, FixedRunLayoutIds.Length)];
    }

    /// <summary>
    /// Port of the layout-ID resolution in LayoutSeed.GenerateMap: RestaurantSetting.ForceLayout
    /// always wins when the setting sets one, otherwise falls back to the seed-random pick from
    /// AssetReference.FixedRunLayout. (The original guards this with a `flag` that's only ever false
    /// for franchise-mode-regenerated LayoutIDs, which don't apply here - see LayoutSeed.cs comment.)
    /// </summary>
    public static int ResolveLayoutId(Seed seed, int? forceLayoutId) =>
        forceLayoutId ?? LayoutIdFromSeed(seed);

    /// <summary>
    /// Port of the seed derivation in LayoutSeed.GenerateMap - the actual int handed to
    /// LayoutGraph.Build/CreateLayoutHelper.ConstructLayout.
    /// </summary>
    public static int ConstructionSeed(Seed seed)
    {
        var rng = new FixedSeedContext(seed, 98234234).UseSubcontext(0).CreateRandom();
        return rng.Range(int.MinValue, int.MaxValue);
    }
}
