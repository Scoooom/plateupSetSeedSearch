using System.Numerics;

namespace PlateUpSeedTools.Engine.Seeding;

/// <summary>
/// Bijective pseudo-shuffle of [0, spaceSize) - a balanced Feistel network with cycle-walking, used
/// so SeedSearcher's brute-force Parallel.For(0, N, ...) loops sample spread across the ENTIRE
/// candidate space from their very first iterations, instead of raw index order. Raw order matters
/// because SeedSearcher.IndexToSeedString treats the loop index like counting in base
/// alphabet.Length, with the seed string's LEFTMOST character as the slowest-changing digit - it
/// only advances past alphabet[0] once the index exceeds alphabet.Length^(length-1) (e.g.
/// ~1.28 billion for a 20-letter/8-character alphabet). A user-configured search "limit" well below
/// that threshold can therefore never produce a seed with a different leading character at all, no
/// matter how long it runs - this isn't fixable by raising the limit (that only moves where the next
/// wall is), it requires decorrelating "how many candidates have been checked" from "which part of
/// the space they come from". Every index in [0, spaceSize) still maps to exactly one output and
/// vice versa (full coverage, no duplicates, no extra memory) - just reordered. The permutation
/// itself is a fixed deterministic function (same key every call) so a given alphabet/length space
/// visits candidates in the same order every run - useful for reproducing a specific prior search.
/// </summary>
internal static class IndexShuffler
{
    // Arbitrary fixed constant (fractional part of the golden ratio, a common PRNG seed choice) -
    // doesn't need to be secret, just needs to mix bits well.
    private const ulong Key = 0x9E3779B97F4A7C15UL;

    private const int Rounds = 4;

    /// <summary>
    /// Maps <paramref name="index"/> (0 &lt;= index &lt; spaceSize) to a pseudo-shuffled index in the
    /// same range, bijectively.
    /// </summary>
    public static long Permute(long index, long spaceSize)
    {
        if (spaceSize <= 1)
        {
            return index;
        }

        var bits = 64 - BitOperations.LeadingZeroCount((ulong)(spaceSize - 1));
        if (bits < 2)
        {
            bits = 2; // Feistel needs two non-empty halves
        }
        else if (bits % 2 != 0)
        {
            bits++; // keep both halves equal width
        }

        var halfBits = bits / 2;
        var halfMask = (1L << halfBits) - 1;

        // Cycle-walking: repeatedly apply the (2^bits)-space permutation to its own output until it
        // lands back inside [0, spaceSize) - always terminates because the permutation restricted to
        // the orbit containing `index` must eventually revisit every value in that orbit, including
        // one below spaceSize (spaceSize is always more than a quarter of 2^bits here, so this
        // converges in a handful of iterations in practice).
        var value = index;
        do
        {
            value = FeistelRound(value, halfBits, halfMask);
        }
        while (value >= spaceSize);

        return value;
    }

    private static long FeistelRound(long value, int halfBits, long halfMask)
    {
        var left = (value >> halfBits) & halfMask;
        var right = value & halfMask;
        for (var round = 0; round < Rounds; round++)
        {
            var f = (long)(Mix((ulong)right ^ ((ulong)round << 48) ^ Key) & (ulong)halfMask);
            var newRight = left ^ f;
            left = right;
            right = newRight;
        }

        return (left << halfBits) | right;
    }

    // splitmix64 finalizer - cheap, well-distributed, no cryptographic requirement here, just needs
    // to decorrelate input from output well enough to break the odometer pattern above.
    private static ulong Mix(ulong x)
    {
        x ^= x >> 30;
        x *= 0xbf58476d1ce4e5b9UL;
        x ^= x >> 27;
        x *= 0x94d049bb133111ebUL;
        x ^= x >> 31;
        return x;
    }
}
