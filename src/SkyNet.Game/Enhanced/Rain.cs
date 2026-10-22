using System.Numerics;
using RetroEng.Audio;
using RetroEng.Physics;
using RetroEng.Render;
using SkyNet.Game.Audio;
using SkyNet.Game.Menu;

namespace SkyNet.Game.Enhanced;

/// <summary>What the frame build needs of the rain, copied by <see cref="Rain.Capture"/> on the
/// simulation's thread: the table, the rain's clock, the wind and the cover round the eye.</summary>
public sealed class RainView : CoverView
{
    /// <summary>The rain (null: none this frame).</summary>
    public RainTable? Table;
    /// <summary>The rain's own clock (seconds): the drops are a function of it.</summary>
    public float Clock;
    /// <summary>The wind now (units a second) and how far it has blown the drops (units).</summary>
    public Vector3 Wind, Drift;
}

/// <summary>A frame of the rain on the simulation's side: whether it rains and the hiss's gain and sound.</summary>
public readonly record struct RainFrame(RainTable? Table, float Hiss, Sound? Sound);

/// <summary>
/// Rain over an outdoor map in the enhanced look, from a mod's <see cref="RainTable"/>: streaks
/// falling round the eye and a hiss. Presentation only: its own clock and hashes, the collision
/// world only read, so demos and network games play the same with it.
/// <code>
///   Tick (sim)   ── the cover: a ray down from high over each 48 u cell round the eye (nearest
///                   first, a few a tick) finds where the rain stops: a roof or the ground
///                ── the hiss: the table's volume × intensity, quieter under cover, eased
///   Capture      ── the cover window and the clock into a RainView for the frame build
///   Draw (build) ── drops hashed in a box round the eye, falling with the wind by the clock,
///                   wrapped into the box; one under its cell's top is not drawn (sheltered)
/// </code>
/// </summary>
public sealed class Rain
{
    /// <summary>Streaks round the eye at full intensity per PARTICLES level (OFF, LOW, HIGH).</summary>
    public static readonly int[] DropCount = [0, 1800, 4500];
    /// <summary>The drops' box round the eye: across (units), above and below the eye.</summary>
    public const float Box = 1000f, Above = 550f, Below = 300f;
    /// <summary>The drops' fall (units a second: 9 m/s at <see cref="Storm.UnitsPerMetre"/>).</summary>
    public const float FallSpeed = 9f * Storm.UnitsPerMetre;
    /// <summary>A streak's length in seconds of its motion (the eye's exposure).</summary>
    public const float StreakSeconds = 0.06f;
    /// <summary>A streak's width (units).</summary>
    public const float StreakWidth = 0.7f;
    /// <summary>No streak nearer the eye than this (units): it would sweep across the view.</summary>
    public const float NearCut = 40f;
    /// <summary>The cover grid's cell (units) and cells across (<see cref="SkyCover"/>: it spans the drops' box).</summary>
    public const float CoverCell = SkyCover.Cell;
    public const int CoverCells = SkyCover.Cells;
    /// <summary>The hiss under cover (a roof over the ears, no open cell near) as a share of the open's.</summary>
    public const float ShelteredHiss = 0.35f;
    /// <summary>The hiss's gain at a table's full volume and intensity: a bed well under the game's
    /// sounds (about -20 dB under a gunshot).</summary>
    public const float HissGain = 0.18f;
    /// <summary>The hiss eases to its new level over this (seconds).</summary>
    public const float HissEase = 0.6f;

    private readonly SkyCover _cover;
    private readonly bool _ownCover;
    private object? _scene;
    private CollisionWorld? _world;
    private float _clock;
    private float _hiss;
    private RainTable? _table;
    private Vector3 _wind, _drift;
    private Particle[] _particles = [];

    /// <param name="cover">The cover shared with the rest of the weather (it is found by its owner);
    /// null: the rain finds its own.</param>
    public Rain(SkyCover? cover = null) => (_cover, _ownCover) = (cover ?? new SkyCover(), cover == null);

    /// <summary>Moves the rain on by <paramref name="dt"/> seconds (0: held) over <paramref name="scene"/>
    /// (a new one starts afresh), the cover read from <paramref name="world"/> (null: open everywhere),
    /// blown by <paramref name="wind"/> (null: the table's own, steady). No table, or indoors: nothing,
    /// and no hiss.</summary>
    public RainFrame Tick(float dt, RainTable? table, object? scene, StormSky sky, Vector3 eye, CollisionWorld? world,
        WindState? wind = null)
    {
        if (table == null || sky != StormSky.Outdoor)
        {
            (_table, _hiss) = (null, 0f);
            return default;
        }

        if (!ReferenceEquals(scene, _scene) || !ReferenceEquals(world, _world))
        {
            (_scene, _world, _hiss, _clock, _drift) = (scene, world, 0f, 0f, Vector3.Zero);
        }

        if (_ownCover)
        {
            _cover.Tick(scene, world, eye);
        }

        _table = table;
        _clock += dt;
        _wind = wind?.Velocity ?? OwnWind(table);
        _drift += _wind * dt;

        var target = table.Volume * HissGain * MathF.Sqrt(table.Intensity) * (ShelteredHiss + (1f - ShelteredHiss) * _cover.Openness(eye));
        _hiss += (target - _hiss) * Math.Clamp(dt / HissEase, 0f, 1f);
        return new RainFrame(table, _hiss, table.Recorded ?? RainHiss.Sound);
    }

    /// <summary>Copies what the frame build draws into <paramref name="view"/>.</summary>
    public void Capture(RainView view)
    {
        view.Table = _table;
        view.Clock = _clock;
        (view.Wind, view.Drift) = (_wind, _drift);
        _cover.Capture(view);
    }

    /// <summary>The table's own wind (units a second): steady, its way and speed.</summary>
    public static Vector3 OwnWind(RainTable table)
    {
        var angle = table.WindDirection * MathF.PI / 180f;
        return new Vector3(MathF.Sin(angle), 0f, MathF.Cos(angle)) * (table.WindSpeed * Storm.UnitsPerMetre);
    }

    /// <summary>Queues the streaks of <paramref name="view"/> seen from <paramref name="eye"/>, lit by the
    /// renderer's light (a flash too): none at PARTICLES OFF, in the original look or without rain.</summary>
    public void Draw(Renderer renderer, RainView view, Vector3 eye, ParticleLevel level)
    {
        if (renderer.Lighting is not { } lighting || view.Table == null)
        {
            return;
        }

        var ambient = (lighting.Sky + lighting.Ground) * 0.5f + lighting.Sun * 0.15f;
        var n = Drops(view, eye, level, ambient, ref _particles);
        renderer.DrawParticles(_particles.AsSpan(0, n), ParticleBlend.Alpha, 2f);
    }

    /// <summary>The streaks of <paramref name="view"/> round <paramref name="eye"/> into
    /// <paramref name="into"/> (grown as needed); returns how many. A drop under its cell's top (a roof,
    /// the ground) is left out.</summary>
    public static int Drops(RainView view, Vector3 eye, ParticleLevel level, Vector3 ambient, ref Particle[] into)
    {
        if (view.Table is not { } table)
        {
            return 0;
        }

        var count = (int)(DropCount[(int)level] * table.Intensity);
        if (into.Length < count)
        {
            into = new Particle[count];
        }

        var colour = ambient * 1.6f + new Vector3(0.025f, 0.028f, 0.034f);
        var (left, bottom) = (eye.X - Box / 2f, eye.Y - Below);
        var back = eye.Z - Box / 2f;
        var n = 0;
        for (var i = 0; i < count; i++)
        {
            var h = Hash3(i, 1);
            var g = Hash3(i, 2);
            // Each drop blown a little more or less than the wind, falling at its own speed.
            var gust = 0.9f + 0.2f * g.Z;
            var fall = FallSpeed * (0.85f + 0.3f * g.X);
            var velocity = view.Wind * gust - Vector3.UnitY * fall;
            var p = new Vector3(h.X * Box, h.Y * (Above + Below), h.Z * Box) + view.Drift * gust - Vector3.UnitY * (fall * view.Clock);
            var at = new Vector3(left + Wrap(p.X - left, Box), bottom + Wrap(p.Y - bottom, Above + Below), back + Wrap(p.Z - back, Box));
            if (at.Y < view.TopAt(at.X, at.Z))
            {
                continue;
            }

            if (Vector3.DistanceSquared(at, eye) < NearCut * NearCut)
            {
                continue;
            }

            // Thinned out towards the box's sides (no wall of rain).
            var edge = MathF.Max(MathF.Abs(at.X - eye.X), MathF.Abs(at.Z - eye.Z)) / Box;
            var fade = 1f - SmoothStep(0.36f, 0.5f, edge);
            var alpha = 0.4f * (0.55f + 0.45f * g.Y) * fade;
            if (alpha <= 0f)
            {
                continue;
            }

            into[n++] = new Particle(at, StreakWidth, new Vector4(colour, alpha), Shape: ParticleShape.Streak, Axis: velocity * StreakSeconds);
        }

        return n;
    }

    private static float Wrap(float v, float size) => v - MathF.Floor(v / size) * size;

    private static float SmoothStep(float a, float b, float x)
    {
        var t = Math.Clamp((x - a) / (b - a), 0f, 1f);
        return t * t * (3f - 2f * t);
    }

    /// <summary>Three hashes in [0, 1) of a drop and a channel.</summary>
    private static Vector3 Hash3(int i, int channel)
    {
        static float H(uint x)
        {
            x ^= x >> 16;
            x *= 0x7feb352d;
            x ^= x >> 15;
            x *= 0x846ca68b;
            x ^= x >> 16;
            return (x & 0xffffff) / (float)0x1000000;
        }

        var b = (uint)i * 3u + (uint)channel * 0x9e3779b9u;
        return new Vector3(H(b), H(b + 1u), H(b + 2u));
    }
}
