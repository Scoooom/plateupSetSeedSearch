namespace PlateUpSeedTools.Engine.Data;

/// <summary>
/// JSON shape for one entry in data/settings/cards/unlocks.json - mirrors the fields KitchenData.Unlock
/// (+ Dish's extra fields) that the card-draw simulator needs. See game-domain-knowledge.md's
/// "Starting dish / heat / card-draw RNG" section for the mechanics these fields drive.
/// </summary>
public sealed class UnlockData
{
    public required int Id { get; init; }

    public required string Name { get; init; }

    /// <summary>"Dish" or "UnlockCard".</summary>
    public required string Kind { get; init; }

    public required int UnlockGroup { get; init; }

    public required int CardType { get; init; }

    public required bool IsUnlockable { get; init; }

    public required bool HasSubOptions { get; init; }

    public int? OptionCard1Id { get; init; }

    public int? OptionCard2Id { get; init; }

    public required int MinimumFranchiseTier { get; init; }

    public required bool IsSpecificFranchiseTier { get; init; }

    public required bool BlocksAllOtherFood { get; init; }

    public required List<int> AllowedFoodIds { get; init; }

    public required List<int> RequiresIds { get; init; }

    public required List<int> BlockedByIds { get; init; }

    public required int HeatCardDisplayedNumber { get; init; }

    /// <summary>DishType enum value (Base=0, Main=1, Extra=2, Side=3, Starter=4, Dessert=5, Null=6) - only set when Kind == "Dish".</summary>
    public int? DishType { get; init; }

    /// <summary>Distinct MenuPhase values (Starter=0, Main=1, Dessert=2, Side=3, Complete=4) this dish's ResultingMenuItems provide - only set when Kind == "Dish".</summary>
    public List<int>? MenuPhases { get; init; }

    public bool IsBaseDish => Kind == "Dish" && DishType == 0;

    public bool ProvidesPhase(int phase) => MenuPhases?.Contains(phase) ?? false;
}
