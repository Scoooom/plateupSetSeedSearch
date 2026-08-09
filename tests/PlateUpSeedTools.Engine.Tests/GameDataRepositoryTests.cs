using PlateUpSeedTools.Engine.Data;
using PlateUpSeedTools.Engine.Layouts;
using PlateUpSeedTools.Engine.Seeding;
using Xunit;

namespace PlateUpSeedTools.Engine.Tests;

/// <summary>
/// Cross-checks the real DataExtractor output (data/settings/banquet-hall/*.json, extracted from
/// the actual game assets) against BanquetHallGraphFixture (hand-transcribed from reading the same
/// assets manually). If these ever diverge, either the extractor or the hand fixture has a bug -
/// this is a strong consistency signal independent of whether either matches the real game.
/// </summary>
public class GameDataRepositoryTests
{
    private static GameDataRepository CreateRepository() =>
        new(Path.Combine(AppContext.BaseDirectory, "data", "settings"));

    [Fact]
    public void ListsBanquetHallSetting()
    {
        var repo = CreateRepository();
        var settings = repo.ListSettings();

        Assert.Contains(settings, s => s.Slug == "banquet-hall");
    }

    [Fact]
    public void ExtractedGraphProducesSameLayoutAsHandFixture()
    {
        var repo = CreateRepository();
        var seed = new Seed("bdghjmqr");
        var (extractedProfile, extractedGraph) = repo.ResolveLayout("banquet-hall", seed);

        var handGraph = BanquetHallGraphFixture.Build();

        var constructionSeed = LayoutSeed.ConstructionSeed(seed);

        var fromExtractor = CreateLayoutHelper.ConstructLayout(extractedGraph, extractedProfile, constructionSeed);
        var fromHandFixture = CreateLayoutHelper.ConstructLayout(handGraph, extractedProfile, constructionSeed);

        Assert.Equal(
            fromHandFixture.Blueprint.Tiles.OrderBy(t => (t.Key.X, t.Key.Y)).Select(t => (t.Key, t.Value.Type)),
            fromExtractor.Blueprint.Tiles.OrderBy(t => (t.Key.X, t.Key.Y)).Select(t => (t.Key, t.Value.Type)));
        Assert.Equal(fromHandFixture.Decorations, fromExtractor.Decorations);
    }

    [Fact]
    public void ListsAllFifteenSettings()
    {
        var repo = CreateRepository();
        Assert.Equal(15, repo.ListSettings().Count);
    }

    [Theory]
    [InlineData("bakery")]
    [InlineData("february")]
    [InlineData("january")]
    [InlineData("coffeeshop")]
    [InlineData("turbo")]
    [InlineData("santa-workshop")]
    [InlineData("christmas-banquet-hall")]
    [InlineData("city")]
    [InlineData("alpine")]
    [InlineData("country")]
    [InlineData("autumn")]
    [InlineData("halloween")]
    [InlineData("halloween-with-cards")]
    [InlineData("witch-hut")]
    public void EveryExtractedSettingProducesAPlausibleLayoutForSeveralSeeds(string slug)
    {
        var repo = CreateRepository();
        // Includes a mixed-case alphanumeric seed (not just the lowercase "friendly" alphabet) -
        // the game's actual Seed constructor has no character restriction, see Seed.cs.
        foreach (var seedString in new[] { "bdghjmqr", "tvwxy346", "b", "789", "AbC123xZ" })
        {
            var seed = new Seed(seedString);
            var (profile, graph) = repo.ResolveLayout(slug, seed);
            var constructionSeed = LayoutSeed.ConstructionSeed(seed);

            var result = CreateLayoutHelper.ConstructLayout(graph, profile, constructionSeed);

            Assert.NotEmpty(result.Blueprint.Tiles);
        }
    }
}
