namespace PlateUpSeedTools.Engine.Data;

/// <summary>
/// Result of a fuzzy dish/card name lookup - exactly one of <see cref="Unlock"/> (unique match) or
/// <see cref="AmbiguousMatches"/> (2+ exact-name matches) or <see cref="Suggestions"/> (no exact
/// match; near-miss names, possibly empty) is populated.
/// </summary>
public readonly record struct UnlockNameLookup(UnlockData? Unlock, IReadOnlyList<UnlockData> AmbiguousMatches, IReadOnlyList<string> Suggestions);

/// <summary>
/// Console-free dish/card name resolution shared by the CLI `search` verb and the web search API -
/// both need identical unknown/ambiguous-name handling, just formatted differently (CLI prints to
/// stdout, the web endpoint turns it into a JSON error).
/// </summary>
public static class UnlockNameResolver
{
    public static UnlockNameLookup TryResolveByName(GameDataRepository repo, string name)
    {
        var all = repo.AllUnlocksOrderedById;
        var matches = all.Where(u => string.Equals(u.Name, name, StringComparison.OrdinalIgnoreCase)).ToList();
        if (matches.Count == 1)
        {
            return new UnlockNameLookup(matches[0], [], []);
        }

        if (matches.Count > 1)
        {
            return new UnlockNameLookup(null, matches, []);
        }

        var close = all.Where(u => u.Name.Contains(name, StringComparison.OrdinalIgnoreCase)).Take(5).Select(u => u.Name).ToList();
        return new UnlockNameLookup(null, [], close);
    }
}
