namespace PlateUpSeedTools.Engine.Layouts;

public static class FeatureTypeExtensions
{
    public static bool IsDoor(this FeatureType type) => type is FeatureType.Door or FeatureType.FrontDoor
        or FeatureType.DoorReversed or FeatureType.LightDoor or FeatureType.MissingDoor
        or FeatureType.EmployeesOnlyDoor or FeatureType.GreenhouseDoor or FeatureType.GarageDoor;

    public static bool IsReaching(this FeatureType type) => type.IsDoor() || type == FeatureType.Hatch;
}
