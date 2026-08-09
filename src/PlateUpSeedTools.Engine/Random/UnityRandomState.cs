namespace PlateUpSeedTools.Engine.Random;

/// <summary>Snapshot of a <see cref="UnityRandom"/> instance's internal Xorshift128 state.</summary>
public readonly record struct UnityRandomState(uint X, uint Y, uint Z, uint W);
