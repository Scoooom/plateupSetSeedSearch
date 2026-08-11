using System.Text.RegularExpressions;
using PlateUpSeedTools.Engine.Cards;
using PlateUpSeedTools.Engine.Data;
using PlateUpSeedTools.Engine.Seeding;
using PlateUpSeedTools.Infrastructure.Cache;
using PlateUpSeedTools.Web.Contracts;

namespace PlateUpSeedTools.Web.Endpoints;

/// <summary>
/// Backs the Seed Explorer page: given one seed + run setup, replay the card-draw simulation up to a
/// requested day and report that day's offer. Deliberately stateless (see ExplorerContracts.cs) so
/// the client can freely branch - going back to an earlier day and requesting it again with a
/// shorter/altered Path just replays from day 1 again, no server-side undo needed.
/// </summary>
public static partial class ExplorerEndpoints
{
    [GeneratedRegex("^[a-zA-Z0-9]{1,8}$")]
    private static partial Regex ValidSeedPattern();

    public static void MapExplorerEndpoints(this WebApplication app)
    {
        app.MapPost("/api/explorer/day", SimulateDay);
        app.MapPost("/api/explorer/preload", SavePreload);
        app.MapGet("/api/explorer/preload/{hash}", GetPreload);
    }

    /// <summary>
    /// Lazy by design, same as SearchEndpoints.ShareSearchResult - only writes to the cache DB when
    /// explicitly called (a "Copy Link"/"Open in Seed Explorer" click), never automatically.
    /// </summary>
    private static async Task<IResult> SavePreload(ExplorerPreload preload, ICacheStore<ExplorerPreload> cache)
    {
        var hash = ShareHash.Generate();
        await cache.SetAsync(hash, preload);
        return Results.Ok(new SaveExplorerPreloadResponse(hash));
    }

    private static async Task<IResult> GetPreload(string hash, ICacheStore<ExplorerPreload> cache)
    {
        var preload = await cache.GetAsync(hash);
        return preload is null
            ? Results.NotFound(new { error = "This shared Explorer link has expired or doesn't exist." })
            : Results.Ok(preload);
    }

    private static IResult SimulateDay(ExplorerDayRequest request, GameDataRepository repo)
    {
        if (!repo.ListSettings().Any(s => s.Slug == request.Setting))
        {
            return Results.BadRequest(new { error = $"Unknown setting '{request.Setting}'." });
        }

        if (!ValidSeedPattern().IsMatch(request.Seed))
        {
            return Results.BadRequest(new { error = "Seed must be 1-8 alphanumeric characters." });
        }

        if (request.Day < 0 || request.Day > 15)
        {
            return Results.BadRequest(new { error = "day must be between 0 and 15." });
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

        // Path entries only cover days the player actually accepted a card - a day they skipped
        // (declined the offer) has no entry, which is a legitimate gap, not an error: SimulateDay
        // still runs through every day 0..request.Day regardless, treating any day missing from the
        // path as a decline. Duplicate/out-of-range entries are the only real error cases here.
        var orderedPath = request.Path.OrderBy(p => p.Day).ToList();
        if (orderedPath.Select(p => p.Day).Distinct().Count() != orderedPath.Count)
        {
            return Results.BadRequest(new { error = "path must not contain duplicate days." });
        }

        if (orderedPath.Count > 0 && orderedPath[^1].Day >= request.Day)
        {
            return Results.BadRequest(new { error = "path must only cover days strictly before the requested day." });
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

        int settingPackId;
        try
        {
            settingPackId = repo.ResolveCardSchedulePackId(request.CardPack) ?? repo.ResolveUnlockPackId(request.Setting);
        }
        catch (KeyNotFoundException ex)
        {
            return Results.BadRequest(new { error = ex.Message });
        }

        var seed = new Seed(request.Seed.ToLowerInvariant());
        var sim = new CardDrawSimulator(repo, seed, settingPackId, setup, request.Tier);

        var pathById = orderedPath.ToDictionary(p => p.Day, p => p.CardId);
        CardDayResult result = null!; // always assigned - day floor is 0, loop always runs at least once
        // Day 0 is the prep day before the first real night - see SeedSearcher's Walk for why this
        // floor is 0 rather than 1 now (santa-workshop's Christmas Special Card fires there).
        for (var day = 0; day <= request.Day; day++)
        {
            var targetId = pathById.TryGetValue(day, out var id) ? id : (int?)null;
            var thisDay = day;
            try
            {
                result = sim.SimulateDay(day, offered =>
                {
                    if (targetId is null)
                    {
                        return null; // The new frontier - report the offer without committing to a pick.
                    }

                    if (offered.Unlock1Id != targetId && offered.Unlock2Id != targetId)
                    {
                        throw new InvalidOperationException($"day {thisDay}'s stored pick is not a valid offer for this setup.");
                    }

                    return targetId;
                });
            }
            catch (InvalidOperationException ex)
            {
                return Results.BadRequest(new { error = ex.Message });
            }
        }

        // result is guaranteed non-null here: the day floor is 0 (see the loop above) and
        // request.Day < 0 is already rejected earlier, so the loop always runs at least once.
        var options = new List<ExplorerCardOptionDto>();
        if (result.Unlock1Id is int u1)
        {
            options.Add(ToOptionDto(repo, u1));
        }

        if (result.Unlock2Id is int u2)
        {
            options.Add(ToOptionDto(repo, u2));
        }

        var sourcePackName = result.SourcePackId is int packId ? repo.GetPack(packId).Name : null;
        return Results.Ok(new ExplorerDayResponse(request.Day, options, sourcePackName, result.SelectedId));
    }

    private static ExplorerCardOptionDto ToOptionDto(GameDataRepository repo, int id)
    {
        var unlock = repo.GetUnlock(id);
        return new ExplorerCardOptionDto(unlock.Id, unlock.Name, unlock.Kind, unlock.UnlockGroup);
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
