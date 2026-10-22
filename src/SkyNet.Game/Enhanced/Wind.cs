using System.Numerics;

namespace SkyNet.Game.Enhanced;

/// <summary>The wind now: its velocity (units a second, level), how far it has blown since the scene
/// began (units: what drifts with it moves by this, so a gust never makes it jump) and its gust (1:
/// the mean speed).</summary>
public readonly record struct WindState(Vector3 Velocity, Vector3 Drift, float Gust);

/// <summary>
/// The weather's one wind (a mod's <see cref="WindTable"/>), on its own clock: a mean way and speed
/// with gusts, smooth noise hashed from the clock (no random numbers kept, nothing of the simulation
/// read), integrated into a drift.
/// <code>
///   gust(t) = 1 + gusts × (slow swell + quick flurry)        0.15 .. 2.2
///   way(t)  = direction ± gusts × 20°, wandering slowly
///   gust   += squall (a dust cloud's gust, DustClouds)
///   velocity = way × speed × gust;  drift += velocity × dt
/// </code>
/// </summary>
public sealed class Wind
{
    /// <summary>The gust's bounds (times the mean speed).</summary>
    public const float MinGust = 0.15f, MaxGust = 2.2f;
    /// <summary>How far the way wanders at full gusts (degrees).</summary>
    public const float Wander = 20f;
    /// <summary>The swell's and the flurry's periods (seconds).</summary>
    public const float SwellSeconds = 4.5f, FlurrySeconds = 1.1f, WanderSeconds = 9f;

    private float _clock;
    private Vector3 _drift;
    private object? _scene;

    /// <summary>Moves the wind on by <paramref name="dt"/> seconds over <paramref name="scene"/> (a new
    /// one starts the drift over), <paramref name="squall"/> (times the mean speed) on its gust; null
    /// without a table.</summary>
    public WindState? Tick(float dt, WindTable? table, object? scene, float squall = 0f)
    {
        if (table == null)
        {
            return null;
        }

        if (!ReferenceEquals(scene, _scene))
        {
            (_scene, _drift) = (scene, Vector3.Zero);
        }

        _clock += dt;
        var velocity = VelocityAt(table, _clock, out var gust);

        // A squall blows harder the same way (the gust is never under MinGust).
        velocity *= (gust + squall) / gust;
        gust += squall;

        _drift += velocity * dt;
        return new WindState(velocity, _drift, gust);
    }

    /// <summary>The wind of <paramref name="table"/> at <paramref name="t"/> seconds (units a second).</summary>
    public static Vector3 VelocityAt(WindTable table, float t, out float gust)
    {
        gust = GustAt(table.Gusts, t);
        var angle = (table.Direction + table.Gusts * Wander * (Smooth(t / WanderSeconds, 3) * 2f - 1f)) * MathF.PI / 180f;
        return new Vector3(MathF.Sin(angle), 0f, MathF.Cos(angle)) * (table.Speed * Storm.UnitsPerMetre * gust);
    }

    /// <summary>The gust at <paramref name="t"/>: 1 steady, swelling and flurrying with <paramref name="gusts"/>.</summary>
    public static float GustAt(float gusts, float t)
    {
        var swell = Smooth(t / SwellSeconds, 1) - 0.5f;
        var flurry = Smooth(t / FlurrySeconds, 2) - 0.5f;
        return Math.Clamp(1f + gusts * (1.7f * swell + 0.8f * flurry), MinGust, MaxGust);
    }

    /// <summary>Smooth value noise along <paramref name="x"/> in [0, 1), one stream per <paramref name="stream"/>.</summary>
    private static float Smooth(float x, int stream)
    {
        var i = (int)MathF.Floor(x);
        var f = x - i;
        var s = f * f * (3f - 2f * f);
        return Hash(i, stream) + (Hash(i + 1, stream) - Hash(i, stream)) * s;
    }

    private static float Hash(int i, int stream) => Atmosphere.Hash3(i, 40 + stream).X;
}
