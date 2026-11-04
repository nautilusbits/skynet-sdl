using RetroEng.Platform;
using RetroEng.Render;
using SkyNet.Game.Enhanced;
using SkyNet.Game.Menu;

namespace SkyNet.Game.Tests.Enhanced;

/// <summary>The lit haze of the enhanced look (<see cref="Lighting.Volumetric"/>): dusty interiors,
/// a light haze outdoors; shadowed shafts on desktop, the closed form on phones; none with PARTICLES Off.</summary>
public class LookVolumetricTests
{
    [Theory]
    [InlineData(LightPreset.Interior)]
    [InlineData(LightPreset.Night)]
    [InlineData(LightPreset.Dusk)]
    public void Each_preset_has_its_own_haze(LightPreset preset)
    {
        var haze = LookLighting.VolumetricOf(preset, Os.Windows, ShadowQuality.High, ParticleLevel.High)!;
        var look = LookLighting.HazeOf(preset);
        Assert.Equal((look.Density, look.Colour, look.Anisotropy, look.Distance, look.Sun),
            (haze.Density, haze.Colour, haze.Anisotropy, haze.Distance, haze.Sun));
        Assert.Equal(VolumetricQuality.High, haze.Quality);
    }

    /// <summary>The interiors' dust is the densest; the outdoor haze light.</summary>
    [Fact]
    public void Interiors_are_dustier_than_the_night()
    {
        Assert.True(LookLighting.HazeOf(LightPreset.Interior).Density > LookLighting.HazeOf(LightPreset.Night).Density);
        Assert.True(LookLighting.HazeOf(LightPreset.Night).Density >= LookLighting.HazeOf(LightPreset.Dusk).Density);
    }

    [Theory]
    [InlineData(Os.Android, ShadowQuality.High, VolumetricQuality.Low)]
    [InlineData(Os.Android, ShadowQuality.Off, VolumetricQuality.Low)]
    [InlineData(Os.Windows, ShadowQuality.Low, VolumetricQuality.High)]
    // No shadowed lights to march through: the closed form.
    [InlineData(Os.Windows, ShadowQuality.Off, VolumetricQuality.Low)]
    [InlineData(Os.Linux, ShadowQuality.High, VolumetricQuality.High)]
    public void Phones_and_no_shadows_take_the_closed_form(Os os, ShadowQuality shadows, VolumetricQuality quality) =>
        Assert.Equal(quality, LookLighting.VolumetricOf(LightPreset.Interior, os, shadows, ParticleLevel.Low)!.Quality);

    [Theory]
    [InlineData(Os.Windows)]
    [InlineData(Os.Android)]
    public void Particles_off_turns_it_off(Os os) =>
        Assert.Null(LookLighting.VolumetricOf(LightPreset.Night, os, ShadowQuality.High, ParticleLevel.Off));
}
