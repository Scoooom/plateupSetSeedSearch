using PlateUpSeedTools.Engine.Seeding;
using Xunit;

namespace PlateUpSeedTools.Engine.Tests;

/// <summary>
/// Exercises SeedHashInverter.FindPreimage - the meet-in-the-middle djb2 preimage finder backing
/// SeedSearcher's IntValue-space search mode. The load-bearing property is the round-trip (the
/// returned string, if any, must hash back to the exact target - collisions mean it needn't be the
/// original string used to pick the target), not literal string equality.
/// </summary>
public class SeedHashInverterTests
{
    [Theory]
    // Real Seed(string).IntValue values ground-truth-verified against the actual game, reused from
    // FixedString32HashTests - a preimage of the same length must exist (the original string
    // itself is one), and any preimage FindPreimage returns must hash back to the same value.
    [InlineData("bdghjmqr", -1317788620)]
    [InlineData("tvwxy346", 1949502740)]
    [InlineData("b", 177671)]
    [InlineData("789", 193440973)]
    [InlineData("bbbbbbbb", 1349542933)]
    [InlineData("dgtqmrvy", -1274453533)]
    public void FindsAPreimage_ThatHashesBackToTheTarget(string original, int expectedHash)
    {
        Assert.Equal(expectedHash, FixedString32Hash.ComputeHashCode(original)); // sanity-check the fixture itself

        var found = SeedHashInverter.FindPreimage(expectedHash, original.Length, Seed.FriendlyCharacters);

        Assert.NotNull(found);
        Assert.Equal(original.Length, found!.Length);
        Assert.Equal(expectedHash, FixedString32Hash.ComputeHashCode(found));
    }

    [Fact]
    public void FindsAPreimage_ForARandomStringInTheAlphabet()
    {
        var random = new System.Random(Seed: 12345);
        for (var trial = 0; trial < 20; trial++)
        {
            var length = random.Next(1, 9);
            var chars = new char[length];
            for (var i = 0; i < length; i++)
            {
                chars[i] = Seed.FriendlyCharacters[random.Next(Seed.FriendlyCharacters.Length)];
            }

            var original = new string(chars);
            var target = FixedString32Hash.ComputeHashCode(original);

            var found = SeedHashInverter.FindPreimage(target, length, Seed.FriendlyCharacters);

            Assert.NotNull(found);
            Assert.Equal(target, FixedString32Hash.ComputeHashCode(found));
        }
    }

    [Fact]
    public void ReturnsNull_WhenNoPreimageExistsForTheGivenLengthAndAlphabet()
    {
        // A small enough space (19^2 = 361 strings) to exhaustively compute every achievable hash
        // and find one genuinely absent from that set.
        const string alphabet = Seed.FriendlyCharacters;
        const int length = 2;

        var achievable = new HashSet<int>();
        foreach (var a in alphabet)
        {
            foreach (var b in alphabet)
            {
                achievable.Add(FixedString32Hash.ComputeHashCode($"{a}{b}"));
            }
        }

        var missing = FindValueNotIn(achievable);

        var found = SeedHashInverter.FindPreimage(missing, length, alphabet);

        Assert.Null(found);
    }

    private static int FindValueNotIn(HashSet<int> achievable)
    {
        // Linear probe from 0 upward (unchecked, wraps harmlessly if it ever needed to) - the
        // achievable set is only 361 elements out of 2^32, so this terminates immediately.
        var candidate = 0;
        while (achievable.Contains(candidate))
        {
            candidate = unchecked(candidate + 1);
        }

        return candidate;
    }

    [Fact]
    public void ThrowsForNonPositiveLength()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => SeedHashInverter.FindPreimage(0, 0));
        Assert.Throws<ArgumentOutOfRangeException>(() => SeedHashInverter.FindPreimage(0, -1));
    }
}
