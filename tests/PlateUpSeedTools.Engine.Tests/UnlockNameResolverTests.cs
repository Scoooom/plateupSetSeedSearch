using PlateUpSeedTools.Engine.Data;
using Xunit;

namespace PlateUpSeedTools.Engine.Tests;

/// <summary>
/// Exercises UnlockNameResolver.TryResolveByName's repeatable-card/occurrence-qualifier handling,
/// added for the real bug report that plain "Variety" (a franchise-carryover card takeable more than
/// once across separate franchise cycles - 6 live records + 3 inert extraction-artifact duplicates,
/// all sharing the literal Name "Variety") was irreducibly ambiguous. CORRECTED after a follow-up
/// user report: the numbered suffix distinguishing the 6 live records (MinimumFranchiseTier 0-5) is
/// an occurrence COUNT ("the Nth time you've taken this card"), not the franchise tier you were on
/// when you took it - those are unrelated, so resolution must never depend on a declared tier value.
/// Uses the real extracted data (not a synthetic fixture) so these tests double as a guard against
/// the underlying data ever changing shape.
/// </summary>
public class UnlockNameResolverTests
{
    // Live (IsUnlockable: true) "Variety" records, one per occurrence (copy) 1-6 - see
    // data/settings/cards/unlocks.json. MinimumFranchiseTier is 0-indexed; "copy N" display is 1-indexed.
    private const int VarietyCopy1 = 409052852;
    private const int VarietyCopy6 = -1189327033;

    // The 3 inert IsUnlockable: false duplicate "Variety" rows - must never surface as a resolved
    // match or an ambiguous-match candidate.
    private static readonly int[] InertVarietyIds = [-1586319402, -771711089, 1366303950];

    // "Advertising" - 2 records, both IsSpecificFranchiseTier: false, identical except raw Id - a
    // genuinely irreducible duplicate (no occurrence signal), out of scope for this fix.
    private const string AdvertisingName = "Advertising";

    private static GameDataRepository LoadRepo() => new(Path.Combine(AppContext.BaseDirectory, "data", "settings"));

    [Fact]
    public void BareRepeatableCardName_IsAlwaysAmbiguous()
    {
        var repo = LoadRepo();
        var lookup = UnlockNameResolver.TryResolveByName(repo, "Variety");
        Assert.Null(lookup.Unlock);
        Assert.Equal(6, lookup.AmbiguousMatches.Count);
    }

    [Fact]
    public void QualifiedCopyNumber_ResolvesToTheCorrectRecord()
    {
        var repo = LoadRepo();
        Assert.Equal(VarietyCopy1, UnlockNameResolver.TryResolveByName(repo, "Variety (copy 1)").Unlock?.Id);
        Assert.Equal(VarietyCopy6, UnlockNameResolver.TryResolveByName(repo, "Variety (copy 6)").Unlock?.Id);
    }

    [Fact]
    public void QualifiedCopyNumber_IsUnaffectedByAnyDeclaredTierConcept()
    {
        // No tier parameter exists on TryResolveByName at all anymore - this test exists to pin that
        // down explicitly, since a prior (reverted) version of this fix wrongly floor-resolved a bare
        // name against a declared tier value. Resolving the same qualified name repeatedly must
        // always give the same answer.
        var repo = LoadRepo();
        var first = UnlockNameResolver.TryResolveByName(repo, "Variety (copy 3)");
        var second = UnlockNameResolver.TryResolveByName(repo, "Variety (copy 3)");
        Assert.Equal(first.Unlock?.Id, second.Unlock?.Id);
        Assert.NotNull(first.Unlock);
    }

    [Fact]
    public void QualifiedCopyNumber_OutOfRange_IsAmbiguousNotSilentlyWrong()
    {
        var repo = LoadRepo();
        var lookup = UnlockNameResolver.TryResolveByName(repo, "Variety (copy 9)");
        Assert.Null(lookup.Unlock);
        Assert.NotEmpty(lookup.AmbiguousMatches);
    }

    [Fact]
    public void InertDuplicateRecords_NeverSurface()
    {
        var repo = LoadRepo();
        var lookup = UnlockNameResolver.TryResolveByName(repo, "Variety");
        Assert.DoesNotContain(lookup.AmbiguousMatches, m => InertVarietyIds.Contains(m.Id));

        foreach (var id in InertVarietyIds)
        {
            var byId = repo.GetUnlock(id);
            var qualifiedLookup = UnlockNameResolver.TryResolveByName(repo, $"Variety (copy {byId.MinimumFranchiseTier + 1})");
            Assert.NotEqual(id, qualifiedLookup.Unlock?.Id);
        }
    }

    [Fact]
    public void GenuinelyAmbiguousNonRepeatableDuplicate_StillReportsAmbiguous()
    {
        var repo = LoadRepo();
        var lookup = UnlockNameResolver.TryResolveByName(repo, AdvertisingName);
        Assert.Null(lookup.Unlock);
        Assert.Equal(2, lookup.AmbiguousMatches.Count);
    }

    [Fact]
    public void GetRepeatableCardSiblings_ReturnsAllSixLiveCopies()
    {
        var repo = LoadRepo();
        var variety = repo.GetUnlock(VarietyCopy1);
        var siblings = UnlockNameResolver.GetRepeatableCardSiblings(repo, variety);
        Assert.Equal(6, siblings.Count);
        Assert.DoesNotContain(siblings, s => InertVarietyIds.Contains(s.Id));
    }

    [Fact]
    public void GetRepeatableCardSiblings_NonRepeatableCard_ReturnsEmpty()
    {
        var repo = LoadRepo();
        var advertising = repo.AllUnlocksOrderedById.First(u => u.Name == AdvertisingName);
        Assert.Empty(UnlockNameResolver.GetRepeatableCardSiblings(repo, advertising));
    }

    [Fact]
    public void BuildAmbiguousOrUnknownErrorMessage_RepeatableCard_SuggestsCopyQualifierSyntax()
    {
        var repo = LoadRepo();
        var lookup = UnlockNameResolver.TryResolveByName(repo, "Variety");
        var message = UnlockNameResolver.BuildAmbiguousOrUnknownErrorMessage("card", "Variety", lookup);
        Assert.Contains("copy 1", message);
        Assert.Contains("copy 6", message);
    }
}
