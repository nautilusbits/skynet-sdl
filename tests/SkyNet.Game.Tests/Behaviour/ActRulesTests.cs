using SkyNet.Game.Behaviour;

namespace SkyNet.Game.Tests.Behaviour;

/// <summary>The act table as data (no game data needed).</summary>
public class ActRulesTests
{
    [Theory]
    [InlineData(0x41, MoverFamily.Slide, 0, 128f, 1f, 70f)]      // BIGDOOR leaf
    [InlineData(0x42, MoverFamily.Slide, 0, 128f, -1f, 70f)]
    [InlineData(0x63, MoverFamily.Slide, 1, 2048f, 1f, 140f)]    // long travel runs fast
    [InlineData(0xa5, MoverFamily.Slide, 1, 688f, 1f, 70f)]      // MAP.233's lift
    [InlineData(0x5f, MoverFamily.Slide5F, 2, 2048f, 1f, 316f * 2.2f)]
    [InlineData(0xa9, MoverFamily.Swing, 1, 512f, 1f, 512f)]
    [InlineData(0x37, MoverFamily.Rot, 1, 1024f, 1f, 0f)]        // wall monitor: an instant half turn
    [InlineData(0x3b, MoverFamily.Rot, 1, 2048f, 1f, 153f)]      // continuous rotator
    [InlineData(0x31, MoverFamily.Jump, 0, 512f, -1f, 0f)]       // signed p6: 65024 = −512 … two flips
    [InlineData(0xbd, MoverFamily.Slide, 0, 0f, 1f, 70f)]        // zero slide
    public void MoverParamsFollowTheTable(int act, MoverFamily family, int axis, float span, float sign, float speed)
    {
        var p = ActRules.Mover(act);
        Assert.Equal(family, p.Family);
        Assert.Equal(axis, p.Axis);
        Assert.Equal(span, p.Span);
        Assert.Equal(sign, p.Sign);
        Assert.Equal(speed, p.Speed, 0.01f);
    }

    [Fact]
    public void KindsAndOneShotRules()
    {
        Assert.Equal(ActKind.Hint, ActRules.For(0x1C).Kind);
        Assert.Equal(OnFire.Retire, ActRules.For(0x26).OnFire);
        Assert.Equal(ActKind.Fail, ActRules.For(0x2B).Kind);
        Assert.Equal(40, ActRules.For(0xDB).P4);
        Assert.Equal(OnFire.Force1, ActRules.For(ActRules.ProxGate).OnFire);
        Assert.Equal(ActKind.Light, ActRules.For(0x01, 2).Kind);
        // A light act on a mesh is not a light.
        Assert.Equal(ActKind.Inert, ActRules.For(0x01, 1).Kind);
        Assert.Equal(ActKind.Undecoded, ActRules.For(0x60).Kind);
        Assert.Equal(OnFire.Swap, ActRules.For(0xd9).OnFire);
    }
}
