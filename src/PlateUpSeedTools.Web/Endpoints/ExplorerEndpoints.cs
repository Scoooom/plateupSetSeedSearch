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

        // Day 0 only has a shop at all if Jasmine granted one (CGrantsFirstDayShop) - without it,
        // day 0 is purely a card-draw prep day (see CardDrawSimulator's own, unrelated day-0
        // handling for the Christmas/February special card - not this gate). A 200 with empty
        // Rounds/PlacedAppliances, not a 400: "no shop today" is a legitimate answer, not a
        // malformed request - see explorer-shop.js's loadAndRenderShopDay for the client-side
        // explanatory message this produces.
        if (request.Day == 0 && !request.HasJasmine)
        {
            return Results.Ok(new ExplorerShopDayResponse(0, [], []));
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

        // Same-day actions (Day == request.Day) are now allowed, unlike before - see ShopActionDto's
        // doc comment. A same-day action must say which round it happened during so it can be applied
        // at the right point in the round-by-round loop below, not just "sometime today."
        var orderedShopActions = request.ShopActions.OrderBy(a => a.Day).ThenBy(a => a.Round ?? -1).ToList();
        if (orderedShopActions.Count > 0 && orderedShopActions[^1].Day > request.Day)
        {
            return Results.BadRequest(new { error = "shopActions must not cover a day after the requested day." });
        }

        foreach (var action in orderedShopActions)
        {
            if (action.Day == request.Day && action.Round is null)
            {
                return Results.BadRequest(new { error = "today's shopActions must specify which round they happened during." });
            }

            if (action.Day == request.Day && !action.IsToss && action.SlotIndex is null)
            {
                return Results.BadRequest(new { error = "today's buy actions must specify which slot index was bought." });
            }

            // Defends against a stale client (recorded an action at a round beyond what this exact
            // request's `rerolls` list will simulate - e.g. the client's own reroll-count state was
            // collapsed back down after a day re-view) - without this check the round-by-round loop
            // below simply never reaches that round and the action silently never applies, with no
            // error at all. Same "fail loudly instead of silently diverging" principle as the
            // slot/appliance staleness check further down.
            if (action.Day == request.Day && action.Round > rerolls.Count)
            {
                return Results.BadRequest(new { error = $"shopActions includes round {action.Round}, but only {rerolls.Count} reroll(s) were requested for today - reload this day's shop and retry." });
            }
        }

        foreach (var action in orderedShopActions.Where(a => a.Day < request.Day))
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

        var todayActionsByRound = orderedShopActions
            .Where(a => a.Day == request.Day)
            .GroupBy(a => a.Round!.Value)
            .ToDictionary(g => g.Key, g => g.ToList());

        // Round-by-round, not one SimulateDayWithRerolls call: a same-day buy must (a) mutate kitchen
        // state before the NEXT round is simulated (so TagStapleWhenMissing etc. see it) and (b)
        // permanently remove that slot from every later round (ShopDayRoundState.RemoveSlot) - neither
        // is possible with the all-at-once API. See ShopDayRoundState's doc comment for the full
        // source-confirmed mechanic this models (buying removes the slot with no backfill).
        var shopSim = new ShopDaySimulator(repo, seed, kitchen, bonusStaples, currentSeason: 0, menuDishIds: ownedUnlockIds, grantedThemes: grantedThemes);
        var roundState = shopSim.BeginDayRounds(request.Day);
        var dayRounds = new List<ShopDayResult>(rerolls.Count + 1);
        for (var round = 0; round <= rerolls.Count; round++)
        {
            var result = roundState.SimulateNextRound();
            dayRounds.Add(result);

            if (!todayActionsByRound.TryGetValue(round, out var roundActions))
            {
                continue;
            }

            foreach (var action in roundActions)
            {
                if (!action.IsToss)
                {
                    // Defends against a stale cached table (the browser acting on a round it no longer
                    // matches, e.g. after the setup changed underneath it) - a buy against a slot that
                    // no longer offers that exact appliance is a real, now-reachable failure mode this
                    // change introduces (previously Buy only ever targeted round 0, which could never
                    // go stale relative to itself).
                    var matchingSlot = result.Slots.FirstOrDefault(s => s.SlotIndex == action.SlotIndex);
                    if (matchingSlot is null || matchingSlot.ApplianceId != action.ApplianceId)
                    {
                        return Results.BadRequest(new { error = $"Round {round} slot {action.SlotIndex} no longer matches appliance {action.ApplianceId} - reload this day's shop and retry." });
                    }
                }

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

                if (!action.IsToss)
                {
                    roundState.RemoveSlot(action.SlotIndex!.Value);
                }
            }
        }

        // Per-round location: round 0 uses InitialIsInside/InitialSomeoneInside. Each later round only
        // gets a FRESH placement roll if it was held at reroll time (ShopRerollInputDto.Held) - unheld
        // rounds inherit the previous round's label unchanged, matching HandleShopReroll.cs's
        // FixedLocation branch (see ShopRerollInputDto's doc for the full mechanic, confirmed from
        // source + a real controlled gameplay test).
        var locations = new List<string>(dayRounds.Count)
        {
            DescribeLocation(ShopSpawnLocationEvaluator.Evaluate(
                request.InitialIsInside ? ShopSpawnSetting.Inside : ShopSpawnSetting.Outside,
                request.InitialSomeoneInside)),
        };
        for (var i = 0; i < rerolls.Count; i++)
        {
            locations.Add(rerolls[i].Held
                ? DescribeLocation(ShopSpawnLocationEvaluator.Evaluate(
                    rerolls[i].IsInside ? ShopSpawnSetting.Inside : ShopSpawnSetting.Outside,
                    rerolls[i].SomeoneInside))
                : locations[^1]);
        }

        var rounds = dayRounds
            .Select((round, index) => new ShopRoundDto(
                index,
                round.Slots.Select(s => new ShopSlotDto(s.SlotIndex, s.ApplianceId, s.ApplianceId is int id && repo.TryGetShopAppliance(id, out var appliance) ? appliance.Name : null)).ToList(),
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
        ShopSpawnLocation.RandomInsideTile => "Somewhere inside the restaurant",
        ShopSpawnLocation.NearPlayer => "Somewhere inside, near a player",
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
