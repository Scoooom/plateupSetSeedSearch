namespace PlateUpSeedTools.Engine.Layouts;

/// <summary>Port of Kitchen.Layouts.LayoutFailureException - thrown when a layout attempt should be retried.</summary>
public class LayoutFailureException(string message) : Exception(message);
