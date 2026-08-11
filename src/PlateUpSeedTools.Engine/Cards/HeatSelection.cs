using PlateUpSeedTools.Engine.Data;

namespace PlateUpSeedTools.Engine.Cards;

/// <summary>
/// Port of CreateNewKitchen.InitialiseHeat (RestaurantMode/Kitchen/CreateNewKitchen.cs): selecting
/// heat level N grants heat cards 1..N (or -1..-N for Chill) cumulatively, all auto-accepted with no
/// RNG - see game-domain-knowledge.md's "Heat levels are cumulative" section. Heat 3 grants Heat 1 +
/// Heat 2 + Heat 3's cards, not just Heat 3's.
/// </summary>
public static class HeatSelection
{
    public static List<int> ResolveGrantedCardIds(HeatLevelsData heatLevels, int level)
    {
        var ids = new List<int>();
        var sign = level < 0 ? -1 : 1;
        for (var i = 1; i <= Math.Abs(level); i++)
        {
            if (heatLevels.GetCardId(sign * i) is int id)
            {
                ids.Add(id);
            }
        }

        return ids;
    }
}

/// <summary>
/// Which heat cards register a bonus UnlockPack via an AddCardPackEffect when accepted (see
/// game-domain-knowledge.md point 8 of the "Card draw RNG" section) - hand-curated, not extracted,
/// since UnlockCard.Effects is polymorphic Odin-blob data not currently parsed generically (see
/// CardExtractor.WritePacksFile's doc comment for why the same tradeoff was made for pack internals).
/// Only Heat 2 ("Second Bite") and Heat 10 are currently known to do this - extend this table, not a
/// generic decoder, if another heat/setting card is found to grant a bonus pack.
/// </summary>
public static class HeatBonusPacks
{
    private static readonly Dictionary<int, int> PackIdByGrantingCardId = new()
    {
        [1382681650] = -1759859435, // Heat 2 "Second Bite" -> Heat - Food Card After Day 2 Pack
        [421796067] = 31750544, // Heat 10 -> Heat - Starter Or Dessert Pack
    };

    public static IEnumerable<int> ResolveBonusPackIds(IEnumerable<int> ownedCardIds) =>
        ownedCardIds.Where(PackIdByGrantingCardId.ContainsKey).Select(id => PackIdByGrantingCardId[id]);
}
