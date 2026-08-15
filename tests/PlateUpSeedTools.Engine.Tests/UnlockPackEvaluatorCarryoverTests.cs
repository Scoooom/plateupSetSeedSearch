using PlateUpSeedTools.Engine.Cards;
using PlateUpSeedTools.Engine.Data;
using Xunit;

namespace PlateUpSeedTools.Engine.Tests;

/// <summary>
/// Exercises UnlockPackEvaluator.IsValidCarryoverPick - previously untested. Covers the real bug
/// report fix: "Variety" (a franchise reward granted once per tier reached, 0-5) was structurally
/// impossible to list more than once as a franchise-carryover pick because the franchise-tier check
/// required EXACT equality with the declared tier for IsSpecificFranchiseTier cards - correct for
/// fresh-draw offer timing, wrong for "could this already be owned from franchise history" (a player
/// who reached tier 5 has been granted tiers 0-5's rewards cumulatively). Fixed via
/// FilterData.TierIsFloorNotExact (floor instead of exact-match, carryover-context only) plus a new
/// cumulative lower-tier prerequisite check (can't list a higher tier without every lower tier of the
/// same card also present - franchise tiers are reached in order).
/// </summary>
public class UnlockPackEvaluatorCarryoverTests
{
    private const int VarietyTier0 = 409052852;
    private const int VarietyTier1 = -1690048134;
    private const int VarietyTier2 = 2026578658;
    private const int VarietyTier3 = -437866401;
    private const int VarietyTier4 = -1122993754;
    private const int VarietyTier5 = -1189327033;

    private static GameDataRepository LoadRepo() => new(Path.Combine(AppContext.BaseDirectory, "data", "settings"));

    [Fact]
    public void AcceptsLowerTierSpecificCard_WhenDeclaredTierIsHigher()
    {
        var repo = LoadRepo();
        var candidate = repo.GetUnlock(VarietyTier1);
        // Every lower tier than 1 is just tier 0, which needs to be present too (cumulative check) -
        // include it so this test isolates the floor-vs-exact behavior, not the cumulative one.
        var currentCards = new HashSet<int> { VarietyTier0 };
        Assert.True(UnlockPackEvaluator.IsValidCarryoverPick(repo, candidate, currentCards, tier: 3));
    }

    [Fact]
    public void RejectsHigherTierSpecificCard_WhenDeclaredTierIsLower()
    {
        var repo = LoadRepo();
        var candidate = repo.GetUnlock(VarietyTier4);
        var currentCards = new HashSet<int> { VarietyTier0, VarietyTier1, VarietyTier2, VarietyTier3 };
        Assert.False(UnlockPackEvaluator.IsValidCarryoverPick(repo, candidate, currentCards, tier: 1));
    }

    [Fact]
    public void AcceptsExactTierMatch()
    {
        var repo = LoadRepo();
        var candidate = repo.GetUnlock(VarietyTier2);
        var currentCards = new HashSet<int> { VarietyTier0, VarietyTier1 };
        Assert.True(UnlockPackEvaluator.IsValidCarryoverPick(repo, candidate, currentCards, tier: 2));
    }

    [Fact]
    public void RejectsHigherTier_WhenLowerTiersOfSameCardAreMissing()
    {
        var repo = LoadRepo();
        var candidate = repo.GetUnlock(VarietyTier2);
        // Tier 0 present, tier 1 missing - even though the declared tier (5) is high enough on its
        // own, the cumulative chain is broken.
        var currentCards = new HashSet<int> { VarietyTier0 };
        Assert.False(UnlockPackEvaluator.IsValidCarryoverPick(repo, candidate, currentCards, tier: 5));
    }

    [Fact]
    public void AcceptsHigherTier_OnceEveryLowerTierIsPresent()
    {
        var repo = LoadRepo();
        var candidate = repo.GetUnlock(VarietyTier2);
        var currentCards = new HashSet<int> { VarietyTier0, VarietyTier1 };
        Assert.True(UnlockPackEvaluator.IsValidCarryoverPick(repo, candidate, currentCards, tier: 5));
    }

    /// <summary>Direct regression test for the bug report's own claim: all 6 tiers simultaneously valid.</summary>
    [Fact]
    public void AllSixVarietyTiers_AreSimultaneouslyValidAtTier5()
    {
        var repo = LoadRepo();
        int[] allTiers = [VarietyTier0, VarietyTier1, VarietyTier2, VarietyTier3, VarietyTier4, VarietyTier5];

        foreach (var id in allTiers)
        {
            var candidate = repo.GetUnlock(id);
            var otherIds = new HashSet<int>(allTiers);
            otherIds.Remove(id);
            Assert.True(UnlockPackEvaluator.IsValidCarryoverPick(repo, candidate, otherIds, tier: 5));
        }
    }
}
