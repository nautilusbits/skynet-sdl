using SkyNet.Formats;
using SkyNet.Game.Baking;
using SkyNet.Game.Campaign;
using SkyNet.Game.Tests.Behaviour;

namespace SkyNet.Game.Tests.Campaign;

/// <summary>Issue #21: an outdoor pickup stands where its record puts it, as indoors (the record
/// <see cref="Level.IndoorSpriteLift"/> over its foot), never under the ground. Items on roofs,
/// ruins and beams (Future Shock MAP.010, SkyNET MAP.230) were snapped down to the terrain.</summary>
public class PickupHeightTests
{
    /// <summary>A fact skipped unless Future Shock's data sits beside SKYNET_DATA's.</summary>
    private sealed class ShockDataFactAttribute : FactAttribute
    {
        public ShockDataFactAttribute()
        {
            if (BehaviourData.Dir is not { } dir || GameDataLocator.OtherGameDir(dir) == null)
            {
                Skip = "no Future Shock data beside SKYNET_DATA's SkyNET folder";
            }
        }
    }

    /// <summary>How far a foot may sit from where the record or the ground puts it.</summary>
    private const float Slack = 1f;

    private static GameData Shock() =>
        GameData.Open(GameDataLocator.OtherGameDir(BehaviourData.Dir!)!, BehaviourData.Dir)!;

    // The foot the record asks for: 16 under it, but not under the ground.
    private static float Expected(MapEntity e, WldTerrain wld) =>
        MathF.Max((float)wld.HeightAt(e.X, e.Z), -e.Y - Level.IndoorSpriteLift);

    [ShockDataFact]
    public void ShockMission1PickupsStandOnTheirRecords()
    {
        var data = Shock();
        var session = new GameSession(data);
        session.StartMission(session.Table.ByNumber(1)!.Value);
        var map = data.LoadMap(10)!;
        var wld = data.LoadWld(10)!;
        var byOffset = map.Entities.ToDictionary(e => e.FileOffset);

        var left = session.PickupsLeft.ToList();
        Assert.NotEmpty(left);
        Assert.All(left, p => Assert.Equal(Expected(byOffset[p.Offset], wld), p.At.Y, Slack));
    }

    [ShockDataFact]
    public void ShockMap010DrawsItemsOnTheirRecords() => AssertBaked(Shock(), 10);

    [BehaviourDataFact]
    public void SkyNetMap230DrawsItemsOnTheirRecords() => AssertBaked(BehaviourData.Data, 230);

    // Every baked item billboard's foot (centre less half its height) where its record puts it.
    private static void AssertBaked(GameData data, int number)
    {
        var block = MapBaker.Bake(data, number)!;
        var map = data.LoadMap(number)!;
        var wld = data.LoadWld(number)!;
        var byOffset = map.Entities.ToDictionary(e => e.FileOffset);

        var items = block.Sprites.Where(g => SpriteRules.IsItem(g.Index)).SelectMany(g => g.Sprites).ToList();
        Assert.NotEmpty(items);
        Assert.All(items, s => Assert.Equal(Expected(byOffset[s.Offset], wld), s.Centre.Y - s.Size.Y / 2f, Slack));
    }
}
