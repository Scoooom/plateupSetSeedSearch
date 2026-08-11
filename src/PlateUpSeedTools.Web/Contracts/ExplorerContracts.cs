namespace PlateUpSeedTools.Web.Contracts;

/// <summary>One already-committed pick along the path being replayed - see ExplorerEndpoints.SimulateDay.</summary>
public sealed record ExplorerPathEntryDto(int Day, int CardId);

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
    string? CardPack = null);

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
