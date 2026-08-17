namespace PlateUpSeedTools.Engine.Shop;

/// <summary>
/// Tracks which appliances are currently PLACED in the kitchen - this, not a purchase ledger, is what
/// "ownership" means throughout the shop system (confirmed from TagStapleWhenMissing.cs/
/// FilterByRequiredAppliances.cs: both query live CAppliance entities, not any kind of history). Grows
/// via <see cref="PlaceAppliance"/> and shrinks via <see cref="TossAppliance"/> - deliberately mutable
/// both ways, unlike CardDrawSimulator's owned-set (which only ever grows), because a future search
/// phase needs to sequence buy/toss actions per day to reproduce strategies like "buy on day 2, toss
/// on day 3 to re-trigger a StapleWhenMissing appliance's staple status for day 4."
/// </summary>
public sealed class KitchenApplianceState
{
    private readonly Dictionary<int, int> _placedCounts = new();

    public KitchenApplianceState(IEnumerable<int>? initiallyPlacedIds = null)
    {
        foreach (var id in initiallyPlacedIds ?? [])
        {
            PlaceAppliance(id);
        }
    }

    /// <summary>Places one more instance of applianceId (buying a shop offer, or an initial-kitchen appliance).</summary>
    public void PlaceAppliance(int applianceId) =>
        _placedCounts[applianceId] = _placedCounts.GetValueOrDefault(applianceId) + 1;

    /// <summary>
    /// Removes one placed instance (sell/toss). Throws if applianceId isn't currently placed - a
    /// simulated toss of something not actually there indicates a bug in the caller's action sequence,
    /// not a state this should silently tolerate.
    /// </summary>
    public void TossAppliance(int applianceId)
    {
        if (!_placedCounts.TryGetValue(applianceId, out var count) || count <= 0)
        {
            throw new InvalidOperationException($"Cannot toss appliance {applianceId} - none currently placed.");
        }

        if (count == 1)
        {
            _placedCounts.Remove(applianceId);
        }
        else
        {
            _placedCounts[applianceId] = count - 1;
        }
    }

    /// <summary>"Owned" for staple/duplicate-filter purposes = has &gt;=1 instance placed right now.</summary>
    public bool IsPlaced(int applianceId) => _placedCounts.GetValueOrDefault(applianceId) > 0;

    public IReadOnlySet<int> PlacedApplianceIds => _placedCounts.Keys.ToHashSet();
}
