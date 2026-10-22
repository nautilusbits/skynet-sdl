using System.Numerics;
using RetroEng.Render;
using SkyNet.Game.Enhanced;

namespace SkyNet.Edit.Tests;

/// <summary>The asset viewers' own scene (RetroEng 0.25 view targets with their own content): the subject at the
/// origin, a neutral studio light in the enhanced look (no level haze, stars or clouds, the window's shadow quality),
/// unlit in the original look, and the effects' own lights.</summary>
public sealed class ViewerStudioTests
{
    private static Lighting Frame(ShadowQuality shadows) =>
        new(-Vector3.UnitY, Vector3.One, Vector3.One, Vector3.One, shadows, 600f, 0.1f, new Vector4(0.2f, 0.2f, 0.3f, 1f), 0.002f)
        {
            Stars = new StarField(1f, Vector3.UnitY, Vector3.UnitY, 0.1f),
        };

    [Fact]
    public void The_subject_stands_at_the_origin_not_far_out()
    {
        Assert.Equal(Vector3.Zero, ViewerStudio.Subject);
    }

    [Fact]
    public void The_original_look_stays_unlit()
    {
        var scene = ViewerStudio.Scene(null, 1000f);
        Assert.Null(scene.Lighting);
        Assert.Null(scene.Fog);
        Assert.Equal(0, scene.Lights.Length);
    }

    [Theory]
    [InlineData(ShadowQuality.Off)]
    [InlineData(ShadowQuality.Low)]
    [InlineData(ShadowQuality.High)]
    public void The_enhanced_look_gets_a_studio_light_without_the_levels_sky(ShadowQuality shadows)
    {
        PointLight[] lights = [new(Vector3.UnitY * 50f, Vector3.One, 500f, 1f)];
        var scene = ViewerStudio.Scene(Frame(shadows), 1000f, lights);
        var lit = Assert.IsType<Lighting>(scene.Lighting);

        Assert.Equal(shadows, lit.ShadowQuality);
        Assert.Equal(0f, lit.HazeDensity);
        Assert.Null(lit.Stars);
        Assert.Null(lit.Clouds);
        Assert.Null(scene.Fog);
        Assert.Equal(Vector3.UnitY, lit.Up);
        Assert.True(lit.Sun.X > 0.5f && lit.Sky.X > 0f, "a key light and a soft ambient");
        Assert.Equal(lights, scene.Lights.ToArray());
    }

    [Fact]
    public void A_fire_lights_its_floor_and_smoke_has_no_light()
    {
        var fire = Assert.Single(ViewerStudio.EffectLights(EffectTable.Fire, EffectPreview.DefaultScale, 0.5f));
        Assert.True(fire.Position.Y > 0f && fire.Position.X == 0f && fire.Position.Z == 0f, "over the fire's foot");
        Assert.True(fire.Colour.X > fire.Colour.Z, "warm");
        Assert.Empty(ViewerStudio.EffectLights(EffectTable.Smoke, EffectPreview.DefaultScale, 0.5f));
        Assert.Single(ViewerStudio.EffectLights(EffectTable.Explosion, EffectPreview.DefaultScale, 0.1f));
    }
}
