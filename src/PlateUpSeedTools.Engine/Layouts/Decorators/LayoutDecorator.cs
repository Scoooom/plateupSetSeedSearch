using PlateUpSeedTools.Engine.Random;

namespace PlateUpSeedTools.Engine.Layouts.Decorators;

/// <summary>
/// Port of Kitchen.LayoutDecorator.AttemptDecoration. Deliberately omits the per-setting custom
/// decorators loop (RestaurantSetting.Decorators, e.g. BanquetDecorator's Christmas exterior props) -
/// that data lives in an Odin/BinaryFormatter-serialized blob not yet decoded (see technical_findings
/// memory), and skipping it only drops cosmetic extras, not room/appliance placement.
/// </summary>
public static class LayoutDecorator
{
    public static List<AppliancePlacement> AttemptDecoration(LayoutBlueprint blueprint, LayoutProfileInfo profile, UnityRandom rng)
    {
        var decorations = new List<AppliancePlacement>();

        foreach (var room in blueprint.Rooms())
        {
            if (room.Type == RoomType.Dining)
            {
                var attempts = 5;
                var success = false;
                while (attempts > 0 && !success)
                {
                    attempts--;
                    success = DiningDecorator.Decorate(room, blueprint, profile, rng, decorations);
                }

                if (!success)
                {
                    throw new LayoutFailureException("Failed to decorate dining room");
                }
            }

            if (room.Type == RoomType.Kitchen)
            {
                KitchenDecorator.Decorate(room, blueprint, profile, rng, decorations);
            }
        }

        return decorations;
    }
}
