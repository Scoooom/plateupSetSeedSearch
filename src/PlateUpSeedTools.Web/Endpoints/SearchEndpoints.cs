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

    public static void MapSearchEndpoints(this WebApplication app)
    {
        app.MapGet("/api/search/unlocks", GetUnlocks);
        app.MapGet("/api/search/heat-levels", GetHeatLevels);
        app.MapPost("/api/search/possible-cards", GetPossibleCards);
        app.MapPost("/api/search", StartSearch);
        app.MapGet("/api/search/{jobId}", GetStatus);
        app.MapPost("/api/search/{jobId}/cancel", CancelSearch);
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
            u.IsBaseDish && u.IsUnlockable)).ToList();

        return Results.Ok(dtos);
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

        var settingPackId = repo.ResolveUnlockPackId(request.Setting);
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

        var settingPackId = repo.ResolveUnlockPackId(request.Setting);
        var spaceSize = BigInteger.Pow(alphabet.Length, request.Length);
        var totalToCheck = request.Limit is long l
            ? Math.Min(l, (long)BigInteger.Min(spaceSize, long.MaxValue))
            : (long)BigInteger.Min(spaceSize, long.MaxValue);

        var searchRequest = new SeedSearchRequest(settingPackId, setup, targetIds, request.Tier, alphabet, request.Length, totalToCheck);
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

    private static IResult CancelSearch(string jobId, SearchJobManager jobs) =>
        jobs.TryCancel(jobId) ? Results.Ok() : Results.NotFound(new { error = $"Unknown search job '{jobId}'." });

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
