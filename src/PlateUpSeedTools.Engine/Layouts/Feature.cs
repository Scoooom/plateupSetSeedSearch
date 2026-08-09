namespace PlateUpSeedTools.Engine.Layouts;

/// <summary>Port of Kitchen.Layouts.Features.Feature - a door/hatch/etc. between two adjacent tiles.</summary>
public sealed class Feature(LayoutPosition tile1, LayoutPosition tile2, FeatureType type)
{
    public LayoutPosition Tile1 { get; set; } = tile1;

    public LayoutPosition Tile2 { get; set; } = tile2;

    public FeatureType Type { get; set; } = type;

    public Feature(Feature copy)
        : this(copy.Tile1, copy.Tile2, copy.Type)
    {
    }
}
