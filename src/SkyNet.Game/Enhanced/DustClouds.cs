using System.Numerics;

namespace SkyNet.Game.Enhanced;

/// <summary>A dust cloud: its core on the ground (world X, Z; Y unused), the way it rolls (level,
/// unit), its age and life (seconds), its peak (0-1) and its radius along the wind (units).</summary>
public readonly record struct DustCloud(Vector3 Centre, Vector3 Way, float Age, float Life, float Peak, float Radius, int Seed)
{
    /// <summary>How far along its life (0-1).</summary>
    public float Phase => Math.Clamp(Age / Life, 0f, 1f);

    /// <summary>Its strength now (0-1): rising in, holding, thinning out.</summary>
    public float Strength => MathF.Pow(MathF.Sin(MathF.PI * Phase), DustClouds.EnvelopeCurve);

    /// <summary>How thin it has spread (1 halfway through its life, as it passes the eye).</summary>
    public float Thinning => (1f + DustClouds.Spread * 0.5f) / (1f + DustClouds.Spread * Phase);

    /// <summary>Its radius along the wind now: it spreads as it rises.</summary>
    public float RadiusNow => Radius * (1f + DustClouds.Spread * Phase);

    /// <summary>How thick it is over (<paramref name="x"/>, <paramref name="z"/>) (0-1): its strength,
    /// thinning, by an elliptic falloff, 1.6 times as wide across the wind as along it.</summary>
    public float DensityAt(float x, float z)
    {
        var d = new Vector2(x - Centre.X, z - Centre.Z);
        var along = (d.X * Way.X + d.Y * Way.Z) / RadiusNow;
        var across = (d.Y * Way.X - d.X * Way.Z) / (RadiusNow * DustClouds.Across);
        return Peak * Strength * Thinning * MathF.Exp(-DustClouds.Falloff * (along * along + across * across));
    }
}

/// <summary>
/// A mod's dust clouds (<see cref="DustTable"/>), on the simulation's thread with the air's own clock:
/// now and then a gust raises a cloud upwind of the eye; it rolls with the wind past the eye halfway
/// through its life, rising and thinning, and is gone. The schedule is hashed from the event's
/// number (no random numbers kept, nothing of the simulation read).
/// <code>
///   event k starts at interval × (k + 0.25 + 0.6 × hash(k))     (gaps 0.4-1.6 × the interval)
///
///   upwind ──►  ░░▒▒▓▓▒▒░░  ──── wind ────►  eye  ────►  gone
///              spawned speed × life / 2 away; strength sin(π age/life)^0.7; the squall
///              (the strongest cloud's strength) adds to the wind's gust
/// </code>
/// </summary>
public sealed class DustClouds
{
    /// <summary>Clouds alive at once at most.</summary>
    public const int MaxClouds = 6;
    /// <summary>The shape of a cloud's strength over its life.</summary>
    public const float EnvelopeCurve = 0.7f;
    /// <summary>How much wider a cloud grows over its life (and thins by).</summary>
    public const float Spread = 0.6f;
    /// <summary>A cloud's width across the wind against its length along it.</summary>
    public const float Across = 1.6f;
    /// <summary>A cloud's falloff from its core (at its radius: e^-2.5, about 8 %).</summary>
    public const float Falloff = 2.5f;
    /// <summary>How much a squall adds to the wind's gust (times the mean speed) at its peak.</summary>
    public const float SquallGust = 0.9f;
    /// <summary>The first event's start and the jitter of each (shares of the interval).</summary>
    public const float FirstShare = 0.25f, JitterShare = 0.6f;
    /// <summary>A spawned cloud lies this far off the wind's line through the eye at most (shares of its width).</summary>
    public const float Sideways = 0.35f;

    private readonly DustCloud[] _clouds = new DustCloud[MaxClouds];
    private int _count;
    private object? _scene;
    private float _clock;
    private int _next;
    private int _forced;

    /// <summary>The clouds alive.</summary>
    public ReadOnlySpan<DustCloud> Clouds => _clouds.AsSpan(0, _count);

    /// <summary>The strongest cloud's strength now (0-1): how much a gust blows.</summary>
    public float Squall { get; private set; }

    /// <summary>When event <paramref name="k"/> of <paramref name="table"/> starts (seconds of the air's clock).</summary>
    public static float StartOf(DustTable table, int k) =>
        table.Interval * (k + FirstShare + JitterShare * Atmosphere.Hash3(k, 71).X);

    /// <summary>Raises a cloud upwind at the next tick, whatever the schedule (the console's <c>weather gust</c>).</summary>
    public void Gust() => Interlocked.Increment(ref _forced);

    /// <summary>Moves the clouds on by <paramref name="dt"/> seconds over <paramref name="scene"/> (a new
    /// one starts afresh) in <paramref name="wind"/> (units a second), raising the ones due upwind of
    /// <paramref name="eye"/>; <paramref name="meanSpeed"/> is the wind's mean (units a second), how far
    /// upwind they rise. None without a table.</summary>
    public void Tick(float dt, DustTable? table, object? scene, Vector3 eye, Vector3 wind, float meanSpeed)
    {
        if (!ReferenceEquals(scene, _scene) || table == null)
        {
            (_scene, _clock, _next, _count, Squall) = (scene, 0f, 0, 0, 0f);
        }

        if (table == null)
        {
            Interlocked.Exchange(ref _forced, 0);
            return;
        }

        _clock += dt;

        // Age and roll the clouds with the wind; the spent ones go.
        var kept = 0;
        for (var i = 0; i < _count; i++)
        {
            var c = _clouds[i];
            c = c with { Age = c.Age + dt, Centre = c.Centre + wind * dt };
            if (c.Age < c.Life)
            {
                _clouds[kept++] = c;
            }
        }

        _count = kept;

        // The ones due: the schedule's, and a forced one.
        while (_clock >= StartOf(table, _next))
        {
            Raise(table, eye, wind, meanSpeed, _next++);
        }

        if (Interlocked.Exchange(ref _forced, 0) > 0)
        {
            Raise(table, eye, wind, meanSpeed, -1 - _next);
        }

        var squall = 0f;
        foreach (var c in Clouds)
        {
            squall = MathF.Max(squall, c.Strength);
        }

        Squall = squall;
    }

    /// <summary>How thick the clouds are over (<paramref name="x"/>, <paramref name="z"/>) (0-1).</summary>
    public static float DensityAt(ReadOnlySpan<DustCloud> clouds, float x, float z)
    {
        var sum = 0f;
        foreach (var c in clouds)
        {
            sum += c.DensityAt(x, z);
        }

        return MathF.Min(1f, sum);
    }

    /// <summary>Raises cloud <paramref name="seed"/> upwind of <paramref name="eye"/>: as far as the
    /// mean wind (with the squall) carries it in half its life, a little off the line.</summary>
    private void Raise(DustTable table, Vector3 eye, Vector3 wind, float meanSpeed, int seed)
    {
        if (_count == MaxClouds)
        {
            return;
        }

        var level = new Vector3(wind.X, 0f, wind.Z);
        var way = level.LengthSquared() > 1e-6f ? Vector3.Normalize(level) : Vector3.UnitZ;
        var radius = table.SizeMetres * Storm.UnitsPerMetre;
        var speed = meanSpeed * (1f + SquallGust * 0.5f);
        var side = new Vector3(way.Z, 0f, -way.X) * ((Atmosphere.Hash3(seed, 72).Y * 2f - 1f) * Sideways * radius * Across);
        var centre = new Vector3(eye.X, 0f, eye.Z) - way * (speed * table.Duration * 0.5f) + side;
        _clouds[_count++] = new DustCloud(centre, way, 0f, table.Duration, table.Peak, radius, seed);
    }
}
