namespace PlateUpSeedTools.Engine.Shop;

/// <summary>Mirrors the real game's Pref.LettersSpawnInside game setting - a player-configured option, not seed-derived.</summary>
public enum ShopSpawnSetting
{
    Outside,
    Inside,
}

/// <summary>
/// Which area a shop letter's physical spawn position falls into - deliberately NOT an exact
/// tile/coordinate (the real game's GetPostTiles, Common/Kitchen/GenericSystemBase.cs:139-208, picks
/// a specific tile via RNG-driven candidate shuffling this engine doesn't attempt to reproduce).
///
/// User-confirmed from real gameplay (not just source-reading): placement depends ONLY on the
/// Pref.LettersSpawnInside setting, never on player position - GetPostTiles' Inside branch does read
/// live player position in source (a per-player IsInside check feeding a "candidate tiles near that
/// player" vs. "shuffled list of every inside tile" split), but this has no user-visible effect in
/// practice, so this port does not model that split. Also user-confirmed: a reroll never changes
/// position either way, held or sitting on the ground - it only redraws the appliance in place (see
/// ShopDaySimulator.SimulateDayWithRerolls's doc comment) - so this evaluator is called once per day,
/// not once per reroll round.
/// </summary>
public enum ShopSpawnLocation
{
    /// <summary>Outside setting: a fixed row of tiles just outside the front door.</summary>
    NearFrontDoor,

    /// <summary>Inside setting: somewhere inside the restaurant - not further narrowed down, see this type's doc.</summary>
    Inside,
}

/// <summary>
/// Port of GetPostTiles' top-level branch (Common/Kitchen/GenericSystemBase.cs:139-208) - see
/// ShopSpawnLocation's doc for why this is a single setting check, not a player-position-dependent one.
/// </summary>
public static class ShopSpawnLocationEvaluator
{
    public static ShopSpawnLocation Evaluate(ShopSpawnSetting setting) => setting switch
    {
        ShopSpawnSetting.Outside => ShopSpawnLocation.NearFrontDoor,
        ShopSpawnSetting.Inside => ShopSpawnLocation.Inside,
        _ => throw new ArgumentOutOfRangeException(nameof(setting)),
    };
}
