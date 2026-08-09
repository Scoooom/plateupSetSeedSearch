using PlateUpSeedTools.Engine.Layouts;
using PlateUpSeedTools.Engine.Seeding;
using Xunit;

namespace PlateUpSeedTools.Engine.Tests;

public class SeedTests
{
    [Fact]
    public void TruncatesToEightCharacters()
    {
        var seed = new Seed("bdghjmqrtvwxy");
        Assert.Equal("bdghjmqr", seed.Value);
    }

    [Fact]
    public void IntValueIsDeterministic()
    {
        Assert.Equal(new Seed("bdghjmqr").IntValue, new Seed("bdghjmqr").IntValue);
    }

    [Fact]
    public void DifferentSeedsProduceDifferentIntValues()
    {
        Assert.NotEqual(new Seed("bdghjmqr").IntValue, new Seed("tvwxy346").IntValue);
    }
}

public class LayoutSeedTests
{
    [Fact]
    public void LayoutIdFromSeedIsDeterministic()
    {
        var seed = new Seed("bdghjmqr");
        Assert.Equal(LayoutSeed.LayoutIdFromSeed(seed), LayoutSeed.LayoutIdFromSeed(seed));
    }

    [Fact]
    public void ForceLayoutAlwaysOverridesSeedPick()
    {
        var seed = new Seed("bdghjmqr");
        Assert.Equal(999, LayoutSeed.ResolveLayoutId(seed, forceLayoutId: 999));
    }

    [Fact]
    public void FallsBackToSeedPickWhenNoForceLayout()
    {
        var seed = new Seed("bdghjmqr");
        Assert.Equal(LayoutSeed.LayoutIdFromSeed(seed), LayoutSeed.ResolveLayoutId(seed, forceLayoutId: null));
    }

    [Fact]
    public void ConstructionSeedIsDeterministic()
    {
        var seed = new Seed("bdghjmqr");
        Assert.Equal(LayoutSeed.ConstructionSeed(seed), LayoutSeed.ConstructionSeed(seed));
    }

    [Fact]
    public void DifferentSeedsProduceDifferentConstructionSeeds()
    {
        Assert.NotEqual(
            LayoutSeed.ConstructionSeed(new Seed("bdghjmqr")),
            LayoutSeed.ConstructionSeed(new Seed("tvwxy346")));
    }
}
