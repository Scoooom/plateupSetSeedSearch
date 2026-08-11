namespace PlateUpSeedTools.Web.Contracts;

public sealed record DayTargetDto(int Day, string CardName);

public sealed record SearchStartRequest(
    string Setting,
    string DishName,
    int? SubOptionChoice,
    int Heat,
    int Tier,
    List<string>? FranchiseCardNames,
    List<DayTargetDto> DayTargets,
    string? Alphabet,
    int Length,
    long? Limit,
    bool? StopAfterFirstMatch);

public sealed record SearchStartResponse(string JobId, long TotalToCheck, string SpaceSize);

public sealed record SearchStatusResponse(
    string JobId,
    string Status,
    long CheckedCount,
    long TotalToCheck,
    double SeedsPerSecond,
    double ElapsedSeconds,
    IReadOnlyList<string> Matches,
    string? Error,
    bool MatchCapReached);

/// <summary>
/// "What cards could possibly be offered on day N" - powers the search UI's day-target dropdown
/// filtering (see UnlockPackEvaluator.GetPossibleCardIds). PriorTargets should only include entries
/// for days strictly before Day that the user has actually picked a card for - the server also
/// defensively re-filters by Day, but the caller is expected to have already excluded later/empty
/// rows.
/// </summary>
public sealed record PossibleCardsRequest(
    string Setting,
    string DishName,
    int? SubOptionChoice,
    int Heat,
    int Tier,
    List<string>? FranchiseCardNames,
    List<DayTargetDto> PriorTargets,
    int Day);

public sealed record PossibleCardsResponse(IReadOnlyList<string> CardNames);

public sealed record UnlockDto(
    int Id,
    string Name,
    string Kind,
    bool HasSubOptions,
    string? OptionCard1Name,
    string? OptionCard2Name,
    bool IsStartingDish);

/// <summary>Level is signed - positive for Heat N, negative for Chill N (magnitude = N), matching RunSetup's heatLevel parameter directly.</summary>
public sealed record HeatLevelDto(int Level, string Name);
