using System.Numerics;
using RetroEng.Render;
using RetroEng.Render.Lights;
using SkyNet.Game.Enhanced;

namespace SkyNet.Game.Tests.Enhanced;

/// <summary>A weather's haze and dust dim the lamps, never put them out: what the depth cue hides of
/// a lamp comes back as its glow.</summary>
public class LampGlowTests
{
    private static readonly Vector3 Eye = new(0f, 100f, 0f);

    /// <summary>MAP.210's lamp post: a floodlight 300 u up, spot down (<see cref="MastLamps"/>).</summary>
    private static readonly PointLight Mast = new PointLight(new Vector3(0f, 300f, -1500f), new Vector3(1f, 0.7f, 0.4f), 2000f,
        LightFalloff.EnergyAtRef(MastLamps.IntensityAtRef)).AsSpot(-Vector3.UnitY, MastLamps.Inner, MastLamps.Outer);

    /// <summary>A dust cloud's full core: the cue closes to 6 m (<see cref="WeatherSky.Dusted"/>).</summary>
    private static readonly Fog Core = new(10f, WeatherSky.DustCoreMetres * Storm.UnitsPerMetre, 63, 0)
    {
        Exponent = WeatherSky.HazeCurve,
        Beyond = FogBeyond.Hold,
        Colour = new Vector4(0.2f, 0.17f, 0.14f, 1f),
    };

    private static float Brightness(ReadOnlySpan<Particle> glow)
    {
        var sum = 0f;
        foreach (var p in glow)
        {
            sum += new Vector3(p.Colour.X, p.Colour.Y, p.Colour.Z).Length();
        }

        return sum;
    }

    [Fact]
    public void ALampInADustCoreStillGlows()
    {
        var glow = new LampGlow().Of([Mast], Core, Eye, 20000f);
        Assert.True(Brightness(glow) > 0.1f);
    }

    [Fact]
    public void DenserDustShowsMoreGlowThanThinner()
    {
        var thin = Core with { Start = 1000f, End = 4000f };
        var dense = new LampGlow().Of([Mast], Core, Eye, 20000f).ToArray();
        var light = new LampGlow().Of([Mast], thin, Eye, 20000f).ToArray();
        Assert.True(Brightness(dense) > Brightness(light));
    }

    [Fact]
    public void ClearAirAddsNoGlow()
    {
        var clear = Core with { Start = 5000f, End = 9000f };
        Assert.Equal(0, new LampGlow().Of([Mast], clear, Eye, 20000f).Length);
        Assert.Equal(0, new LampGlow().Of([Mast], null, Eye, 20000f).Length);
    }

    [Fact]
    public void NoGlowPastAClippedCueOrTheReach()
    {
        Assert.Equal(0, new LampGlow().Of([Mast], Core with { Beyond = FogBeyond.Clip }, Eye, 20000f).Length);
        Assert.Equal(0, new LampGlow().Of([Mast], Core, Eye, 1000f).Length);
    }
}
