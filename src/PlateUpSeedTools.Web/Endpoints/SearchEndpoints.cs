using System.Numerics;
using PlateUpSeedTools.Engine.Cards;
using PlateUpSeedTools.Engine.Data;
using PlateUpSeedTools.Engine.Seeding;
using PlateUpSeedTools.Infrastructure.Cache;
using PlateUpSeedTools.Web.Contracts;
using PlateUpSeedTools.Web.Services;

namespace PlateUpSeedTools.Web.Endpoints;

public static class SearchEndpoints
{
    // Hard server-side cap: every web search stops after this many matches, no user override (the
    // CLI has a --max-results override, the web UI intentionally doesn't - see SearchCommand.cs and
    // the project plan's "Match cap + stop after first match" section). "Stop after first match"
    // is just this same mechanism with a cap of 1.
    private const long DefaultMatchCap = 500;

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
            setup = RunSetup.Resolve(repo, dish.Id, request.SubOptionChoice, request.Heat, setting.StartingUnlockId);
        }
        catch (ArgumentException ex)
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

        var names = repo.AllUnlocksOrderedById
            .Where(c => UnlockPackEvaluator.IsValidCarryoverPick(repo, c, ownedIds, request.Tier))
            .Select(c => c.Name)
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
            setup = RunSetup.Resolve(repo, dish.Id, request.SubOptionChoice, request.Heat, setting.StartingUnlockId, franchiseCarryoverIds);
        }
        catch (ArgumentException ex)
        {
            return Results.BadRequest(new { error = ex.Message });
        }

        var ownedIds = new HashSet<int>(setup.InitiallyOwnedIds);
        foreach (var prior in request.PriorTargets)
        {
            if (string.IsNullOrEmpty(prior.CardName))
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

        var names = possibleIds.Select(id => repo.GetUnlock(id).Name).OrderBy(n => n).ToList();
        return Results.Ok(new PossibleCardsResponse(names));
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

        if (request.DayTargets is not { Count: > 0 })
        {
            return Results.BadRequest(new { error = "At least one card target is required." });
        }

        var targetIds = new Dictionary<int, int>();
        var flexibleTargetIds = new HashSet<int>();
        foreach (var target in request.DayTargets)
        {
            if (!TryResolveUnlock(repo, target.CardName, "card", out var card, out var cardError))
            {
                return Results.BadRequest(new { error = cardError });
            }

            if (target.Day is int day)
            {
                targetIds[day] = card.Id;
            }
            else
            {
                flexibleTargetIds.Add(card.Id);
            }
        }

        if (UnlockPackEvaluator.HasBlockedByConflict(repo, new HashSet<int>(targetIds.Values.Concat(flexibleTargetIds)), out var targetConflict))
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
            if (!UnlockPackEvaluator.IsValidCarryoverPick(repo, repo.GetUnlock(id), otherIds, request.Tier))
            {
                return Results.BadRequest(new { error = $"'{repo.GetUnlock(id).Name}' isn't a valid franchise-carryover pick given the rest of the selection and tier {request.Tier}." });
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
        ResolvedRunSetup setup;
        try
        {
            setup = RunSetup.Resolve(repo, dish.Id, request.SubOptionChoice, request.Heat, setting.StartingUnlockId, franchiseCarryoverIds);
        }
        catch (ArgumentException ex)
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
        var searchRequest = new SeedSearchRequest(settingPackId, setup, targetIds, request.Tier, alphabet, request.Length, totalToCheck, excludedSeeds, flexibleTargetIds);
        var matchCap = request.StopAfterFirstMatch == true ? 1L : DefaultMatchCap;
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
            job.Matches.ToList(),
            job.Error,
            job.MatchCapReached));
    }

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

        var targetIds = new Dictionary<int, int>();
        var flexibleTargetIds = new HashSet<int>();
        foreach (var target in request.DayTargets)
        {
            if (!TryResolveUnlock(repo, target.CardName, "card", out var card, out var cardError))
            {
                return Results.BadRequest(new { error = cardError });
            }

            if (target.Day is int day)
            {
                targetIds[day] = card.Id;
            }
            else
            {
                flexibleTargetIds.Add(card.Id);
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
            setup = RunSetup.Resolve(repo, dish.Id, request.SubOptionChoice, request.Heat, setting.StartingUnlockId, franchiseCarryoverIds);
        }
        catch (ArgumentException ex)
        {
            return Results.BadRequest(new { error = ex.Message });
        }

        if (!TryResolvePackId(repo, request.Setting, request.CardPack, out var settingPackId, out var packError))
        {
            return Results.BadRequest(new { error = packError });
        }

        var maxDay = Math.Max(targetIds.Count == 0 ? 0 : targetIds.Keys.Max(), flexibleTargetIds.Count > 0 ? FlexibleHorizon : 0);
        var paths = SeedSearcher.ResolveCardPaths(repo, new Seed(request.Seed), settingPackId, setup, targetIds, flexibleTargetIds, maxDay, request.Tier, MaxCardPaths);

        var pathDtos = paths
            .Select(path => new CardPathDto(path
                .Where(entry => entry.CardId is not null)
                .Select(entry => new CardPathEntryDto(entry.Day, repo.GetUnlock(entry.CardId!.Value).Name))
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
        var cached = new CachedSearchResult(request, job.Matches.ToList(), job.TotalToCheck, job.CheckedCount, job.MatchCapReached);
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

        error = lookup.AmbiguousMatches.Count > 0
            ? $"Ambiguous {label} name '{name}' - {lookup.AmbiguousMatches.Count} matches (IDs: {string.Join(", ", lookup.AmbiguousMatches.Select(m => m.Id))})."
            : lookup.Suggestions.Count > 0
                ? $"Unknown {label} '{name}'. Did you mean: {string.Join(", ", lookup.Suggestions)}?"
                : $"Unknown {label} '{name}'.";
        unlock = null!;
        return false;
    }
}
