using RetroEng.Render;
using SkyNet.Game.Enhanced;

namespace SkyNet.Game.Tests.Enhanced;

/// <summary>The baked bounced light's strength at run time (<see cref="Lighting.IrradianceScale"/>)
/// and the darker auto exposure: the owner found the maps too evenly lit.</summary>
public class LookBounceTests
{
    [Theory]
    [InlineData(LightPreset.Interior)]
    [InlineData(LightPreset.Night)]
    [InlineData(LightPreset.Dusk)]
    public void Ambient_scales_the_bounce(LightPreset preset)
    {
        Assert.Equal(LookLighting.BounceOf(preset), LookLighting.For(preset, null, 4000f, 1f).IrradianceScale);
        Assert.Equal(LookLighting.BounceOf(preset) * 0.5f, LookLighting.For(preset, null, 4000f, 1f, ambient: 0.5f).IrradianceScale, 1e-6f);
    }

    /// <summary>The DOS art bounces little (albedo ≈ 0.13): interiors lift it, less than the ×8
    /// first baked, so the lamps light the room, not the bounce.</summary>
    [Fact]
    public void Interiors_lift_the_bounce_a_little()
    {
        Assert.InRange(LookLighting.BounceOf(LightPreset.Interior), 1f, 4f);
        Assert.Equal(1f, LookLighting.BounceOf(LightPreset.Night));
    }

    /// <summary>A dark room stays dark: the exposure lifts it at most this much.</summary>
    [Theory]
    [InlineData(LightPreset.Interior)]
    [InlineData(LightPreset.Night)]
    [InlineData(LightPreset.Dusk)]
    public void Dark_rooms_stay_dark(LightPreset preset) =>
        Assert.True(LookLighting.ExposureOf(preset).Max <= 1.25f);
}
