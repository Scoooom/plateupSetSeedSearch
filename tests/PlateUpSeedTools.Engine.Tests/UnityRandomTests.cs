using PlateUpSeedTools.Engine.Random;
using Xunit;

namespace PlateUpSeedTools.Engine.Tests;

/// <summary>
/// Pins UnityRandom's output against a documented, independently-verified sequence of real
/// UnityEngine.Random outputs (gist.github.com/macklinb/a00be6b616cbf20fa95e4227575fe50b),
/// generated sequentially from a single UnityEngine.Random.InitState(1234) with no state reset
/// between blocks. If this test ever fails after touching UnityRandom, the RNG port is wrong -
/// every downstream computation (layouts, and eventually card search) depends on it.
/// </summary>
public class UnityRandomTests
{
    [Fact]
    public void MatchesDocumentedUnityRandomSequence()
    {
        var rng = new UnityRandom(1234);

        // 0 to int.MaxValue
        Assert.Equal(1315917191, rng.Range(0, int.MaxValue));
        Assert.Equal(1348720129, rng.Range(0, int.MaxValue));
        Assert.Equal(1305464022, rng.Range(0, int.MaxValue));
        Assert.Equal(1278673611, rng.Range(0, int.MaxValue));
        Assert.Equal(2021684663, rng.Range(0, int.MaxValue));

        // 0 to int.MinValue
        Assert.Equal(-916287344, rng.Range(0, int.MinValue));
        Assert.Equal(-92775442, rng.Range(0, int.MinValue));
        Assert.Equal(-1901252403, rng.Range(0, int.MinValue));
        Assert.Equal(-176433514, rng.Range(0, int.MinValue));
        Assert.Equal(-1472147877, rng.Range(0, int.MinValue));

        // int.MinValue to int.MaxValue
        Assert.Equal(1872799860, rng.Range(int.MinValue, int.MaxValue));
        Assert.Equal(-2006136348, rng.Range(int.MinValue, int.MaxValue));
        Assert.Equal(587759354, rng.Range(int.MinValue, int.MaxValue));
        Assert.Equal(-1919663833, rng.Range(int.MinValue, int.MaxValue));
        Assert.Equal(1738386409, rng.Range(int.MinValue, int.MaxValue));

        // int.MaxValue to int.MinValue
        Assert.Equal(-164658456, rng.Range(int.MaxValue, int.MinValue));
        Assert.Equal(372294278, rng.Range(int.MaxValue, int.MinValue));
        Assert.Equal(-1191040031, rng.Range(int.MaxValue, int.MinValue));
        Assert.Equal(-1278602700, rng.Range(int.MaxValue, int.MinValue));
        Assert.Equal(-1174866336, rng.Range(int.MaxValue, int.MinValue));

        // int.MinValue to int.MinValue: min == max short-circuits, no state change/RNG consumption.
        for (var i = 0; i < 5; i++)
        {
            Assert.Equal(int.MinValue, rng.Range(int.MinValue, int.MinValue));
        }

        // Random.value, tolerance since float precision isn't proven bit-exact (see UnityRandom doc comment).
        AssertFloat(0.4043221f, rng.Value);
        AssertFloat(0.551855f, rng.Value);
        AssertFloat(0.9868958f, rng.Value);
        AssertFloat(0.593608f, rng.Value);
        AssertFloat(0.426595f, rng.Value);

        // 0 to 100
        AssertFloat(73.35463f, rng.Range(0f, 100f));
        AssertFloat(69.16753f, rng.Range(0f, 100f));
        AssertFloat(91.95337f, rng.Range(0f, 100f));
        AssertFloat(7.068896f, rng.Range(0f, 100f), tolerance: 0.001f);
        AssertFloat(2.080286f, rng.Range(0f, 100f), tolerance: 0.001f);
    }

    private static void AssertFloat(float expected, float actual, float tolerance = 0.0001f)
    {
        Assert.True(
            Math.Abs(expected - actual) <= tolerance,
            $"Expected {expected} but got {actual} (tolerance {tolerance})");
    }
}
