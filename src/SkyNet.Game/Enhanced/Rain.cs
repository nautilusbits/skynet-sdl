using System.Numerics;
using RetroEng.Audio;
using RetroEng.Physics;
using SkyNet.Game.Audio;

namespace SkyNet.Game.Enhanced;

/// <summary>A frame of the rain on the simulation's side: whether it rains and the hiss's gain and sound.</summary>
public readonly record struct RainFrame(RainTable? Table, float Hiss, Sound? Sound);

/// <summary>
/// Rain's hiss over an outdoor map in the enhanced look, from a mod's <see cref="RainTable"/>; the
/// engine draws the drops (<see cref="WeatherSky"/>). Presentation only: the collision world only read.
/// <code>
///   Tick (sim) ── the cover (SkyCover): how open the air round the ears is
///              ── the hiss: the table's volume × intensity, quieter under cover, eased
/// </code>
/// </summary>
public sealed class Rain
{
    /// <summary>The hiss under cover (a roof over the ears, no open cell near) as a share of the open's.</summary>
    public const float ShelteredHiss = 0.35f;
    /// <summary>The hiss's gain at a table's full volume and intensity: a bed well under the game's
    /// sounds (about -20 dB under a gunshot).</summary>
    public const float HissGain = 0.49f;
    /// <summary>The hiss eases to its new level over this (seconds).</summary>
    public const float HissEase = 0.6f;

    private readonly SkyCover _cover;
    private readonly bool _ownCover;
    private object? _scene;
    private CollisionWorld? _world;
    private float _hiss;

    /// <param name="cover">The cover shared with the rest of the weather (it is found by its owner);
    /// null: the rain finds its own.</param>
    public Rain(SkyCover? cover = null) => (_cover, _ownCover) = (cover ?? new SkyCover(), cover == null);

    /// <summary>Moves the rain on by <paramref name="dt"/> seconds (0: held) over <paramref name="scene"/>
    /// (a new one starts afresh), the cover read from <paramref name="world"/> (null: open everywhere).
    /// No table, or indoors: no hiss.</summary>
    public RainFrame Tick(float dt, RainTable? table, object? scene, StormSky sky, Vector3 eye, CollisionWorld? world)
    {
        if (table == null || sky != StormSky.Outdoor)
        {
            _hiss = 0f;
            return default;
        }

        if (!ReferenceEquals(scene, _scene) || !ReferenceEquals(world, _world))
        {
            (_scene, _world, _hiss) = (scene, world, 0f);
        }

        if (_ownCover)
        {
            _cover.Tick(scene, world, eye);
        }

        var target = table.Volume * HissGain * MathF.Sqrt(table.Intensity) * (ShelteredHiss + (1f - ShelteredHiss) * _cover.Openness(eye));
        _hiss += (target - _hiss) * Math.Clamp(dt / HissEase, 0f, 1f);
        return new RainFrame(table, _hiss, table.Recorded ?? RainHiss.Sound);
    }
}
