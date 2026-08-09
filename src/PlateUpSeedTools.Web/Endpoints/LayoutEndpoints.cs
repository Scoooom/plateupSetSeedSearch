using PlateUpSeedTools.Engine.Data;
using PlateUpSeedTools.Engine.Layouts;
using PlateUpSeedTools.Engine.Seeding;
using PlateUpSeedTools.Infrastructure.Cache;
using PlateUpSeedTools.Web.Contracts;

namespace PlateUpSeedTools.Web.Endpoints;

public static class LayoutEndpoints
{
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

        if (seed.Length is 0 or > Seed.MaxLength || !seed.All(c => Seed.FriendlyCharacters.Contains(c)))
        {
            return Results.BadRequest(new { error = $"Seed must be 1-{Seed.MaxLength} characters from '{Seed.FriendlyCharacters}'." });
        }

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

        return new LayoutResponse(
            seed,
            settingSlug,
            settingName,
            layoutProfileId,
            bounds.MaxX - bounds.MinX + 1,
            bounds.MaxY - bounds.MinY + 1,
            tiles,
            appliances);
    }

    private sealed record LayoutRequest(string Setting, string Seed);
}
