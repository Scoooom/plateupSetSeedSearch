using PlateUpSeedTools.Engine.Data;

namespace PlateUpSeedTools.Engine.Cards;

/// <summary>Everything a CardDrawSimulator needs before it can simulate day 1 - all deterministic, no RNG (see game-domain-knowledge.md).</summary>
public sealed record ResolvedRunSetup(HashSet<int> InitiallyOwnedIds, List<int> BonusPackIds);

/// <summary>
/// Resolves the starting-dish (+ mandatory sub-option choice), heat level, and setting's bonus
/// StartingUnlock into the initial owned-card set and active bonus packs a CardDrawSimulator needs.
/// Mirrors CreateStartingItems/CreateCardSetsFromSetting/DelegateUnlockSystem's sub-option handling,
/// none of which involve RNG - see game-domain-knowledge.md's "Starting dish" and "Heat levels are
/// cumulative" sections.
/// </summary>
public static class RunSetup
{
    /// <param name="startingDishId">The dish the player picked at run start (search input, not RNG - see game-domain-knowledge.md's "Starting dish: player choice, not RNG").</param>
    /// <param name="subOptionChoice">Required (1 or 2) iff the starting dish has HasSubOptions=true - which of OptionCard1/OptionCard2 was picked (e.g. Spaghetti -> Traditional Recipe vs Extra Starch).</param>
    /// <param name="heatLevel">Cumulative heat level (grants heat cards 1..N) or negative for Chill.</param>
    /// <param name="startingUnlockId">The setting's bonus starting card (RestaurantSettingData.StartingUnlockId), if any - granted immediately, separate from the nightly draw system.</param>
    /// <param name="franchiseCarryoverIds">
    /// Cards already owned from a previous franchise run, if this run was started via "Franchise" off
    /// an existing save rather than fresh (BeginRestaurant.cs copies the selected franchise slot's
    /// CFranchiseItem.Cards onto the new run's starting items - see game-domain-knowledge.md's
    /// "Franchise tier / carryover cards" section). Ground-truth verified 2026-08-10 via
    /// GroundTruthLogger v1.13.0's current_cards capture: a real franchise-continuation run's day=0
    /// owned set was exactly {starting dish, heat cards} plus 5 extra IDs from the prior run - feeding
    /// those 5 in here reproduced the real captured day-by-day sequence bit-for-bit across all 16
    /// days (pinned as CardDrawSimulatorGroundTruthTests.MatchesRealFranchiseCapture). Pass null/empty
    /// for a fresh (non-franchise) run.
    /// </param>
    public static ResolvedRunSetup Resolve(GameDataRepository repo, int startingDishId, int? subOptionChoice, int heatLevel, int? startingUnlockId, IEnumerable<int>? franchiseCarryoverIds = null)
    {
        var owned = new HashSet<int> { startingDishId };
        if (franchiseCarryoverIds is not null)
        {
            owned.UnionWith(franchiseCarryoverIds);
        }

        var startingDish = repo.GetUnlock(startingDishId);
        if (startingDish.HasSubOptions)
        {
            var chosenId = subOptionChoice switch
            {
                1 => startingDish.OptionCard1Id,
                2 => startingDish.OptionCard2Id,
                _ => throw new ArgumentException($"'{startingDish.Name}' has sub-options - subOptionChoice must be 1 or 2", nameof(subOptionChoice)),
            };
            if (chosenId is int id)
            {
                owned.Add(id);
            }
        }

        if (startingUnlockId is int su)
        {
            owned.Add(su);
        }

        var heatCardIds = HeatSelection.ResolveGrantedCardIds(repo.HeatLevels, heatLevel);
        foreach (var id in heatCardIds)
        {
            owned.Add(id);
        }

        var bonusPackIds = HeatBonusPacks.ResolveBonusPackIds(heatCardIds).ToList();
        return new ResolvedRunSetup(owned, bonusPackIds);
    }
}
