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
/// CORRECTION (superseding an earlier, incomplete claim that player position never matters): Outside
/// mode IS confirmed player-position-independent - GetPostTiles' Outside branch is pure front-door
/// geometry (a fixed row of tiles), no player query at all. But Inside mode genuinely branches on
/// live player position: it queries every player, and for any player currently standing on an
/// "inside" tile, gathers nearby same-room tiles and puts them FIRST in a shuffled candidate list;
/// then, regardless, appends every OTHER inside tile in the layout (also shuffled) as a fallback
/// batch. So if at least one player is standing inside at that moment, the result is near that
/// player; if none are, it's effectively a random inside tile. This is a real, distinct second input
/// from the Inside/Outside setting itself - not modeled before, now exposed via
/// ShopSpawnLocationEvaluator.Evaluate's second parameter.
///
/// CORRECTION (superseding an earlier, incomplete claim): a reroll does NOT always keep the same
/// position. HandleShopReroll.cs branches per letter on whether it's currently held by a player
/// (CHeldBy+CPlayer) at the moment of reroll: held -> FixedLocation=false -> the letter gets a FRESH
/// placement roll reading whatever ShopSpawnSetting (and, if Inside, player position) is live at that
/// instant; unheld/on the ground -> FixedLocation=true -> the old position is kept verbatim, nothing
/// live is consulted. Confirmed both from source and from a real controlled two-part test (toggle the
/// setting mid-day, then reroll once holding the letter and once not): the held case visibly moved
/// outside->inside, the unheld case stayed put. So this evaluator must be called once per reroll
/// round that was held, not once per day - see ShopDaySimulator.SimulateDayWithRerolls's doc comment
/// for how callers should thread this (an unheld round should inherit the previous round's location
/// unchanged, not re-evaluate).
/// </summary>
public enum ShopSpawnLocation
{
    /// <summary>Outside setting: a fixed row of tiles just outside the front door.</summary>
    NearFrontDoor,

    /// <summary>Inside setting, and at least one player was standing on an inside tile at that moment: near that player.</summary>
    NearPlayer,

    /// <summary>Inside setting, but no player was standing inside at that moment: a random inside tile anywhere in the layout.</summary>
    RandomInsideTile,
}

/// <summary>
/// Port of GetPostTiles' top-level branch (Common/Kitchen/GenericSystemBase.cs:139-208) - see
/// ShopSpawnLocation's doc for the full mechanic, including why player position matters for the
/// Inside branch specifically (and is irrelevant for Outside).
/// </summary>
public static class ShopSpawnLocationEvaluator
{
    public static ShopSpawnLocation Evaluate(ShopSpawnSetting setting, bool someoneStandingInside) => setting switch
    {
        ShopSpawnSetting.Outside => ShopSpawnLocation.NearFrontDoor,
        ShopSpawnSetting.Inside => someoneStandingInside ? ShopSpawnLocation.NearPlayer : ShopSpawnLocation.RandomInsideTile,
        _ => throw new ArgumentOutOfRangeException(nameof(setting)),
    };
}
