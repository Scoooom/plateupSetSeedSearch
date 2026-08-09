using System.Text.RegularExpressions;
using PlateUpSeedTools.Engine.Data;
using PlateUpSeedTools.Engine.Layouts;
using PlateUpSeedTools.Engine.Seeding;
using PlateUpSeedTools.Infrastructure.Cache;
using PlateUpSeedTools.Web.Contracts;
using PlateUpSeedTools.Web.Rendering;

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

    // Matches the synthesized fallback label SettingExtractor.ResolveAppliance assigns to appliance
    // IDs with no real English display name in any locale (currently only "Item Source Reservation",
    // id 1270423542 - a generic dish-dependent ingredient-provider slot, not a concrete appliance).
    // Excluded from the response entirely (not just given a generic icon) - user decision: a box
    // that can't say what it actually is adds visual noise rather than information.
    private const string GenericProviderApplianceName = "Provider";

    // OG_META is the marker comment GetPreviewHtml injects <meta property="og:..."> tags at - see
    // wwwroot/preview.html's <head>.
    private const string OgMetaMarker = "<!-- OG_META -->";

    public static void MapLayoutEndpoints(this WebApplication app)
    {
        app.MapGet("/api/settings", (GameDataRepository repo) => repo.ListSettings());

        app.MapGet("/api/layout/{setting}/{seed}", GetLayout);
        app.MapPost("/api/layout", (LayoutRequest request, GameDataRepository repo, ICacheStore<LayoutResponse> cache) =>
            GetLayout(request.Setting, request.Seed, repo, cache));

        // Shareable image (see Rendering/LayoutImageRenderer) - the single source of truth also
        // displayed by the interactive page itself (wwwroot/js/app.js). Registered as an explicit,
        // more-specific route ("{seed}.png" has literal text after the parameter, same mechanism as
        // a typical "{id}.json"-style route) so it's matched in preference to the bare-seed route
        // below for the same URL shape, regardless of registration order.
        app.MapGet("/preview/{setting}/{seed}.png", GetLayoutPng);

        // Real (non-fallback) HTML endpoint for the bare hotlink, so it can inject an og:image tag
        // pointing at the .png above - Discord (and similar link-unfurlers) can then show *a* image
        // embed for a bare share, even though only a true image/* response gets the fully
        // chrome-less embed treatment. Takes over from Program.cs's MapFallbackToFile for this exact
        // 2-segment shape, so it must reproduce "just serve the static file" for invalid input itself.
        app.MapGet("/preview/{setting}/{seed}", GetPreviewHtml);
    }

    private static async Task<IResult> GetLayout(
        string setting, string seed, GameDataRepository repo, ICacheStore<LayoutResponse> cache)
    {
        var (layout, error) = await ResolveLayout(setting, seed, repo, cache);
        return layout is not null ? Results.Ok(layout) : Results.BadRequest(new { error });
    }

    private static async Task<IResult> GetLayoutPng(
        string setting, string seed, HttpContext httpContext, GameDataRepository repo,
        ICacheStore<LayoutResponse> cache, IWebHostEnvironment env)
    {
        var (layout, error) = await ResolveLayout(setting, seed, repo, cache);
        if (layout is null)
        {
            return Results.BadRequest(new { error });
        }

        // Content is deterministic per setting+seed - safe for browsers/Discord's own fetcher to cache.
        httpContext.Response.Headers.CacheControl = "public, max-age=86400";
        var png = LayoutImageRenderer.Render(layout, env.WebRootPath);
        return Results.File(png, "image/png");
    }

    private static IResult GetPreviewHtml(
        string setting, string seed, HttpContext httpContext, GameDataRepository repo, IWebHostEnvironment env)
    {
        var html = File.ReadAllText(Path.Combine(env.WebRootPath, "preview.html"));

        var isValidSetting = repo.ListSettings().Any(s => s.Slug == setting);
        var isValidSeed = ValidSeedPattern().IsMatch(seed);
        if (isValidSetting && isValidSeed)
        {
            var request = httpContext.Request;
            var imageUrl = $"{request.Scheme}://{request.Host}/preview/{Uri.EscapeDataString(setting)}/{Uri.EscapeDataString(seed.ToLowerInvariant())}.png";
            // og:title/og:description are deliberately omitted - either would reintroduce the
            // title-bar chrome around the embed that a bare-URL share was hoping to minimize (see
            // project plan's "Open Graph tags on the bare URL" section).
            var ogTags = $"""<meta property="og:image" content="{imageUrl}" /><meta property="og:type" content="website" />""";
            html = html.Replace(OgMetaMarker, ogTags);
        }

        return Results.Content(html, "text/html");
    }

    private static async Task<(LayoutResponse? Layout, string? Error)> ResolveLayout(
        string setting, string seed, GameDataRepository repo, ICacheStore<LayoutResponse> cache)
    {
        if (!repo.ListSettings().Any(s => s.Slug == setting))
        {
            return (null, $"Unknown setting '{setting}'.");
        }

        if (!ValidSeedPattern().IsMatch(seed))
        {
            return (null, $"Seed must be 1-{Seed.MaxLength} alphanumeric characters.");
        }

        // CORRECTED (was ToUpperInvariant - confirmed wrong via ground-truth capture, see
        // game-domain-knowledge.md's "Seed case normalization" section): the real game's seed text
        // input (Common/Kitchen/TextInputView.cs, `_Value = IsSeedMode ? value.ToLower() : value`)
        // lowercases what the player types before it's ever used to construct a Seed - the uppercase
        // shown on screen is a display style over a lowercase-stored string. Normalized here so
        // "abc"/"ABC"/"AbC" are treated as the same seed (same cache entry, same hash) matching what
        // the game itself actually computes. This is deliberately NOT done inside Engine.Seeding.Seed
        // - that's a faithful, verified port of the real Seed struct, which hashes exactly the bytes
        // it's given with no case transform (see its doc comment); normalizing belongs at this
        // application boundary, not in the port.
        seed = seed.ToLowerInvariant();

        var cacheKey = $"{setting}:{seed}";
        var cached = await cache.GetAsync(cacheKey);
        if (cached is not null)
        {
            return (cached, null);
        }

        var settingData = repo.GetSetting(setting);
        var seedObj = new Seed(seed);
        var layoutProfileId = settingData.ForceLayoutId ?? LayoutSeed.LayoutIdFromSeed(seedObj);
        var (profile, graph) = repo.ResolveLayout(setting, seedObj);
        var constructionSeed = LayoutSeed.ConstructionSeed(seedObj);
        var constructed = CreateLayoutHelper.ConstructLayout(graph, profile, constructionSeed);

        var response = ToResponse(seedObj.Value, setting, settingData.DisplayName, layoutProfileId, constructed, repo);
        await cache.SetAsync(cacheKey, response);
        return (response, null);
    }

    private static LayoutResponse ToResponse(
        string seed, string settingSlug, string settingName, int layoutProfileId, ConstructedLayout layout, GameDataRepository repo)
    {
        var bounds = layout.Blueprint.GetBounds();
        var tiles = layout.Blueprint.Tiles
            .Where(t => t.Value.Type != RoomType.NoRoom)
            .Select(t => new TileDto(t.Key.X, t.Key.Y, t.Value.Type.ToString(), t.Value.Id))
            .ToList();
        var appliances = layout.Decorations
            .Select(d => new ApplianceDto(d.Position.X, d.Position.Y, d.ApplianceId, repo.GetApplianceName(d.ApplianceId), d.RotationDegrees))
            .Where(a => a.ApplianceName != GenericProviderApplianceName)
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
