using System.Numerics;
using System.Reflection;
using RetroEng.Render;
using SkyNet.Game.Menu;
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

    // Issue #63: ORIGINAL draws every bolt's model at DOS's 1:1 (the 2x fatten was the Godot
    // port's); ENHANCED draws a laser as a thin, long streak that shines by itself (a bright
    // unlit core into the glow), not a lit ice shard. Rockets keep their lit model.
    [Theory]
    [InlineData("LASER1.3D")]
    [InlineData("laser2.3d")]
    [InlineData("LASER3.3D")]
    public void An_enhanced_laser_is_a_thin_glowing_streak(string model)
    {
        var lit = new Material(default, default, Vector4.One, 1, RenderState.DoubleSided, Shading.Lit) { Shadowless = true };

        Assert.Equal(Vector2.One, BoltScale(model, Look.Original));
        Assert.Equal(lit, BoltMaterial(lit, model, Look.Original));

        var scale = BoltScale(model, Look.Enhanced);
        Assert.True(scale.X < 1f, "thinner than DOS's model");
        Assert.True(scale.Y > 1f, "longer along its flight");
        var core = BoltMaterial(lit, model, Look.Enhanced);
        Assert.Equal(Shading.Unlit, core.Shading);
        Assert.True(core.Colour.X > 1f && core.Colour.Y > 1f && core.Colour.Z > 1f, "brighter than its texels: the glow takes it");
    }

    [Fact]
    public void A_rocket_keeps_its_model_in_both_looks()
    {
        var lit = new Material(default, default, Vector4.One, 1, RenderState.DoubleSided, Shading.Lit) { Shadowless = true };

        foreach (var look in new[] { Look.Original, Look.Enhanced })
        {
            Assert.Equal(Vector2.One, BoltScale("ROCKET.3D", look));
            Assert.Equal(lit, BoltMaterial(lit, "ROCKET.3D", look));
        }
    }

    // The private helpers, render-free (a Renderer needs a window).
    private static Vector2 BoltScale(string model, Look look) =>
        (Vector2)Private(nameof(BoltScale)).Invoke(null, [model, look])!;

    private static Material BoltMaterial(Material material, string model, Look look) =>
        (Material)Private(nameof(BoltMaterial)).Invoke(null, [material, model, look])!;

    private static MethodInfo Private(string name) =>
        typeof(WeaponRenderer).GetMethod(name, BindingFlags.NonPublic | BindingFlags.Static)
        ?? throw new MissingMethodException(nameof(WeaponRenderer), name);
}
