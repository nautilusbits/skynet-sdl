using System.Numerics;
using RetroEng.Render;
using SkyNet.Game.Enhanced;

namespace SkyNet.Game.Tests.Enhanced;

/// <summary>The enhanced look's light per map (S13): the presets, the moonlight's way, the dusk
/// sun's height band, and the water's blend colour in linear light.</summary>
public class LookLightingTests
{
    [Theory]
    [InlineData(210, true, LightPreset.Night)]
    [InlineData(250, true, LightPreset.Dusk)]
    [InlineData(271, true, LightPreset.Dusk)]
    [InlineData(211, false, LightPreset.Interior)]
    [InlineData(252, false, LightPreset.Interior)]
    public void PresetFollowsTheMap(int map, bool outdoor, LightPreset preset) =>
        Assert.Equal(preset, LookLighting.PresetOf(map, outdoor));

    [Fact]
    public void MoonlightComesDownFromTheMoon()
    {
        var light = LookLighting.MoonLightDirection();
        Assert.True(light.Y < 0f);
        Assert.True(Vector3.Dot(light, Level.MoonDirection()) < -0.999f);
    }

    [Fact]
    public void InteriorHasNoSunAndAnEvenAmbient()
    {
        var lighting = LookLighting.For(LightPreset.Interior, null, 3992f, 1.2f);
        Assert.Equal(Vector3.Zero, lighting.Sun);
        Assert.Equal(ShadowQuality.Off, lighting.ShadowQuality);
        Assert.Equal(LookLighting.IndoorAmbient * LookLighting.InteriorAmbientMood, lighting.Sky);
        Assert.Equal(LookLighting.IndoorAmbient * LookLighting.InteriorAmbientMood, lighting.Ground);
        Assert.Equal(Vector3.UnitY, lighting.Up);
        Assert.Equal(1.2f * LookLighting.ExposureMood, lighting.Exposure);
        Assert.Null(lighting.Stars);
        Assert.Equal(3992f, lighting.ShadowReach);
    }

    [Fact]
    public void DuskWithoutADomeFallsBackToTheMoonsWay()
    {
        var lighting = LookLighting.For(LightPreset.Dusk, null, 1944f, 1f);
        Assert.Equal(LookLighting.MoonLightDirection(), lighting.SunDirection);
        Assert.Equal(ShadowQuality.High, lighting.ShadowQuality);
    }

    [Theory]
    [InlineData(0f)]
    [InlineData(5f)]
    [InlineData(40f)]
    [InlineData(85f)]
    public void DuskSunStaysInItsBand(float elevationDeg)
    {
        var e = elevationDeg * MathF.PI / 180f;
        var clamped = LookLighting.ClampElevation(new Vector3(MathF.Cos(e), MathF.Sin(e), 0f));
        var degrees = MathF.Asin(clamped.Y) * 180f / MathF.PI;
        Assert.InRange(degrees, LookLighting.MinSunElevation - 0.01f, LookLighting.MaxSunElevation + 0.01f);
        Assert.InRange(clamped.Length(), 0.999f, 1.001f);
        Assert.True(clamped.X > 0f);
    }

    [Fact]
    public void SmokeRisesFromFiresAndBurningDrops()
    {
        var fire = new LampLight(new Vector3(0f, 35f, 0f), EmissiveLamps.FireColour, 900f, 1f, Flicker: true);
        var lamp = new LampLight(new Vector3(500f, 50f, 0f), Vector3.One, 900f, 1f, Flicker: false);
        var burning = EmissiveLamps.GodotFires.Keys.First();
        var sources = Atmosphere.SourcesOf([fire, lamp], [(burning, new Vector3(100f, 0f, 0f)), (0, new Vector3(200f, 0f, 0f))]).ToList();
        Assert.Equal(2, sources.Count);   // the fire and the burning drop; not the steady lamp or a plain drop
        Assert.Equal(100f, sources[0].Height);   // range / 9
        Assert.True(sources[0].Top.Y > fire.Position.Y);
        Assert.Equal(100f, sources[1].Top.X);
    }
}
