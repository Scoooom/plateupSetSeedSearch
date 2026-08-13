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
    // Palette chosen with the dataviz skill's validate_palette.js against this site's actual dark
    // background (#1b1410): all 14 pass the lightness-band/chroma-floor/contrast gates, one
    // adjacent CVD pair (Garden/Unassigned) sits in the 6-8 floor band, which the skill allows only
    // with secondary encoding - covered here by the always-on per-room RoomCodes label below.
    // Replaces a prior palette of uniformly muted/desaturated colors that real players reported
    // reading as "void/ignore" (most acutely Unassigned's old near-black #3a3a3a) even though
    // Unassigned tiles are confirmed-real walkable rooms, not padding - see
    // game-domain-knowledge.md's ground-truth notes.
    // Unassigned's hex was re-picked a second time (from an earlier #14a0b8) after a real player
    // reported it reading too close to Kitchen's blue - confirmed objectively (normal-vision ΔE 11.0,
    // below the skill's 15 floor, both landing in the same blue/cyan cluster as Garage). Moved to a
    // red-pink hue instead - normal-vision ΔE 30.1 from Kitchen, 30.5 from Garage - clear of every
    // other hue in this table.
    public static readonly IReadOnlyDictionary<string, SKColor> RoomColors = new Dictionary<string, SKColor>
    {
        ["Dining"] = SKColor.Parse("#d9622c"),
        ["Kitchen"] = SKColor.Parse("#3987e5"),
        ["Garden"] = SKColor.Parse("#3fae52"),
        ["Workshop"] = SKColor.Parse("#b06a2e"),
        ["Office"] = SKColor.Parse("#6b6bdb"),
        ["FrontEntrance"] = SKColor.Parse("#c9782a"),
        ["Locations"] = SKColor.Parse("#2f9e70"),
        ["Storage"] = SKColor.Parse("#9c5a2e"),
        ["Bedroom"] = SKColor.Parse("#8b7fe0"),
        ["Trophies"] = SKColor.Parse("#a8871a"),
        ["Garage"] = SKColor.Parse("#1aa8a8"),
        ["Contracts"] = SKColor.Parse("#b8873a"),
        ["Unassigned"] = SKColor.Parse("#d43d5c"),
        ["SocialSpace"] = SKColor.Parse("#d5548f"),
    };

    public static readonly SKColor UnassignedColor = RoomColors["Unassigned"];

    // Short room-identity codes drawn on every rendered room instance (LayoutImageRenderer) and
    // appended to legend labels (app.js) so identity never depends on color alone - real player
    // feedback specifically asked for colorblind-safe symbols, not just brighter colors. Hand-picked
    // rather than auto-abbreviated (first-letters would collide Garden/Garage).
    public static readonly IReadOnlyDictionary<string, string> RoomCodes = new Dictionary<string, string>
    {
        ["Dining"] = "DIN",
        ["Kitchen"] = "KIT",
        ["Garden"] = "GDN",
        ["FrontEntrance"] = "FNT",
        ["SocialSpace"] = "SOC",
        ["Bedroom"] = "BED",
        ["Garage"] = "GRG",
        ["Office"] = "OFF",
        ["Workshop"] = "WRK",
        ["Contracts"] = "CON",
        ["Trophies"] = "TRO",
        ["Locations"] = "LOC",
        ["Storage"] = "STO",
        ["Unassigned"] = "UNA",
    };

    public static string RoomCode(string roomType) =>
        RoomCodes.TryGetValue(roomType, out var code) ? code : "UNA";

    // Drawn by LayoutImageRenderer.DrawWalls for every tile boundary that isn't a door/hatch
    // opening - a real player asked for walls to read as explicit/solid the same way doors already
    // do, rather than the same thin hairline as every other tile-to-tile edge. A warm stone tone,
    // deliberately distinct from every door/hatch color above and from the room palette below (see
    // RoomColors) so it never gets mistaken for a room fill or an opening.
    public static readonly SKColor WallColor = SKColor.Parse("#b8a688");

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
        // Floor raised from the old 28 to 50 (ceiling correspondingly to 82) - with the brighter room
        // palette (LayoutColors.RoomColors), a -12/-24 shift used to collapse a shaded instance into a
        // near-black variant (confirmed visually: a second Unassigned room rendered near-navy,
        // defeating the whole point of brightening Unassigned; the +12 hue rotation per index can also
        // land on a hue - e.g. teal rotated into blue/indigo - that reads noticeably darker than its
        // raw HSL lightness number suggests, so the floor needs real headroom, not just a few points
        // above the old one). The hue rotation itself already carries the distinctness between
        // instances, so this floor doesn't need to reach anywhere near black.
        l = Math.Clamp(l + lShift, 50f, 82f);
        return SKColor.FromHsl(h, s, l);
    }
}
