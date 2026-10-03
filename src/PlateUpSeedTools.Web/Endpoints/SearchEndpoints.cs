using System.Numerics;
using PlateUpSeedTools.Engine.Cards;
using PlateUpSeedTools.Engine.Data;
using PlateUpSeedTools.Engine.Layouts;
using PlateUpSeedTools.Engine.Seeding;
using PlateUpSeedTools.Infrastructure.Cache;
using PlateUpSeedTools.Web.Contracts;
using PlateUpSeedTools.Web.Services;

namespace PlateUpSeedTools.Web.Endpoints;

public static class SearchEndpoints
{
    // Default server-side cap when the request doesn't specify one: every web search stops after this
    // many matches. The user can lower it (or, in principle, raise it up to this same value) via the
    // "Max results" field, validated into SeedSearcher.MinMatchCap..MaxMatchCap below - see
    // SearchCommand.cs and the project plan's "Match cap + stop after first match" section. "Stop
    // after first match" is just this same mechanism with a cap of 1.
    private const long DefaultMatchCap = SeedSearcher.DefaultMatchCap;

    // How many alternate card-pick paths GetSearchPath surfaces per matched seed - see
    // SeedSearcher.ResolveCardPaths's doc comment for why "all of them" isn't offered.
    private const int MaxCardPaths = 3;

    // Fixed cutoff for "any day" flexible targets - see SeedSearchRequest.FlexibleHorizon and the
    // per-card "any day, by day 15" toggle in the search form. Not user-configurable by design.
    private const int FlexibleHorizon = 15;

    public static void MapSearchEndpoints(this WebApplication app)
    {
        app.MapGet("/api/search/unlocks", GetUnlocks);
        app.MapGet("/api/search/heat-levels", GetHeatLevels);
        app.MapGet("/api/search/card-packs", GetCardPacks);
        app.MapPost("/api/search/possible-cards", GetPossibleCards);
        app.MapPost("/api/search/franchise-candidates", GetFranchiseCandidates);
        app.MapPost("/api/search", StartSearch);
        app.MapGet("/api/search/{jobId}", GetStatus);
        app.MapPost("/api/search/{jobId}/cancel", CancelSearch);
        app.MapPost("/api/search/path", GetSearchPath);
        app.MapPost("/api/search/{jobId}/share", ShareSearchResult);
        app.MapGet("/api/search/cached/{hash}", GetCachedSearchResult);
    }

    private static IResult GetUnlocks(string setting, GameDataRepository repo)
    {
        if (!repo.ListSettings().Any(s => s.Slug == setting))
        {
            return Results.BadRequest(new { error = $"Unknown setting '{setting}'." });
        }

        var dtos = repo.AllUnlocksOrderedById.Select(u => new UnlockDto(
            u.Id,
            u.Name,
            u.Kind,
            u.HasSubOptions,
            u.OptionCard1Id is int o1 ? repo.GetUnlock(o1).Name : null,
            u.OptionCard2Id is int o2 ? repo.GetUnlock(o2).Name : null,
            u.IsBaseDish && u.IsUnlockable,
            u.UnlockGroup,
            u.CardType,
            u.MinimumFranchiseTier,
            u.IsSpecificFranchiseTier,
            u.RequiresIds,
            u.BlockedByIds)).ToList();

        return Results.Ok(dtos);
    }

    /// <summary>
    /// Candidate cards for one franchise-carryover row - every Unlock in the game (not restricted to
    /// Franchise/Theme groups - carryover isn't restricted by card type, ground-truth-verified in
    /// game-domain-knowledge.md's "Franchise tier / carryover cards" section) that's internally
    /// consistent with the rest of the selection - see
    /// <see cref="UnlockPackEvaluator.IsValidCarryoverPick"/>'s doc comment for exactly what "valid"
    /// means here (Requires/BlockedBy/duplicate/tier checks, real data-verified constraints) and why
    /// this deliberately does NOT reuse the Franchise/Theme pack's own offer-eligibility Filter or
    /// the count caps a previous version of this endpoint had (both were invented, not real).
    /// Unresolvable/blank names in <see cref="FranchiseCandidatesRequest.OtherSelectedCardNames"/>
    /// are skipped rather than erroring - this endpoint is called on every keystroke elsewhere in
    /// the form and a momentarily-stale other-row value shouldn't break this row's own candidate list.
    ///
    /// Deliberately validates against dish/sub-option/heat cards but NOT the resolved setting/
    /// schedule's own bonus StartingUnlockId (e.g. Autumn's "Community") - a carryover card
    /// represents something owned from a PRIOR run, so it can't be invalidated by a bonus card THIS
    /// run grants. Some franchise cards ARE genuinely BlockedBy a specific bonus card (e.g. every
    /// "Variety" copy is BlockedBy Autumn's "Community" - real, ground-truth-verified data), which
    /// correctly prevents ever freshly drawing them again once that bonus card is owned (see
    /// GetPossibleCards, unchanged) - but it doesn't retroactively un-own a copy already carried in
    /// from before that bonus card existed. CardDrawSimulatorGroundTruthTests
    /// .MatchesRealAutumnFranchiseCapture pins exactly this combination (carried-over Variety +
    /// this-run's Community) against a real captured game session. Mirrors StartSearch's own
    /// "defense in depth" re-validation (below), which already excludes StartingUnlockId from its
    /// baseline for the same reason.
    /// </summary>
    private static IResult GetFranchiseCandidates(FranchiseCandidatesRequest request, GameDataRepository repo)
    {
        if (!repo.ListSettings().Any(s => s.Slug == request.Setting))
        {
            return Results.BadRequest(new { error = $"Unknown setting '{request.Setting}'." });
        }

        if (!TryResolveUnlock(repo, request.DishName, "dish", out var dish, out var dishError))
        {
            return Results.BadRequest(new { error = dishError });
        }

        var setting = repo.GetSetting(request.Setting);
        ResolvedRunSetup setup;
        try
        {
            ResolveStartingUnlockId(repo, setting, request.CardPack); // validates cardPack slug; deliberately not fed into the baseline below
            setup = RunSetup.Resolve(repo, dish.Id, request.SubOptionChoice, request.Heat, startingUnlockId: null);
        }
        catch (Exception ex) when (ex is ArgumentException or KeyNotFoundException)
        {
            return Results.BadRequest(new { error = ex.Message });
        }

        var ownedIds = new HashSet<int>(setup.InitiallyOwnedIds);
        foreach (var name in request.OtherSelectedCardNames ?? [])
        {
            if (string.IsNullOrEmpty(name) || !TryResolveUnlock(repo, name, "card", out var other, out _))
            {
                continue;
            }

            ownedIds.Add(other.Id);
        }

        var names = BuildDisplayNames(repo, UnlockNameResolver.DropInertDuplicateNames(repo.AllUnlocksOrderedById)
                .Where(c => UnlockPackEvaluator.IsValidCarryoverPick(repo, c, ownedIds, request.Tier)))
            .OrderBy(n => n)
            .ToList();

        return Results.Ok(new FranchiseCandidatesResponse(names));
    }

    // Not setting-scoped - heat/chill cards are the same regardless of restaurant setting (see
    // RunSetup.Resolve, which takes heatLevel independent of any setting parameter).
    private static IResult GetHeatLevels(GameDataRepository repo)
    {
        var levels = repo.AllUnlocksOrderedById
            .Where(u => u.HeatCardDisplayedNumber != 0)
            .Select(u => new HeatLevelDto(u.HeatCardDisplayedNumber, u.Name))
            .OrderBy(h => h.Level)
            .ToList();

        return Results.Ok(levels);
    }

    // Not setting-scoped - a card schedule is a selectable pack independent of restaurant setting
    // (see GameDataRepository.ListCardSchedules's doc comment).
    private static IResult GetCardPacks(GameDataRepository repo) =>
        Results.Ok(repo.ListCardSchedules().Select(s => new CardScheduleDto(s.Slug, s.DisplayName)).ToList());

    private static IResult GetPossibleCards(PossibleCardsRequest request, GameDataRepository repo)
    {
        if (!repo.ListSettings().Any(s => s.Slug == request.Setting))
        {
            return Results.BadRequest(new { error = $"Unknown setting '{request.Setting}'." });
        }

        if (!TryResolveUnlock(repo, request.DishName, "dish", out var dish, out var dishError))
        {
            return Results.BadRequest(new { error = dishError });
        }

        var franchiseCarryoverIds = new List<int>();
        foreach (var cardName in request.FranchiseCardNames ?? [])
        {
            if (!TryResolveUnlock(repo, cardName, "franchise card", out var card, out var cardError))
            {
                return Results.BadRequest(new { error = cardError });
            }

            franchiseCarryoverIds.Add(card.Id);
        }

        var setting = repo.GetSetting(request.Setting);
        ResolvedRunSetup setup;
        try
        {
            setup = RunSetup.Resolve(repo, dish.Id, request.SubOptionChoice, request.Heat, ResolveStartingUnlockId(repo, setting, request.CardPack), franchiseCarryoverIds);
        }
        catch (Exception ex) when (ex is ArgumentException or KeyNotFoundException)
        {
            return Results.BadRequest(new { error = ex.Message });
        }

        var ownedIds = new HashSet<int>(setup.InitiallyOwnedIds);
        foreach (var prior in request.PriorTargets)
        {
            // A class-kind prior (CardName null, DishType set) contributes nothing here - we don't
            // know which concrete card of that class it'll actually resolve to, so it can't sharpen
            // a later row's Requires/BlockedBy-based reachability the way an exact-card prior can.
            // Known, accepted limitation, not solved generically.
            if (prior.CardName is null)
            {
                continue;
            }

            // A flexible prior (Day: null) could end up owned at any point, so it's always included
            // regardless of this row's own Day - see PossibleCardsRequest's doc comment. Two fixed
            // days only exclude each other when the prior's day isn't strictly before this row's.
            if (prior.Day is int priorDay && request.Day is int day && priorDay >= day)
            {
                continue;
            }

            if (!TryResolveUnlock(repo, prior.CardName, "card", out var card, out var cardError))
            {
                return Results.BadRequest(new { error = cardError });
            }

            ownedIds.Add(card.Id);
        }

        if (!TryResolvePackId(repo, request.Setting, request.CardPack, out var settingPackId, out var packError))
        {
            return Results.BadRequest(new { error = packError });
        }

        List<int> activePackIds = [settingPackId, .. setup.BonusPackIds];

        // A flexible row (Day: null) isn't scoped to one specific day - union reachability across
        // every day up to the flexible horizon, since offer eligibility genuinely varies per day
        // (not every pack fires every day - see UnlockPackEvaluator.GetPossibleCardIds's doc comment).
        var possibleIds = new HashSet<int>();
        if (request.Day is int fixedDay)
        {
            possibleIds = UnlockPackEvaluator.GetPossibleCardIds(repo, activePackIds, ownedIds, fixedDay, request.Tier);
        }
        else
        {
            for (var day = 0; day <= FlexibleHorizon; day++)
            {
                possibleIds.UnionWith(UnlockPackEvaluator.GetPossibleCardIds(repo, activePackIds, ownedIds, day, request.Tier));
            }
        }

        var names = BuildDisplayNames(repo, possibleIds.Select(repo.GetUnlock)).OrderBy(n => n).ToList();

        var classes = possibleIds
            .Select(repo.GetUnlock)
            .Where(u => u.Kind == "Dish" && u.DishType is int dt && dt != DishTypeCatalog.NullValue)
            .Select(u => u.DishType!.Value)
            .Distinct()
            .OrderBy(dt => dt)
            .Select(dt => new ClassOptionDto(dt, DishTypeCatalog.Names[dt]))
            .ToList();

        return Results.Ok(new PossibleCardsResponse(names, classes));
    }

    /// <summary>
    /// Resolves one DayTargetDto into what SeedSearcher actually matches on - either an exact card
    /// (CardName) or any card of a dish class (DishType, e.g. "Any Side" - see TargetMatcher.cs).
    /// Exactly one of the two should be set on the DTO; an empty CardName with no DishType is the
    /// "genuinely empty row" case the frontend is expected to filter out before submitting, but is
    /// still handled here as an error rather than silently skipped, since StartSearch/GetSearchPath
    /// are also reachable directly (CLI-equivalent callers, curl, etc.).
    /// </summary>
    private static bool TryResolveTargetMatcher(GameDataRepository repo, DayTargetDto target, out TargetMatcher matcher, out string? error)
    {
        if (target.DishType is int dishType)
        {
            if (!DishTypeCatalog.Names.ContainsKey(dishType))
            {
                matcher = null!;
                error = $"Unknown dish class '{dishType}'.";
                return false;
            }

            matcher = new TargetMatcher.AnyOfClass(dishType);
            error = null;
            return true;
        }

        if (string.IsNullOrEmpty(target.CardName))
        {
            matcher = null!;
            error = "Each day target needs either a card name or a dish class.";
            return false;
        }

        if (!TryResolveUnlock(repo, target.CardName, "card", out var card, out var cardError))
        {
            matcher = null!;
            error = cardError;
            return false;
        }

        matcher = new TargetMatcher.ExactCard(card.Id);
        error = null;
        return true;
    }

    private static IResult StartSearch(SearchStartRequest request, GameDataRepository repo, SearchJobManager jobs)
    {
        if (!repo.ListSettings().Any(s => s.Slug == request.Setting))
        {
            return Results.BadRequest(new { error = $"Unknown setting '{request.Setting}'." });
        }

        if (!TryResolveUnlock(repo, request.DishName, "dish", out var dish, out var dishError))
        {
            return Results.BadRequest(new { error = dishError });
        }

        if (dish.HasSubOptions && request.SubOptionChoice is null)
        {
            var opt1 = dish.OptionCard1Id is int o1 ? repo.GetUnlock(o1).Name : null;
            var opt2 = dish.OptionCard2Id is int o2 ? repo.GetUnlock(o2).Name : null;
            return Results.BadRequest(new { error = $"'{dish.Name}' has sub-options - subOptionChoice must be 1 or 2. 1) {opt1}   2) {opt2}" });
        }

        if (request.DayTargets is not { Count: > 0 } && request.OtFriendly != true && request.MapSizes is not { Count: > 0 })
        {
            return Results.BadRequest(new { error = "At least one card target is required (or check OT Friendly / a map size)." });
        }

        var dayTargets = new Dictionary<int, TargetMatcher>();
        var flexibleTargets = new List<TargetMatcher>();
        foreach (var target in request.DayTargets)
        {
            if (!TryResolveTargetMatcher(repo, target, out var matcher, out var matcherError))
            {
                return Results.BadRequest(new { error = matcherError });
            }

            if (target.Day is int day)
            {
                dayTargets[day] = matcher;
            }
            else
            {
                flexibleTargets.Add(matcher);
            }
        }

        // Class targets (TargetMatcher.AnyOfClass) can't participate in this check - we don't know
        // in advance which concrete card a class target will resolve to - so only exact-card targets
        // are checked here. See TargetMatcher.cs / UnlockPackEvaluator.HasBlockedByConflict's scope.
        var exactCardIds = new HashSet<int>(dayTargets.Values.Concat(flexibleTargets).OfType<TargetMatcher.ExactCard>().Select(m => m.CardId));
        if (UnlockPackEvaluator.HasBlockedByConflict(repo, exactCardIds, out var targetConflict))
        {
            return Results.BadRequest(new { error = $"'{repo.GetUnlock(targetConflict.CardId).Name}' and '{repo.GetUnlock(targetConflict.BlockedById).Name}' can never coexist (BlockedBy) - this target set can never be satisfied." });
        }

        var franchiseCarryoverIds = new List<int>();
        foreach (var cardName in request.FranchiseCardNames ?? [])
        {
            if (!TryResolveUnlock(repo, cardName, "franchise card", out var card, out var cardError))
            {
                return Results.BadRequest(new { error = cardError });
            }

            franchiseCarryoverIds.Add(card.Id);
        }

        // Defense in depth for non-JS callers (the CLI/curl/etc. don't go through
        // GetFranchiseCandidates' filtering) - the same Requires/BlockedBy/tier consistency check
        // the candidate-list endpoint uses, applied to the whole submitted set at once. See
        // UnlockPackEvaluator.IsValidCarryoverPick's doc comment for what "valid" means here.
        var franchiseIdSet = new HashSet<int>(franchiseCarryoverIds);
        foreach (var id in franchiseCarryoverIds)
        {
            var otherIds = new HashSet<int>(franchiseIdSet);
            otherIds.Remove(id);
            var candidate = repo.GetUnlock(id);
            if (!UnlockPackEvaluator.IsValidCarryoverPick(repo, candidate, otherIds, request.Tier))
            {
                // A repeatable card (e.g. "Variety") specifically needs every earlier copy of the
                // same card also listed - copies are taken in order (unrelated to the declared Tier
                // field - see IsValidCarryoverPick's doc comment) - so name the missing ones directly
                // rather than the generic message below, which wouldn't tell the user what to add.
                var missingLowerCopies = UnlockNameResolver.GetRepeatableCardSiblings(repo, candidate)
                    .Where(s => s.MinimumFranchiseTier < candidate.MinimumFranchiseTier && !otherIds.Contains(s.Id))
                    .OrderBy(s => s.MinimumFranchiseTier)
                    .Select(s => $"{s.Name} (copy {s.MinimumFranchiseTier + 1})")
                    .ToList();
                var error = missingLowerCopies.Count > 0
                    ? $"'{candidate.Name} (copy {candidate.MinimumFranchiseTier + 1})' also needs its earlier copies listed: {string.Join(", ", missingLowerCopies)}."
                    : $"'{candidate.Name}' isn't a valid franchise-carryover pick given the rest of the selection and tier {request.Tier}.";
                return Results.BadRequest(new { error });
            }
        }

        if (request.Length is < 1 or > Seed.MaxLength)
        {
            return Results.BadRequest(new { error = $"length must be between 1 and {Seed.MaxLength}." });
        }

        var alphabet = string.IsNullOrEmpty(request.Alphabet) ? Seed.FriendlyCharacters : request.Alphabet;
        if (alphabet.Length == 0)
        {
            return Results.BadRequest(new { error = "alphabet must be at least one character." });
        }

        var setting = repo.GetSetting(request.Setting);

        if (!TryResolveMapSizes(setting, request.MapSizes, out var mapSizes, out var mapSizeError))
        {
            return Results.BadRequest(new { error = mapSizeError });
        }

        ResolvedRunSetup setup;
        try
        {
            setup = RunSetup.Resolve(repo, dish.Id, request.SubOptionChoice, request.Heat, ResolveStartingUnlockId(repo, setting, request.CardPack), franchiseCarryoverIds);
        }
        catch (Exception ex) when (ex is ArgumentException or KeyNotFoundException)
        {
            return Results.BadRequest(new { error = ex.Message });
        }

        if (!TryResolvePackId(repo, request.Setting, request.CardPack, out var settingPackId, out var packError))
        {
            return Results.BadRequest(new { error = packError });
        }

        var spaceSize = BigInteger.Pow(alphabet.Length, request.Length);
        var totalToCheck = request.Limit is long l
            ? Math.Min(l, (long)BigInteger.Min(spaceSize, long.MaxValue))
            : (long)BigInteger.Min(spaceSize, long.MaxValue);

        var excludedSeeds = request.ExcludedSeeds is { Count: > 0 }
            ? new HashSet<string>(request.ExcludedSeeds.Select(s => s.ToLowerInvariant()), StringComparer.Ordinal)
            : null;
        var searchRequest = new SeedSearchRequest(
            settingPackId, setup, dayTargets, request.Tier, alphabet, request.Length, totalToCheck, excludedSeeds, flexibleTargets,
            // Reroll temporarily disabled (known-buggy skipChoices mechanic) - re-enable by
            // restoring `request.AllowRerollPerDay == true` here and in GetSearchPath, and
            // un-hiding the checkbox in search.html.
            SettingSlug: request.Setting, OtFriendly: request.OtFriendly == true, AllowRerollPerDay: false, MapSizes: mapSizes);
        long matchCap;
        if (request.StopAfterFirstMatch == true)
        {
            matchCap = 1L;
        }
        else if (request.MaxResults is int maxResults)
        {
            if (maxResults < SeedSearcher.MinMatchCap || maxResults > SeedSearcher.MaxMatchCap)
            {
                return Results.BadRequest(new { error = $"maxResults must be between {SeedSearcher.MinMatchCap} and {SeedSearcher.MaxMatchCap}." });
            }

            matchCap = maxResults;
        }
        else
        {
            matchCap = DefaultMatchCap;
        }

        var job = jobs.StartSearch(searchRequest, matchCap);

        return Results.Accepted($"/api/search/{job.Id}", new SearchStartResponse(job.Id, totalToCheck, spaceSize.ToString()));
    }

    private static IResult GetStatus(string jobId, SearchJobManager jobs)
    {
        if (!jobs.TryGetJob(jobId, out var job))
        {
            return Results.NotFound(new { error = $"Unknown search job '{jobId}'." });
        }

        // This poll IS the heartbeat SearchJobHeartbeatMonitor watches for - see SearchJob.Touch.
        job.Touch();

        var elapsed = (job.FinishedAt ?? DateTimeOffset.UtcNow) - job.StartedAt;
        var seedsPerSecond = job.CheckedCount / Math.Max(elapsed.TotalSeconds, 0.001);

        return Results.Ok(new SearchStatusResponse(
            job.Id,
            job.Status.ToString(),
            job.CheckedCount,
            job.TotalToCheck,
            seedsPerSecond,
            elapsed.TotalSeconds,
            WithEquivalents(job),
            job.Error,
            job.MatchCapReached));
    }

    /// <summary>
    /// Folds a finished job's equivalent-seed groups (see SeedSearchResult.EquivalentSeeds) into its
    /// match list. During the run job.Equivalents is empty, so this is a no-op passthrough; once the
    /// search completes each representative match gains the other seed strings that resolve to the
    /// same game.
    /// </summary>
    private static List<SearchMatchDto> WithEquivalents(SearchJob job) =>
        job.Matches
            .Select(m => job.Equivalents.TryGetValue(m.Seed, out var eq)
                ? m with { EquivalentSeeds = eq.Sample, EquivalentCount = eq.Count }
                : m)
            .ToList();

    /// <summary>
    /// "Which card to pick each day to reproduce this seed's match" for the search results'
    /// click-to-preview dialog - see SeedSearcher.ResolveCardPath's doc comment for why this walks
    /// EVERY day up to the last target (not just target days): schedules like "turbo" draw a card
    /// every day, not just every 3rd, so days without an explicit target still need a concrete pick
    /// or the user has no way to know what to choose there.
    /// </summary>
    private static IResult GetSearchPath(SearchPathRequest request, GameDataRepository repo)
    {
        if (!repo.ListSettings().Any(s => s.Slug == request.Setting))
        {
            return Results.BadRequest(new { error = $"Unknown setting '{request.Setting}'." });
        }

        if (!TryResolveUnlock(repo, request.DishName, "dish", out var dish, out var dishError))
        {
            return Results.BadRequest(new { error = dishError });
        }

        var targetIds = new Dictionary<int, TargetMatcher>();
        var flexibleTargets = new List<TargetMatcher>();
        foreach (var target in request.DayTargets)
        {
            if (!TryResolveTargetMatcher(repo, target, out var matcher, out var matcherError))
            {
                return Results.BadRequest(new { error = matcherError });
            }

            if (target.Day is int day)
            {
                targetIds[day] = matcher;
            }
            else
            {
                flexibleTargets.Add(matcher);
            }
        }

        var franchiseCarryoverIds = new List<int>();
        foreach (var cardName in request.FranchiseCardNames ?? [])
        {
            if (!TryResolveUnlock(repo, cardName, "franchise card", out var card, out var cardError))
            {
                return Results.BadRequest(new { error = cardError });
            }

            franchiseCarryoverIds.Add(card.Id);
        }

        var setting = repo.GetSetting(request.Setting);
        ResolvedRunSetup setup;
        try
        {
            setup = RunSetup.Resolve(repo, dish.Id, request.SubOptionChoice, request.Heat, ResolveStartingUnlockId(repo, setting, request.CardPack), franchiseCarryoverIds);
        }
        catch (Exception ex) when (ex is ArgumentException or KeyNotFoundException)
        {
            return Results.BadRequest(new { error = ex.Message });
        }

        if (!TryResolvePackId(repo, request.Setting, request.CardPack, out var settingPackId, out var packError))
        {
            return Results.BadRequest(new { error = packError });
        }

        var maxDay = Math.Max(targetIds.Count == 0 ? 0 : targetIds.Keys.Max(), flexibleTargets.Count > 0 ? FlexibleHorizon : 0);
        var paths = SeedSearcher.ResolveCardPaths(
            repo, new Seed(request.Seed), settingPackId, setup, targetIds, flexibleTargets, maxDay, request.Tier,
            // Reroll temporarily disabled (known-buggy skipChoices mechanic) - see StartSearch.
            false, MaxCardPaths);

        var pathDtos = paths
            .Select(path => new CardPathDto(path
                .Where(entry => entry.CardId is not null)
                .Select(entry => new CardPathEntryDto(entry.Day, repo.GetUnlock(entry.CardId!.Value).Name, entry.Rerolled))
                .ToList()))
            .ToList();

        return Results.Ok(new SearchPathResponse(pathDtos));
    }

    private static IResult CancelSearch(string jobId, SearchJobManager jobs) =>
        jobs.TryCancel(jobId) ? Results.Ok() : Results.NotFound(new { error = $"Unknown search job '{jobId}'." });

    /// <summary>
    /// Creates a shareable snapshot of a completed search under a fresh random hash - lazy by
    /// design, nothing is written to the cache DB until this is explicitly called (the "Copy Link"
    /// button, not something that fires automatically when a search finishes). The request body is
    /// the client's own form payload resent as-is (SearchJob doesn't retain it server-side today,
    /// and adding that just for this one-off action isn't worth it - see SearchContracts.cs's
    /// CachedSearchResult doc comment).
    /// </summary>
    private static async Task<IResult> ShareSearchResult(
        string jobId, SearchStartRequest request, SearchJobManager jobs, ICacheStore<CachedSearchResult> cache)
    {
        if (!jobs.TryGetJob(jobId, out var job))
        {
            return Results.NotFound(new { error = $"Unknown search job '{jobId}'." });
        }

        if (job.Status != SearchJobStatus.Completed)
        {
            return Results.BadRequest(new { error = "Only a completed search can be shared." });
        }

        var hash = ShareHash.Generate();
        var cached = new CachedSearchResult(request, WithEquivalents(job), job.TotalToCheck, job.CheckedCount, job.MatchCapReached);
        await cache.SetAsync(hash, cached);

        return Results.Ok(new ShareSearchResultResponse(hash));
    }

    private static async Task<IResult> GetCachedSearchResult(string hash, ICacheStore<CachedSearchResult> cache)
    {
        var cached = await cache.GetAsync(hash);
        return cached is null
            ? Results.NotFound(new { error = "This shared search result has expired or doesn't exist." })
            : Results.Ok(cached);
    }

    /// <summary>
    /// Resolves the bonus starting card: an explicit cardPack schedule's own StartingUnlockId if
    /// given (authoritative even when null - e.g. Halloween/January grant none, which isn't a
    /// "fall back to the setting" signal), otherwise the setting's own StartingUnlockId (existing
    /// default behavior). Can throw KeyNotFoundException for an unknown cardPack slug - callers
    /// already catch that alongside RunSetup.Resolve's ArgumentException.
    /// </summary>
    private static int? ResolveStartingUnlockId(GameDataRepository repo, RestaurantSettingData setting, string? cardPack) =>
        string.IsNullOrEmpty(cardPack) ? setting.StartingUnlockId : repo.ResolveCardScheduleStartingUnlockId(cardPack);

    /// <summary>
    /// Resolves the requested map-size names into a set of LayoutSeed.LayoutSize - empty/null input
    /// resolves to null (no filter), matching SeedSearchRequest.MapSizes' convention. Rejects an
    /// unknown name, or any non-empty request against a setting that forces its own bespoke layout
    /// (ForceLayoutId set) - size isn't a meaningful concept there, see LayoutSeed.TryGetSize. Defense
    /// in depth behind the client-side gating in search.js (updateCardPackLock-style
    /// settingSelect.value !== "normal" check that disables the checkboxes).
    /// </summary>
    private static bool TryResolveMapSizes(RestaurantSettingData setting, List<string>? requested, out IReadOnlySet<LayoutSeed.LayoutSize>? mapSizes, out string? error)
    {
        if (requested is not { Count: > 0 })
        {
            mapSizes = null;
            error = null;
            return true;
        }

        if (setting.ForceLayoutId is not null)
        {
            mapSizes = null;
            error = $"Map size filtering doesn't apply to '{setting.DisplayName}' - it always uses its own fixed layout, not the shared size pool.";
            return false;
        }

        var sizes = new HashSet<LayoutSeed.LayoutSize>();
        foreach (var name in requested)
        {
            if (!Enum.TryParse<LayoutSeed.LayoutSize>(name, ignoreCase: true, out var size))
            {
                mapSizes = null;
                error = $"Unknown map size '{name}'. Valid: {string.Join(", ", Enum.GetNames<LayoutSeed.LayoutSize>())}";
                return false;
            }

            sizes.Add(size);
        }

        mapSizes = sizes;
        error = null;
        return true;
    }

    /// <summary>Resolves the pack governing card draws: an explicit cardPack schedule slug if given, otherwise the setting's own pack (existing default behavior).</summary>
    private static bool TryResolvePackId(GameDataRepository repo, string setting, string? cardPack, out int packId, out string? error)
    {
        try
        {
            packId = repo.ResolveCardSchedulePackId(cardPack) ?? repo.ResolveUnlockPackId(setting);
            error = null;
            return true;
        }
        catch (KeyNotFoundException ex)
        {
            packId = 0;
            error = ex.Message;
            return false;
        }
    }

    private static bool TryResolveUnlock(GameDataRepository repo, string name, string label, out UnlockData unlock, out string? error)
    {
        var lookup = UnlockNameResolver.TryResolveByName(repo, name);
        if (lookup.Unlock is not null)
        {
            unlock = lookup.Unlock;
            error = null;
            return true;
        }

        error = UnlockNameResolver.BuildAmbiguousOrUnknownErrorMessage(label, name, lookup);
        unlock = null!;
        return false;
    }

    /// <summary>
    /// Turns a filtered candidate list into display/lookup names, qualifying every repeatable-card
    /// candidate (e.g. "Variety" - one record per time it's been taken across separate franchise
    /// cycles, see UnlockNameResolver.GetRepeatableCardSiblings) as "Name (copy N)" - ALWAYS, not just
    /// when 2+ copies happen to be simultaneously valid in this particular call's result. This
    /// matters because IsValidCarryoverPick's cumulative lower-copy check (see its doc comment) means
    /// at most one copy of a given card is ever addable at once for a fixed owned-set - so two
    /// different &lt;datalist&gt; rows built one after another would otherwise both suggest the exact
    /// same bare "Variety" string (row 2's dropdown reflecting row 1's pick as already-owned), and
    /// submitting two identical bare names would collide during resolution instead of representing
    /// the two distinct copies the user actually picked. Non-repeatable duplicate names
    /// (Advertising/Fish Selection-style - out of scope, see project plan) pass through unqualified,
    /// same as today.
    /// </summary>
    private static List<string> BuildDisplayNames(GameDataRepository repo, IEnumerable<UnlockData> candidates) =>
        candidates
            .Select(c => UnlockNameResolver.GetRepeatableCardSiblings(repo, c).Count > 0
                ? $"{c.Name} (copy {c.MinimumFranchiseTier + 1})"
                : c.Name)
            .ToList();
}
