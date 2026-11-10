using System.Numerics;

namespace SkyNet.Game.Enhanced;

/// <summary>The wind now: its velocity (units a second, level), how far it has blown since the scene
/// began (units: what drifts with it moves by this, so a gust never makes it jump) and its gust (1:
/// the mean speed).</summary>
public readonly record struct WindState(Vector3 Velocity, Vector3 Drift, float Gust);

/// <summary>
/// The weather's one wind (a mod's <see cref="WindTable"/>) on the simulation's side: the engine's
/// wind (<see cref="WeatherMap.WindOf(WindTable)"/>, the one the frame build's rain slants in) read at
/// the eye on the air's own clock, integrated into a drift. Nothing of the simulation read.
/// <code>
///   gust     = the engine's 1 ± gusts (smooth in space and time)
///   gust    += squall (a dust cloud's gust, DustClouds)
///   velocity = way × speed × gust;  drift += velocity × dt
/// </code>
/// </summary>
public sealed class Wind
{
    /// <summary>The strongest gust (times the mean speed): the wildest gusts' 1 + 1.</summary>
    public const float MaxGust = 2f;

    private float _clock;
    private Vector3 _drift;
    private object? _scene;

    /// <summary>Moves the wind on by <paramref name="dt"/> seconds over <paramref name="scene"/> (a new
    /// one starts the clock and drift over) at <paramref name="eye"/>, <paramref name="squall"/> (times
    /// the mean speed) on its gust; null without a table.</summary>
    public WindState? Tick(float dt, WindTable? table, object? scene, Vector3 eye, float squall = 0f)
    {
        if (table == null)
        {
            return null;
        }

        if (!ReferenceEquals(scene, _scene))
        {
            (_scene, _clock, _drift) = (scene, 0f, Vector3.Zero);
        }

        _clock += dt;
        var wind = WeatherMap.WindOf(table);
        var mean = wind.Velocity.LengthSquared();
        var gust = mean > 0f ? Vector3.Dot(wind.At(eye, _clock), wind.Velocity) / mean : 1f;

        // A squall blows harder the same way.
        gust += squall;
        var velocity = wind.Velocity * gust;
        _drift += velocity * dt;
        return new WindState(velocity, _drift, gust);
    }
}
