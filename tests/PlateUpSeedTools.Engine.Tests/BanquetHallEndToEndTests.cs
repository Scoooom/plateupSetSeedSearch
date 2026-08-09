using PlateUpSeedTools.Engine.Layouts;
using PlateUpSeedTools.Engine.Layouts.Decorators;
using PlateUpSeedTools.Engine.Seeding;
using Xunit;

namespace PlateUpSeedTools.Engine.Tests;

/// <summary>
/// End-to-end test running the full ported pipeline (Seed -> LayoutSeed -> graph execution ->
/// decoration) against Banquet Hall's real graph topology. This is a structural/determinism check,
/// NOT yet verified against the real game's actual output for these seeds - that's the manual
/// in-game comparison step in the project plan (M13), still pending.
/// </summary>
public class BanquetHallEndToEndTests
{
    // Placeholder IDs standing in for the real appliance GUIDs (not yet resolved from
    // "Banquet Layout.asset" - that's the M6 extractor's job). Counts matter for decorator
    // behavior/RNG consumption; the specific values don't.
    private static readonly LayoutProfileInfo Profile = new()
    {
        TableApplianceId = 100,
        CounterApplianceId = 101,
        MaximumTables = 2,
        RequiredApplianceIds = [200, 201, 202, 203, 204, 205, 206, 207, 208],
    };

    [Theory]
    [InlineData("bdghjmqr")]
    [InlineData("tvwxy346")]
    [InlineData("b")]
    [InlineData("789")]
    public void ProducesAPlausibleLayout(string seedString)
    {
        var graph = BanquetHallGraphFixture.Build();
        var seed = new Seed(seedString);
        var constructionSeed = LayoutSeed.ConstructionSeed(seed);

        var result = CreateLayoutHelper.ConstructLayout(graph, Profile, constructionSeed);

        Assert.NotEmpty(result.Blueprint.Tiles);
        Assert.Contains(result.Blueprint.Rooms(), r => r.Type == RoomType.Dining);
        Assert.Contains(result.Blueprint.Rooms(), r => r.Type == RoomType.Kitchen);

        // DiningDecorator only commits placements once it hits exactly MaximumTables.
        Assert.Equal(Profile.MaximumTables, result.Decorations.Count(d => d.ApplianceId == Profile.TableApplianceId));

        // KitchenDecorator places every required appliance exactly once (counters at hatches are extra).
        foreach (var applianceId in Profile.RequiredApplianceIds)
        {
            Assert.Single(result.Decorations, d => d.ApplianceId == applianceId);
        }

        // A front door feature must exist somewhere in the final blueprint.
        Assert.Contains(result.Blueprint.Features, f => f.Type == FeatureType.FrontDoor);
    }

    [Fact]
    public void SameSeedProducesIdenticalLayout()
    {
        var graph1 = BanquetHallGraphFixture.Build();
        var graph2 = BanquetHallGraphFixture.Build();
        var seed = new Seed("bdghjmqr");
        var constructionSeed = LayoutSeed.ConstructionSeed(seed);

        var result1 = CreateLayoutHelper.ConstructLayout(graph1, Profile, constructionSeed);
        var result2 = CreateLayoutHelper.ConstructLayout(graph2, Profile, constructionSeed);

        Assert.Equal(result1.Blueprint.Tiles.Count, result2.Blueprint.Tiles.Count);
        Assert.Equal(
            result1.Blueprint.Tiles.OrderBy(t => (t.Key.X, t.Key.Y)).Select(t => (t.Key, t.Value.Type)),
            result2.Blueprint.Tiles.OrderBy(t => (t.Key.X, t.Key.Y)).Select(t => (t.Key, t.Value.Type)));
        Assert.Equal(result1.Decorations, result2.Decorations);
    }

    [Fact]
    public void DifferentSeedsProduceDifferentLayouts()
    {
        var resultA = CreateLayoutHelper.ConstructLayout(
            BanquetHallGraphFixture.Build(), Profile, LayoutSeed.ConstructionSeed(new Seed("bdghjmqr")));
        var resultB = CreateLayoutHelper.ConstructLayout(
            BanquetHallGraphFixture.Build(), Profile, LayoutSeed.ConstructionSeed(new Seed("tvwxy346")));

        Assert.NotEqual(resultA.Decorations, resultB.Decorations);
    }
}
