using System.Text.RegularExpressions;
using PlateUpSeedTools.Engine.Data;
using PlateUpSeedTools.Engine.Layouts;
using PlateUpSeedTools.Engine.Seeding;
using PlateUpSeedTools.Infrastructure.Cache;
using PlateUpSeedTools.Web.Contracts;

namespace PlateUpSeedTools.Web.Endpoints;

public static partial class LayoutEndpoints
{
    // Kitchen.Seed's constructor (DecompiledSource/GameData/Kitchen/Seed.cs) accepts ANY string,
    // only truncating to 8 chars - Seed.FriendlyCharacters (bdghjmqrtvwxy346789) is just the
    // alphabet the game uses to auto-*generate* a seed for display, not an input restriction, and
    // Seed.Characters (a-z1-9) is declared but never actually referenced/enforced anywhere in the
    // decompiled source either. Real seeds people type/share can be any alphanumeric string up to
    // 8 characters. We still reject non-alphanumeric input here (not a game rule - just avoids
    // control characters/whitespace/URL-unsafe input reaching the cache key and hash function).
    [GeneratedRegex("^[a-zA-Z0-9]{1,8}$")]
    private static partial Regex ValidSeedPattern();
    public static void MapLayoutEndpoints(this WebApplication app)
    {
        app.MapGet("/api/settings", (GameDataRepository repo) => repo.ListSettings());

        app.MapGet("/api/layout/{setting}/{seed}", GetLayout);
        app.MapPost("/api/layout", (LayoutRequest request, GameDataRepository repo, ICacheStore<LayoutResponse> cache) =>
            GetLayout(request.Setting, request.Seed, repo, cache));
    }

    private static async Task<IResult> GetLayout(
        string setting, string seed, GameDataRepository repo, ICacheStore<LayoutResponse> cache)
    {
        if (!repo.ListSettings().Any(s => s.Slug == setting))
        {
            return Results.BadRequest(new { error = $"Unknown setting '{setting}'." });
        }

        if (!ValidSeedPattern().IsMatch(seed))
        {
            return Results.BadRequest(new { error = $"Seed must be 1-{Seed.MaxLength} alphanumeric characters." });
        }

        // Real PlateUp seeds are always uppercase internally - not enforced above (any case is
        // accepted as valid input) but normalized here so "abc"/"ABC"/"AbC" are treated as the same
        // seed: same cache entry, same hash, matching what the game itself would compute. This is
        // deliberately NOT done inside Engine.Seeding.Seed - that's a faithful, verified port of the
        // real Seed struct, which hashes exactly the bytes it's given with no case transform (see its
        // doc comment); normalizing belongs at this application boundary, not in the port.
        seed = seed.ToUpperInvariant();

        var cacheKey = $"{setting}:{seed}";
        var cached = await cache.GetAsync(cacheKey);
        if (cached is not null)
        {
            return Results.Ok(cached);
        }

        var settingData = repo.GetSetting(setting);
        var seedObj = new Seed(seed);
        var layoutProfileId = settingData.ForceLayoutId ?? LayoutSeed.LayoutIdFromSeed(seedObj);
        var (profile, graph) = repo.ResolveLayout(setting, seedObj);
        var constructionSeed = LayoutSeed.ConstructionSeed(seedObj);
        var constructed = CreateLayoutHelper.ConstructLayout(graph, profile, constructionSeed);

        var response = ToResponse(seedObj.Value, setting, settingData.DisplayName, layoutProfileId, constructed);
        await cache.SetAsync(cacheKey, response);
        return Results.Ok(response);
    }

    private static LayoutResponse ToResponse(
        string seed, string settingSlug, string settingName, int layoutProfileId, ConstructedLayout layout)
    {
        var bounds = layout.Blueprint.GetBounds();
        var tiles = layout.Blueprint.Tiles
            .Where(t => t.Value.Type != RoomType.NoRoom)
            .Select(t => new TileDto(t.Key.X, t.Key.Y, t.Value.Type.ToString()))
            .ToList();
        var appliances = layout.Decorations
            .Select(d => new ApplianceDto(d.Position.X, d.Position.Y, d.ApplianceId, d.RotationDegrees))
            .ToList();
        var features = layout.Blueprint.Features
            .Select(f => new FeatureDto(f.Tile1.X, f.Tile1.Y, f.Tile2.X, f.Tile2.Y, f.Type.ToString()))
            .ToList();

        return new LayoutResponse(
            seed,
            settingSlug,
            settingName,
            layoutProfileId,
            bounds.MaxX - bounds.MinX + 1,
            bounds.MaxY - bounds.MinY + 1,
            tiles,
            appliances,
            features);
    }

    private sealed record LayoutRequest(string Setting, string Seed);
}
