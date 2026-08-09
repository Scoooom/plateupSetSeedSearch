namespace PlateUpSeedTools.Engine.Layouts;

/// <summary>Port of Kitchen.Layouts.LayoutPosition - an integer grid coordinate.</summary>
public readonly record struct LayoutPosition(int X, int Y)
{
    public static LayoutPosition operator +(LayoutPosition p1, LayoutPosition p2) => new(p1.X + p2.X, p1.Y + p2.Y);

    public static LayoutPosition operator -(LayoutPosition p1, LayoutPosition p2) => new(p1.X - p2.X, p1.Y - p2.Y);

    public static LayoutPosition operator -(LayoutPosition p) => new(-p.X, -p.Y);
}
