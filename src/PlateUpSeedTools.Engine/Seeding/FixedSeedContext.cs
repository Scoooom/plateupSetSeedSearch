using PlateUpSeedTools.Engine.Random;

namespace PlateUpSeedTools.Engine.Seeding;

/// <summary>
/// Port of Kitchen.FixedSeedContext (DecompiledSource/GameData/Kitchen/FixedSeedContext.cs).
///
/// The original is an IDisposable that saves/overrides/restores UnityEngine's single *global*
/// Random state, because the same global RNG is shared with non-deterministic gameplay elsewhere
/// in the running game and mustn't be left in a deterministic state afterwards. We have no such
/// ambient randomness - every computation in this engine is a deterministic function of a seed -
/// so there's nothing to save or restore. This port keeps only the part that affects output: the
/// seed-derivation arithmetic (FixedSeed + 124192293 * InstanceSeed, and subcontext composition),
/// and hands back a fresh UnityRandom instance instead of mutating global state.
/// </summary>
public readonly struct FixedSeedContext
{
    private const int InstanceMultiplier = 124192293;

    public int FixedSeed { get; }

    public int InstanceSeed { get; }

    public FixedSeedContext(Seed seed, int instanceSeed)
        : this(seed.IntValue, instanceSeed)
    {
    }

    public FixedSeedContext(int fixedSeed, int instanceSeed)
    {
        FixedSeed = fixedSeed;
        InstanceSeed = instanceSeed;
    }

    public FixedSeedContext UseSubcontext(int contextId) =>
        new(FixedSeed, unchecked(InstanceMultiplier * contextId + InstanceSeed));

    public UnityRandom CreateRandom() => new(unchecked(FixedSeed + InstanceMultiplier * InstanceSeed));
}
