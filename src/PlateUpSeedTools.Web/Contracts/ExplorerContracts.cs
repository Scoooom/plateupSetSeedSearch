namespace PlateUpSeedTools.Web.Contracts;

/// <summary>One already-committed pick along the path being replayed - see ExplorerEndpoints.SimulateDay.
/// Rerolled models the Bay Leaves plant's 1-reroll-per-night mechanic (same meaning as
/// CardPathEntryDto.Rerolled in SearchContracts.cs) - replaying this day uses skipChoices: 1 instead
/// of the base offer.</summary>
public sealed record ExplorerPathEntryDto(int Day, int CardId, bool Rerolled = false);

/// <summary>
/// Stateless "what does this seed offer on day N, given these earlier picks" query - the server
/// holds no session state; the client resends the whole path (day 1..Day-1's committed picks) on
/// every request and the server replays a fresh CardDrawSimulator from day 1 each time (cheap: at
/// most 15 deterministic simulation steps, no I/O). See CardDrawSimulator.SimulateDay's "days must
/// be simulated in increasing order" note for why replay-from-1 is required rather than resuming
/// mid-stream.
/// </summary>
public sealed record ExplorerDayRequest(
    string Setting,
    string Seed,
    string DishName,
    int? SubOptionChoice,
    int Heat,
    int Tier,
    List<string>? FranchiseCardNames,
    List<ExplorerPathEntryDto> Path,
    int Day,
    string? CardPack = null,
    // View the frontier day (Day, never covered by Path - see the "path must only cover days
    // strictly before the requested day" validation) with skipChoices: 1 instead of the base offer.
    // Distinct from ExplorerPathEntryDto.Rerolled, which is the stored bit for an already-committed
    // day being replayed.
    bool Reroll = false);

public sealed record ExplorerCardOptionDto(int Id, string Name, string Kind, int UnlockGroup);

/// <summary>
/// SelectedId is non-null only when Day is already covered by the request's Path (i.e. the caller is
/// re-viewing an already-decided day rather than the new frontier) - SourcePackName is the
/// human-readable name of whichever pack produced the offer (e.g. "Theme Cards Pack"), null if
/// nothing was offered that day.
/// </summary>
public sealed record ExplorerDayResponse(
    int Day,
    IReadOnlyList<ExplorerCardOptionDto> Options,
    string? SourcePackName,
    int? SelectedId);

/// <summary>
/// A shareable "arrive here already set up and walked to this point" snapshot for the Explorer -
/// setup fields plus a committed path, stored under a random hash rather than encoded in the URL
/// (same "Copy Link" pattern as CachedSearchResult; see the "hash, not an encoded blob" rationale
/// on that type). Used both when Search's path-preview dialog links into the Explorer, and by the
/// Explorer's own "Copy Link" button sharing whatever's currently been explored.
/// </summary>
public sealed record ExplorerPreload(
    string DishName,
    int? SubOptionChoice,
    int Heat,
    int Tier,
    List<string>? FranchiseCardNames,
    string? CardPack,
    List<ExplorerPathEntryDto> Path);

public sealed record SaveExplorerPreloadResponse(string Hash);

// --- Blueprints view (shop/appliance offers) - shares the Cards walk's setup fields (Setting/Seed/
// DishName/SubOptionChoice/Heat/Tier/FranchiseCardNames/CardPack/CardPath) so switching views on the
// same page doesn't require re-deriving owned-unlock state; see ExplorerEndpoints.SimulateShopDay.

/// <summary>One buy or toss action taken on a given (already-passed) day, applied in day order before simulating a later day's shop.</summary>
public sealed record ShopActionDto(int Day, int ApplianceId, bool IsToss);

/// <summary>
/// One reroll round's held/live-setting/someone-inside scenario. CORRECTS an earlier, incomplete
/// model: a reroll does NOT always keep the previous round's position. HandleShopReroll.cs branches
/// per letter on whether it's held by a player at the moment of reroll (confirmed both from source
/// and a real controlled gameplay test - toggling the setting mid-day then rerolling once held, once
/// not): held -> a FRESH placement roll reading whatever IsInside (and, if Inside, SomeoneInside) is
/// right then; unheld -> the previous round's location carries over unchanged and both fields are
/// ignored for that round (still required on the DTO so the client always has a definite value to
/// send, even for an unheld round). SomeoneInside mirrors whether at least one player was standing on
/// an inside tile at that exact moment - only consulted when IsInside is true (see
/// ShopSpawnLocationEvaluator.Evaluate's second parameter); irrelevant, but still present, otherwise.
/// </summary>
public sealed record ShopRerollInputDto(bool Held, bool IsInside, bool SomeoneInside);

public sealed record ExplorerShopDayRequest(
    string Setting,
    string Seed,
    string DishName,
    int? SubOptionChoice,
    int Heat,
    int Tier,
    List<string>? FranchiseCardNames,
    List<ExplorerPathEntryDto> CardPath,
    string? CardPack,
    List<ShopActionDto> ShopActions,
    int Day,
    // The live Pref.LettersSpawnInside setting at the moment this day's shop first opens (round 0).
    // Defaults to the game's own default (true).
    bool InitialIsInside = true,
    // Whether at least one player was standing on an inside tile at that exact moment - only
    // consulted when InitialIsInside is true (see ShopSpawnLocationEvaluator.Evaluate). Defaults to
    // false deliberately: false resolves to ShopSpawnLocation.RandomInsideTile, which produces the
    // EXACT caption the old 2-value model always produced for Inside - so a caller that never thinks
    // about this field sees unchanged behavior, not a silently different result.
    bool InitialSomeoneInside = false,
    // One entry per reroll beyond the initial offer - Rerolls.Count IS the reroll count (no separate
    // count field). See ShopRerollInputDto for how each entry affects that round's location.
    List<ShopRerollInputDto>? Rerolls = null);

public sealed record ShopSlotDto(int? ApplianceId, string? ApplianceName);

/// <summary>Location is THIS round's own plain-language spawn-area label (see ShopSpawnLocation) -
/// per-round, not day-level, since a held reroll can genuinely change it mid-day.</summary>
public sealed record ShopRoundDto(int RerollIndex, IReadOnlyList<ShopSlotDto> Slots, string Location);

public sealed record PlacedApplianceDto(int ApplianceId, string ApplianceName);

/// <summary>PlacedAppliances is the kitchen state heading INTO this day (before any of today's rounds) - what a "Toss" button can target.</summary>
public sealed record ExplorerShopDayResponse(int Day, IReadOnlyList<ShopRoundDto> Rounds, IReadOnlyList<PlacedApplianceDto> PlacedAppliances);
