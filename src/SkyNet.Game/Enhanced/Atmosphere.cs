using System.Numerics;
using RetroEng.Render;
using SkyNet.Game.Menu;

namespace SkyNet.Game.Enhanced;

/// <summary>A place smoke rises from: the top of a flame, and how tall the fire is (units).</summary>
public readonly record struct SmokeSource(Vector3 Top, float Height);

/// <summary>A map's ground: its middle (Y: its mean height) and how far its edge reaches from it (units).</summary>
public readonly record struct GroundSpan(Vector3 Centre, float Radius);

/// <summary>
/// The enhanced look's air (ENHANCED OPTIONS' PARTICLES): dust motes and ash drifting round the
/// eye, and smoke rising from the fires and burning wrecks, as the renderer's soft particles
/// (<see cref="Renderer.DrawParticles"/>: one instanced draw per kind). Everything is a function of
/// the animation clock, nothing is simulated or kept: a frozen clock draws the same frame.
/// <code>
///   motes: a box of hashed points round the eye, each drifting by the wind and its own wobble,
///          wrapped back into the box (faded at its faces)
///   smoke: per fire, puffs on a loop of SmokeLife seconds: born small at the flame's top (lit
///          by it), rising, swelling, drifting downwind, thinning out; outdoors a fire past
///          SmokeReach (one in two, by its place) a tall slow column of big puffs instead
///   far:   outdoors, a few columns on the horizon past the far clip (their ways and sizes hashed
///          from the map, so they stay put), behind the terrain, the haze mixed into their colour
/// </code>
/// </summary>
public sealed class Atmosphere
{
    /// <summary>Motes round the eye per level (OFF, LOW, HIGH).</summary>
    public static readonly int[] MoteCount = [0, 110, 280];
    /// <summary>Puffs per fire and the fires that smoke at once (the nearest) per level.</summary>
    public static readonly int[] PuffsPerSource = [0, 8, 16];
    public static readonly int[] SourceCount = [0, 8, 24];
    /// <summary>Fires farther than this from the eye (units) don't smoke.</summary>
    public const float SmokeReach = 2600f;
    /// <summary>A puff's life (seconds).</summary>
    public const float SmokeLife = 6f;
    /// <summary>The motes' box round the eye, outdoors and indoors (units across).</summary>
    public const float OutdoorBox = 900f, IndoorBox = 520f;
    /// <summary>The wind outdoors and the draught indoors (units a second).</summary>
    public static readonly Vector3 OutdoorWind = new(16f, 0f, 7f), IndoorWind = new(2.5f, 0f, 1.5f);
    /// <summary>Far columns on the horizon per level (at most; the map's hash picks 2 to this).</summary>
    public static readonly int[] HorizonColumns = [0, 2, 4];
    /// <summary>Puffs per far column, and per distant fire's column, per level.</summary>
    public static readonly int[] ColumnPuffs = [0, 10, 18];
    /// <summary>The distant fires (past <see cref="SmokeReach"/>) that raise a column, at most.</summary>
    public const int DistantFires = 6;
    /// <summary>A column's puff's life (seconds).</summary>
    public const float ColumnLife = 36f;
    /// <summary>The horizon's columns stand this far times the far clip from the eye.</summary>
    public const float HorizonDistance = 1.25f;
    /// <summary>A flame's colour on the smoke it has just made (linear).</summary>
    public static readonly Vector3 EmberGlow = new(0.42f, 0.17f, 0.05f);

    private Particle[] _motes = [];
    private Particle[] _smoke = [];
    private float[] _keys = [];
    private readonly List<(SmokeSource Source, float Distance)> _near = [];
    private readonly List<SmokeSource> _distant = [];
    private Particle[] _columns = [];
    private float[] _columnKeys = [];
    private ParticleLevel _level;
    private EffectTable _effects = EffectTable.Defaults;
    private WindState? _wind;

    /// <summary>The air's wind now outdoors (units a second): a mod's, else <see cref="OutdoorWind"/>.</summary>
    private Vector3 Breeze => _wind?.Velocity ?? OutdoorWind;

    /// <summary>Queues this frame's motes and smoke (nothing at <see cref="ParticleLevel.Off"/>).</summary>
    /// <param name="lighting">The enhanced look's light: the motes and smoke take its light all around.</param>
    /// <param name="seconds">The animation clock.</param>
    /// <param name="map">The map's number: the horizon's columns are hashed from it.</param>
    /// <param name="far">The far clip (units): the horizon's columns stand past it (0: none).</param>
    /// <param name="ground">The map's ground the horizon's columns rise past; null: none (they stand round the eye).</param>
    /// <param name="wind">A mod's wind outdoors: the ash drifts by its drift, the smoke leans with it; null: <see cref="OutdoorWind"/>.</param>
    /// <param name="effects">How the motes and smoke draw; null: <see cref="EffectTable.Current"/>.</param>
    public void Draw(IFrameRecorder renderer, Lighting lighting, ParticleLevel level, bool outdoor, Vector3 eye, float seconds,
        IEnumerable<SmokeSource> sources, int map = 0, float far = 0f, GroundSpan? ground = null, WindState? wind = null,
        EffectTable? effects = null)
    {
        if (level == ParticleLevel.Off)
        {
            return;
        }

        // The light all around: half the sky's and half the ground's (a billboard's, LookLighting.SpriteTint).
        var ambient = (lighting.Sky + lighting.Ground) * 0.5f;
        _level = level;
        _effects = effects ?? EffectTable.Current;
        _wind = wind;
        DrawMotes(renderer, outdoor, eye, seconds, ambient);
        DrawColumns(renderer, outdoor, eye, seconds, ambient, map, far, ground);
        DrawSmoke(renderer, SourceCount[(int)level], outdoor, eye, seconds, ambient, sources);
    }

    private void DrawMotes(IFrameRecorder renderer, bool outdoor, Vector3 eye, float seconds, Vector3 ambient)
    {
        var row = _effects[EffectTable.Motes];
        var count = EffectTable.Count(row, _level);
        if (_motes.Length < count)
        {
            _motes = new Particle[count];
        }

        var box = outdoor ? OutdoorBox : IndoorBox;
        var wind = outdoor ? OutdoorWind : IndoorWind;
        // Ash outdoors (grey flakes, falling), dust indoors (paler, smaller, hanging).
        var fall = outdoor ? -7f : -0.8f;
        var colour = (outdoor ? ambient * 2.4f + new Vector3(0.012f) : ambient * 2.8f + new Vector3(0.01f)) * EffectTable.Tint(row);
        float scale = (float)row.Size, pace = (float)row.Speed, cover = (float)row.Intensity;
        var n = 0;
        for (var i = 0; i < count; i++)
        {
            var h = Hash3(i, 1);
            var g = Hash3(i, 2);
            var speed = 0.5f + g.X;
            var blown = outdoor && _wind is { } w ? w.Drift : wind * seconds;
            var drift = (blown * speed + new Vector3(0f, fall * (0.6f + g.Y) * seconds, 0f)) * pace;
            var wobble = new Vector3(MathF.Sin(seconds * (0.25f + 0.4f * g.Z) + h.X * 40f), MathF.Sin(seconds * (0.2f + 0.3f * g.X) + h.Y * 40f) * 0.5f,
                MathF.Cos(seconds * (0.23f + 0.35f * g.Y) + h.Z * 40f)) * (outdoor ? 18f : 7f);
            var p = h * box + drift + wobble;
            var q = Wrap((p - eye) / box);
            var edge = MathF.Max(MathF.Abs(q.X), MathF.Max(MathF.Abs(q.Y), MathF.Abs(q.Z)));
            var fade = 1f - SmoothStep(0.32f, 0.5f, edge);
            if (fade <= 0f)
            {
                continue;
            }

            var size = (outdoor ? 0.5f + 0.7f * g.Z : 0.3f + 0.4f * g.Z) * scale;
            var alpha = (outdoor ? 0.45f : 0.35f) * (0.5f + 0.5f * h.X) * fade * cover;
            _motes[n++] = new Particle(eye + q * box, size, new Vector4(colour, alpha));
        }

        renderer.DrawParticles(_motes.AsSpan(0, n), ParticleBlend.Alpha, 4f);
    }

    private void DrawSmoke(IFrameRecorder renderer, int maxSources, bool outdoor, Vector3 eye, float seconds, Vector3 ambient,
        IEnumerable<SmokeSource> sources)
    {
        var row = _effects[EffectTable.Smoke];
        var puffs = EffectTable.Count(row, _level);
        _near.Clear();
        _distant.Clear();
        foreach (var s in sources)
        {
            var d = Vector3.Distance(s.Top, eye);
            if (d < SmokeReach)
            {
                _near.Add((s, d));
            }
            else if (outdoor && _distant.Count < DistantFires && PlaceHash(s.Top) < 0.5f)
            {
                _distant.Add(s);
            }
        }

        DrawDistantFires(renderer, eye, seconds, ambient);
        if (_near.Count == 0)
        {
            return;
        }

        _near.Sort((a, b) => a.Distance.CompareTo(b.Distance));
        var sourceCount = Math.Min(_near.Count, maxSources);
        var total = sourceCount * puffs;
        if (_smoke.Length < total)
        {
            _smoke = new Particle[total];
            _keys = new float[total];
        }

        var wind = (outdoor ? Breeze : IndoorWind) * (float)row.Speed;
        var grey = (ambient * 0.55f + new Vector3(0.006f)) * EffectTable.Tint(row);
        float life = (float)row.Life, scale = (float)row.Size, rise = (float)row.Rise, widen = (float)row.Spread, cover = (float)row.Intensity;
        var n = 0;
        for (var s = 0; s < sourceCount; s++)
        {
            var (source, _) = _near[s];
            var h = Math.Clamp(source.Height, 30f, 260f);
            // A fire's own phase (from its place), so neighbouring fires don't puff together.
            var phase = PlaceHash(source.Top);
            for (var k = 0; k < puffs; k++)
            {
                var age = Fract(seconds / life + (k + 0.5f * Hash3(k, 4).X) / puffs + phase);
                var j = Hash3(k + s * 131, 5) * 2f - Vector3.One;
                var spread = (0.15f + age * 0.6f) * h * widen;
                var position = source.Top + new Vector3(j.X * spread, age * (h * 2.5f + 160f) * rise, j.Z * spread)
                    + wind * (age * life * (outdoor ? 1.6f : 1f));
                var size = h * (0.45f + 2.1f * age) * scale;
                var glow = 1f - SmoothStep(0f, 0.3f, age);
                var colour = grey + EmberGlow * glow;
                var alpha = 0.5f * SmoothStep(0f, 0.08f, age) * MathF.Pow(1f - age, 1.6f) * cover;
                _keys[n] = -Vector3.DistanceSquared(position, eye);
                _smoke[n++] = new Particle(position, size, new Vector4(colour, alpha), Spin: j.Y * 3.1f + age * 1.2f, Seed: 1f + k + s * 17);
            }
        }

        // Back to front: the farthest first.
        Array.Sort(_keys, _smoke, 0, n);
        renderer.DrawParticles(_smoke.AsSpan(0, n), ParticleBlend.Alpha, 45f);
    }

    /// <summary>The distant fires' columns: big slow puffs rising high (into the haze, as the map's
    /// own things are).</summary>
    private void DrawDistantFires(IFrameRecorder renderer, Vector3 eye, float seconds, Vector3 ambient)
    {
        var row = _effects[EffectTable.SmokeColumns];
        var puffs = EffectTable.Count(row, _level);
        if (_distant.Count == 0 || puffs == 0)
        {
            return;
        }

        var n = 0;
        EnsureColumns(_distant.Count * puffs);
        var grey = (ambient * 0.5f + new Vector3(0.005f)) * EffectTable.Tint(row);
        var drift = Breeze * (float)row.Speed;
        float scale = (float)row.Size, rise = (float)row.Rise, widen = (float)row.Spread, cover = (float)row.Intensity;
        foreach (var s in _distant)
        {
            var phase = PlaceHash(s.Top);
            var h = Math.Clamp(s.Height, 60f, 260f);
            var life = (float)row.Life * 0.6f;
            for (var k = 0; k < puffs; k++)
            {
                var j = Hash3(k, 21 + (int)(phase * 1000f));
                var age = Fract(seconds / life + (k + 0.5f * j.X) / puffs + phase);
                var position = s.Top + new Vector3((j.Y - 0.5f) * h * (0.4f + age) * widen, age * h * 9f * rise, (j.Z - 0.5f) * h * (0.4f + age) * widen)
                    + drift * (age * life * 1.4f);
                var alpha = 0.42f * SmoothStep(0f, 0.08f, age) * MathF.Pow(1f - age, 1.4f) * cover;
                Push(ref n, position, h * (1.4f + 3.2f * age) * scale, new Vector4(grey + EmberGlow * (1f - SmoothStep(0f, 0.15f, age)), alpha),
                    j.X * 6.2f + age * 0.8f, 3f + k + phase * 50f, eye);
            }
        }

        FlushColumns(renderer, n, true);
    }

    /// <summary>The horizon's columns: ways and heights hashed from the map, past the ground's edge
    /// and the far clip so all the terrain hides them, the haze mixed into their colour.</summary>
    private void DrawColumns(IFrameRecorder renderer, bool outdoor, Vector3 eye, float seconds, Vector3 ambient, int map, float far, GroundSpan? ground)
    {
        var row = _effects[EffectTable.SmokeColumns];
        var puffs = EffectTable.Count(row, _level);
        if (!outdoor || far <= 0f || puffs == 0)
        {
            return;
        }

        var seed = Hash3(map, 31);
        var columns = Math.Min(HorizonColumns[(int)_level], 2 + (int)(seed.X * (HorizonColumns[^1] - 1)));
        var haze = renderer.Fog is { } fog && fog.Colour.W > 0f ? new Vector3(fog.Colour.X, fog.Colour.Y, fog.Colour.Z) : ambient * 3f;
        var grey = (haze * 1.2f + ambient * 2.5f + new Vector3(0.015f)) * EffectTable.Tint(row);
        float life = (float)row.Life, scale = (float)row.Size, rise = (float)row.Rise, widen = (float)row.Spread, cover = (float)row.Intensity;
        EnsureColumns(columns * puffs);
        var n = 0;
        for (var c = 0; c < columns; c++)
        {
            var h = Hash3(map * 7 + c, 32);
            var (foot, d) = ColumnFoot(map, c, columns, far, eye, ground);
            // Its top 17 to 25 degrees over its foot.
            var tall = d * (0.32f + 0.15f * h.Z);
            var lean = Vector3.Normalize(OutdoorWind) * tall * (0.25f + 0.3f * h.X) * (float)row.Speed;
            for (var k = 0; k < puffs; k++)
            {
                var j = Hash3(k + c * 97, 33);
                var age = Fract(seconds / life + (k + 0.5f * j.X) / puffs + h.Z);
                var spread = tall * (0.05f + 0.2f * age) * widen;
                var position = foot + new Vector3((j.Y - 0.5f) * spread, age * tall * rise, (j.Z - 0.5f) * spread) + lean * (age * age);
                var alpha = 0.6f * SmoothStep(0f, 0.1f, age) * MathF.Pow(1f - age, 1.2f) * cover;
                // The fire under it lights its foot (a town burning past the hills).
                var colour = grey + EmberGlow * (1.3f * (1f - SmoothStep(0f, 0.45f, age)));
                Push(ref n, position, tall * (0.12f + 0.3f * age) * scale, new Vector4(colour, alpha), j.X * 6.2f + age * 0.5f, 5f + k + c * 13, eye);
            }
        }

        FlushColumns(renderer, n, false);
    }

    /// <summary>Where horizon column <paramref name="c"/> of <paramref name="columns"/> stands (its foot) and how
    /// far off it is (its size goes by it): its way hashed from the map, past the far clip.</summary>
    public static (Vector3 Foot, float Distance) ColumnFoot(int map, int c, int columns, float far, Vector3 eye, GroundSpan? ground)
    {
        var seed = Hash3(map, 31);
        var h = Hash3(map * 7 + c, 32);
        var angle = (seed.Y + c / (float)columns + 0.25f * (h.X - 0.5f) / columns) * MathF.Tau;
        var way = new Vector3(MathF.Sin(angle), 0f, MathF.Cos(angle));
        var d = far * HorizonDistance * (1f + 0.25f * h.Y);

        // No ground: round the eye, a little under it (it goes where the eye goes).
        if (ground is not { } g)
        {
            return (eye + way * d - new Vector3(0f, d * 0.06f, 0f), d);
        }

        // Past the ground's edge, fixed in the world (a strafe slides the hills past it, not it
        // along them), its foot a little under the ground's level.
        var reach = g.Radius + d;
        return (g.Centre + way * reach - new Vector3(0f, reach * 0.02f, 0f), reach);
    }

    private void EnsureColumns(int count)
    {
        if (_columns.Length < count)
        {
            _columns = new Particle[count];
            _columnKeys = new float[count];
        }
    }

    private void Push(ref int n, Vector3 position, float size, Vector4 colour, float spin, float seed, Vector3 eye)
    {
        _columnKeys[n] = -Vector3.DistanceSquared(position, eye);
        _columns[n++] = new Particle(position, size, colour, spin, seed);
    }

    private void FlushColumns(IFrameRecorder renderer, int n, bool hazed)
    {
        Array.Sort(_columnKeys, _columns, 0, n);
        renderer.DrawParticles(_columns.AsSpan(0, n), ParticleBlend.Alpha, hazed ? 120f : 1000f, hazed);
    }

    /// <summary>A place's hash in [0, 1) (a fire's phase, whether a distant fire raises a column).</summary>
    private static float PlaceHash(Vector3 at) => Hash3((int)(at.X * 0.37f) ^ (int)(at.Z * 0.71f) * 7919, 3).X;

    /// <summary>The smoke sources of a map: its fire lamps (the flame's light sits at its base +
    /// 0.35 of its height, and its range is 9 times the height, clamped: <see cref="EmissiveLamps.Fires"/>),
    /// and the burning sprites dropped by destruction (their foot; <see cref="EmissiveLamps.IsFire"/>).</summary>
    public static IEnumerable<SmokeSource> SourcesOf(IEnumerable<LampLight> lamps, IEnumerable<(int Sprite, Vector3 At)> drops)
    {
        foreach (var lamp in lamps)
        {
            if (lamp.Flicker)
            {
                var h = lamp.Range / EmissiveLamps.FireRangeScale;
                yield return new SmokeSource(lamp.Position + new Vector3(0f, h * 0.65f, 0f), h);
            }
        }

        foreach (var (sprite, at) in drops)
        {
            if (EmissiveLamps.IsFire(sprite))
            {
                yield return new SmokeSource(at + new Vector3(0f, 55f, 0f), 60f);
            }
        }
    }

    private static Vector3 Wrap(Vector3 v) => new(Fract(v.X + 0.5f) - 0.5f, Fract(v.Y + 0.5f) - 0.5f, Fract(v.Z + 0.5f) - 0.5f);

    private static float Fract(float x) => x - MathF.Floor(x);

    private static float SmoothStep(float a, float b, float x)
    {
        var t = Math.Clamp((x - a) / (b - a), 0f, 1f);
        return t * t * (3f - 2f * t);
    }

    /// <summary>Three hashed values in [0, 1) of an index and a stream.</summary>
    internal static Vector3 Hash3(int index, int stream)
    {
        var x = (uint)index * 0x9E3779B1u ^ (uint)stream * 0x85EBCA77u;
        return new Vector3(Next(ref x), Next(ref x), Next(ref x));

        static float Next(ref uint s)
        {
            s ^= s >> 16;
            s *= 0x7FEB352Du;
            s ^= s >> 15;
            s *= 0x846CA68Bu;
            s ^= s >> 16;
            return (s >> 8) / 16777216f;
        }
    }
}
