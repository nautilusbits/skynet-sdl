using System.Numerics;

namespace SkyNet.Game.Tests.Mods;

/// <summary>Issue #59: a sprite drawn as a mod's model follows its record's jump, as its billboard
/// does. Bishop's dead picture (202/19, a model in skynet-hd) stayed in the wall it starts in.</summary>
public class SpriteModelShiftTests
{
    [Fact]
    public void JumpedModelFootFollowsTheShift()
    {
        var centre = new Vector3(33169f, -2816f, -38300f);
        var size = new Vector2(63f, 143f);

        var foot = Level.ModelFoot(centre, size, new Vector3(0f, 0f, 512f), floorY: null);

        Assert.Equal(new Vector3(33169f, -2816f - 71.5f, -37788f), foot);
    }

    [Fact]
    public void ModelStandsOnTheFloorUnderItsRecord()
    {
        var foot = Level.ModelFoot(new Vector3(10f, 20f, 30f), new Vector2(8f, 10f), Vector3.Zero, floorY: 12f);

        Assert.Equal(new Vector3(10f, 12f, 30f), foot);
    }
}
