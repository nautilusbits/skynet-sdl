using System.Numerics;
using System.Reflection;
using RetroEng.Render;
using SkyNet.Game.Enhanced;

namespace SkyNet.Game.Tests.Enhanced;

public class GroundShadowTests
{
    // Night maps: the ground's 256-unit triangles lit by the smooth corner normals but shadowed by
    // their faceted selves showed dark triangles and bands along the cell edges (the shadow
    // terminator). The engine's terminator fade trusts the shadow less there, so the ground casts
    // the sun's shadow again (hills on the base). Render-free: the material is a private helper.
    [Fact]
    public void The_ground_casts_the_suns_shadow()
    {
        var make = typeof(Terrain).GetMethod("GroundMaterial", BindingFlags.NonPublic | BindingFlags.Static);

        Assert.NotNull(make);
        var ground = (Material)make.Invoke(null, [default(TextureHandle), default(TextureHandle)])!;
        Assert.False(ground.Shadowless);
        Assert.Equal(new Material(default, default, Vector4.One, 1, RenderState.DoubleSided, Shading.Lit), ground);
    }

    // Every enhanced preset fades the sun's shadow on faces lit by smoothed normals.
    [Theory]
    [InlineData(LightPreset.Night)]
    [InlineData(LightPreset.Dusk)]
    [InlineData(LightPreset.Interior)]
    public void Every_preset_fades_the_shadow_terminator(LightPreset preset)
    {
        var lighting = LookLighting.For(preset, null, 8000f, 1f);

        Assert.Equal(ShadowTerminator.Fade, lighting.ShadowTerminator);
    }
}
