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
    long? Limit);

public sealed record SearchStartResponse(string JobId, long TotalToCheck, string SpaceSize);

public sealed record SearchStatusResponse(
    string JobId,
    string Status,
    long CheckedCount,
    long TotalToCheck,
    double SeedsPerSecond,
    double ElapsedSeconds,
    IReadOnlyList<string> Matches,
    string? Error);

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
