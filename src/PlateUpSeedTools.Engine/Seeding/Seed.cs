namespace PlateUpSeedTools.Engine.Seeding;

/// <summary>
/// Port of Kitchen.Seed (DecompiledSource/GameData/Kitchen/Seed.cs). Truncates to 8 characters like
/// the original. The original constructor accepts any string content with no character validation
/// at all - FriendlyCharacters below is only the alphabet Seed.Generate() uses to auto-generate a
/// seed for display, NOT a restriction on what a typed/shared seed can contain (a seed can be any
/// alphanumeric string up to 8 characters - see LayoutEndpoints for where that's actually enforced).
/// </summary>
public readonly struct Seed
{
    public const int MaxLength = 8;

    /// <summary>The alphabet Seed.Generate() draws from - not an input restriction, see class doc comment.</summary>
    public const string FriendlyCharacters = "bdghjmqrtvwxy346789";

    public string Value { get; }

    public Seed(string s)
    {
        Value = s.Length > MaxLength ? s[..MaxLength] : s;
    }

    public int IntValue => FixedString32Hash.ComputeHashCode(Value);
}
