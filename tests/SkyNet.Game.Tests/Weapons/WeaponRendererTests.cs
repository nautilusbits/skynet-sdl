using System.Numerics;
using System.Reflection;
using RetroEng.Render;
using SkyNet.Game.Weapons;

namespace SkyNet.Game.Tests.Weapons;

public class WeaponRendererTests
{
    // Issue #4: a bolt's model (laser, plasma, rocket) carries its own light; drawn Lit it casts
    // the sun's shadow. Lit glows; Material.Shadowless keeps it out of the shadow pass.
    // Render-free: the shading is a private constant and the part's material a private helper
    // (a Renderer needs a window).
    [Fact]
    public void A_bolts_model_glows_and_casts_no_shadow()
    {
        var field = typeof(WeaponRenderer).GetField("BoltShading", BindingFlags.NonPublic | BindingFlags.Static);
        var glow = typeof(WeaponRenderer).GetMethod("Shadowless", BindingFlags.NonPublic | BindingFlags.Static);

        Assert.NotNull(field);
        Assert.Equal(Shading.Lit, (Shading)field.GetValue(null)!);
        Assert.NotNull(glow);
        var part = new ModelMesh.Part(0, 3, new Material(default, default, Vector4.One, 1, RenderState.DoubleSided, Shading.Lit), null);
        var bolt = (ModelMesh.Part)glow.Invoke(null, [part])!;
        Assert.True(bolt.Material.Shadowless);
        Assert.Equal(part with { Material = part.Material with { Shadowless = true } }, bolt);
    }
}
