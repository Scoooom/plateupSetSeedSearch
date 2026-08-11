namespace PlateUpSeedTools.Engine.Cards;

/// <summary>Port of KitchenData.UnlockRequest.</summary>
public readonly record struct UnlockRequest(int Day, int Tier);

/// <summary>
/// Port of KitchenData.UnlockOptions. A single pack-evaluation result can offer up to two candidate
/// cards - the player picks one (see game-domain-knowledge.md's "genuinely a choice between TWO
/// cards" note). Both null means nothing was offered.
/// </summary>
public readonly record struct UnlockOptions(int? Unlock1Id, int? Unlock2Id);
