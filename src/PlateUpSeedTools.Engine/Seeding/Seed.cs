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

    // Set only by FromIntValue - a search-hot-loop fast path that carries a bare IntValue with no
    // real backing string (Value is left empty). Never null for a Seed built from a string.
    private readonly int? _rawIntValue;

    public string Value { get; }

    public Seed(string s)
    {
        Value = s.Length > MaxLength ? s[..MaxLength] : s;
    }

    private Seed(int intValue)
    {
        Value = string.Empty;
        _rawIntValue = intValue;
    }

    /// <summary>
    /// Builds a Seed carrying only a 32-bit hash, bypassing string hashing entirely - for
    /// SeedSearcher's IntValue-space search mode only. The result has no real backing string
    /// (Value is empty); do not surface it anywhere a user-facing seed string is expected. Use
    /// SeedHashInverter to recover a real, typeable seed string from IntValue once a match is
    /// found.
    /// </summary>
    internal static Seed FromIntValue(int intValue) => new(intValue);

    public int IntValue => _rawIntValue ?? FixedString32Hash.ComputeHashCode(Value);
}
