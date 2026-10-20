namespace PlateUpSeedTools.Web.Contracts;

/// <summary>One already-committed pick along the path being replayed - see ExplorerEndpoints.SimulateDay.
/// Rerolled models the Bay Leaves plant's 1-reroll-per-night mechanic (same meaning as
/// CardPathEntryDto.Rerolled in SearchContracts.cs) - replaying this day uses skipChoices: 1 instead
/// of the base offer.</summary>
public sealed record ExplorerPathEntryDto(int Day, int CardId, bool Rerolled = false);

/// <summary>
/// Stateless "what does this seed offer on day N, given these earlier picks" query - the server
/// holds no session state; the client resends the whole path (day 1..Day-1's committed picks) on
/// every request and the server replays a fresh CardDrawSimulator from day 1 each time (cheap: a
/// handful of deterministic simulation steps per day requested, no I/O - unbounded above day 15
/// for overtime-day scouting, though nothing past day 15 is ground-truth-verified). See
/// CardDrawSimulator.SimulateDay's "days must be simulated in increasing order" note for why
/// replay-from-1 is required rather than resuming mid-stream.
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
