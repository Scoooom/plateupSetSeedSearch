using System.Numerics;
using PlateUpSeedTools.Engine.Cards;
using PlateUpSeedTools.Engine.Data;
using PlateUpSeedTools.Engine.Seeding;

namespace PlateUpSeedTools.Cli;

/// <summary>
/// `search` CLI verb - brute-forces seeds (from the game's own generation alphabet, see
/// Seed.FriendlyCharacters/Seed.Generate) for ones where a chosen starting dish + heat level produces
/// specific cards on specific days. The actual seed-space loop lives in
/// PlateUpSeedTools.Engine.Cards.SeedSearcher, shared with the web search job manager - this file is
/// just CLI arg parsing/output around it. The underlying card-draw RNG port is ground-truth-verified
/// for the base schedule (Heat 0) and for Heat 2 + a franchise-tier-1 continuation (see
/// game-domain-knowledge.md's "Ground-truth VERIFIED" section and CardDrawSimulatorGroundTruthTests)
/// - other heat levels, non-Normal settings' packs, and the same-night multi-pack collision case
/// (point 5 below) remain best-effort/untested.
///
/// Matching policy (a real modeling decision, not just an implementation detail - see
/// game-domain-knowledge.md's CardDrawSimulator notes): on a target day, the simulated player accepts
/// the target card if it's offered in EITHER slot (the real game always offers a two-card choice, see
/// "genuinely a choice between TWO cards"); on every other day, the simulated player always accepts
/// whichever card is in the first slot (Unlock1) if anything is offered. This is a deliberate,
/// documented default for a single deterministic simulation path, not a game rule - a different
/// non-target-day policy could theoretically produce different downstream results since accepted
/// cards affect later nights' candidate filtering.
/// </summary>
public static class SearchCommand
{
    public static int Run(string[] args, string dataRoot)
    {
        var options = ParseOptions(args);
        if (options is null)
        {
            return 1;
        }

        var repo = new GameDataRepository(dataRoot);

        RestaurantSettingData setting;
        try
        {
            setting = repo.GetSetting(options.SettingSlug);
        }
        catch (KeyNotFoundException)
        {
            Console.WriteLine($"Unknown setting '{options.SettingSlug}'. Known: {string.Join(", ", repo.ListSettings().Select(s => s.Slug))}");
            return 1;
        }

        if (!TryResolveUnlockByName(repo, options.DishName, "dish", out var dish))
        {
            return 1;
        }

        if (dish.HasSubOptions && options.SubOptionChoice is null)
        {
            var opt1 = repo.GetUnlock(dish.OptionCard1Id!.Value).Name;
            var opt2 = repo.GetUnlock(dish.OptionCard2Id!.Value).Name;
            Console.WriteLine($"'{dish.Name}' has sub-options - pass --sub-option 1 or 2. 1) {opt1}   2) {opt2}");
            return 1;
        }

        var targetNames = new Dictionary<int, string>();
        var targetIds = new Dictionary<int, int>();
        foreach (var (day, cardName) in options.DayTargets)
        {
            if (!TryResolveUnlockByName(repo, cardName, "card", out var card))
            {
                return 1;
            }

            targetNames[day] = card.Name;
            targetIds[day] = card.Id;
        }

        var flexibleTargetNames = new List<string>();
        var flexibleTargetIds = new HashSet<int>();
        foreach (var cardName in options.AnyDayCardNames)
        {
            if (!TryResolveUnlockByName(repo, cardName, "card", out var card))
            {
                return 1;
            }

            flexibleTargetNames.Add(card.Name);
            flexibleTargetIds.Add(card.Id);
        }

        if (UnlockPackEvaluator.HasBlockedByConflict(repo, new HashSet<int>(targetIds.Values.Concat(flexibleTargetIds)), out var conflict))
        {
            Console.WriteLine($"'{repo.GetUnlock(conflict.CardId).Name}' and '{repo.GetUnlock(conflict.BlockedById).Name}' can never coexist (BlockedBy) - this target set can never be satisfied.");
            return 1;
        }

        var franchiseCarryoverIds = new List<int>();
        foreach (var cardName in options.FranchiseCardNames)
        {
            if (!TryResolveUnlockByName(repo, cardName, "franchise card", out var card))
            {
                return 1;
            }

            franchiseCarryoverIds.Add(card.Id);
        }

        // Same Requires/BlockedBy/cumulative-copy consistency check the web UI's franchise-candidate
        // dropdown and its own StartSearch backstop use - the CLI has no interactive dropdown to
        // filter this during typing, so it needs this explicit check here instead. See
        // UnlockPackEvaluator.IsValidCarryoverPick's doc comment for what "valid" means, including
        // the cumulative lower-copy prerequisite for repeatable cards like "Variety" (independent of
        // --tier - a repeatable card's numbered suffix is an occurrence count, not a franchise tier).
        var franchiseIdSet = new HashSet<int>(franchiseCarryoverIds);
        foreach (var id in franchiseCarryoverIds)
        {
            var otherIds = new HashSet<int>(franchiseIdSet);
            otherIds.Remove(id);
            var candidate = repo.GetUnlock(id);
            if (!UnlockPackEvaluator.IsValidCarryoverPick(repo, candidate, otherIds, options.Tier))
            {
                var missingLowerCopies = UnlockNameResolver.GetRepeatableCardSiblings(repo, candidate)
                    .Where(s => s.MinimumFranchiseTier < candidate.MinimumFranchiseTier && !otherIds.Contains(s.Id))
                    .OrderBy(s => s.MinimumFranchiseTier)
                    .Select(s => $"{s.Name} (copy {s.MinimumFranchiseTier + 1})")
                    .ToList();
                Console.WriteLine(missingLowerCopies.Count > 0
                    ? $"'{candidate.Name} (copy {candidate.MinimumFranchiseTier + 1})' also needs its earlier copies listed: {string.Join(", ", missingLowerCopies)}."
                    : $"'{candidate.Name}' isn't a valid franchise-carryover pick given the rest of the selection and tier {options.Tier}.");
                return 1;
            }
        }

        int settingPackId;
        try
        {
            settingPackId = repo.ResolveCardSchedulePackId(options.CardPack) ?? repo.ResolveUnlockPackId(options.SettingSlug);
        }
        catch (KeyNotFoundException ex)
        {
            Console.WriteLine(ex.Message);
            Console.WriteLine($"Known card schedules: {string.Join(", ", repo.ListCardSchedules().Select(s => s.Slug))}");
            return 1;
        }

        var setup = RunSetup.Resolve(repo, dish.Id, options.SubOptionChoice, options.HeatLevel, setting.StartingUnlockId, franchiseCarryoverIds);

        var spaceSize = BigInteger.Pow(options.Alphabet.Length, options.Length);
        var totalToCheck = options.Limit is long l ? Math.Min(l, (long)BigInteger.Min(spaceSize, long.MaxValue)) : (long)BigInteger.Min(spaceSize, long.MaxValue);

        if (!options.MatchesOnly)
        {
            Console.WriteLine($"Setting: {setting.DisplayName}  Dish: {dish.Name}  Heat: {options.HeatLevel}  Tier: {options.Tier}");
            if (franchiseCarryoverIds.Count > 0)
            {
                Console.WriteLine($"Franchise carryover: {string.Join(", ", options.FranchiseCardNames)}");
            }

            var targetSummary = targetNames.OrderBy(t => t.Key).Select(t => $"d{t.Key}={t.Value}")
                .Concat(flexibleTargetNames.Select(n => $"{n} (any day, by day 15)"));
            Console.WriteLine($"Targets: {string.Join(", ", targetSummary)}");
            Console.WriteLine($"Searching {options.Alphabet.Length}^{options.Length} = {spaceSize:N0} seed(s) (checking {totalToCheck:N0})...");
        }

        var request = new SeedSearchRequest(
            settingPackId, setup, targetIds, options.Tier, options.Alphabet, options.Length, totalToCheck, options.ExcludedSeeds, flexibleTargetIds,
            SettingSlug: options.SettingSlug, OtFriendly: options.OtFriendly, AllowRerollPerDay: options.AllowRerollPerDay);

        var stopwatch = System.Diagnostics.Stopwatch.StartNew();
        var matchCount = 0;
        var matchLock = new object();
        var checkedCount = 0L;

        if (!options.MatchesOnly && options.ExcludedSeeds.Count > 0)
        {
            Console.WriteLine($"Excluding {options.ExcludedSeeds.Count:N0} previously-found seed(s).");
        }

        var result = SeedSearcher.Search(
            repo,
            request,
            maxDegreeOfParallelism: -1, // CLI keeps today's unbounded, single-user-machine behavior.
            matchCap: options.MaxResults,
            onMatch: (seedString, otFriendly) =>
            {
                lock (matchLock)
                {
                    matchCount++;
                    Console.WriteLine(options.MatchesOnly ? seedString : $"MATCH: {seedString}{(otFriendly ? " [OT]" : "")}");
                }
            },
            onProgress: count => checkedCount = count,
            cancellationToken: CancellationToken.None);

        stopwatch.Stop();
        if (options.MatchesOnly)
        {
            return 0;
        }

        var seedsPerSecond = checkedCount / Math.Max(stopwatch.Elapsed.TotalSeconds, 0.001);
        Console.WriteLine($"Checked {checkedCount:N0} seed(s) in {stopwatch.Elapsed.TotalSeconds:N1}s ({seedsPerSecond:N0} seeds/sec) - {matchCount} match(es).");
        if (result.MatchCapReached)
        {
            Console.WriteLine($"Stopped after reaching the {options.MaxResults:N0}-match cap - pass --max-results <n> to raise it (0 or negative = unlimited).");
        }

        return 0;
    }

    private static bool TryResolveUnlockByName(GameDataRepository repo, string name, string label, out UnlockData unlock)
    {
        var lookup = UnlockNameResolver.TryResolveByName(repo, name);
        if (lookup.Unlock is not null)
        {
            unlock = lookup.Unlock;
            return true;
        }

        Console.WriteLine(UnlockNameResolver.BuildAmbiguousOrUnknownErrorMessage(label, name, lookup));
        unlock = null!;
        return false;
    }

    private sealed class Options
    {
        public required string SettingSlug { get; init; }
        public required string DishName { get; init; }
        public int? SubOptionChoice { get; init; }
        public int HeatLevel { get; init; }
        public int Tier { get; init; }
        public required List<string> FranchiseCardNames { get; init; }
        public required Dictionary<int, string> DayTargets { get; init; }
        public required List<string> AnyDayCardNames { get; init; }
        public required string Alphabet { get; init; }
        public required int Length { get; init; }
        public long? Limit { get; init; }
        public required long MaxResults { get; init; }
        public required IReadOnlySet<string> ExcludedSeeds { get; init; }
        public bool MatchesOnly { get; init; }
        public string? CardPack { get; init; }
        public bool OtFriendly { get; init; }
        public bool AllowRerollPerDay { get; init; }
    }

    // Same hard default the web UI uses (SearchEndpoints.DefaultMatchCap) - a genuine engine-level
    // rule per the project plan, not a web-only limit. Unlike the web UI, the CLI can raise or
    // disable it via --max-results, since it's a power-user/scripting tool where "find everything"
    // is often the actual point.
    private const long DefaultMaxResults = 500;

    private static Options? ParseOptions(string[] args)
    {
        string? settingSlug = null;
        string? dishName = null;
        int? subOption = null;
        var heat = 0;
        var tier = 0;
        var franchiseCardNames = new List<string>();
        var dayTargets = new Dictionary<int, string>();
        var anyDayCardNames = new List<string>();
        var alphabet = Seed.FriendlyCharacters;
        var length = 4;
        long? limit = null;
        var maxResults = DefaultMaxResults;
        var excludedSeeds = new HashSet<string>(StringComparer.Ordinal);
        var matchesOnly = false;
        string? cardPack = null;
        var otFriendly = false;
        var allowRerollPerDay = false;

        for (var i = 0; i < args.Length; i++)
        {
            switch (args[i])
            {
                case "--setting" when i + 1 < args.Length:
                    settingSlug = args[++i];
                    break;
                case "--dish" when i + 1 < args.Length:
                    dishName = args[++i];
                    break;
                case "--sub-option" when i + 1 < args.Length:
                    subOption = int.Parse(args[++i]);
                    break;
                case "--heat" when i + 1 < args.Length:
                    heat = int.Parse(args[++i]);
                    break;
                case "--tier" when i + 1 < args.Length:
                    tier = int.Parse(args[++i]);
                    break;
                case "--franchise-card" when i + 1 < args.Length:
                    franchiseCardNames.Add(args[++i]);
                    break;
                case "--day" when i + 1 < args.Length:
                    var raw = args[++i];
                    var parts = raw.Split('=', 2);
                    if (parts.Length != 2 || !int.TryParse(parts[0], out var day))
                    {
                        Console.WriteLine($"Invalid --day value '{raw}' - expected <day>=<card name>, e.g. --day 3=\"Second Bite\"");
                        return null;
                    }

                    dayTargets[day] = parts[1];
                    break;
                case "--any-day-card" when i + 1 < args.Length:
                    anyDayCardNames.Add(args[++i]);
                    break;
                case "--alphabet" when i + 1 < args.Length:
                    alphabet = args[++i];
                    break;
                case "--length" when i + 1 < args.Length:
                    length = int.Parse(args[++i]);
                    break;
                case "--limit" when i + 1 < args.Length:
                    limit = long.Parse(args[++i]);
                    break;
                case "--max-results" when i + 1 < args.Length:
                    var parsedMaxResults = long.Parse(args[++i]);
                    maxResults = parsedMaxResults <= 0 ? long.MaxValue : parsedMaxResults;
                    break;
                case "--exclude-seed" when i + 1 < args.Length:
                    excludedSeeds.Add(args[++i].ToLowerInvariant());
                    break;
                case "--exclude-seeds-file" when i + 1 < args.Length:
                    var excludeFilePath = args[++i];
                    foreach (var line in File.ReadAllLines(excludeFilePath))
                    {
                        var trimmed = line.Trim();
                        if (trimmed.Length > 0)
                        {
                            excludedSeeds.Add(trimmed.ToLowerInvariant());
                        }
                    }

                    break;
                case "--matches-only":
                    matchesOnly = true;
                    break;
                case "--card-pack" when i + 1 < args.Length:
                    cardPack = args[++i];
                    break;
                case "--ot-friendly":
                    otFriendly = true;
                    break;
                case "--reroll-per-day":
                    allowRerollPerDay = true;
                    break;
                default:
                    Console.WriteLine($"Unknown or incomplete argument '{args[i]}'");
                    return null;
            }
        }

        if (settingSlug is null || dishName is null || (dayTargets.Count == 0 && anyDayCardNames.Count == 0 && !otFriendly))
        {
            Console.WriteLine("Usage: search --setting <slug> --dish <name> [--sub-option 1|2] [--heat <n>]");
            Console.WriteLine("              [--tier <n>] [--franchise-card <name> ...]");
            Console.WriteLine("              [--day <n>=<card name> ...] [--any-day-card <card name> ...] [--ot-friendly]");
            Console.WriteLine("              [--reroll-per-day]");
            Console.WriteLine("              [--alphabet <chars>] [--length <n>] [--limit <n>] [--max-results <n>]");
            Console.WriteLine();
            Console.WriteLine("--day pins a card to an exact day; --any-day-card (repeatable) only requires the card be");
            Console.WriteLine("obtained by day 15, in any order - combine both freely in the same search. At least one of");
            Console.WriteLine("--day/--any-day-card/--ot-friendly is required.");
            Console.WriteLine("--ot-friendly restricts to seeds where days 1, 2, and 4 offer Research Desks - an");
            Console.WriteLine("additional AND-condition layered onto any --day/--any-day-card targets, or usable alone.");
            Console.WriteLine("--reroll-per-day models the Bay Leaves plant's 1-reroll-per-night mechanic (not Sage's");
            Console.WriteLine("lifetime-total cap): a seed also matches if a target card is only reachable by rerolling");
            Console.WriteLine("the offered pair once that night. Matched seeds gain a [OT] suffix in MATCH output when");
            Console.WriteLine("they're also OT-Friendly (tagged on every match, independent of --ot-friendly).");
            Console.WriteLine($"--max-results caps how many matches are found before stopping early (default {DefaultMaxResults},");
            Console.WriteLine("same as the web UI's hard cap) - pass 0 or a negative number to search without a cap.");
            Console.WriteLine("--tier/--franchise-card model continuing an existing franchise save into this run");
            Console.WriteLine("(BeginRestaurant.cs copies the prior run's cards onto the new one) - --tier is the");
            Console.WriteLine("franchise tier of that prior save (0 for a fresh, non-franchise run), and each");
            Console.WriteLine("--franchise-card names one card already owned from it. See");
            Console.WriteLine("CardDrawSimulatorGroundTruthTests.MatchesRealFranchiseCapture for a real example.");
            Console.WriteLine("Some franchise cards can be taken more than once across separate franchise cycles (e.g.");
            Console.WriteLine("\"Variety\") - qualify which occurrence with \"Name (copy N)\", e.g. --franchise-card");
            Console.WriteLine("\"Variety (copy 2)\"; the copy number is unrelated to --tier (it's just the Nth time you've");
            Console.WriteLine("taken that specific card). Each copy also requires every earlier copy of the same card to");
            Console.WriteLine("be listed too (copies are taken in order).");
            Console.WriteLine("--exclude-seed <seed> (repeatable) / --exclude-seeds-file <path> skip already-found seeds");
            Console.WriteLine("so a rerun surfaces new matches instead of the same ones. --matches-only prints just the");
            Console.WriteLine("MATCH seed strings (no header/summary lines), suitable for piping into a future");
            Console.WriteLine("--exclude-seeds-file.");
            Console.WriteLine("--card-pack <slug> overrides which pack drives card draws, independent of --setting (e.g.");
            Console.WriteLine("'turbo' for Turbo's every-day schedule) - see the 'card-packs' list below. Defaults to the");
            Console.WriteLine("chosen setting's own pack (normal schedule for every currently-extracted setting).");
            return null;
        }

        return new Options
        {
            SettingSlug = settingSlug,
            DishName = dishName,
            SubOptionChoice = subOption,
            HeatLevel = heat,
            Tier = tier,
            FranchiseCardNames = franchiseCardNames,
            DayTargets = dayTargets,
            AnyDayCardNames = anyDayCardNames,
            Alphabet = alphabet,
            Length = length,
            Limit = limit,
            MaxResults = maxResults,
            ExcludedSeeds = excludedSeeds,
            MatchesOnly = matchesOnly,
            CardPack = cardPack,
            OtFriendly = otFriendly,
            AllowRerollPerDay = allowRerollPerDay,
        };
    }
}
