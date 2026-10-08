using System.Text.RegularExpressions;
using PlateUpSeedTools.Engine.Cards;
using PlateUpSeedTools.Engine.Data;
using PlateUpSeedTools.Engine.Seeding;
using PlateUpSeedTools.Engine.Shop;
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
        app.MapPost("/api/explorer/shop-day", SimulateShopDay);
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
            var startingUnlockId = string.IsNullOrEmpty(request.CardPack)
                ? setting.StartingUnlockId
                : repo.ResolveCardScheduleStartingUnlockId(request.CardPack);
            setup = RunSetup.Resolve(repo, dish.Id, request.SubOptionChoice, request.Heat, startingUnlockId, franchiseCarryoverIds);
        }
        catch (Exception ex) when (ex is ArgumentException or KeyNotFoundException)
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

        var pathByDay = orderedPath.ToDictionary(p => p.Day, p => p);
        CardDayResult result = null!; // always assigned - day floor is 0, loop always runs at least once
        // RestaurantMode/Kitchen/FindBonusUnlocks.cs's skip_choices = SCardActionDetails.RerollIndex + 1
        // - RerollIndex is a RUN-WIDE cumulative counter, NOT reset per night (ground-truth-confirmed
        // 2026-08-20: a real capture with rerolls on both day 3 and day 5 showed day 3's reroll at
        // skipChoices=1 but day 5's at skipChoices=2, not 1 - the day-3 reroll's usage carried over).
        // So this must count every EARLIER rerolled day in the path, not just ask "was this day
        // rerolled" in isolation.
        var rerollsUsedSoFar = 0;
        // Day 0 is the prep day before the first real night - see SeedSearcher's Walk for why this
        // floor is 0 rather than 1 now (santa-workshop's Christmas Special Card fires there).
        for (var day = 0; day <= request.Day; day++)
        {
            var pathEntry = pathByDay.TryGetValue(day, out var entry) ? entry : null;
            var targetId = pathEntry?.CardId;
            // An already-committed day replays with its own stored reroll bit; the frontier day
            // (request.Day, never in Path) uses the live Reroll flag from this request instead.
            // Reroll temporarily disabled (known-buggy skipChoices mechanic) - see SearchEndpoints.
            // Restore `day == request.Day ? request.Reroll : pathEntry?.Rerolled == true` to re-enable.
            var isRerolled = false;
            var skipChoices = isRerolled ? rerollsUsedSoFar + 1 : 0;
            if (isRerolled)
            {
                rerollsUsedSoFar++;
            }
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
                }, skipChoices);
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

    /// <summary>
    /// Backs the Blueprints view's per-day shop offers, mirroring SimulateDay's "resend the whole
    /// path, replay from day 0" statelessness. Deliberately NOT refactored to share SimulateDay's
    /// replay loop - that loop's frontier-day/live-reroll-flag handling doesn't apply here (this only
    /// ever needs the owned-unlock set as of strictly BEFORE request.Day, never today's own card
    /// offer), and duplicating the simpler subset was safer than risking SimulateDay's existing,
    /// ground-truth-tested behavior.
    /// </summary>
    private static IResult SimulateShopDay(ExplorerShopDayRequest request, GameDataRepository repo)
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

        var rerolls = request.Rerolls ?? [];

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

        var orderedCardPath = request.CardPath.OrderBy(p => p.Day).ToList();
        if (orderedCardPath.Select(p => p.Day).Distinct().Count() != orderedCardPath.Count)
        {
            return Results.BadRequest(new { error = "cardPath must not contain duplicate days." });
        }

        if (orderedCardPath.Count > 0 && orderedCardPath[^1].Day >= request.Day)
        {
            return Results.BadRequest(new { error = "cardPath must only cover days strictly before the requested day." });
        }

        var setting = repo.GetSetting(request.Setting);
        ResolvedRunSetup setup;
        try
        {
            var startingUnlockId = string.IsNullOrEmpty(request.CardPack)
                ? setting.StartingUnlockId
                : repo.ResolveCardScheduleStartingUnlockId(request.CardPack);
            setup = RunSetup.Resolve(repo, dish.Id, request.SubOptionChoice, request.Heat, startingUnlockId, franchiseCarryoverIds);
        }
        catch (Exception ex) when (ex is ArgumentException or KeyNotFoundException)
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
        var cardSim = new CardDrawSimulator(repo, seed, settingPackId, setup, request.Tier);

        // Replay every committed card day strictly before request.Day to arrive at the owned-unlock
        // set the shop's conditional-staple pipeline needs (menu composition, bonus staples, granted
        // themes) - no frontier day, no reroll flag, this never needs to know what TODAY'S card offer
        // is or was.
        var cardPathByDay = orderedCardPath.ToDictionary(p => p.Day, p => p);
        for (var day = 0; day < request.Day; day++)
        {
            var targetId = cardPathByDay.TryGetValue(day, out var entry) ? entry.CardId : (int?)null;
            var thisDay = day;
            try
            {
                cardSim.SimulateDay(day, offered =>
                {
                    if (targetId is null)
                    {
                        return null;
                    }

                    if (offered.Unlock1Id != targetId && offered.Unlock2Id != targetId)
                    {
                        throw new InvalidOperationException($"day {thisDay}'s stored card pick is not a valid offer for this setup.");
                    }

                    return targetId;
                });
            }
            catch (InvalidOperationException ex)
            {
                return Results.BadRequest(new { error = ex.Message });
            }
        }

        var ownedUnlockIds = cardSim.OwnedUnlockIds;
        var bonusStaples = ShopOptionEvaluator.ResolveBonusStapleApplianceIds(repo, ownedUnlockIds);
        var grantedThemes = ShopOptionEvaluator.ResolveGrantedThemes(repo, ownedUnlockIds);

        var profile = repo.ResolveLayoutProfile(request.Setting, seed);
        var kitchen = new KitchenApplianceState(profile.RequiredApplianceIds);
        var orderedShopActions = request.ShopActions.OrderBy(a => a.Day).ToList();
        if (orderedShopActions.Count > 0 && orderedShopActions[^1].Day >= request.Day)
        {
            return Results.BadRequest(new { error = "shopActions must only cover days strictly before the requested day." });
        }

        foreach (var action in orderedShopActions)
        {
            try
            {
                if (action.IsToss)
                {
                    kitchen.TossAppliance(action.ApplianceId);
                }
                else
                {
                    kitchen.PlaceAppliance(action.ApplianceId);
                }
            }
            catch (InvalidOperationException ex)
            {
                return Results.BadRequest(new { error = ex.Message });
            }
        }

        var shopSim = new ShopDaySimulator(repo, seed, kitchen, bonusStaples, currentSeason: 0, menuDishIds: ownedUnlockIds, grantedThemes: grantedThemes);
        var dayRounds = shopSim.SimulateDayWithRerolls(request.Day, rerolls.Count);

        // Per-round location: round 0 uses InitialIsInside. Each later round only gets a FRESH
        // placement roll if it was held at reroll time (ShopRerollInputDto.Held) - unheld rounds
        // inherit the previous round's label unchanged, matching HandleShopReroll.cs's FixedLocation
        // branch (see ShopRerollInputDto's doc for the full mechanic, confirmed from source + a real
        // controlled gameplay test).
        var locations = new List<string>(dayRounds.Count)
        {
            DescribeLocation(ShopSpawnLocationEvaluator.Evaluate(request.InitialIsInside ? ShopSpawnSetting.Inside : ShopSpawnSetting.Outside)),
        };
        for (var i = 0; i < rerolls.Count; i++)
        {
            locations.Add(rerolls[i].Held
                ? DescribeLocation(ShopSpawnLocationEvaluator.Evaluate(rerolls[i].IsInside ? ShopSpawnSetting.Inside : ShopSpawnSetting.Outside))
                : locations[^1]);
        }

        var rounds = dayRounds
            .Select((round, index) => new ShopRoundDto(
                index,
                round.Slots.Select(s => new ShopSlotDto(s.ApplianceId, s.ApplianceId is int id && repo.TryGetShopAppliance(id, out var appliance) ? appliance.Name : null)).ToList(),
                locations[index]))
            .ToList();

        // LayoutProfile.RequiredApplianceIds (what seeds KitchenApplianceState - see
        // ShopDaySimulatorGroundTruthTests' doc comment) includes real placed starter items
        // (Starter Sink, Starter Bin, Counter - all non-purchasable but carry a real ShoppingTags
        // category) alongside internal/structural entities the real game places via the same
        // CLayoutAppliancePlacement mechanism but that no player would recognize as "an appliance"
        // (Item Source, Item Source Reservation, Mess markers, tutorial triggers, etc. - none of
        // these carry ANY shop category). ShoppingTags == 0 combined with non-purchasable is a
        // reliable, data-verified signal for "not a real player-facing kitchen appliance" (209/451
        // appliances match this, confirmed by inspection to be exclusively junk/internal/decor
        // entities, never a legitimate starter item) - filtered here, display-only, so it doesn't
        // affect KitchenApplianceState's actual simulation correctness (e.g. FilterBlueprintUpgraders
        // still sees the real full set internally).
        var placedAppliances = kitchen.PlacedApplianceIds
            .Select(id => (Id: id, Found: repo.TryGetShopAppliance(id, out var appliance), Appliance: appliance))
            .Where(x => !x.Found || x.Appliance.IsPurchasable || x.Appliance.ShoppingTags != 0)
            .Select(x => new PlacedApplianceDto(x.Id, x.Found ? x.Appliance.Name : $"#{x.Id}"))
            .OrderBy(a => a.ApplianceName)
            .ToList();

        return Results.Ok(new ExplorerShopDayResponse(request.Day, rounds, placedAppliances));
    }

    /// <summary>
    /// Plain-language only (see ShopSpawnLocation's doc) - never claim exact-tile precision, this
    /// engine doesn't have ground truth for the exact tile, only the broad setting-driven category.
    /// </summary>
    private static string DescribeLocation(ShopSpawnLocation location) => location switch
    {
        ShopSpawnLocation.NearFrontDoor => "Near the front door",
        ShopSpawnLocation.Inside => "Somewhere inside the restaurant",
        _ => "Unknown",
    };

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

        error = UnlockNameResolver.BuildAmbiguousOrUnknownErrorMessage(label, name, lookup);
        unlock = null!;
        return false;
    }
}
