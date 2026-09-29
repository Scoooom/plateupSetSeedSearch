using PlateUpSeedTools.Engine.Data;

namespace PlateUpSeedTools.Engine.Cards;

/// <summary>
/// What a search's day-target/loose-target slot actually requires - either one exact card
/// (<see cref="ExactCard"/>, the original and only behavior before class targets) or any card of a
/// given <see cref="UnlockData.DishType"/> (<see cref="AnyOfClass"/>, e.g. "Any Side"). Unifying both
/// behind one interface lets SeedSearcher's Walk/Matches/Backtrack treat every target uniformly
/// instead of branching by kind at each call site.
/// </summary>
public abstract record TargetMatcher
{
    public abstract bool Matches(UnlockData card);

    public sealed record ExactCard(int CardId) : TargetMatcher
    {
        public override bool Matches(UnlockData card) => card.Id == CardId;
    }

    public sealed record AnyOfClass(int DishType) : TargetMatcher
    {
        public override bool Matches(UnlockData card) => card.Kind == "Dish" && card.DishType == DishType;
    }
}
