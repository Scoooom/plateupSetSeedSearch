# PlateUp Seed Tools

Self-hosted PlateUp! seed layout previewer — given a seed and a restaurant setting, shows what
the kitchen layout will look like (room shapes, dining tables, kitchen appliance placements).
Seed search (find seeds with a specific card) is a future feature, not yet built.

## Running

```
docker compose up -d
```

Then visit http://localhost:8080. Pick a restaurant setting, type a seed (1-8 characters from
`bdghjmqrtvwxy346789`, same alphabet the game itself generates), and preview.

Currently supported settings: Banquet Setting, Christmas Banquet Setting, Santa Workshop Setting,
Bakery Setting, February Setting, January Setting, June Setting - Coffeeshop, March Setting - Turbo.

## Cache

Layout computations are cached in SQLite (a Docker volume, `cache-data`). To clear it:

```
docker compose run --rm cli cache clear
docker compose run --rm cli cache stats
```

## Project layout

```
src/PlateUpSeedTools.Engine/            Pure C# port of the game's seed/RNG/layout-generation logic
src/PlateUpSeedTools.Infrastructure/    SQLite cache abstraction, shared by Web and Cli
src/PlateUpSeedTools.Web/               ASP.NET Core minimal API + static frontend
src/PlateUpSeedTools.Cli/               `cache clear`/`cache stats` verbs
tools/PlateUpSeedTools.DataExtractor/   Dev-only tool: extracts data/settings/*.json from a local
                                         PlateUpSourceFiles checkout (net8.0, not shipped in Docker)
tests/PlateUpSeedTools.Engine.Tests/    xunit: RNG/hash parity, determinism, end-to-end layout tests
data/settings/                          Committed, derived JSON (room graph topology, appliance IDs -
                                         no game art/text/audio) that the Engine loads at runtime
```

## Development

Requires the .NET 10 SDK (or use `docker run --rm -v "$(pwd)":/src -w /src mcr.microsoft.com/dotnet/sdk:10.0 ...`
if you don't have it installed locally):

```
dotnet test tests/PlateUpSeedTools.Engine.Tests
```

### Regenerating data/settings/

Only needed if the game updates and the layout data needs re-extracting, or to add more settings.
Requires a local checkout of the game's decompiled source + extracted assets
(`PlateUpSourceFiles/`, not part of this repo - see the session 1 plan for how it was produced):

```
dotnet run --project tools/PlateUpSeedTools.DataExtractor -- <path to PlateUpSourceFiles> data/settings
```

Add more settings by extending the `settingsToExtract` list in
`tools/PlateUpSeedTools.DataExtractor/Program.cs`. If the extractor reports "unrecognised module
type", the setting's layout graph uses a module not yet ported - see
`src/PlateUpSeedTools.Engine/Layouts/Modules/` for the ones that exist, and the decompiled
`Kitchen.Layouts.Modules/*.cs` source for the rest.

## Status

Session 1 (working website + seed layout previewer) is functionally complete: all 8 discovered
restaurant settings produce real, deterministic layouts from real seeds, served through a working
web UI with SQLite caching and a CLI cache-clear command, all packaged in Docker.

Verified so far: the RNG and seed-hash ports are confirmed byte-exact against values captured from
the real running game (see `PlateUpSourceFiles/GroundTruthLogger/`). The layout-generation output
itself (room shapes, appliance placement) has **not yet** been visually compared against the real
game - that's the next verification step, and the reason to treat current output as "plausible,
not yet proven correct."

Known gaps:
- Per-setting cosmetic decorations (Christmas trees/sheds etc., `RestaurantSetting.Decorators`) are
  skipped - that data is Odin/BinaryFormatter-serialized and hasn't been decoded. Room and appliance
  placement are unaffected.
- 20 of the game's ~38 layout graph modules are ported - enough for the 8 currently-extracted
  settings. Adding a setting that uses an unported module will need that module ported first (see
  "Regenerating data/settings/" above).
- Appliances are shown by their raw numeric ID (no display names/icons yet).
- Seed search (find seeds with a specific starting card) is a future feature, not started.

See `/root/.claude/plans/new-project-i-am-proud-wand.md` for the full session 1 plan.
