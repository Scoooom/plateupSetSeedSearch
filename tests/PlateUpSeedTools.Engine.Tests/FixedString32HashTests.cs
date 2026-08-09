using PlateUpSeedTools.Engine.Seeding;
using Xunit;

namespace PlateUpSeedTools.Engine.Tests;

/// <summary>
/// Pins FixedString32Hash against real Seed(string).IntValue values captured from the actual game
/// via a one-off BepInEx logger plugin (see PlateUpSourceFiles/GroundTruthLogger/), not just from
/// reading decompiled source. All 6 matched exactly on first capture (2026-08-09).
/// </summary>
public class FixedString32HashTests
{
    [Theory]
    [InlineData("bdghjmqr", -1317788620)]
    [InlineData("tvwxy346", 1949502740)]
    [InlineData("b", 177671)]
    [InlineData("789", 193440973)]
    [InlineData("bbbbbbbb", 1349542933)]
    [InlineData("dgtqmrvy", -1274453533)]
    public void MatchesRealGameSeedHash(string value, int expected)
    {
        Assert.Equal(expected, FixedString32Hash.ComputeHashCode(value));
    }
}
