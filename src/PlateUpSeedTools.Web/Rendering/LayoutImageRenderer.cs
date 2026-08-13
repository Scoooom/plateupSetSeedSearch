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
    private const float WallThicknessPx = TileSizePx * 4f / 28f;

    // Full-tile room-code badge - deliberately near the tile's full size (a thin margin, not the
    // appliance box's) so it reads as "this whole tile is the label", per real player feedback that
    // the old small floating text didn't stand out enough.
    private const float RoomLabelMarginPx = TileSizePx * 1f / 28f;
    private const float RoomLabelRadiusPx = TileSizePx * 2f / 28f;
    private const float RoomLabelBorderPx = TileSizePx * 2.5f / 28f;

    private static readonly HashSet<string> PerInstanceRoomTypes = new(StringComparer.Ordinal) { "Dining", "Unassigned" };
    private static readonly SKColor ApplianceBoxColor = SKColor.Parse("#e0793c");
    private static readonly SKColor ApplianceTextColor = SKColor.Parse("#1b1410");
    private static readonly SKColor RoomLabelBackground = SKColor.Parse("#f1e6d8");
    private static readonly SKColor RoomLabelBorder = SKColor.Parse("#1b1410");
    private static readonly SKColor RoomLabelText = SKColor.Parse("#1b1410");

    private static readonly (int Dx, int Dy)[] Neighbors4 = [(1, 0), (-1, 0), (0, 1), (0, -1)];

    // Icon files never change without a redeploy - safe to cache decoded bitmaps for the process
    // lifetime rather than re-decoding on every request.
    private static readonly ConcurrentDictionary<string, SKBitmap?> IconCache = new();

    // Player-supplied per-request color overrides (LayoutEndpoints.GetLayoutPng's `?colors=` query
    // param) - lowercase keys: a RoomType name ("kitchen", "unassigned", ...), "wall", or a feature
    // type ("door" - also covers "doorreversed", "frontdoor", "hatch"). Browser-local by design (see
    // app.js) - never affects the canonical /preview/.../seed.png used for og:image/Discord embeds
    // or shared hotlinks, only a request a player's own page makes for itself.
    public static byte[] Render(LayoutResponse layout, string webRootPath, IReadOnlyDictionary<string, SKColor>? colorOverrides = null)
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
                tilePaint.Color = ColorForTile(tile, roomOrderByType, indexByRoomId, colorOverrides);
                var (x, y) = CellOrigin(tile.X, tile.Y, bounds);
                var rect = SKRect.Create(x + HairlinePx, y + HairlinePx, TileSizePx - HairlinePx, TileSizePx - HairlinePx);
                canvas.DrawRect(rect, tilePaint);
            }
        }

        DrawRoomLabels(canvas, layout.Tiles, layout.Appliances, layout.Features, bounds);
        DrawAppliances(canvas, layout.Appliances, bounds, webRootPath);
        DrawWalls(canvas, layout.Tiles, layout.Features, bounds, colorOverrides);
        DrawFeatures(canvas, layout.Features, bounds, colorOverrides);

        using var image = SKImage.FromBitmap(bitmap);
        using var data = image.Encode(SKEncodedImageFormat.Png, 100);
        return data.ToArray();
    }

    // A player override wins if present for `key`; otherwise the caller's normal default.
    private static SKColor Resolve(IReadOnlyDictionary<string, SKColor>? overrides, string key, SKColor fallback) =>
        overrides is not null && overrides.TryGetValue(key, out var color) ? color : fallback;

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
        TileDto tile, Dictionary<string, List<int>> roomOrderByType, Dictionary<int, int> indexByRoomId,
        IReadOnlyDictionary<string, SKColor>? colorOverrides)
    {
        var typeKey = tile.RoomType.ToLowerInvariant();
        // Unassigned always goes through the per-instance path, even with only one room in the
        // layout - DrawRoomLabels always numbers Unassigned rooms R1/R2/... regardless of count
        // (see its comment), so the fill color has to key the same way or a "unassigned:0"
        // override set while viewing a 2+-instance seed silently stops applying the moment the
        // player loads a seed with only one Unassigned room (real player-reported bug: a
        // customized color "not carrying over" to a new seed). Other per-instance types (Dining)
        // keep the >1 gate - they're never re-numbered on a single instance, so there's no such
        // mismatch to fix.
        var usesInstanceKey = tile.RoomType == "Unassigned"
            || (roomOrderByType.TryGetValue(tile.RoomType, out var order) && order.Count > 1);
        if (usesInstanceKey)
        {
            var index = indexByRoomId[tile.RoomId];
            // A per-instance override ("unassigned:1", matching wwwroot/js/app.js's customize-colors
            // panel - one swatch per physical instance, e.g. R1/R2, not one shared type-level swatch)
            // wins outright; otherwise fall back to the type-level override/default, auto-shaded.
            if (colorOverrides is not null && colorOverrides.TryGetValue($"{typeKey}:{index}", out var instanceColor))
            {
                return instanceColor;
            }
            var baseColor = Resolve(colorOverrides, typeKey, LayoutColors.RoomColor(tile.RoomType));
            return LayoutColors.ShadeColor(baseColor, index);
        }
        return Resolve(colorOverrides, typeKey, LayoutColors.RoomColor(tile.RoomType));
    }

    // Draws a room-type code (LayoutColors.RoomCode, e.g. "KIT"/"UNA") as a full-tile badge - solid
    // near-white background, dark border and bold ink - on one representative tile per physical room
    // instance (grouped by RoomId, regardless of type - not just the two PerInstanceRoomTypes that
    // get their own shaded color). Real player feedback asked for colorblind-safe symbols that
    // actually stand out (the original small floating text over the room's own fill color didn't),
    // and to avoid landing on an appliance or touching a door/hatch (a badge overlapping the door bar
    // it sits next to reads as broken, not deliberate) - a fixed neutral badge (rather than text
    // colored for contrast against whatever the room's fill happens to be) reads clearly against
    // every room color and every appliance icon alike. Always on, not a toggle: appliances get an
    // equivalent always-on box+text treatment already. The representative tile is the best-scoring
    // tile nearest the room's centroid, preferring (in order) a tile with no appliance and no
    // adjacent door/hatch/front-door, then no appliance regardless of adjacency, then any tile at
    // all - so a badge only ever covers a fixture or crowds an opening when a room genuinely leaves
    // no cleaner option (e.g. a room that's entirely doorway-adjacent).
    private static void DrawRoomLabels(
        SKCanvas canvas, IReadOnlyList<TileDto> tiles, IReadOnlyList<ApplianceDto> appliances,
        IReadOnlyList<FeatureDto> features, Bounds bounds)
    {
        var applianceTiles = new HashSet<(int X, int Y)>(appliances.Select(a => (a.X, a.Y)));
        var featureTiles = new HashSet<(int X, int Y)>(
            features.SelectMany(f => new[] { (f.Tile1X, f.Tile1Y), (f.Tile2X, f.Tile2Y) }));
        var unassignedIndexByRoomId = UnassignedIndexByRoomId(tiles);

        using var backgroundPaint = new SKPaint { Style = SKPaintStyle.Fill, Color = RoomLabelBackground, IsAntialias = true };
        using var borderPaint = new SKPaint { Style = SKPaintStyle.Stroke, Color = RoomLabelBorder, StrokeWidth = RoomLabelBorderPx, IsAntialias = true };
        using var textPaint = new SKPaint { Color = RoomLabelText, IsAntialias = true };
        using var font = new SKFont { Size = TileSizePx * 0.4f, Embolden = true };

        foreach (var room in tiles.GroupBy(t => t.RoomId))
        {
            var roomTiles = room.ToList();
            var candidates = roomTiles.Where(t => !applianceTiles.Contains((t.X, t.Y)) && !featureTiles.Contains((t.X, t.Y))).ToList();
            if (candidates.Count == 0)
            {
                candidates = roomTiles.Where(t => !applianceTiles.Contains((t.X, t.Y))).ToList();
            }
            if (candidates.Count == 0)
            {
                candidates = roomTiles;
            }

            var centroidX = candidates.Average(t => t.X);
            var centroidY = candidates.Average(t => t.Y);
            var labelTile = candidates
                .OrderBy(t => ((t.X - centroidX) * (t.X - centroidX)) + ((t.Y - centroidY) * (t.Y - centroidY)))
                .First();

            var (x, y) = CellOrigin(labelTile.X, labelTile.Y, bounds);
            var badgeRect = SKRect.Create(
                x + RoomLabelMarginPx, y + RoomLabelMarginPx,
                TileSizePx - (2 * RoomLabelMarginPx), TileSizePx - (2 * RoomLabelMarginPx));

            var code = labelTile.RoomType == "Unassigned"
                ? $"R{unassignedIndexByRoomId[labelTile.RoomId] + 1}"
                : LayoutColors.RoomCode(labelTile.RoomType);

            canvas.DrawRoundRect(badgeRect, RoomLabelRadiusPx, RoomLabelRadiusPx, backgroundPaint);
            canvas.DrawRoundRect(badgeRect, RoomLabelRadiusPx, RoomLabelRadiusPx, borderPaint);
            canvas.DrawText(code, badgeRect.MidX, badgeRect.MidY + (font.Size * 0.35f), SKTextAlign.Center, font, textPaint);
        }
    }

    // Unassigned rooms have no real per-room identity in the game data (they're just "the graph
    // never assigned this a type") - a shared "UNA" code on every one of them was ambiguous when a
    // layout has several, so each physical Unassigned room instead gets its own R1/R2/R3.../ number,
    // in first-seen tile order - the same order LayoutColors.ShadeColor already uses to give each
    // instance a distinguishable fill color, so "R2" always lines up with the second-shaded instance.
    private static Dictionary<int, int> UnassignedIndexByRoomId(IReadOnlyList<TileDto> tiles)
    {
        var indexByRoomId = new Dictionary<int, int>();
        foreach (var tile in tiles)
        {
            if (tile.RoomType == "Unassigned" && !indexByRoomId.ContainsKey(tile.RoomId))
            {
                indexByRoomId[tile.RoomId] = indexByRoomId.Count;
            }
        }
        return indexByRoomId;
    }

    // Draws an explicit, solid bar on every tile boundary that is neither same-room nor covered by a
    // door/hatch/front-door Feature - i.e. every real wall - so walls read as a deliberate barrier
    // the same way DrawFeatures already makes doors/hatches read as deliberate openings, rather than
    // being indistinguishable from the plain 1px hairline drawn between every tile regardless of
    // whether it's a real wall. There's no "Wall" concept in the underlying data (Engine/Layouts) -
    // a wall is just the absence of a Feature between two adjacent tiles of different RoomId (or a
    // tile and the exterior, i.e. no tile at all at that position) - so this is computed here from
    // tile/feature adjacency, not read off a field. Square corners (vs. DrawFeatures' rounded pill
    // shapes) reinforce "solid barrier" vs. "opening" as a second, shape-based cue.
    private static void DrawWalls(
        SKCanvas canvas, IReadOnlyList<TileDto> tiles, IReadOnlyList<FeatureDto> features, Bounds bounds,
        IReadOnlyDictionary<string, SKColor>? colorOverrides)
    {
        var roomIdByPos = tiles.ToDictionary(t => (t.X, t.Y), t => t.RoomId);
        var featureEdges = new HashSet<(int Ax, int Ay, int Bx, int By)>(
            features.Select(f => EdgeKey(f.Tile1X, f.Tile1Y, f.Tile2X, f.Tile2Y)));

        var wallEdges = new HashSet<(int Ax, int Ay, int Bx, int By)>();
        foreach (var tile in tiles)
        {
            foreach (var (dx, dy) in Neighbors4)
            {
                var neighborPos = (tile.X + dx, tile.Y + dy);
                var neighborRoomId = roomIdByPos.TryGetValue(neighborPos, out var rid) ? rid : (int?)null;
                if (neighborRoomId == tile.RoomId)
                {
                    continue;
                }

                var edge = EdgeKey(tile.X, tile.Y, neighborPos.Item1, neighborPos.Item2);
                if (!featureEdges.Contains(edge))
                {
                    wallEdges.Add(edge);
                }
            }
        }

        using var paint = new SKPaint
        {
            Style = SKPaintStyle.Fill,
            Color = Resolve(colorOverrides, "wall", LayoutColors.WallColor),
            IsAntialias = true,
        };
        foreach (var (ax, ay, bx, by) in wallEdges)
        {
            var dx = bx - ax;
            var dy = by - ay;
            var (x, y) = CellOrigin(ax, ay, bounds);

            var rect = dy > 0
                ? SKRect.Create(x, y - WallThicknessPx / 2f, TileSizePx, WallThicknessPx)
                : dy < 0
                    ? SKRect.Create(x, y + TileSizePx - WallThicknessPx / 2f, TileSizePx, WallThicknessPx)
                    : dx > 0
                        ? SKRect.Create(x + TileSizePx - WallThicknessPx / 2f, y, WallThicknessPx, TileSizePx)
                        : SKRect.Create(x - WallThicknessPx / 2f, y, WallThicknessPx, TileSizePx);

            canvas.DrawRect(rect, paint);
        }
    }

    // Normalizes an (unordered) pair of adjacent grid positions into a single lexically-sorted key,
    // so a wall/feature edge between A and B matches regardless of which tile happens to be Tile1.
    private static (int Ax, int Ay, int Bx, int By) EdgeKey(int x1, int y1, int x2, int y2) =>
        (x1 < x2 || (x1 == x2 && y1 < y2)) ? (x1, y1, x2, y2) : (x2, y2, x1, y1);

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

    private static void DrawFeatures(
        SKCanvas canvas, IReadOnlyList<FeatureDto> features, Bounds bounds,
        IReadOnlyDictionary<string, SKColor>? colorOverrides)
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

            // "doorreversed" shares the "door" override key too - the default table already
            // collapses them to the same hex (LayoutColors.FeatureColors), so a player picking one
            // "Door" swatch should recolor both.
            var overrideKey = string.Equals(feature.Type, "doorreversed", StringComparison.OrdinalIgnoreCase)
                ? "door"
                : feature.Type.ToLowerInvariant();
            paint.Color = Resolve(colorOverrides, overrideKey, LayoutColors.FeatureColor(feature.Type));
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
