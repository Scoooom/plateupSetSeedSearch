using PlateUpSeedTools.Engine.Cards;
using PlateUpSeedTools.Engine.Data;
using Xunit;

namespace PlateUpSeedTools.Engine.Tests;

/// <summary>
/// Exercises UnlockPackEvaluator.IsValidCarryoverPick - previously untested. Covers the real bug
/// report fix: "Variety" (a franchise-carryover card takeable more than once across separate
/// franchise cycles) was structurally impossible to list more than once because the franchise-tier
/// check required EXACT equality with the declared tier for IsSpecificFranchiseTier cards.
/// CORRECTED after a follow-up user report: that declared-tier comparison was never meaningful for
/// repeatable cards in the first place - a repeatable card's MinimumFranchiseTier is a same-name
/// occurrence index ("the Nth time you've taken this card"), completely decoupled from which overall
/// franchise cycle you were on when you took it (you could take your first "Variety" on your fifth
/// franchise cycle, having taken five different cards on the four before it). Fixed via
/// FilterData.SkipSpecificFranchiseTierCheck (skips the tier comparison entirely for repeatable
/// cards, carryover-context only - no floor, no exact match, no relationship to declared tier at
/// all) plus the cumulative lower-copy prerequisite check (can't list copy 3 without copies 1 and 2
/// already listed - independent of declared tier, unaffected by this correction).
/// </summary>
public class UnlockPackEvaluatorCarryoverTests
{
    private const int VarietyCopy1 = 409052852;
    private const int VarietyCopy2 = -1690048134;
    private const int VarietyCopy3 = 2026578658;
    private const int VarietyCopy4 = -437866401;
    private const int VarietyCopy5 = -1122993754;
    private const int VarietyCopy6 = -1189327033;

    private static GameDataRepository LoadRepo() => new(Path.Combine(AppContext.BaseDirectory, "data", "settings"));

    [Fact]
    public void FirstCopyIsValid_AtAnyDeclaredTier_IncludingZero()
    {
        // Direct regression test for the user's own scenario: taking your first "Variety" doesn't
        // require having reached any particular franchise tier.
        var repo = LoadRepo();
        var candidate = repo.GetUnlock(VarietyCopy1);
        var currentCards = new HashSet<int>();
        Assert.True(UnlockPackEvaluator.IsValidCarryoverPick(repo, candidate, currentCards, tier: 0));
        Assert.True(UnlockPackEvaluator.IsValidCarryoverPick(repo, candidate, currentCards, tier: 5));
    }

    [Fact]
    public void SecondCopyIsValid_AtDeclaredTierZero_OnceFirstCopyIsPresent()
    {
        // The user's exact example: taking "Bootstrapping" on franchise cycle 1, then "Variety" on
        // cycle 2 - your FIRST Variety, with no connection to the overall cycle count. Modeled here
        // as copy 2 being valid even at a low/zero declared tier, as long as copy 1 is present.
        var repo = LoadRepo();
        var candidate = repo.GetUnlock(VarietyCopy2);
        var currentCards = new HashSet<int> { VarietyCopy1 };
        Assert.True(UnlockPackEvaluator.IsValidCarryoverPick(repo, candidate, currentCards, tier: 0));
    }

    [Fact]
    public void RejectsHigherCopy_WhenLowerCopiesOfSameCardAreMissing_RegardlessOfDeclaredTier()
    {
        var repo = LoadRepo();
        var candidate = repo.GetUnlock(VarietyCopy3);
        // Copy 1 present, copy 2 missing - the cumulative chain is broken regardless of tier.
        var currentCards = new HashSet<int> { VarietyCopy1 };
        Assert.False(UnlockPackEvaluator.IsValidCarryoverPick(repo, candidate, currentCards, tier: 0));
        Assert.False(UnlockPackEvaluator.IsValidCarryoverPick(repo, candidate, currentCards, tier: 99));
    }

    [Fact]
    public void AcceptsHigherCopy_OnceEveryLowerCopyIsPresent()
    {
        var repo = LoadRepo();
        var candidate = repo.GetUnlock(VarietyCopy3);
        var currentCards = new HashSet<int> { VarietyCopy1, VarietyCopy2 };
        Assert.True(UnlockPackEvaluator.IsValidCarryoverPick(repo, candidate, currentCards, tier: 0));
    }

    /// <summary>
    /// Direct regression test for the bug report's own claim: all 6 copies simultaneously valid, even
    /// at declared tier 0 - this specifically could NOT pass under the previously-rejected
    /// floor-based design, since tier 0 would only ever permit copy 1.
    /// </summary>
    [Fact]
    public void AllSixVarietyCopies_AreSimultaneouslyValidAtTierZero()
    {
        var repo = LoadRepo();
        int[] allCopies = [VarietyCopy1, VarietyCopy2, VarietyCopy3, VarietyCopy4, VarietyCopy5, VarietyCopy6];

        foreach (var id in allCopies)
        {
            var candidate = repo.GetUnlock(id);
            var otherIds = new HashSet<int>(allCopies);
            otherIds.Remove(id);
            Assert.True(UnlockPackEvaluator.IsValidCarryoverPick(repo, candidate, otherIds, tier: 0));
        }
    }
}
