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
/// Float outputs (Value/Range(float,float)) match to ~6-7 significant digits but are not proven
/// bit-exact — see UnityRandomTests for the known deviation.
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

    /// <summary>Random float in [minInclusive, maxInclusive], matching UnityEngine.Random.Range(float,float).</summary>
    public float Range(float minInclusive, float maxInclusive)
    {
        return (minInclusive - maxInclusive) * ((float)(NextUInt32() << 9) / uint.MaxValue) + maxInclusive;
    }

    /// <summary>Random float in [0, 1), matching UnityEngine.Random.value.</summary>
    public float Value => 1.0f - Range(0.0f, 1.0f);
}
