using PlateUpSeedTools.Engine.Data;

namespace PlateUpSeedTools.Engine.Cards;

/// <summary>
/// Enforces the search UI's two card-selection caps, shared between client-facing candidate-list
/// endpoints (pre-filtering what a user can even pick) and server-side search validation (defense in
/// depth for non-JS callers): at most one Theme card (UnlockGroup.Theme) across the WHOLE selection
/// (day-target picks and franchise-carryover rows combined - Theme cards are reachable from both,
/// via the day-5 guarantee or as a carryover pick), and at most one Franchise card
/// (UnlockGroup.Franchise) per distinct MinimumFranchiseTier value.
/// </summary>
public static class SelectionCapValidator
{
    /// <summary>Returns a user-facing error message if the combined selection violates a cap, else null.</summary>
    public static string? Validate(GameDataRepository repo, IEnumerable<int> selectedCardIds)
    {
        var themeCount = 0;
        var tiersSeen = new HashSet<int>();
        foreach (var id in selectedCardIds)
        {
            if (!repo.TryGetUnlock(id, out var card))
            {
                continue;
            }

            switch ((UnlockGroup)card.UnlockGroup)
            {
                case UnlockGroup.Theme:
                    themeCount++;
                    if (themeCount > 1)
                    {
                        return "Only one Theme card may be selected.";
                    }

                    break;
                case UnlockGroup.Franchise:
                    if (!tiersSeen.Add(card.MinimumFranchiseTier))
                    {
                        return $"Only one Franchise card per tier may be selected (tier {card.MinimumFranchiseTier} was selected more than once).";
                    }

                    break;
            }
        }

        return null;
    }
}
