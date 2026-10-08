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
/// practice, so this port does not model that split.
///
/// CORRECTION (superseding an earlier, incomplete claim): a reroll does NOT always keep the same
/// position. HandleShopReroll.cs branches per letter on whether it's currently held by a player
/// (CHeldBy+CPlayer) at the moment of reroll: held -> FixedLocation=false -> the letter gets a FRESH
/// placement roll reading whatever ShopSpawnSetting is live at that instant; unheld/on the ground ->
/// FixedLocation=true -> the old position is kept verbatim, the live setting is never consulted.
/// Confirmed both from source and from a real controlled two-part test (toggle the setting mid-day,
/// then reroll once holding the letter and once not): the held case visibly moved outside->inside,
/// the unheld case stayed put. So this evaluator must be called once per reroll round that was held,
/// not once per day - see ShopDaySimulator.SimulateDayWithRerolls's doc comment for how callers should
/// thread this (an unheld round should inherit the previous round's location unchanged, not
/// re-evaluate).
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
