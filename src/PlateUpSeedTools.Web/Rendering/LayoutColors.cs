using SkiaSharp;

namespace PlateUpSeedTools.Web.Rendering;

/// <summary>
/// Server-side port of the color tables that otherwise only exist client-side
/// (wwwroot/css/theme.css's --room-* custom properties and wwwroot/js/app.js's FEATURE_COLORS/
/// shadeColor). Kept in sync with those by convention, not by a shared source file - the same
/// tradeoff this codebase already accepts for FEATURE_COLORS vs theme.css.
/// </summary>
public static class LayoutColors
{
    // Mirrors theme.css:10-23. Keyed by RoomType.ToString() (TileDto.RoomType's exact format).
    public static readonly IReadOnlyDictionary<string, SKColor> RoomColors = new Dictionary<string, SKColor>
    {
        ["Dining"] = SKColor.Parse("#b5542f"),
        ["Kitchen"] = SKColor.Parse("#4c7a8a"),
        ["Garden"] = SKColor.Parse("#4f7d4a"),
        ["FrontEntrance"] = SKColor.Parse("#8a7a4c"),
        ["SocialSpace"] = SKColor.Parse("#8a4c7a"),
        ["Bedroom"] = SKColor.Parse("#5a5a8a"),
        ["Garage"] = SKColor.Parse("#6b6b6b"),
        ["Office"] = SKColor.Parse("#4c6b8a"),
        ["Workshop"] = SKColor.Parse("#8a5a2f"),
        ["Contracts"] = SKColor.Parse("#7a6a4c"),
        ["Trophies"] = SKColor.Parse("#a68a2f"),
        ["Locations"] = SKColor.Parse("#4c8a6b"),
        ["Storage"] = SKColor.Parse("#5a4c3a"),
        ["Unassigned"] = SKColor.Parse("#3a3a3a"),
    };

    public static readonly SKColor UnassignedColor = RoomColors["Unassigned"];

    // Mirrors app.js:32-38's FEATURE_COLORS. Keyed by FeatureDto.Type.ToLowerInvariant().
    private static readonly IReadOnlyDictionary<string, SKColor> FeatureColors = new Dictionary<string, SKColor>
    {
        ["frontdoor"] = SKColor.Parse("#ffe066"),
        ["door"] = SKColor.Parse("#f1e6d8"),
        ["doorreversed"] = SKColor.Parse("#f1e6d8"),
        ["hatch"] = SKColor.Parse("#7fd4c1"),
    };

    private static readonly SKColor DefaultFeatureColor = SKColor.Parse("#9a8a76");

    // Panel color used for hairline separators between tiles, replacing the CSS .grid gap (theme.css:286-287)
    // that the image can no longer rely on - see the interactive-page alignment note in app.js's
    // renderLayout for why gap had to move here.
    public static readonly SKColor PanelBorder = SKColor.Parse("#3a2c20");

    public static SKColor FeatureColor(string type) =>
        FeatureColors.TryGetValue(type.ToLowerInvariant(), out var color) ? color : DefaultFeatureColor;

    public static SKColor RoomColor(string roomType) =>
        RoomColors.TryGetValue(roomType, out var color) ? color : UnassignedColor;

    /// <summary>
    /// Port of app.js:142-153's shadeColor - derives a visually distinct variant of a base room
    /// color for the Nth (0-based) separate physical room of the same type. index 0 returns the
    /// base color unchanged. See app.js's doc comment for why saturation is floored rather than
    /// just shifted (near-neutral bases like Unassigned need it to make the hue shift visible).
    /// </summary>
    public static SKColor ShadeColor(SKColor baseColor, int index)
    {
        if (index == 0)
        {
            return baseColor;
        }

        baseColor.ToHsl(out var h, out var s, out var l);
        h = (h + 0.13f * 360f * index) % 360f;
        if (h < 0)
        {
            h += 360f;
        }
        s = Math.Max(s, 40f);
        var lShift = (index % 2 == 0 ? 12f : -12f) * MathF.Ceiling(index / 2f);
        l = Math.Clamp(l + lShift, 28f, 72f);
        return SKColor.FromHsl(h, s, l);
    }
}
