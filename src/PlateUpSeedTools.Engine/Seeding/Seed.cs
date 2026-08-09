namespace PlateUpSeedTools.Engine.Seeding;

/// <summary>
/// Port of Kitchen.Seed (DecompiledSource/GameData/Kitchen/Seed.cs). Truncates to 8 characters like
/// the original - it does not validate against FriendlyCharacters itself, callers (e.g. the web API)
/// are expected to do that, matching the game's own behavior.
/// </summary>
public readonly struct Seed
{
    public const int MaxLength = 8;

    public const string FriendlyCharacters = "bdghjmqrtvwxy346789";

    public string Value { get; }

    public Seed(string s)
    {
        Value = s.Length > MaxLength ? s[..MaxLength] : s;
    }

    public int IntValue => FixedString32Hash.ComputeHashCode(Value);
}
