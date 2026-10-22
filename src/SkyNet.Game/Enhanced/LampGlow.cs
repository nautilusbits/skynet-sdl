using System.Numerics;
using RetroEng.Render;
using RetroEng.Render.Lights;

namespace SkyNet.Game.Enhanced;

/// <summary>
/// Lamps seen through a weather's haze or dust. The renderer's depth cue mixes a pixel's whole lit
/// colour, the lamps' light with it, to one flat colour, so in a dust cloud's core (the cue ending
/// at 6 m) every lamp past it was gone. Here the share the cue hides of each light comes back as
/// its light scattered in the air: unhazed additive glows, brighter the thicker the air.
/// <code>
///   hidden = the cue's level at the distance (0 clear .. 1 past its end; the shader's curve)
///   halo   at the light,                      HaloSize u,                 colour × intensity × HaloGain × hidden
///   pool   PoolDrop u down a spot (omni: at it), range × PoolShare ≤ PoolMax, colour × intensity × PoolGain × hidden
/// </code>
/// None without a haze or dust, in clear air (nothing hidden), past a cue that clips, or past the reach.
/// </summary>
public sealed class LampGlow
{
    /// <summary>A light this little hidden shows no glow.</summary>
    public const float MinHidden = 0.02f;
    /// <summary>The halo round the light: its size (units) and its gain on the light's intensity at 150 u.</summary>
    public const float HaloSize = 120f, HaloGain = 0.08f;
    /// <summary>The glow over its pool: how far down a spot (units), its size and its gain.</summary>
    public const float PoolDrop = 250f, PoolShare = 0.4f, PoolMax = 900f, PoolGain = 0.03f;

    private Particle[] _particles = new Particle[64];

    /// <summary>The glow of <paramref name="lights"/> seen from <paramref name="eye"/> through
    /// <paramref name="fog"/>, within <paramref name="reach"/>: valid until the next call.</summary>
    public ReadOnlySpan<Particle> Of(IReadOnlyList<PointLight> lights, Fog? fog, Vector3 eye, float reach)
    {
        var n = 0;
        if (fog == null)
        {
            return _particles.AsSpan(0, 0);
        }

        var unit = LightFalloff.EnergyAtRef(1f);
        foreach (var light in lights)
        {
            if (light.Energy <= 0f || Vector3.Distance(light.Position, eye) > reach)
            {
                continue;
            }

            var intensity = light.Energy / unit;
            Add(ref n, fog, eye, light.Position, HaloSize, light.Colour * (intensity * HaloGain));

            var pool = light.IsSpot ? light.Position + light.SpotDirection * PoolDrop : light.Position;
            Add(ref n, fog, eye, pool, MathF.Min(light.Range * PoolShare, PoolMax), light.Colour * (intensity * PoolGain));
        }

        return _particles.AsSpan(0, n);
    }

    /// <summary>Queues the glow (<see cref="Of"/>) on <paramref name="renderer"/>, through its depth
    /// cue: only in <paramref name="air"/>'s haze or dust (the map's own cue stays as DOS drew it).</summary>
    public void Draw(Renderer renderer, AirView air, IReadOnlyList<PointLight> lights, Vector3 eye, float reach)
    {
        if (air.Haze == null && air.Dust == null)
        {
            return;
        }

        var glow = Of(lights, renderer.Fog, eye, reach);
        if (glow.Length > 0)
        {
            renderer.DrawParticles(glow, ParticleBlend.Add, PoolMax * 0.5f, false);
        }
    }

    /// <summary>The cue's level at <paramref name="distance"/> (as the enhanced shader works it out);
    /// null past a cue that clips (nothing drawn there).</summary>
    public static float? Hidden(Fog fog, float distance)
    {
        if (fog.Beyond == FogBeyond.Clip && distance > fog.End)
        {
            return null;
        }

        var t = Math.Clamp((distance - fog.Start) / MathF.Max(fog.End - fog.Start, 1e-6f), 0f, 1f);
        return MathF.Pow(t, fog.Exponent);
    }

    private void Add(ref int n, Fog fog, Vector3 eye, Vector3 at, float size, Vector3 colour)
    {
        if (Hidden(fog, Vector3.Distance(at, eye)) is not { } hidden || hidden < MinHidden)
        {
            return;
        }

        if (n == _particles.Length)
        {
            Array.Resize(ref _particles, n * 2);
        }

        _particles[n++] = new Particle(at, size, new Vector4(colour * hidden, 1f));
    }
}
