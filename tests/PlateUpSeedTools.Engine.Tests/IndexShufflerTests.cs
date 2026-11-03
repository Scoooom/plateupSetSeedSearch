using PlateUpSeedTools.Engine.Seeding;
using Xunit;

namespace PlateUpSeedTools.Engine.Tests;

/// <summary>
/// IndexShuffler is what fixes the real reported bug: SeedSearcher's brute-force loop used to walk
/// candidates in plain lexicographic index order, which - because the leftmost character of a seed
/// string is the slowest-changing "digit" - meant any search with a limit smaller than
/// alphabet.Length^(length-1) could NEVER produce a seed with a different leading character, no
/// matter how long it ran. These tests pin the two properties that actually matter for that fix:
/// every index still gets checked exactly once (no gaps/duplicates - the search stays exhaustive),
/// and a small early prefix of the shuffled walk already reaches across the whole space rather than
/// clustering near index 0.
/// </summary>
public class IndexShufflerTests
{
    [Theory]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(3)]
    [InlineData(7)]
    [InlineData(1000)]
    [InlineData(100003)] // prime, not a power of two - exercises cycle-walking on an odd-shaped space
    public void IsABijectionOverTheFullSpace(long spaceSize)
    {
        var seen = new HashSet<long>();
        for (var i = 0L; i < spaceSize; i++)
        {
            var permuted = IndexShuffler.Permute(i, spaceSize);
            Assert.InRange(permuted, 0, spaceSize - 1);
            Assert.True(seen.Add(permuted), $"index {i} produced a duplicate output {permuted}");
        }

        Assert.Equal(spaceSize, seen.Count);
    }

    // Reproduces the reported bug at a tractable scale: a lopsided space where plain index order
    // would stay confined to one half for a long time (analogous to a real alphabet.Length^8 space
    // staying on one leading character for its first alphabet.Length^7 indices). A shuffled walk of
    // a tiny prefix of this space should already reach both halves.
    [Fact]
    public void EarlyIndicesSpreadAcrossBothHalvesOfALopsidedSpace()
    {
        const long spaceSize = 1L << 20;
        const long prefixToCheck = 1000;
        var sawLowerHalf = false;
        var sawUpperHalf = false;

        for (var i = 0L; i < prefixToCheck; i++)
        {
            var permuted = IndexShuffler.Permute(i, spaceSize);
            if (permuted < spaceSize / 2)
            {
                sawLowerHalf = true;
            }
            else
            {
                sawUpperHalf = true;
            }
        }

        Assert.True(sawLowerHalf);
        Assert.True(sawUpperHalf);
    }

    // Deliberately deterministic (fixed key, no per-run randomization) - a given alphabet/length
    // search always visits candidates in the same order, so a prior search's progress/results are
    // reproducible.
    [Fact]
    public void IsDeterministicAcrossCalls()
    {
        Assert.Equal(IndexShuffler.Permute(12345, 1_000_000), IndexShuffler.Permute(12345, 1_000_000));
    }

    [Fact]
    public void TrivialSpacesReturnTheIndexUnchanged()
    {
        Assert.Equal(0, IndexShuffler.Permute(0, spaceSize: 1));
    }
}
