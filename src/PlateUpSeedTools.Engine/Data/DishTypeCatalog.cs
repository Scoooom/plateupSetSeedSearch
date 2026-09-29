namespace PlateUpSeedTools.Engine.Data;

/// <summary>
/// Display names for <see cref="UnlockData.DishType"/>, used by class-target search (see
/// Cards.TargetMatcher.AnyOfClass). No such mapping existed anywhere in the codebase before class
/// targets - only the raw int plus a doc comment on UnlockData.DishType.
/// </summary>
public static class DishTypeCatalog
{
    /// <summary>Not a real user-facing category ("not classified") - never offered/parsed as a class target. See UnlockData.DishType's doc comment.</summary>
    public const int NullValue = 6;

    public static readonly IReadOnlyDictionary<int, string> Names = new Dictionary<int, string>
    {
        [0] = "Base",
        [1] = "Main",
        [2] = "Extra",
        [3] = "Side",
        [4] = "Starter",
        [5] = "Dessert",
    };

    public static bool TryParseName(string name, out int dishType)
    {
        foreach (var (id, label) in Names)
        {
            if (string.Equals(label, name, StringComparison.OrdinalIgnoreCase))
            {
                dishType = id;
                return true;
            }
        }

        dishType = default;
        return false;
    }
}
