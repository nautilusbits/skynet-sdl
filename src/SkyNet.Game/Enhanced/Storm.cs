using System.Collections.Concurrent;
using System.Numerics;
using RetroEng.Audio;
using SkyNet.Game.Audio;
using Strike = RetroEng.Render.Strike;

namespace SkyNet.Game.Enhanced;

/// <summary>Whether the map shown is under the sky (a storm shows there only).</summary>
public enum StormSky { Indoor, Outdoor }

/// <summary>
/// A mod's storm's thunder (<see cref="StormTable"/>) on the simulation's side; the engine's
/// <see cref="RetroEng.Render.WeatherSystem"/> strikes and draws the lightning on the frame build's
/// (<see cref="WeatherSky"/>). Presentation only: nothing of the simulation read or changed.
/// <code>
///   build: WeatherSystem.Struck ─► Post (any thread) ─► queue
///   sim:   Tick ─► each strike's thunder after distance / 343 m/s ─► Thunder callback (Effects bus)
///          Strike (the console) ─► TakeStrike ─► the frame build strikes ahead
/// </code>
/// </summary>
public sealed class Storm
{
    /// <summary>World units per metre (docs/modding.md: the soldier's eye 75 u up, about 40 u a metre).</summary>
    public const float UnitsPerMetre = 40f;
    /// <summary>Strikes nearer than this (metres) crack; the thunder is full up to <see cref="FullMetres"/>.</summary>
    public const float NearMetres = 2000f, FullMetres = 1500f;

    /// <summary>A thunder's place from the eye: this far towards the bolt and this high, so it comes
    /// from the bolt's side and both ears hear it (a diffuse roll; the pan stays under 0.1).</summary>
    private const float ThunderOffset = 1000f, ThunderHeight = 10000f;
    /// <summary>A thunder's gain is the table's volume, falling as the root of the distance past
    /// <see cref="FullMetres"/>, never under this.</summary>
    public const float ThunderFloor = 0.5f;

    private readonly ConcurrentQueue<Strike> _struck = new();
    private readonly List<(float Left, int Seed, ThunderKind Kind, float Gain, Vector3 At, Sound? Recorded)> _thunder = [];
    private object? _scene;
    private Vector3? _ahead;

    /// <summary>Plays a thunder: the sound, its place (world units, a little way towards the bolt
    /// from the eye) and its gain (0-1). Null: no thunder (the editor).</summary>
    public Action<Sound, Vector3, float>? Thunder { get; set; }

    /// <summary>Thunders queued, not yet heard.</summary>
    public int ThunderPending => _thunder.Count + _struck.Count;

    /// <summary>Asks for a strike ahead of the view (the console's <c>storm strike</c>): the frame
    /// build takes it (<see cref="TakeStrike"/>) and strikes along <paramref name="ahead"/>.</summary>
    public void Strike(Vector3 ahead) => _ahead = ahead;

    /// <summary>The strike asked for (its way), once; null: none.</summary>
    public Vector3? TakeStrike()
    {
        var ahead = _ahead;
        _ahead = null;
        return ahead;
    }

    /// <summary>A strike the engine raised (on the frame build's thread): its thunder is heard at a
    /// later <see cref="Tick"/>.</summary>
    public void Post(Strike strike) => _struck.Enqueue(strike);

    /// <summary>Moves the thunder on by <paramref name="dt"/> seconds (0: held, e.g. paused) over
    /// <paramref name="scene"/> (a new one forgets what was not heard). No table, or indoors: nothing.</summary>
    public void Tick(float dt, StormTable? table, object? scene, StormSky sky, Vector3 eye)
    {
        if (table is not { On: true } || sky != StormSky.Outdoor)
        {
            _scene = scene;
            _thunder.Clear();
            _struck.Clear();
            return;
        }

        // A new scene: the old map's strikes (posted in the switch's frame too) are not heard.
        if (!ReferenceEquals(scene, _scene))
        {
            if (_scene != null)
            {
                _struck.Clear();
            }

            _scene = scene;
            _thunder.Clear();
        }

        while (_struck.TryDequeue(out var strike))
        {
            Queue(table, strike);
        }

        Hear(dt, eye);
    }

    /// <summary>The gain (0-1) of a thunder <paramref name="metres"/> away at the table's <paramref name="volume"/>.</summary>
    public static float ThunderGain(float volume, float metres) => volume * Math.Clamp(MathF.Sqrt(FullMetres / metres), ThunderFloor, 1f);

    /// <summary>A strike's thunder queued: after the sound's travel, from the bolt's side.</summary>
    private void Queue(StormTable table, Strike strike)
    {
        var metres = strike.Distance / UnitsPerMetre;
        var gain = ThunderGain(table.ThunderVolume, metres);
        if (gain <= 0f)
        {
            return;
        }

        var kind = metres < NearMetres ? ThunderKind.Near : ThunderKind.Far;
        _thunder.Add((strike.ThunderDelay(1f / UnitsPerMetre), strike.Seed, kind, gain, strike.Position, table.Recorded));
    }

    /// <summary>Counts the queued thunders down; one that is due is made (or the mod's own) and played.</summary>
    private void Hear(float dt, Vector3 eye)
    {
        for (var i = _thunder.Count - 1; i >= 0; i--)
        {
            var t = _thunder[i];
            t.Left -= dt;
            if (t.Left > 0f)
            {
                _thunder[i] = t;
                continue;
            }

            _thunder.RemoveAt(i);
            var to = t.At - eye;
            var level = new Vector2(to.X, to.Z);
            var way = level.LengthSquared() > 0f ? Vector2.Normalize(level) : Vector2.UnitY;
            Thunder?.Invoke(t.Recorded ?? Audio.Thunder.Make(t.Seed, t.Kind), eye + new Vector3(way.X, 0f, way.Y) * ThunderOffset + Vector3.UnitY * ThunderHeight, t.Gain);
        }
    }
}
