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

    // The 5 real IDs from AssetReference.FixedRunLayout, with their real in-game names - see
    // game-domain-knowledge.md's "RestaurantSetting / LayoutProfile data model" section.
    [Theory]
    [InlineData(222370461, LayoutSeed.LayoutSize.Basic)]
    [InlineData(-80202533, LayoutSeed.LayoutSize.Diner)]
    [InlineData(557943155, LayoutSeed.LayoutSize.Extended)]
    [InlineData(154938708, LayoutSeed.LayoutSize.Huge)]
    [InlineData(-2045800810, LayoutSeed.LayoutSize.Medium)]
    public void TryGetSize_ResolvesEachKnownPoolId(int layoutProfileId, LayoutSeed.LayoutSize expected)
    {
        Assert.True(LayoutSeed.TryGetSize(layoutProfileId, out var size));
        Assert.Equal(expected, size);
    }

    // A ForceLayoutId value (e.g. banquet-hall's -921257357, see project-history.md) is never one of
    // the 5 pool IDs above - size isn't a meaningful concept for a setting with its own fixed layout.
    [Fact]
    public void TryGetSize_ReturnsFalseForAnIdOutsideThePool()
    {
        Assert.False(LayoutSeed.TryGetSize(-921257357, out _));
    }
}
