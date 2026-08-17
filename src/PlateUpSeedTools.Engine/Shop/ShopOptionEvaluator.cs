using PlateUpSeedTools.Engine.Data;

namespace PlateUpSeedTools.Engine.Shop;

/// <summary>
/// Port of the Kitchen.ShopBuilder/Kitchen.ShopBuilder.Filters system-group pipeline
/// (CreateShopOptions -&gt; TagStapleWhenMissing -&gt; TagStaples -&gt; FilterByRequiredAppliances -&gt;
/// FilterByRequiredCourse -&gt; FilterByRequiredProcessOrIngredient -&gt; FilterByRequirements -&gt;
/// FilterBlueprintUpgraders -&gt; FilterByTheme), verified against source in this exact order. See
/// docs/game-domain-knowledge.md's "Shop / appliance purchase RNG" section for the full writeup.
///
/// v1 implements steps 2/3/4/8 (TagStapleWhenMissing/TagStaples/FilterByRequiredAppliances/
/// FilterBlueprintUpgraders) fully - these govern the staple/duplicate/blueprint-upgrader-cap
/// behavior the search feature actually cares about (e.g. Research Desk). Steps 5/6/7/9
/// (FilterByRequiredCourse/FilterByRequiredProcessOrIngredient/FilterByRequirements/FilterByTheme)
/// are STUBBED to always-pass (no removal) - they need served-dish/menu-phase/placed-provider/theme
/// state this engine doesn't model yet. The extraction diagnostic (ApplianceExtractor's non-empty
/// field counts) showed RequiresForShop always empty, RequiresPhaseForShop always empty,
/// RequiresProcessForShop populated on 27/451, RequiresIngredientForShop on 1/451, ThemeRequired on
/// 26/451, ShopRequirementFilter on 1/451 - a real minority, and none of them apply to Research Desk
/// specifically (its only non-default fields are ShoppingTags and StapleWhenMissing) - acceptable for
/// v1 but revisit if a future target appliance turns out to need one of the stubbed four.
/// </summary>
public static class ShopOptionEvaluator
{
    /// <summary>
    /// Builds one day's full candidate option list (step 1, CreateShopOptions, plus steps 2/3/4/8).
    /// Does NOT apply per-slot request-tag/dedup logic - that's ShopDaySimulator's job, since it needs
    /// to re-run per slot against the same list (matching the real game re-shuffling the same
    /// candidate set for every shop slot that night).
    /// </summary>
    public static List<ShopOptionState> BuildDailyOptions(
        GameDataRepository repo,
        KitchenApplianceState kitchen,
        IReadOnlySet<int>? bonusStapleApplianceIds = null,
        int currentSeason = 0 /* Season.Normal - real game drives this from wall-clock time, not the seed; v1 always builds the Normal-season pool */)
    {
        var bonusStaples = bonusStapleApplianceIds ?? new HashSet<int>();

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

        // Steps 5, 6, 7 (FilterByRequiredCourse/FilterByRequiredProcessOrIngredient/
        // FilterByRequirements) stubbed - see class doc.

        foreach (var option in options)
        {
            FilterBlueprintUpgraders(repo, option, kitchen);
        }

        // Step 9 (FilterByTheme) stubbed - see class doc.

        return options;
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
}
