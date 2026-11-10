using SkyNet.Formats;
using SkyNet.Game.Baking;
using SkyNet.Game.Enhanced;
using SkyNet.Game.Tests.Behaviour;

namespace SkyNet.Game.Tests.Baking;

/// <summary>Indoors DOS draws a map sprite centred on its record: v1.01 0x136f3f passes mode 1,
/// which the table at 0x6d100 sends to 0x14f0dc (half the width, half the height about the
/// point). Issue #59: Bishop (Future Shock MAP.151) floated 55 over his rack; issue #45: small
/// items sank into the floor.</summary>
public class SpriteAnchorTests
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

    /// <summary>Bishop's billboard on MAP.151 (202/18, record Y 2816).</summary>
    private const int Bishop = 0x9E7B;

    /// <summary>The lamp under the conveyor on Future Shock's MAP.010 (204/11, record Y 1089, #45).</summary>
    private const int ConveyorLamp = 0xFBC4;

    private static GameData Shock() =>
        GameData.Open(GameDataLocator.OtherGameDir(BehaviourData.Dir!)!, BehaviourData.Dir)!;

    [ShockDataFact]
    public void ShockBishopCentresOnRecord()
    {
        var block = MapBaker.Bake(Shock(), 151)!;

        var bishop = block.Sprites.SelectMany(g => g.Sprites).Single(s => s.Offset == Bishop);
        Assert.Equal(-2816f, bishop.Centre.Y, 0.01f);
    }

    // Outdoors too: the draw loop (0x136f18) has no ground lookup.
    [ShockDataFact]
    public void ShockOutdoorLampHangs()
    {
        var block = MapBaker.Bake(Shock(), 10)!;

        var lamp = block.Sprites.SelectMany(g => g.Sprites).Single(s => s.Offset == ConveyorLamp);
        Assert.Equal(1089f, lamp.Centre.Y, 0.01f);
    }

    // ENHANCED's bulb lamp of that lamp shines from its picture, not the ground (744 there).
    [ShockDataFact]
    public void ShockOutdoorBulbLampHangs()
    {
        var data = Shock();
        var lamps = EmissiveLamps.ForMap(data, data.LoadMap(10)!, 10);

        var bulb = lamps.Single(l => l.Entity == ConveyorLamp);
        Assert.InRange(bulb.Position.Y, 1089f - 32f, 1089f + 32f);
    }

    [ShockDataFact]
    public void ShockMap151CentresOnRecords() => AssertCentred(Shock(), 151);

    [BehaviourDataFact]
    public void SkyNetMap231CentresOnRecords() => AssertCentred(BehaviourData.Data, 231);

    // Every billboard of an indoor map: its centre is its record's point.
    private static void AssertCentred(GameData data, int number)
    {
        var map = data.LoadMap(number)!;
        Assert.False(map.IsOutdoor);
        var block = MapBaker.Bake(data, number)!;
        var byOffset = map.Entities.ToDictionary(e => e.FileOffset);

        var sprites = block.Sprites.SelectMany(g => g.Sprites).ToList();
        Assert.NotEmpty(sprites);
        Assert.All(sprites, s => Assert.Equal(-byOffset[s.Offset].Y, s.Centre.Y, 0.01f));
    }
}
