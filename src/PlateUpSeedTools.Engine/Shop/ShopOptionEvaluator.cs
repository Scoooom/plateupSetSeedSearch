using PlateUpSeedTools.Engine.Data;

namespace PlateUpSeedTools.Engine.Shop;

/// <summary>
/// Port of the Kitchen.ShopBuilder/Kitchen.ShopBuilder.Filters system-group pipeline
/// (CreateShopOptions -&gt; TagStapleWhenMissing -&gt; TagStaples -&gt; FilterByRequiredAppliances -&gt;
/// FilterByRequiredCourse -&gt; FilterByRequiredProcessOrIngredient -&gt; FilterByRequirements -&gt;
/// FilterBlueprintUpgraders -&gt; FilterByTheme), verified against source in this exact order. See
/// docs/game-domain-knowledge.md's "Shop / appliance purchase RNG" section for the full writeup.
///
/// v1 implements steps 2/3/4/5(partial)/8/9 fully. Only step 5's RequiresPhaseForShop half, step 6,
/// and step 7 remain stubbed to always-pass - each needs a dynamic input that genuinely requires new
/// Odin-blob decode work (see OdinFieldReader's class doc and game-domain-knowledge.md's
/// "AddStaple/AddsTheme/AllowRefreshes decode" section for what was and wasn't solvable):
/// - Step 3 (TagStaples' card-driven bonus-staple list, UnlockCard.Effects' ShopEffect.AddStaple) and
///   step 9 (FilterByTheme, KitchenData.ThemeAddEffect.AddsTheme) are BOTH fully implemented - both
///   fields decode as simple scalars from the card's Odin blob via OdinFieldReader, validated against
///   real assets (see UnlockData.GrantsBonusStapleApplianceId/GrantsTheme, populated by
///   CardExtractor.cs). Callers resolve these from the Cards walk's owned-unlock set via
///   <see cref="ResolveBonusStapleApplianceIds"/>/<see cref="ResolveGrantedThemes"/> below.
/// - Step 5's RequiresPhaseForShop half (the SellOnlyIfPlatesNeeded half IS fully implemented, see
///   FilterByRequiredCourse below): needs Dish.RequiresCleaning (derived from Dish.MinimumIngredients,
///   a HashSet&lt;Item&gt; Unity doesn't natively serialize - a genuinely harder decode problem than
///   AddStaple/AddsTheme, see OdinFieldReader's class doc) - RequiresPhaseForShop is 0/451 anyway so
///   this is currently a dormant gap, not an active one.
/// - Step 6 (FilterByRequiredProcessOrIngredient): needs Dish.RequiredProcesses/BeneficialIngredients -
///   same HashSet&lt;T&gt; decode problem as above. 27/451 appliances use RequiresProcessForShopIds -
///   an ACTIVE, still-open gap, the highest-value one remaining.
/// - Step 7 (FilterByRequirements/ShopRequirementFilter): needs Kitchen.CItemProvider.AllowRefreshes -
///   confirmed to decode as a simple scalar too (validated against 114 real appliance assets), but its
///   companion CRequiresSpecificRefresher check wasn't investigated (low priority, only 1/451
///   appliances affected) - not wired up yet.
/// ShopOptionState still carries every appliance-side field for the stubbed steps (plainly extracted,
/// present in shop/appliances.json) so finishing step 6/7 later only needs the dynamic input, not
/// another pass over this pipeline.
/// </summary>
public static class ShopOptionEvaluator
{
    /// <summary>
    /// Builds one day's full candidate option list (step 1, CreateShopOptions, plus steps 2/3/4/5/8).
    /// Does NOT apply per-slot request-tag/dedup logic - that's ShopDaySimulator's job, since it needs
    /// to re-run per slot against the same list (matching the real game re-shuffling the same
    /// candidate set for every shop slot that night).
    /// </summary>
    /// <param name="menuDishIds">
    /// Dish unlock IDs currently "on the menu" - drives step 5's SellOnlyIfPlatesNeeded gate. v1
    /// simplification: approximated as the Cards walk's owned-dish set as of the current day (the real
    /// game's CMenuItem-based menu selection and CProgressionUnlock upcoming-unlock queue aren't
    /// modeled by this engine), documented here rather than silently assumed by the caller.
    /// </param>
    /// <param name="bonusStapleApplianceIds">
    /// Resolve via <see cref="ResolveBonusStapleApplianceIds"/> against the Cards walk's owned-unlock
    /// set - not computed internally since this method doesn't otherwise need the Cards engine.
    /// </param>
    /// <param name="grantedThemes">
    /// DecorationType flags granted so far this run (permanent, OR'd, never cleared - matches the real
    /// game's SGlobalStatusList.Theme). Resolve via <see cref="ResolveGrantedThemes"/>.
    /// </param>
    public static List<ShopOptionState> BuildDailyOptions(
        GameDataRepository repo,
        KitchenApplianceState kitchen,
        IReadOnlySet<int>? bonusStapleApplianceIds = null,
        int currentSeason = 0 /* Season.Normal - real game drives this from wall-clock time, not the seed; v1 always builds the Normal-season pool */,
        IReadOnlySet<int>? menuDishIds = null,
        int grantedThemes = 0)
    {
        var bonusStaples = bonusStapleApplianceIds ?? new HashSet<int>();
        var requiresPlates = MenuRequiresPlates(repo, menuDishIds ?? new HashSet<int>());

        // Step 1 (CreateShopOptions): candidates = purchasable appliances available this season.
        var options = repo.AllShopAppliances
            .Where(a => (a.IsPurchasable || a.IsPurchasableAsUpgrade) && (a.RestrictedToSeason == 0 || a.RestrictedToSeason == currentSeason))
            .Select(a => new ShopOptionState
            {
                ApplianceId = a.Id,
                Tags = (ShoppingTags)a.ShoppingTags,
                SellAsUpgrade = a.IsPurchasableAsUpgrade,
                StapleWhenMissing = a.StapleWhenMissing,
                SellOnlyAsDuplicate = a.SellOnlyAsDuplicate,
                SellOnlyAsUnique = a.SellOnlyAsUnique,
                RequiresForShopIds = a.RequiresForShopIds,
                SellOnlyIfPlatesNeeded = a.SellOnlyIfPlatesNeeded,
                RequiresPhaseForShopIds = a.RequiresPhaseForShopIds,
                RequiresProcessForShopIds = a.RequiresProcessForShopIds,
                RequiresIngredientForShopIds = a.RequiresIngredientForShopIds,
                ShopRequirementFilter = a.ShopRequirementFilter,
                ThemeRequired = a.ThemeRequired,
            })
            .ToList();

        foreach (var option in options)
        {
            TagStapleWhenMissing(option, kitchen);
        }

        foreach (var option in options)
        {
            TagStaples(option, bonusStaples);
        }

        foreach (var option in options)
        {
            FilterByRequiredAppliances(option, kitchen);
        }

        foreach (var option in options)
        {
            FilterByRequiredCourse(option, requiresPlates);
        }

        // Steps 6, 7 (FilterByRequiredProcessOrIngredient/FilterByRequirements) stubbed - see class doc.

        foreach (var option in options)
        {
            FilterBlueprintUpgraders(repo, option, kitchen);
        }

        foreach (var option in options)
        {
            FilterByTheme(option, grantedThemes);
        }

        return options;
    }

    /// <summary>
    /// v1 approximation of HandleNewShop's RestaurantRequiresPlates: true if any dish currently "on the
    /// menu" provides a Main-phase item where Dish.IsMainThatDoesNotNeedPlates is false. See
    /// BuildDailyOptions' menuDishIds doc for the simplification this relies on.
    /// </summary>
    private static bool MenuRequiresPlates(GameDataRepository repo, IReadOnlySet<int> menuDishIds)
    {
        const int mainPhase = 1;

        foreach (var dishId in menuDishIds)
        {
            if (repo.TryGetUnlock(dishId, out var dish) && dish.ProvidesPhase(mainPhase) && dish.IsMainThatDoesNotNeedPlates != true)
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>
    /// Resolves step 3's bonus-staple appliance IDs from a set of owned unlock IDs - every owned
    /// card's UnlockData.GrantsBonusStapleApplianceId (ShopEffect.AddStaple), deduplicated.
    /// </summary>
    public static IReadOnlySet<int> ResolveBonusStapleApplianceIds(GameDataRepository repo, IReadOnlySet<int> ownedUnlockIds)
    {
        var result = new HashSet<int>();
        foreach (var id in ownedUnlockIds)
        {
            if (repo.TryGetUnlock(id, out var unlock) && unlock.GrantsBonusStapleApplianceId is { } applianceId)
            {
                result.Add(applianceId);
            }
        }

        return result;
    }

    /// <summary>
    /// Resolves step 9's granted-theme bitmask from a set of owned unlock IDs - every owned card's
    /// UnlockData.GrantsTheme (ThemeAddEffect.AddsTheme), OR'd together. Matches the real game's
    /// permanent, never-cleared SGlobalStatusList.Theme - callers should pass the Cards walk's FULL
    /// owned-unlock history as of the current day, not just today's menu/dish set.
    /// </summary>
    public static int ResolveGrantedThemes(GameDataRepository repo, IReadOnlySet<int> ownedUnlockIds)
    {
        var themes = 0;
        foreach (var id in ownedUnlockIds)
        {
            if (repo.TryGetUnlock(id, out var unlock) && unlock.GrantsTheme is { } theme)
            {
                themes |= theme;
            }
        }

        return themes;
    }

    /// <summary>Step 2, port of TagStapleWhenMissing.OriginalLambdaBody - byte-verified.</summary>
    private static void TagStapleWhenMissing(ShopOptionState option, KitchenApplianceState kitchen)
    {
        if (option.Staple == ShopStapleType.NonStaple && option.StapleWhenMissing && !kitchen.IsPlaced(option.ApplianceId))
        {
            option.Staple = ShopStapleType.WhenMissing;
        }
    }

    /// <summary>Step 3, port of TagStaples.OriginalLambdaBody - byte-verified.</summary>
    private static void TagStaples(ShopOptionState option, IReadOnlySet<int> bonusStapleApplianceIds)
    {
        if (option.Staple != ShopStapleType.NonStaple)
        {
            return;
        }

        if (bonusStapleApplianceIds.Contains(option.ApplianceId))
        {
            option.Staple = ShopStapleType.BonusStaple;
            return;
        }

        option.Staple = (option.Tags & ShoppingTags.Basic) != 0 ? ShopStapleType.FixedStaple : ShopStapleType.NonStaple;
    }

    /// <summary>Step 4, port of FilterByRequiredAppliances.OriginalLambdaBody/HasRequirements - byte-verified.</summary>
    private static void FilterByRequiredAppliances(ShopOptionState option, KitchenApplianceState kitchen)
    {
        if (option.IsRemoved || option.Staple is ShopStapleType.BonusStaple or ShopStapleType.WhenMissing)
        {
            return;
        }

        if (!HasRequirements(option, kitchen))
        {
            option.IsRemoved = true;
        }
    }

    private static bool HasRequirements(ShopOptionState option, KitchenApplianceState kitchen)
    {
        if (option.SellOnlyAsDuplicate && !kitchen.IsPlaced(option.ApplianceId))
        {
            return false;
        }

        if (option.SellOnlyAsUnique && kitchen.IsPlaced(option.ApplianceId))
        {
            return false;
        }

        if (option.RequiresForShopIds.Count == 0)
        {
            return true;
        }

        return option.RequiresForShopIds.Any(kitchen.IsPlaced);
    }

    /// <summary>
    /// Step 5, port of FilterByRequiredCourse.OriginalLambdaBody - the SellOnlyIfPlatesNeeded half only
    /// (RequiresPhaseForShop is 0/451 and its own dynamic input is Odin-blocked, see class doc - not
    /// implemented). Staple-gated on BonusStaple ONLY, matching the real source exactly - a WhenMissing
    /// tag from step 2 does NOT exempt an option here, unlike steps 4/6. This is why Plates can be
    /// force-included as WhenMissing by step 2 and still get removed here on a day the menu doesn't
    /// need a plate - don't "fix" this guard to also exempt WhenMissing, that would be wrong.
    /// </summary>
    private static void FilterByRequiredCourse(ShopOptionState option, bool requiresPlates)
    {
        if (option.IsRemoved || option.Staple == ShopStapleType.BonusStaple)
        {
            return;
        }

        if (option.SellOnlyIfPlatesNeeded && !requiresPlates)
        {
            option.IsRemoved = true;
        }
    }

    /// <summary>
    /// Step 8, port of FilterBlueprintUpgraders.OriginalLambdaBody - byte-verified. NOT staple-gated
    /// (unlike steps 4-7). Caps BlueprintUpgrader-tagged appliances (Research Desk is the current
    /// example) to one at a time - looks up each PLACED appliance's own tags via the repository
    /// (not today's candidate list), since a placed appliance may not even be in it.
    /// </summary>
    private static void FilterBlueprintUpgraders(GameDataRepository repo, ShopOptionState option, KitchenApplianceState kitchen)
    {
        if ((option.Tags & ShoppingTags.BlueprintUpgrader) == 0)
        {
            return;
        }

        foreach (var placedId in kitchen.PlacedApplianceIds)
        {
            if (placedId == option.ApplianceId)
            {
                continue;
            }

            if (repo.TryGetShopAppliance(placedId, out var placed) && ((ShoppingTags)placed.ShoppingTags & ShoppingTags.BlueprintUpgrader) != 0)
            {
                option.IsRemoved = true;
                break;
            }
        }
    }

    /// <summary>
    /// Step 9, port of FilterByTheme.OriginalLambdaBody - requires ALL of ThemeRequired's bits to be
    /// present in grantedThemes (a themed decoration can in principle require more than one theme at
    /// once, though every appliance in the current dataset has a single-bit ThemeRequired). Not
    /// staple-gated - matches the real source, which has no staple-exemption guard for this filter
    /// (unlike steps 4/5/6).
    /// </summary>
    private static void FilterByTheme(ShopOptionState option, int grantedThemes)
    {
        if (option.IsRemoved || option.ThemeRequired == 0)
        {
            return;
        }

        if ((grantedThemes & option.ThemeRequired) != option.ThemeRequired)
        {
            option.IsRemoved = true;
        }
    }
}
