using System.Numerics;
using RetroEng.Render;
using SkyNet.Game.Enhanced;
using SkyNet.Game.Tests.Behaviour;

namespace SkyNet.Game.Tests.Enhanced;

public class MastLampsTests
{
    private static readonly MastLamps.Post Post = new(new Vector3(1000f, 700f, -2000f), 328f, 204 << 7 | 22);

    [Fact]
    public void ALightAtAPostsFootShinesDownFromItsHead()
    {
        var light = new MapLight(1, new Vector3(1010f, 724f, -2005f), Vector3.One, 1000f, 60f, 100, true);
        var heads = MastLamps.Heads(light, [Post]);
        Assert.NotNull(heads);
        var h = Assert.Single(heads);
        Assert.True(h.IsSpot);
        Assert.Equal(700f + 328f * MastLamps.HeadHeight, h.Position.Y, 1f);
        Assert.Equal(1000f, h.Position.X);
        Assert.True(h.SpotDirection.Y < -0.99f);
        Assert.True(h.Range >= 1000f);
    }

    [Fact]
    public void ALightAwayFromAnyPostStaysAsPlaced()
    {
        Assert.Null(MastLamps.Heads(new MapLight(1, new Vector3(1300f, 724f, -2000f), Vector3.One, 1000f, 60f, 100, true), [Post]));
        Assert.Null(MastLamps.Heads(new MapLight(1, new Vector3(1000f, 1100f, -2000f), Vector3.One, 1000f, 60f, 100, true), [Post]));
    }

    [Fact]
    public void TheHeadsFollowTheMapLightsSwitch()
    {
        var light = new MapLight(1, new Vector3(1000f, 724f, -2000f), Vector3.One, 1000f, 60f, 100, false);
        light = light with { Heads = MastLamps.Heads(light, [Post]) };
        var into = new List<PointLight>();
        new LevelLights([light], [], -1, []).Append(into, 0f, null);
        Assert.Empty(into);
        new LevelLights([light with { StartsOn = true }], [], -1, []).Append(into, 0f, null);
        Assert.True(Assert.Single(into).IsSpot);
    }

    /// <summary>MAP.210's base lamp posts (204/22) each carry their map light up to the head.</summary>
    [BehaviourDataFact]
    public void Map210sLampPostsAreLitFromTheirHeads()
    {
        var data = BehaviourData.Data;
        var map = data.LoadMap(210)!;
        var lights = LevelLights.MapLightsOf(map, data, 210);
        Assert.Equal(5, lights.Count(l => l.Heads != null));
        Assert.Equal(LevelLights.MapLightsOf(map).Count, lights.Count);
    }
}
