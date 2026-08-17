namespace PlateUpSeedTools.Engine.Shop;

/// <summary>Port of Common/Kitchen/DifficultyHelpers.cs's shop-related formulas - byte-verified against source.</summary>
public static class ShopDifficultyHelpers
{
    public static int TotalShopCount(int day) => 5;

    public static int StapleCount(int day) => Math.Clamp(4 - (int)Math.Floor(day / 5f), 2, 4);

    public static float BaseUpgradedShopChance(int day) => (float)Math.Floor(day / 5f) * 0.1f;

    /// <summary>
    /// Port of CreateShopRequests.OnUpdate's slot-composition logic - byte-verified against source.
    /// <paramref name="availableStapleTaggedOptionCount"/> is the day's already-tagged
    /// ShopOptionState.TakesStapleSlot count (from ShopOptionEvaluator.BuildDailyOptions), which the
    /// real game requires the day%5-off staple-slot count to be clamped against.
    ///
    /// <paramref name="removedShopCount"/> ports CRemovesShopBlueprint's slot-count adjustment -
    /// hardcoded to 0 by every v1 caller (not extracted/modeled this pass, see class doc). Note the
    /// real game sums this two different ways depending on branch (day%5 sums only NEGATIVE Count
    /// entries; the normal-day branch sums all entries) - moot while callers always pass 0.
    /// </summary>
    public static List<ShoppingTags> GetSlotRequests(int day, int availableStapleTaggedOptionCount, int removedShopCount = 0)
    {
        var requests = new List<ShoppingTags>();

        if (day > 0 && day % 5 == 0)
        {
            for (var i = 0; i < 8 - removedShopCount; i++)
            {
                requests.Add(ShoppingTags.Decoration);
            }

            for (var i = 0; i < 2; i++)
            {
                requests.Add(ShoppingTags.SpecialEvent);
            }

            // 6 "decor shops" (CNewDecorShop) intentionally NOT represented here - a separate
            // mechanic drawing from a Decor pool, not CShopBuilderOption/appliances at all. Callers
            // must not assume this list's length means "every shop slot that day."
            return requests;
        }

        var totalSlots = Math.Max(1, TotalShopCount(day) - removedShopCount);
        var stapleSlots = Math.Max(0, Math.Min(StapleCount(day), totalSlots));
        if (stapleSlots > availableStapleTaggedOptionCount)
        {
            stapleSlots = availableStapleTaggedOptionCount;
        }

        var randomSlots = Math.Max(0, totalSlots - stapleSlots);

        for (var i = 0; i < stapleSlots; i++)
        {
            requests.Add(ShoppingTags.Basic);
        }

        for (var i = 0; i < randomSlots; i++)
        {
            requests.Add(ShoppingTagsExtensions.DefaultShoppingTag);
        }

        return requests;
    }
}
