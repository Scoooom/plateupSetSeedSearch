using System.Collections.Concurrent;
using System.Text.RegularExpressions;
using PlateUpSeedTools.Web.Contracts;
using SkiaSharp;

namespace PlateUpSeedTools.Web.Rendering;

/// <summary>
/// Server-side PNG rendering of a resolved layout - the single source of truth shared by the
/// shareable /preview/{setting}/{seed}.png endpoint and (as a plain &lt;img&gt;) the interactive
/// preview page. Ports the drawing logic that used to live only in wwwroot/js/app.js's
/// renderLayout()/CSS - see that file's comments for the *why* behind each rule being ported here
/// (Y-flip, per-instance Dining/Unassigned shading, edge-bar placement, icon-vs-abbreviation
/// fallback). Consumes the already-built LayoutResponse DTO only - never touches
/// PlateUpSeedTools.Engine, which must stay I/O-free.
/// </summary>
public static class LayoutImageRenderer
{
    // No longer needs to match the on-page CSS --tile-size (28px) - see app.js's renderLayout
    // comment on why the interactive page's tooltip overlay is scale-independent of this value.
    private const float TileSizePx = 48f;

    // Proportional ports of theme.css's pixel constants, all originally tuned for a 28px tile.
    private const float HairlinePx = TileSizePx * 1f / 28f;
    private const float ApplianceMarginPx = TileSizePx * 2f / 28f;
    private const float ApplianceRadiusPx = TileSizePx * 3f / 28f;
    private const float FeatureRadiusPx = TileSizePx * 2f / 28f;
    private const float FeatureThicknessPx = TileSizePx * 5f / 28f;
    private const float FrontDoorThicknessPx = TileSizePx * 7f / 28f;

    private static readonly HashSet<string> PerInstanceRoomTypes = new(StringComparer.Ordinal) { "Dining", "Unassigned" };
    private static readonly SKColor ApplianceBoxColor = SKColor.Parse("#e0793c");
    private static readonly SKColor ApplianceTextColor = SKColor.Parse("#1b1410");

    // Icon files never change without a redeploy - safe to cache decoded bitmaps for the process
    // lifetime rather than re-decoding on every request.
    private static readonly ConcurrentDictionary<string, SKBitmap?> IconCache = new();

    public static byte[] Render(LayoutResponse layout, string webRootPath)
    {
        var bounds = ComputeBounds(layout.Tiles, layout.Appliances);
        var widthPx = (int)MathF.Round(bounds.Width * TileSizePx);
        var heightPx = (int)MathF.Round(bounds.Height * TileSizePx);

        using var bitmap = new SKBitmap(widthPx, heightPx);
        using var canvas = new SKCanvas(bitmap);

        // Fills the whole canvas with the hairline color; each tile is then drawn inset, leaving a
        // thin line showing through - replaces the CSS .grid `gap: 1px` + `background` trick, which
        // the image can't reproduce directly (see app.js's renderLayout for why gap had to become 0
        // there once this image is stretched underneath it).
        canvas.Clear(LayoutColors.PanelBorder);

        var (roomOrderByType, indexByRoomId) = GroupRoomsByInstance(layout.Tiles);

        using (var tilePaint = new SKPaint { Style = SKPaintStyle.Fill })
        {
            foreach (var tile in layout.Tiles)
            {
                tilePaint.Color = ColorForTile(tile, roomOrderByType, indexByRoomId);
                var (x, y) = CellOrigin(tile.X, tile.Y, bounds);
                var rect = SKRect.Create(x + HairlinePx, y + HairlinePx, TileSizePx - HairlinePx, TileSizePx - HairlinePx);
                canvas.DrawRect(rect, tilePaint);
            }
        }

        DrawAppliances(canvas, layout.Appliances, bounds, webRootPath);
        DrawFeatures(canvas, layout.Features, bounds);

        using var image = SKImage.FromBitmap(bitmap);
        using var data = image.Encode(SKEncodedImageFormat.Png, 100);
        return data.ToArray();
    }

    // Tiles ∪ appliances, matching app.js:182-191 - not LayoutResponse.Width/Height, which is
    // tiles-only (LayoutEndpoints.ToResponse) and already goes unused by the client's own renderer
    // for exactly this reason (appliances can sit at/near the tile bounds' edge).
    private static Bounds ComputeBounds(IReadOnlyList<TileDto> tiles, IReadOnlyList<ApplianceDto> appliances)
    {
        var xs = tiles.Select(t => t.X).Concat(appliances.Select(a => a.X)).ToList();
        var ys = tiles.Select(t => t.Y).Concat(appliances.Select(a => a.Y)).ToList();
        return new Bounds(xs.Min(), xs.Max(), ys.Min(), ys.Max());
    }

    // Port of app.js:210-228: groups Dining/Unassigned tiles by physical room instance (roomId),
    // in first-seen order, so each gets a distinguishable shaded color (see LayoutColors.ShadeColor).
    private static (Dictionary<string, List<int>> OrderByType, Dictionary<int, int> IndexByRoomId) GroupRoomsByInstance(
        IReadOnlyList<TileDto> tiles)
    {
        var orderByType = new Dictionary<string, List<int>>();
        var seenRoomIds = new HashSet<int>();
        foreach (var tile in tiles)
        {
            if (PerInstanceRoomTypes.Contains(tile.RoomType) && !seenRoomIds.Contains(tile.RoomId))
            {
                if (!orderByType.TryGetValue(tile.RoomType, out var order))
                {
                    order = [];
                    orderByType[tile.RoomType] = order;
                }
                order.Add(tile.RoomId);
            }
            seenRoomIds.Add(tile.RoomId);
        }

        var indexByRoomId = new Dictionary<int, int>();
        foreach (var order in orderByType.Values)
        {
            for (var i = 0; i < order.Count; i++)
            {
                indexByRoomId[order[i]] = i;
            }
        }

        return (orderByType, indexByRoomId);
    }

    private static SKColor ColorForTile(
        TileDto tile, Dictionary<string, List<int>> roomOrderByType, Dictionary<int, int> indexByRoomId)
    {
        if (roomOrderByType.TryGetValue(tile.RoomType, out var order) && order.Count > 1)
        {
            return LayoutColors.ShadeColor(LayoutColors.RoomColor(tile.RoomType), indexByRoomId[tile.RoomId]);
        }
        return LayoutColors.RoomColor(tile.RoomType);
    }

    private static void DrawAppliances(
        SKCanvas canvas, IReadOnlyList<ApplianceDto> appliances, Bounds bounds, string webRootPath)
    {
        using var boxPaint = new SKPaint { Color = ApplianceBoxColor, Style = SKPaintStyle.Fill, IsAntialias = true };
        using var iconPaint = new SKPaint { IsAntialias = true };
        using var textPaint = new SKPaint { Color = ApplianceTextColor, IsAntialias = true };
        using var font = new SKFont { Size = TileSizePx * 0.32f, Embolden = true };

        foreach (var appliance in appliances)
        {
            var (x, y) = CellOrigin(appliance.X, appliance.Y, bounds);
            var boxRect = SKRect.Create(
                x + ApplianceMarginPx, y + ApplianceMarginPx,
                TileSizePx - (2 * ApplianceMarginPx), TileSizePx - (2 * ApplianceMarginPx));
            var cx = boxRect.MidX;
            var cy = boxRect.MidY;

            var icon = ResolveIcon(appliance.ApplianceName, webRootPath);
            if (icon is not null)
            {
                // Box stays axis-aligned; only the icon rotates - mirrors app.js:265 (`img.style.transform`
                // is set on the <img> child, never on the .appliance div itself). All 5 real icon files
                // are 500x500 (square), same as boxRect, so `object-fit: contain` never needs letterboxing.
                canvas.DrawRoundRect(boxRect, ApplianceRadiusPx, ApplianceRadiusPx, boxPaint);
                canvas.Save();
                canvas.RotateDegrees(appliance.RotationDegrees, cx, cy);
                canvas.DrawBitmap(icon, boxRect, SKSamplingOptions.Default, iconPaint);
                canvas.Restore();
            }
            else
            {
                // No icon (e.g. Enchanting Desk) - box + text rotate together, mirrors app.js:268-269
                // (`div.style.transform` set on the whole div once the <img> 404s and is removed).
                canvas.Save();
                canvas.RotateDegrees(appliance.RotationDegrees, cx, cy);
                canvas.DrawRoundRect(boxRect, ApplianceRadiusPx, ApplianceRadiusPx, boxPaint);
                var text = Abbreviate(appliance.ApplianceName);
                canvas.DrawText(text, cx, cy + (font.Size * 0.35f), SKTextAlign.Center, font, textPaint);
                canvas.Restore();
            }
        }
    }

    private static void DrawFeatures(SKCanvas canvas, IReadOnlyList<FeatureDto> features, Bounds bounds)
    {
        using var paint = new SKPaint { Style = SKPaintStyle.Fill, IsAntialias = true };

        foreach (var feature in features)
        {
            var dx = feature.Tile2X - feature.Tile1X;
            var dy = feature.Tile2Y - feature.Tile1Y;
            var (x, y) = CellOrigin(feature.Tile1X, feature.Tile1Y, bounds);
            var thickness = string.Equals(feature.Type, "FrontDoor", StringComparison.OrdinalIgnoreCase)
                ? FrontDoorThicknessPx
                : FeatureThicknessPx;

            // North is up (screen y decreases toward larger tile.Y - see CellOrigin). Centered ON
            // the shared boundary between Tile1/Tile2 (half the bar's thickness on each tile's
            // side), not flush against one edge of Tile1's own cell - the latter used to make the
            // bar hug whichever tile happened to be Tile1 in the underlying data, which isn't
            // consistent from one feature to the next, so visually similar doors/hatches looked
            // like they were "randomly picking a side" instead of all centering the same way.
            var rect = dy > 0
                ? SKRect.Create(x, y - thickness / 2f, TileSizePx, thickness)
                : dy < 0
                    ? SKRect.Create(x, y + TileSizePx - thickness / 2f, TileSizePx, thickness)
                    : dx > 0
                        ? SKRect.Create(x + TileSizePx - thickness / 2f, y, thickness, TileSizePx)
                        : SKRect.Create(x - thickness / 2f, y, thickness, TileSizePx);

            paint.Color = LayoutColors.FeatureColor(feature.Type);
            canvas.DrawRoundRect(rect, FeatureRadiusPx, FeatureRadiusPx, paint);
        }
    }

    // North is up: increasing tile.Y moves toward the top of the image (smaller pixel Y), matching
    // app.js:247's `gridRow = maxY - tile.y + 1`.
    private static (float X, float Y) CellOrigin(int tileX, int tileY, Bounds bounds) =>
        ((tileX - bounds.MinX) * TileSizePx, (bounds.MaxY - tileY) * TileSizePx);

    private static SKBitmap? ResolveIcon(string applianceName, string webRootPath)
    {
        var slug = Slugify(applianceName);
        return IconCache.GetOrAdd(slug, s =>
        {
            var path = Path.Combine(webRootPath, "images", "appliances", s + ".png");
            return File.Exists(path) ? SKBitmap.Decode(path) : null;
        });
    }

    // Port of app.js:169-177's iconPathFor slug logic.
    private static string Slugify(string name) =>
        Regex.Replace(name.ToLowerInvariant(), "[^a-z0-9]+", "-").Trim('-');

    // Port of app.js:157-162's abbreviate().
    private static string Abbreviate(string name)
    {
        var words = name.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
        if (words.Length == 0)
        {
            return "?";
        }
        if (words.Length == 1)
        {
            return words[0].Length <= 3 ? words[0].ToUpperInvariant() : words[0][..3].ToUpperInvariant();
        }
        return string.Concat(words.Take(3).Select(w => char.ToUpperInvariant(w[0])));
    }

    private readonly record struct Bounds(int MinX, int MaxX, int MinY, int MaxY)
    {
        public int Width => MaxX - MinX + 1;
        public int Height => MaxY - MinY + 1;
    }
}
