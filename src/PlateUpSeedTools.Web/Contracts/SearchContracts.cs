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
    bool? StopAfterFirstMatch,
    List<string>? ExcludedSeeds = null,
    string? CardPack = null);

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
    int Day,
    string? CardPack = null);

public sealed record PossibleCardsResponse(IReadOnlyList<string> CardNames);

public sealed record UnlockDto(
    int Id,
    string Name,
    string Kind,
    bool HasSubOptions,
    string? OptionCard1Name,
    string? OptionCard2Name,
    bool IsStartingDish,
    int UnlockGroup,
    int CardType,
    int MinimumFranchiseTier,
    bool IsSpecificFranchiseTier,
    IReadOnlyList<int> RequiresIds,
    IReadOnlyList<int> BlockedByIds);

/// <summary>Level is signed - positive for Heat N, negative for Chill N (magnitude = N), matching RunSetup's heatLevel parameter directly.</summary>
public sealed record HeatLevelDto(int Level, string Name);

/// <summary>One selectable card-draw schedule (independent of restaurant setting) - see GameDataRepository.ListCardSchedules.</summary>
public sealed record CardScheduleDto(string Slug, string DisplayName);

/// <summary>
/// Candidate list for one franchise-carryover row - Franchise (group 5) and Theme (group 3) cards
/// only, filtered by Requires/BlockedBy/tier eligibility against the starting dish and every OTHER
/// already-selected card in the form (day-target picks and other franchise rows alike), plus the
/// "at most 1 Theme card, at most 1 Franchise card per tier" caps (see SelectionCapValidator).
/// </summary>
public sealed record FranchiseCandidatesRequest(
    string Setting,
    string DishName,
    int? SubOptionChoice,
    int Heat,
    int Tier,
    List<string>? OtherSelectedCardNames);

public sealed record FranchiseCandidatesResponse(IReadOnlyList<string> CardNames);

/// <summary>
/// "Which card to actually pick each day to reproduce this match" - powers the search results'
/// click-to-preview dialog. Same setup shape as SearchStartRequest (minus the space-search-only
/// fields alphabet/length/limit/stopAfterFirstMatch/excludedSeeds) plus the specific matched Seed.
/// Stateless like PossibleCardsRequest/FranchiseCandidatesRequest - the job's original request
/// isn't retained server-side, so the client resends the setup it already has on hand.
/// </summary>
public sealed record SearchPathRequest(
    string Setting,
    string DishName,
    int? SubOptionChoice,
    int Heat,
    int Tier,
    List<string>? FranchiseCardNames,
    List<DayTargetDto> DayTargets,
    string Seed,
    string? CardPack = null);

public sealed record CardPathEntryDto(int Day, string CardName);

/// <summary>One complete, self-consistent day-by-day pick sequence - see SeedSearcher.ResolveCardPaths.</summary>
public sealed record CardPathDto(IReadOnlyList<CardPathEntryDto> Days);

/// <summary>
/// Up to a handful of alternate valid paths, not just one - non-target ("filler") days can often be
/// filled with more than one card without breaking later targets, so there's rarely only one way to
/// play a matched seed. Capped (currently 3) rather than exhaustive - see
/// SeedSearcher.ResolveCardPaths's doc comment for why "all of them" isn't practical in general.
/// </summary>
public sealed record SearchPathResponse(IReadOnlyList<CardPathDto> Paths);

/// <summary>
/// A shareable snapshot of one completed search - everything needed to reproduce the results page
/// from just a hash, without a live SearchJob (which is evicted ~30 min after finishing). Stored in
/// ICacheStore&lt;CachedSearchResult&gt;, expired after CacheStore:MaxAgeHours like every other cache
/// (see CacheAdmin.DeleteExpiredAsync) - see SearchEndpoints' POST /api/search/{jobId}/share and
/// GET /api/search/cached/{hash}.
/// </summary>
public sealed record CachedSearchResult(
    SearchStartRequest Request,
    IReadOnlyList<string> Matches,
    long TotalToCheck,
    long CheckedCount,
    bool MatchCapReached);

public sealed record ShareSearchResultResponse(string Hash);
