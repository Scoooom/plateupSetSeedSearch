using System.Numerics;
using PlateUpSeedTools.Engine.Cards;
using PlateUpSeedTools.Engine.Data;
using PlateUpSeedTools.Engine.Seeding;
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

    // AssetReference IDs for the two hand-curated packs whose eligibility rules
    // (Requires/BlockedBy/tier/unlockability) drive the franchise-candidates endpoint below - see
    // data/settings/cards/packs.json.
    private const int FranchiseCardsPackId = 1355831133;
    private const int ThemeCardsPackId = 786043106;

    // How many alternate card-pick paths GetSearchPath surfaces per matched seed - see
    // SeedSearcher.ResolveCardPaths's doc comment for why "all of them" isn't offered.
    private const int MaxCardPaths = 3;

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
    /// Eligible Franchise (group 5) / Theme (group 3) cards for one franchise-carryover row - filters
    /// by the starting dish's initial owned set, Requires/BlockedBy against every OTHER already-
    /// selected card in the form, and the Theme/franchise-tier caps (<see cref="SelectionCapValidator"/>).
    /// Unresolvable/blank names in <see cref="FranchiseCandidatesRequest.OtherSelectedCardNames"/> are
    /// skipped rather than erroring - this endpoint is called on every keystroke elsewhere in the form
    /// and a momentarily-stale other-row value shouldn't break this row's own candidate list.
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
        var tiersUsed = new HashSet<int>();
        var themeUsed = false;
        foreach (var name in request.OtherSelectedCardNames ?? [])
        {
            if (string.IsNullOrEmpty(name) || !TryResolveUnlock(repo, name, "card", out var other, out _))
            {
                continue;
            }

            ownedIds.Add(other.Id);
            switch ((Engine.Data.UnlockGroup)other.UnlockGroup)
            {
                case Engine.Data.UnlockGroup.Theme:
                    themeUsed = true;
                    break;
                case Engine.Data.UnlockGroup.Franchise:
                    tiersUsed.Add(other.MinimumFranchiseTier);
                    break;
            }
        }

        var franchiseFilter = repo.GetPack(FranchiseCardsPackId).Filter ?? throw new InvalidOperationException("Franchise Cards Pack has no Filter");
        var themeFilter = repo.GetPack(ThemeCardsPackId).Filter ?? throw new InvalidOperationException("Theme Cards Pack has no Filter");

        var names = repo.AllUnlocksOrderedById
            .Where(c => (Engine.Data.UnlockGroup)c.UnlockGroup is Engine.Data.UnlockGroup.Franchise or Engine.Data.UnlockGroup.Theme)
            .Where(c => (Engine.Data.UnlockGroup)c.UnlockGroup != Engine.Data.UnlockGroup.Theme || !themeUsed)
            .Where(c => (Engine.Data.UnlockGroup)c.UnlockGroup != Engine.Data.UnlockGroup.Franchise || !tiersUsed.Contains(c.MinimumFranchiseTier))
            .Where(c => UnlockPackEvaluator.IsEligible(repo, c, ownedIds, request.Tier, (Engine.Data.UnlockGroup)c.UnlockGroup == Engine.Data.UnlockGroup.Theme ? themeFilter : franchiseFilter))
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
            if (prior.Day >= request.Day || string.IsNullOrEmpty(prior.CardName))
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

        HashSet<int> possibleIds;
        try
        {
            possibleIds = UnlockPackEvaluator.GetPossibleCardIds(repo, activePackIds, ownedIds, request.Day, request.Tier);
        }
        catch (KeyNotFoundException)
        {
            // santa-workshop's Christmas Pack has an undecoded sub-pack - see
            // UnlockPackEvaluator.GetPossibleCardIds's doc comment and docs/project-history.md's
            // "Known gaps" section. Fail soft here (empty list) rather than 500ing the page.
            possibleIds = [];
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
            return Results.BadRequest(new { error = "At least one day target is required." });
        }

        var targetIds = new Dictionary<int, int>();
        foreach (var target in request.DayTargets)
        {
            if (!TryResolveUnlock(repo, target.CardName, "card", out var card, out var cardError))
            {
                return Results.BadRequest(new { error = cardError });
            }

            targetIds[target.Day] = card.Id;
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

        var capError = SelectionCapValidator.Validate(repo, targetIds.Values.Concat(franchiseCarryoverIds));
        if (capError is not null)
        {
            return Results.BadRequest(new { error = capError });
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
        var searchRequest = new SeedSearchRequest(settingPackId, setup, targetIds, request.Tier, alphabet, request.Length, totalToCheck, excludedSeeds);
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
        foreach (var target in request.DayTargets)
        {
            if (!TryResolveUnlock(repo, target.CardName, "card", out var card, out var cardError))
            {
                return Results.BadRequest(new { error = cardError });
            }

            targetIds[target.Day] = card.Id;
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

        var maxDay = targetIds.Count == 0 ? 0 : targetIds.Keys.Max();
        var paths = SeedSearcher.ResolveCardPaths(repo, new Seed(request.Seed), settingPackId, setup, targetIds, maxDay, request.Tier, MaxCardPaths);

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
