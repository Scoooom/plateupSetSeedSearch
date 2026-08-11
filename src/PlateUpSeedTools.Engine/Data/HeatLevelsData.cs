namespace PlateUpSeedTools.Engine.Data;

/// <summary>
/// JSON shape for data/settings/cards/heat-levels.json - the default Heat/Chill card ladder
/// (HeatLevels.Cards/ChillCards). HeatCardIds[0] = Heat 1's card, ..., HeatCardIds[14] = Heat 15's;
/// ChillCardIds[0] = Chill 1's card, ..., ChillCardIds[2] = Chill 3's. See HeatLevels.GetCard.
/// </summary>
public sealed class HeatLevelsData
{
    public required List<int> HeatCardIds { get; init; }

    public required List<int> ChillCardIds { get; init; }

    /// <summary>Port of HeatLevels.GetCard(int) - level &gt; 0 for Heat, &lt; 0 for Chill, 0 = none.</summary>
    public int? GetCardId(int level)
    {
        if (level == 0)
        {
            return null;
        }

        return level < 0
            ? (ChillCardIds.Count < -level ? null : ChillCardIds[-level - 1])
            : (HeatCardIds.Count < level ? null : HeatCardIds[level - 1]);
    }
}
