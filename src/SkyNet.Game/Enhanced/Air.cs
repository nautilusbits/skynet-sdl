using System.Numerics;
using RetroEng.Audio;
using RetroEng.Physics;
using SkyNet.Game.Audio;

namespace SkyNet.Game.Enhanced;

/// <summary>What the frame build needs of the air (<see cref="Air.Capture"/>, on the simulation's
/// thread): the clouds, haze and dust, the wind, the deck's drift, the air's clock and the cover.</summary>
public sealed class AirView : CoverView
{
    public CloudTable? Clouds;
    public HazeAirTable? Haze;
    public DustTable? Dust;
    /// <summary>The mod's wind (null: none; the clouds drift calmly or with the rain's).</summary>
    public WindState? Wind;
    /// <summary>How far the cloud deck has drifted (world units).</summary>
    public Vector3 CloudDrift;
    /// <summary>The air's own clock (seconds).</summary>
    public float Clock;
    /// <summary>The dust clouds alive (the first <see cref="DustCount"/>).</summary>
    public readonly DustCloud[] DustClouds = new DustCloud[Enhanced.DustClouds.MaxClouds];
    public int DustCount;
    /// <summary>How thick the clouds are at the eye (0-1): raw, and as the haze takes it (eased,
    /// none under cover).</summary>
    public float DustNear, DustFog;
    /// <summary>The strongest cloud's strength (0-1).</summary>
    public float Squall;

    /// <summary>Whether anything of the air shows.</summary>
    public bool Any => Clouds != null || Haze != null || Dust != null || Wind != null;
}

/// <summary>A frame of the air on the simulation's side: the wind and the howl's gain, pitch and sound.</summary>
public readonly record struct AirFrame(WindState? Wind, float Howl, float Pitch, Sound? Sound);

/// <summary>
/// The weather's air over an outdoor map in the enhanced look: the cover round the eye (shared with
/// the rain), the wind (<see cref="Enhanced.Wind"/>), the cloud deck's drift and the wind's howl.
/// Presentation only: its own clock, the collision world only read.
/// <code>
///   Tick (sim)  ── the cover (SkyCover), the wind (with the dust clouds' squall), the dust clouds
///                  (DustClouds) and how thick they are at the eye, the deck's drift (the wind × the
///                  deck's drift, or the rain's wind, or a calm air), the howl: louder in a gust,
///                  quieter under cover
///   Capture     ── the tables, the wind, the drift, the clouds and the cover into an AirView for the frame build
/// </code>
/// </summary>
public sealed class Air
{
    /// <summary>The deck's drift without a wind (metres a second).</summary>
    public const float CalmMetres = 1.5f;
    /// <summary>The wind the howl's volume is set for (metres a second).</summary>
    public const float HowlMetres = 12f;
    /// <summary>The howl under cover (a roof over the ears, no open cell near) as a share of the open's.</summary>
    public const float ShelteredHowl = 0.4f;
    /// <summary>The howl eases to its new level over this (seconds).</summary>
    public const float HowlEase = 0.3f;
    /// <summary>The howl's pitch in a lull and at the strongest gust.</summary>
    public const float LowPitch = 0.85f, HighPitch = 1.2f;
    /// <summary>The dust at the eye eases to its new thickness over this (seconds): stepping under a roof.</summary>
    public const float DustEase = 0.6f;

    private readonly Wind _wind = new();
    private readonly DustClouds _dust = new();
    private object? _scene;
    private Weather _weather = Weather.None;
    private WindState? _state;
    private Vector3 _cloudDrift;
    private float _clock;
    private float _howl;
    private float _dustNear, _dustFog;
    private bool _on;

    /// <summary>What is open to the sky round the eye (shared with <see cref="Rain"/>).</summary>
    public SkyCover Cover { get; } = new();

    /// <summary>Moves the air on by <paramref name="dt"/> seconds (0: held) over <paramref name="scene"/>
    /// (a new one starts afresh), the cover read from <paramref name="world"/> (null: open everywhere).
    /// Indoors, or no weather: nothing, and no howl.</summary>
    public AirFrame Tick(float dt, Weather weather, object? scene, StormSky sky, Vector3 eye, CollisionWorld? world)
    {
        _on = sky == StormSky.Outdoor && weather.Any;
        if (!_on)
        {
            (_state, _howl, _dustNear, _dustFog) = (null, 0f, 0f, 0f);
            _dust.Tick(dt, null, scene, eye, Vector3.Zero, 0f);
            return default;
        }

        if (!ReferenceEquals(scene, _scene))
        {
            (_scene, _clock, _cloudDrift, _howl, _dustFog) = (scene, 0f, Vector3.Zero, 0f, 0f);
        }

        _weather = weather;
        _clock += dt;
        Cover.Tick(scene, world, eye);
        _state = _wind.Tick(dt, weather.Wind, scene, _dust.Squall * DustClouds.SquallGust);
        TickDust(dt, weather, scene, eye);
        if (weather.Clouds is { } clouds)
        {
            var wind = _state?.Velocity ?? (weather.Rain is { } r ? Rain.OwnWind(r) : new Vector3(0.6f, 0f, 0.8f) * (CalmMetres * Storm.UnitsPerMetre));
            _cloudDrift += wind * (clouds.Drift * dt);
        }

        if (weather.Wind is not { } table || _state is not { } state)
        {
            _howl = 0f;
            return new AirFrame(_state, 0f, 1f, null);
        }

        var target = HowlGain(table, state.Gust, Cover.Openness(eye));
        _howl += (target - _howl) * Math.Clamp(dt / HowlEase, 0f, 1f);
        var pitch = LowPitch + (HighPitch - LowPitch) * Math.Clamp((state.Gust - Wind.MinGust) / (Wind.MaxGust - Wind.MinGust), 0f, 1f);
        return new AirFrame(_state, _howl, pitch, table.Recorded ?? WindHowl.Sound);
    }

    /// <summary>Raises a dust cloud upwind now (with a mod's dust; the console's <c>weather gust</c>).</summary>
    public void Gust() => _dust.Gust();

    /// <summary>Moves the dust clouds on in the wind (or a calm air) and finds how thick they are at
    /// the eye: none under cover.</summary>
    private void TickDust(float dt, Weather weather, object? scene, Vector3 eye)
    {
        var (wind, mean) = (_state?.Velocity ?? Dust.CalmWind, Dust.CalmWind.Length());
        if (weather.Wind is { } table)
        {
            mean = table.Speed * Storm.UnitsPerMetre;
        }

        _dust.Tick(dt, weather.Dust, scene, eye, wind, mean);
        _dustNear = DustClouds.DensityAt(_dust.Clouds, eye.X, eye.Z);
        var target = _dustNear * Cover.Openness(eye);
        _dustFog += (target - _dustFog) * Math.Clamp(dt / DustEase, 0f, 1f);
    }

    /// <summary>The howl's gain: the table's volume by the wind's strength and gust, a share of it
    /// under cover (<paramref name="openness"/> 0: sheltered, 1: open).</summary>
    public static float HowlGain(WindTable table, float gust, float openness)
    {
        var strength = Math.Clamp(table.Speed / HowlMetres, 0f, 1.5f) * (0.55f + 0.45f * gust);
        return table.Volume * WindHowl.Gain * strength * (ShelteredHowl + (1f - ShelteredHowl) * openness);
    }

    /// <summary>Copies what the frame build draws into <paramref name="view"/>.</summary>
    public void Capture(AirView view)
    {
        var w = _on ? _weather : Weather.None;
        (view.Clouds, view.Haze, view.Dust) = (w.Clouds, w.Haze, w.Dust);
        (view.Wind, view.CloudDrift, view.Clock) = (_on ? _state : null, _cloudDrift, _clock);
        var clouds = _on ? _dust.Clouds : [];
        clouds.CopyTo(view.DustClouds);
        (view.DustCount, view.DustNear, view.DustFog, view.Squall) = (clouds.Length, _on ? _dustNear : 0f, _on ? _dustFog : 0f, _on ? _dust.Squall : 0f);
        Cover.Capture(view);
    }
}
