using PlateUpSeedTools.Engine.Cards;
using PlateUpSeedTools.Engine.Data;
using Xunit;

namespace PlateUpSeedTools.Engine.Tests;

/// <summary>
/// Exercises UnlockPackEvaluator.GetPossibleCardIds - the deterministic "what could possibly be
/// offered on day N" query added for the search UI's day-target dropdown filtering. Uses the same
/// Normal-setting/Pies-dish/Heat-0 setup as CardDrawSimulatorGroundTruthTests, and the exact
/// Requires/BlockedBy pair the user's own feature request used as an example: "Large Groups"
/// requires "Medium Groups", and "Medium Groups"/"Individual Dining" mutually block each other.
/// </summary>
public class UnlockPackEvaluatorPossibleCardsTests
{
    private const int PiesId = -133939790;
    private const int MediumGroupsId = -1183014556;
    private const int LargeGroupsId = -523195599;
    private const int IndividualDiningId = -1747821833;

    [Fact]
    public void RequiresIds_CardOnlyPossibleOnceTheRequirementIsOwned()
    {
        var repo = new GameDataRepository(Path.Combine(AppContext.BaseDirectory, "data", "settings"));
        var settingPackId = repo.ResolveUnlockPackId("normal");
        var setup = RunSetup.Resolve(repo, PiesId, subOptionChoice: null, heatLevel: 0, startingUnlockId: null);
        List<int> activePackIds = [settingPackId, .. setup.BonusPackIds];

        var withoutRequirement = UnlockPackEvaluator.GetPossibleCardIds(repo, activePackIds, setup.InitiallyOwnedIds, day: 3, tier: 0);
        Assert.DoesNotContain(LargeGroupsId, withoutRequirement);

        var withRequirement = new HashSet<int>(setup.InitiallyOwnedIds) { MediumGroupsId };
        var withRequirementResult = UnlockPackEvaluator.GetPossibleCardIds(repo, activePackIds, withRequirement, day: 3, tier: 0);
        Assert.Contains(LargeGroupsId, withRequirementResult);
    }

    [Fact]
    public void BlockedByIds_CardNoLongerPossibleOnceTheBlockerIsOwned()
    {
        var repo = new GameDataRepository(Path.Combine(AppContext.BaseDirectory, "data", "settings"));
        var settingPackId = repo.ResolveUnlockPackId("normal");
        var setup = RunSetup.Resolve(repo, PiesId, subOptionChoice: null, heatLevel: 0, startingUnlockId: null);
        List<int> activePackIds = [settingPackId, .. setup.BonusPackIds];

        var withoutBlocker = UnlockPackEvaluator.GetPossibleCardIds(repo, activePackIds, setup.InitiallyOwnedIds, day: 3, tier: 0);
        Assert.Contains(IndividualDiningId, withoutBlocker);

        var withBlocker = new HashSet<int>(setup.InitiallyOwnedIds) { MediumGroupsId };
        var withBlockerResult = UnlockPackEvaluator.GetPossibleCardIds(repo, activePackIds, withBlocker, day: 3, tier: 0);
        Assert.DoesNotContain(IndividualDiningId, withBlockerResult);
    }

    [Fact]
    public void ExactIdDedup_OwnedCardIsNeverOfferedAgain()
    {
        var repo = new GameDataRepository(Path.Combine(AppContext.BaseDirectory, "data", "settings"));
        var settingPackId = repo.ResolveUnlockPackId("normal");
        var setup = RunSetup.Resolve(repo, PiesId, subOptionChoice: null, heatLevel: 0, startingUnlockId: null);
        List<int> activePackIds = [settingPackId, .. setup.BonusPackIds];

        var owned = new HashSet<int>(setup.InitiallyOwnedIds) { MediumGroupsId };
        var possible = UnlockPackEvaluator.GetPossibleCardIds(repo, activePackIds, owned, day: 3, tier: 0);
        Assert.DoesNotContain(MediumGroupsId, possible);
    }
}
