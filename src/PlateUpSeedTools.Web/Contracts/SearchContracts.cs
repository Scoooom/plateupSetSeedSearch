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
    int? MaxResults = null,
    // Restricts matches to seeds whose layout resolves to one of these named sizes (e.g. "Diner",
    // "Medium") - see SeedSearchRequest.MapSizes. Only meaningful for a setting with no ForceLayoutId
    // (today, just "normal") - StartSearch rejects a non-empty list for any other setting, and the UI
    // disables the checkboxes the same way it already locks the card-pack dropdown for non-normal
    // settings (see shared.js's updateCardPackLock). Null/empty means no size filter.
    List<string>? MapSizes = null,
    // Additional AND-condition: each required appliance for this named achievement must be
    // reachable via the shop by day 15 - see Engine.Shop.AchievementPreset/
    // ShopApplianceReachabilityChecker. Null/empty means no achievement filter. Unlike OtFriendly/
    // MapSizes this is a slug the server resolves (not a raw appliance-id list) - see
    // SearchEndpoints.GetAchievementPresets for the available slugs.
    string? AchievementPreset = null,
    // Opts into: heartbeat-abandonment immunity (see SearchJobHeartbeatMonitor/
    // SearchJobManager.SweepAbandonedJobs), a hard wall-clock deadline instead
    // (Search:MaxJobDurationSeconds, default 1h), an eagerly-minted ShareHash so the job has a
    // live/cached link from the moment it starts, and a Web Push notification on completion. Only
    // takes effect if the client actually attached a push subscription AND the server has
    // WebPush:* configured - see SearchJobManager.StartSearch.
    bool? NotifyOnCompletion = null);

/// <summary>
/// ShareHash is non-null only for a notify-enabled job - see SearchJobManager.StartSearch. Lets the
/// client surface a working link immediately rather than waiting for completion (see the existing,
/// still-separate lazy "Copy Link" flow for a job that didn't opt into notify).
/// </summary>
public sealed record SearchStartResponse(string JobId, long TotalToCheck, string SpaceSize, string? ShareHash = null);

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
    int EquivalentCount = 0,
    // The resolved named map size (e.g. "Diner") - computed for every match regardless of whether the
    // search itself was size-filtered, same "always tag" convention as OtFriendly above. Null for a
    // setting that forces its own bespoke layout, where size isn't a meaningful concept at all.
    string? MapSize = null);

public sealed record SearchStatusResponse(
    string JobId,
    string Status,
    long CheckedCount,
    long TotalToCheck,
    double SeedsPerSecond,
    double ElapsedSeconds,
    IReadOnlyList<SearchMatchDto> Matches,
    string? Error,
    bool MatchCapReached,
    // True when this Completed job actually stopped because it hit the notify-mode hard deadline
    // (Search:MaxJobDurationSeconds) rather than exhausting its search space - see
    // SearchJobManager.RunAsync. Surfaced as Completed, not a separate status, for the same reason
    // MatchCapReached is: it's a "stopped early, still show partial results as a success" case, not
    // a failure - see SearchJob.TimedOut's doc comment.
    bool TimedOut = false);

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

/// <summary>One selectable achievement preset for the search form's achievement filter - see Engine.Shop.AchievementPreset.</summary>
public sealed record AchievementPresetDto(string Slug, string DisplayName, string Explanation);

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
    bool MatchCapReached,
    bool TimedOut = false);

public sealed record ShareSearchResultResponse(string Hash);

/// <summary>
/// A user-initiated personal save of one completed search's results - distinct from
/// CachedSearchResult above, which is the permanent, no-expiry "Copy Link" share snapshot. This
/// type is field-identical by design but stored in its own ICacheStore&lt;SavedSearchResult&gt;/
/// cache_SavedSearchResult table, swept after ~7 days by SavedSearchExpiryMonitor. Any completed
/// search can be saved this way (not just notify-enabled/background ones) - see SearchEndpoints'
/// POST /api/search/{jobId}/save, POST /api/search/save-from-cached/{hash}, and
/// GET /api/search/saved/{hash}. The nickname a user gives a saved search lives only in their
/// browser's localStorage (wwwroot/js/favorites.js) - never sent to or stored on the server.
/// </summary>
public sealed record SavedSearchResult(
    SearchStartRequest Request,
    IReadOnlyList<SearchMatchDto> Matches,
    long TotalToCheck,
    long CheckedCount,
    bool MatchCapReached,
    bool TimedOut = false);

/// <summary>
/// Resolves a shareable hash to whichever of the two places it could point at still exists - a
/// permanent cached snapshot (Kind "cached", any job this age) or a still-running/just-finished
/// in-memory job (Kind "live", only exists for a notify-enabled job - see
/// SearchJobManager.TryGetJobByHash). See SearchEndpoints.GetSearchLink.
/// </summary>
public sealed record SearchLinkResponse(string Kind, string? JobId, SearchStartRequest? Request, CachedSearchResult? Cached);
