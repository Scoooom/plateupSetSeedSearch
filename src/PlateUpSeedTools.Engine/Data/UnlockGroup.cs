namespace PlateUpSeedTools.Engine.Data;

/// <summary>
/// Named values for UnlockData.UnlockGroup, inferred from data/settings/cards/unlocks.json's
/// observed group->card mappings (no symbolic names found in the decompiled source/docs, so these
/// are descriptive labels, not verbatim game enum names): 0/1 are ordinary dish/card draws, 3 is the
/// 4-card Theme set (Exclusive/Charming/Formal/Affordable), 5 is the 40-card Franchise set (gated by
/// MinimumFranchiseTier/IsSpecificFranchiseTier), 6 is misc/heat-adjacent special cards.
/// </summary>
public enum UnlockGroup
{
    Regular = 0,
    Food = 1,
    Theme = 3,
    Franchise = 5,
    Misc = 6,
}
