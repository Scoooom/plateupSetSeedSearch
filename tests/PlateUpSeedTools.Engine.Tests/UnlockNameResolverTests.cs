using PlateUpSeedTools.Engine.Data;
using Xunit;

namespace PlateUpSeedTools.Engine.Tests;

/// <summary>
/// Exercises UnlockNameResolver.TryResolveByName's tier-aware/tier-ladder-qualifier handling, added
/// for the real bug report that plain "Variety" (a franchise reward granted once per tier reached,
/// 0-5, all 9 records sharing the literal Name "Variety" - 6 live + 3 inert extraction-artifact
/// duplicates) was irreducibly ambiguous. Uses the real extracted data (not a synthetic fixture) so
/// these tests double as a guard against the underlying data ever changing shape.
/// </summary>
public class UnlockNameResolverTests
{
    // Live (IsUnlockable: true) "Variety" records, one per franchise tier 0-5 - see
    // data/settings/cards/unlocks.json.
    private const int VarietyTier0 = 409052852;
    private const int VarietyTier1 = -1690048134;
    private const int VarietyTier2 = 2026578658;
    private const int VarietyTier3 = -437866401;
    private const int VarietyTier4 = -1122993754;
    private const int VarietyTier5 = -1189327033;

    // The 3 inert IsUnlockable: false duplicate "Variety" rows - must never surface as a resolved
    // match or an ambiguous-match candidate at any tier.
    private static readonly int[] InertVarietyIds = [-1586319402, -771711089, 1366303950];

    // "Advertising" - 2 records, both IsSpecificFranchiseTier: false, identical except raw Id - a
    // genuinely irreducible duplicate (no tier signal), out of scope for this fix.
    private const string AdvertisingName = "Advertising";

    private static GameDataRepository LoadRepo() => new(Path.Combine(AppContext.BaseDirectory, "data", "settings"));

    [Fact]
    public void BareName_LowTier_ResolvesToLiveRecordAtThatTier()
    {
        var repo = LoadRepo();
        var lookup = UnlockNameResolver.TryResolveByName(repo, "Variety", tier: 0);
        Assert.Equal(VarietyTier0, lookup.Unlock?.Id);
    }

    [Fact]
    public void BareName_HigherTier_FloorResolvesToHighestRungAtOrBelowTier()
    {
        var repo = LoadRepo();
        var lookup = UnlockNameResolver.TryResolveByName(repo, "Variety", tier: 3);
        Assert.Equal(VarietyTier3, lookup.Unlock?.Id);
    }

    [Fact]
    public void QualifiedTierSuffix_ResolvesRegardlessOfAmbientTier()
    {
        var repo = LoadRepo();
        var lookup = UnlockNameResolver.TryResolveByName(repo, "Variety (Tier 2)", tier: 0);
        Assert.Equal(VarietyTier2, lookup.Unlock?.Id);
    }

    [Fact]
    public void QualifiedTierSuffix_UnknownTier_IsAmbiguousNotSilentlyWrong()
    {
        var repo = LoadRepo();
        var lookup = UnlockNameResolver.TryResolveByName(repo, "Variety (Tier 9)", tier: 5);
        Assert.Null(lookup.Unlock);
        Assert.NotEmpty(lookup.AmbiguousMatches);
    }

    [Fact]
    public void InertDuplicateRecords_NeverSurfaceAtAnyTier()
    {
        var repo = LoadRepo();
        for (var tier = 0; tier <= 5; tier++)
        {
            var lookup = UnlockNameResolver.TryResolveByName(repo, "Variety", tier);
            Assert.DoesNotContain(InertVarietyIds, id => lookup.Unlock?.Id == id);
            Assert.DoesNotContain(lookup.AmbiguousMatches, m => InertVarietyIds.Contains(m.Id));
        }
    }

    [Fact]
    public void GenuinelyAmbiguousNonTierDuplicate_StillReportsAmbiguous()
    {
        var repo = LoadRepo();
        var lookup = UnlockNameResolver.TryResolveByName(repo, AdvertisingName, tier: 0);
        Assert.Null(lookup.Unlock);
        Assert.Equal(2, lookup.AmbiguousMatches.Count);
    }

    [Fact]
    public void GetTierLadderSiblings_ReturnsAllSixLiveTiers()
    {
        var repo = LoadRepo();
        var variety = repo.GetUnlock(VarietyTier3);
        var siblings = UnlockNameResolver.GetTierLadderSiblings(repo, variety);
        var expected = new HashSet<int> { VarietyTier0, VarietyTier1, VarietyTier2, VarietyTier3, VarietyTier4, VarietyTier5 };
        Assert.Equal(expected, siblings.Select(s => s.Id).ToHashSet());
    }

    [Fact]
    public void GetTierLadderSiblings_NonLadderCard_ReturnsEmpty()
    {
        var repo = LoadRepo();
        var advertising = repo.AllUnlocksOrderedById.First(u => u.Name == AdvertisingName);
        Assert.Empty(UnlockNameResolver.GetTierLadderSiblings(repo, advertising));
    }

    [Fact]
    public void BuildAmbiguousOrUnknownErrorMessage_TierLadder_SuggestsQualifierSyntax()
    {
        var repo = LoadRepo();
        var lookup = UnlockNameResolver.TryResolveByName(repo, "Variety (Tier 9)", tier: 5);
        var message = UnlockNameResolver.BuildAmbiguousOrUnknownErrorMessage("card", "Variety (Tier 9)", lookup);
        Assert.Contains("Tier 0", message);
        Assert.Contains("Tier 5", message);
    }
}
