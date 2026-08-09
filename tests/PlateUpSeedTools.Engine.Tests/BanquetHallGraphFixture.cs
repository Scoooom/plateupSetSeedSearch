using PlateUpSeedTools.Engine.Layouts;
using PlateUpSeedTools.Engine.Layouts.Modules;

namespace PlateUpSeedTools.Engine.Tests;

/// <summary>
/// Banquet Hall's real layout graph (12 module types, 15 effective nodes - a 16th, "Require Feature
/// Count Even_0" guid 930ad1fd..., is present in the asset's node list but has a null script
/// reference and zero wiring, so it's dead/orphaned and excluded here), hand-transcribed from the
/// actual game assets in PlateUpSourceFiles/AssetRipper/.../MonoBehaviour/Banquet Layout Graph.asset
/// and its 16 referenced node assets. Node order matches the asset's `nodes:` list order exactly,
/// since LayoutGraphExecutor's requeue algorithm's execution order can depend on it.
///
/// This is a hand-built stand-in for what the M6 data extractor will eventually produce generically
/// from the YAML - it exists now to prove the module/executor/decorator port works end-to-end
/// against a real graph before the extractor exists.
/// </summary>
public static class BanquetHallGraphFixture
{
    public static LayoutGraphDefinition Build()
    {
        var output = new OutputNode { Id = "219b715b", InputId = "dbee480b" };

        List<LayoutModuleNode> nodes =
        [
            new RoomGridNode { Id = "219cef31", Width = 9, Height = 2, Type = RoomType.Dining, SetType = true },
            output,
            new PadWithRoomNode { Id = "170b36f8", InputId = "2e891d44", Type = RoomType.Kitchen, Above = 0, Left = 0, Right = 4, Below = 0 },
            new CreateFrontDoorNode { Id = "67f3fe7e", InputId = "d9040ad4", Type = RoomType.Dining, ForceFirstHalf = true },
            new AppendFeaturesNode { Id = "f3c81a05", InputId = "67f3fe7e", AppendFromIds = ["3afc5f75", "1c935c42"] },
            new FindAllFeaturesNode { Id = "4ad6b2b3", InputId = "d9040ad4", Feature = FeatureType.Door },
            new FilterByRoomNode { Id = "4a958b85", InputId = "4ad6b2b3", RemoveMode = false, Type1 = RoomType.Kitchen, FilterSecond = true, Type2 = RoomType.Dining },
            new FindAllFeaturesNode { Id = "b7663316", InputId = "d9040ad4", Feature = FeatureType.Hatch },
            new FilterByRoomNode { Id = "1c935c42", InputId = "b7663316", RemoveMode = false, Type1 = RoomType.Dining, FilterSecond = true, Type2 = RoomType.Kitchen },
            new RecentreLayoutNode { Id = "dbee480b", InputId = "f3c81a05" },
            new CreateDoubleDoorsNode { Id = "3afc5f75", InputId = "4a958b85", OffsetY = 0 },
            new MirrorNode { Id = "2e891d44", InputId = "79bf2327", IsVertical = true },
            new RandomExpandNode { Id = "79bf2327", InputId = "219cef31", MinX = 0, MinY = 0, MaxX = 4, MaxY = 5 },
            new RecentreLayoutNode { Id = "d9040ad4", InputId = "ebdb6f4a" },
            new RandomExpandNode { Id = "ebdb6f4a", InputId = "170b36f8", MinX = 0, MinY = 0, MaxX = 0, MaxY = 2 },
        ];

        return new LayoutGraphDefinition(nodes, output);
    }
}
