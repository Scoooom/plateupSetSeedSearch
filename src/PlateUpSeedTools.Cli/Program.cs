using PlateUpSeedTools.Cli;
using PlateUpSeedTools.Infrastructure.Cache;

if (args is ["search", .. var searchArgs])
{
    var dataRoot = Environment.GetEnvironmentVariable("DataRoot") ?? Path.Combine(AppContext.BaseDirectory, "data", "settings");
    return SearchCommand.Run(searchArgs, dataRoot);
}

var sqlitePath = Environment.GetEnvironmentVariable("CacheStore__SqlitePath") ?? "cache.db";
var admin = new CacheAdmin(new CacheOptions { SqlitePath = sqlitePath });

if (args is not ["cache", var verb])
{
    Console.WriteLine("Usage: cache <clear|stats>");
    Console.WriteLine("       search --setting <slug> --dish <name> [--sub-option 1|2] [--heat <n>] [--tier <n>]");
    Console.WriteLine("              [--franchise-card <name> ...] --day <n>=<card name> [...]");
    return 1;
}

switch (verb)
{
    case "clear":
        var removed = await admin.ClearAllAsync();
        Console.WriteLine($"Cleared {removed} cache entr{(removed == 1 ? "y" : "ies")}.");
        return 0;

    case "stats":
        var stats = await admin.GetStatsAsync();
        if (stats.Count == 0)
        {
            Console.WriteLine("Cache is empty (no cache tables yet).");
            return 0;
        }

        foreach (var (table, count) in stats)
        {
            Console.WriteLine($"{table}: {count} entr{(count == 1 ? "y" : "ies")}");
        }

        return 0;

    default:
        Console.WriteLine($"Unknown verb '{verb}'. Usage: cache <clear|stats>");
        return 1;
}
