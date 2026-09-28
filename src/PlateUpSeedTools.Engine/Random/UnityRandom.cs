namespace PlateUpSeedTools.Engine.Random;

/// <summary>
/// Standalone port of UnityEngine.Random (Xorshift128, seeded via an MT19937-style state mix).
/// Deliberately instance-based rather than a static/global RNG like the original: the game only
/// ever has one Unity process running the simulation on a single thread, but our web server can
/// handle multiple concurrent layout requests, and a shared static RNG would let them corrupt each
/// other's sequences. Each seeded computation should construct its own instance.
///
/// InitState's seed-mix formula and the Xorshift128 step are verified against two independent
/// sources: a community C# port (gist.github.com/macklinb/a00be6b616cbf20fa95e4227575fe50b) whose
/// integer Range() outputs matched real UnityEngine.Random.Range() exactly across many test seeds,
/// and a separate community-reported reference value (UnityEngine.Random.InitState(0) produces
/// state (0, 1, 1812433254, 1900727103)), which this implementation reproduces exactly.
///
/// Value is now ALSO bit-exact (fixed 2026-09-28), found from a real GroundTruthLogger v1.23.0
/// capture: 206 consecutive real `UnityEngine.Random.value` outputs (raw IEEE-754 bit patterns) from
/// a known seed, diffed against several candidate formulas. The low 23 bits of the raw xorshift
/// output, divided by 2^23-1 (NOT 2^23 - Unity's `value` is apparently [0,1] inclusive of 1, not the
/// [0,1) its own docs claim) matched all 206/206 bit-for-bit. The previous formula here (shifting
/// left by 9 and dividing by uint.MaxValue, all in single/float precision) only matched to ~6-7
/// significant digits - close enough for layout rendering (tolerant of sub-pixel drift) but NOT for
/// `OrderBy(_ => rng.Value)` shuffles (the card-draw pack), where two candidates' keys landing 1-2
/// ULPs apart can flip their relative order and change which 2 cards get offered - this exact gap
/// was the root cause of a real, previously-unexplained card-draw mismatch (see
/// Engine.Cards.CardDrawSimulatorGroundTruthTests' trailing comment for the full capture that found
/// it). A first, unverified guess at a bit-exact formula (the common "top-23-bits-as-mantissa via
/// right-shift" trick) was tried and measurably WRONG (didn't match at all, not even close) - the
/// real bits come from the LOW 23 bits, not the high ones.
/// </summary>
public sealed class UnityRandom
{
    private const uint Mt19937Multiplier = 1812433253;

    private uint _x, _y, _z, _w;

    public UnityRandom(int seed) => InitState(seed);

    public void InitState(int seed)
    {
        _x = unchecked((uint)seed);
        _y = unchecked(Mt19937Multiplier * _x + 1);
        _z = unchecked(Mt19937Multiplier * _y + 1);
        _w = unchecked(Mt19937Multiplier * _z + 1);
    }

    public UnityRandomState GetState() => new(_x, _y, _z, _w);

    public void SetState(UnityRandomState state) => (_x, _y, _z, _w) = (state.X, state.Y, state.Z, state.W);

    private uint NextUInt32()
    {
        uint t = _x ^ (_x << 11);
        _x = _y;
        _y = _z;
        _z = _w;
        return _w = _w ^ (_w >> 19) ^ t ^ (t >> 8);
    }

    /// <summary>Random integer in [minInclusive, maxExclusive), matching UnityEngine.Random.Range(int,int).</summary>
    public int Range(int minInclusive, int maxExclusive)
    {
        if (maxExclusive - minInclusive == 0)
        {
            return minInclusive;
        }

        long min = minInclusive;
        long max = maxExclusive;
        long r = NextUInt32();

        return maxExclusive < minInclusive
            ? (int)(min - r % (max - min))
            : (int)(min + r % (max - min));
    }

    /// <summary>
    /// Random float in [minInclusive, maxInclusive], matching UnityEngine.Random.Range(float,float).
    /// Built from the now-bit-exact Value, preserving the exact (min-max)*Value+max relationship the
    /// pre-fix code's Range/Value pair implied (Value used to be defined as `1 - Range(0,1)`, which
    /// algebraically simplifies to Range(min,max) = (min-max)*rawFraction+max where rawFraction IS
    /// what Value now returns directly) - confirmed against UnityRandomTests' documented Range(0,100)
    /// reference values, which still pass with this relationship kept, unlike the naive
    /// Value*(max-min)+min ordering (tried first, inverted, failed those same assertions).
    /// </summary>
    public float Range(float minInclusive, float maxInclusive) => (minInclusive - maxInclusive) * Value + maxInclusive;

    /// <summary>Random float in [0, 1], matching UnityEngine.Random.value - see this class's doc comment.</summary>
    public float Value => (float)((double)(NextUInt32() & 0x7FFFFFu) / 8388607.0);
}
