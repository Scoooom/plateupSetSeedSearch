namespace PlateUpSeedTools.Web.Contracts;

/// <summary>
/// Day is null for a "flexible" target - the card just needs to be obtained by day 15, order/day
/// unspecified (see SeedSearchRequest.FlexibleTargets). Exactly one of CardName/DishType should be
/// set: CardName for an exact-card target (the original and only kind before class targets),
/// DishType for a "class" target - any card of that dish category (Base=0, Main=1, Extra=2,
/// Side=3, Starter=4, Dessert=5 - see Engine.Data.DishTypeCatalog) satisfies it, e.g. "Any Side".
/// Mutually exclusive by an explicit per-row UI mode toggle, not a sentinel value in CardName.
/// </summary>
public sealed record DayTargetDto(int? Day, string? CardName, int? DishType = null);

/// <summary>One dish class selectable as a class target - see DayTargetDto.DishType.</summary>
public sealed record ClassOptionDto(int DishType, string DisplayName);

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
    string? CardPack = null,
    // Additional AND-condition layered onto DayTargets - see Engine.Shop.OtFriendlyChecker.
    bool? OtFriendly = null,
    // Models the Bay Leaves plant's "1 reroll per night" mechanic - see SeedSearchRequest.AllowRerollPerDay.
    bool? AllowRerollPerDay = null,
    // Stop the search once this many matches are found. Null falls back to SeedSearcher.DefaultMatchCap;
    // ignored when StopAfterFirstMatch is set (that's just a cap of 1). Must be within
    // SeedSearcher.MinMatchCap..MaxMatchCap or StartSearch rejects the request (the UI enforces the
    // same range loudly before submit).
    int? MaxResults = null);

public sealed record SearchStartResponse(string JobId, long TotalToCheck, string SpaceSize);

/// <summary>
/// One matched seed. <see cref="OtFriendly"/> is computed for every match regardless of whether the
/// search itself was OtFriendly-gated (see SeedSearcher's private Matches method).
/// <see cref="EquivalentSeeds"/> lists OTHER seed strings the search found that hash to the same
/// value and are therefore the byte-identical game (see SeedSearchResult.EquivalentSeeds);
/// <see cref="EquivalentCount"/> is the true total, which can exceed the sample when a
/// collision-dense space truncated it. Both are empty/0 on the live in-progress snapshot and only
/// populated once the search completes (see SearchEndpoints.GetStatus).
/// </summary>
public sealed record SearchMatchDto(
    string Seed,
    bool OtFriendly,
    IReadOnlyList<string>? EquivalentSeeds = null,
    int EquivalentCount = 0);

public sealed record SearchStatusResponse(
    string JobId,
    string Status,
    long CheckedCount,
    long TotalToCheck,
    double SeedsPerSecond,
    double ElapsedSeconds,
    IReadOnlyList<SearchMatchDto> Matches,
    string? Error,
    bool MatchCapReached);

/// <summary>
/// "What cards could possibly be offered on day N" - powers the search UI's day-target dropdown
/// filtering (see UnlockPackEvaluator.GetPossibleCardIds). PriorTargets should only include entries
/// for days strictly before Day that the user has actually picked a card for - the server also
/// defensively re-filters by Day, but the caller is expected to have already excluded later/empty
/// rows. A flexible PriorTargets entry (Day: null) is always included regardless of Day, since a
/// flexible card could end up owned at any point. Day itself is null when querying a flexible row's
/// own dropdown - the server unions reachability across every day up to the flexible horizon instead
/// of one specific day.
/// </summary>
public sealed record PossibleCardsRequest(
    string Setting,
    string DishName,
    int? SubOptionChoice,
    int Heat,
    int Tier,
    List<string>? FranchiseCardNames,
    List<DayTargetDto> PriorTargets,
    int? Day,
    string? CardPack = null);

/// <summary>Classes is filtered to what's actually reachable given the same setup/day(s) as CardNames - see SearchEndpoints.GetPossibleCards.</summary>
public sealed record PossibleCardsResponse(IReadOnlyList<string> CardNames, IReadOnlyList<ClassOptionDto> Classes);

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
/// Candidate list for one franchise-carryover row - every Unlock in the game (any group, not just
/// Franchise/Theme - carryover is unrestricted by card type) that's internally consistent with the
/// rest of the selection: Requires/BlockedBy chains, no exact duplicates, franchise-tier
/// eligibility. See UnlockPackEvaluator.IsValidCarryoverPick's doc comment for why those three
/// specifically stay enforced (real, data-verified constraints - e.g. every tier of the "Variety"
/// franchise bonus is genuinely BlockedBy ["Community", "Coffee"]) while the old "1 Theme card
/// total, 1 Franchise card per tier" count caps and Franchise/Theme-group-only restriction were
/// removed (both were invented, contradicted by a ground-truth capture that carried over 5 cards
/// under a single tier - see game-domain-knowledge.md's "Franchise tier / carryover cards" section).
/// </summary>
public sealed record FranchiseCandidatesRequest(
    string Setting,
    string DishName,
    int? SubOptionChoice,
    int Heat,
    int Tier,
    List<string>? OtherSelectedCardNames,
    string? CardPack = null);

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
    string? CardPack = null,
    bool? AllowRerollPerDay = null);

public sealed record CardPathEntryDto(int Day, string CardName, bool Rerolled);

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
/// ICacheStore&lt;CachedSearchResult&gt; permanently (no expiry - see SearchEndpoints' POST
/// /api/search/{jobId}/share and GET /api/search/cached/{hash}).
/// </summary>
public sealed record CachedSearchResult(
    SearchStartRequest Request,
    IReadOnlyList<SearchMatchDto> Matches,
    long TotalToCheck,
    long CheckedCount,
    bool MatchCapReached);

public sealed record ShareSearchResultResponse(string Hash);
