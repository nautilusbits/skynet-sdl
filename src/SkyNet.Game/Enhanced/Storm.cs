using System.Numerics;
using RetroEng.Audio;
using RetroEng.Render;
using SkyNet.Game.Audio;

namespace SkyNet.Game.Enhanced;

/// <summary>Whether the map shown is under the sky (a storm shows there only).</summary>
public enum StormSky { Indoor, Outdoor }

/// <summary>One line of a bolt in its own plane: x across, y up (0 the ground, 1 the cloud), its
/// width and brightness relative to the main channel's.</summary>
public sealed record BoltStroke(Vector2[] Points, float Width, float Brightness);

/// <summary>A lightning strike: where (its way round the eye, radians from +Z; its distance), how
/// its flash flickers (pulse start in seconds, strength) and the bolt's shape.</summary>
public sealed record StormStrike(int Seed, float Metres, float Azimuth, (float At, float Strength)[] Pulses, BoltStroke[] Bolt)
{
    /// <summary>The unit way from the eye to the bolt (level).</summary>
    public Vector3 Way => new(MathF.Sin(Azimuth), 0f, MathF.Cos(Azimuth));
}

/// <summary>What a frame shows of the storm: the strike (null: none), its age, the flash's strength
/// (scaled by the table's brightness and the distance) and the bolt's cover.</summary>
public readonly record struct StormFrame(StormStrike? Strike, float Age, float Flash, float BoltAlpha);

/// <summary>
/// Lightning and thunder over an outdoor map in the enhanced look, from a mod's
/// <see cref="StormTable"/>. Presentation only: its own random numbers and clock, nothing of the
/// simulation read or changed, so demos and network games play the same with it.
/// <code>
///   Tick (sim thread)  ── waits an exponential interval ──► strike: 2-3 flash pulses, a bolt,
///                          thunder queued for distance / 343 m/s ──► Thunder callback (Effects bus)
///   ApplyLight (build) ── the level's Lighting + a flash: sun from the bolt, sky and ground brighter
///   Draw (build)       ── the bolt as additive soft particles past the far clip
/// </code>
/// </summary>
public sealed class Storm
{
    /// <summary>World units per metre (docs/modding.md: the soldier's eye 75 u up, about 40 u a metre).</summary>
    public const float UnitsPerMetre = 40f;
    /// <summary>The speed of sound, in world units a second.</summary>
    public const float SoundSpeed = 343f * UnitsPerMetre;
    /// <summary>The shortest wait between two strikes (seconds), whatever the mean.</summary>
    public const float MinGap = 0.8f;
    /// <summary>A pulse's decay (seconds) and how long the bolt fades after the last pulse.</summary>
    public const float PulseDecay = 0.07f, BoltHold = 0.08f, BoltFade = 0.2f;
    /// <summary>Strikes nearer than this (metres) crack; the flash and thunder are full up to it.</summary>
    public const float NearMetres = 2000f, FullMetres = 1500f;
    /// <summary>The cloud base (metres): sets how high the bolt reaches over the horizon.</summary>
    public const float CloudBase = 1500f;
    /// <summary>The bolt is drawn this many far clips away (past the terrain), scaled to look as far as it is.</summary>
    public const float DrawDistance = 1.6f;
    /// <summary>The flash light's elevation (degrees): from above the bolt's way.</summary>
    public const float FlashElevation = 35f;
    /// <summary>A flash weaker than this leaves the light as it is.</summary>
    public const float MinFlash = 0.01f;

    /// <summary>The flash (linear): the light from the bolt's way and the sky's.</summary>
    public static readonly Vector3 FlashLight = new(0.78f, 0.84f, 1f), FlashSky = new(0.55f, 0.6f, 0.8f);
    /// <summary>The flash's gain on the sun, the sky and the ground light.</summary>
    public const float SunGain = 3f, SkyGain = 2f, GroundGain = 0.5f;
    /// <summary>The clear sky's colour at a full flash (sRGB, added).</summary>
    public static readonly Vector4 SkyFlash = new(0.42f, 0.46f, 0.58f, 0f);
    /// <summary>The bolt's core and glow (linear light, HDR: the core glows).</summary>
    public static readonly Vector3 CoreColour = new(2.6f, 2.8f, 3.4f), GlowColour = new(0.16f, 0.2f, 0.34f);
    /// <summary>The core's width as an angle (radians) and the glow's as a multiple of it.</summary>
    public const float CoreAngle = 0.004f, GlowScale = 7f;

    /// <summary>A thunder's place from the eye: this far towards the bolt and this high, so it comes
    /// from the bolt's side and both ears hear it (a diffuse roll; the pan stays under 0.1).</summary>
    private const float ThunderOffset = 1000f, ThunderHeight = 10000f;
    /// <summary>A thunder's gain is the table's volume, falling as the root of the distance past
    /// <see cref="FullMetres"/>, never under this.</summary>
    public const float ThunderFloor = 0.5f;
    private const int MaxParticles = 6000;

    private readonly Random _random;
    private object? _scene;
    private float _wait = -1f;
    private bool _forced;
    private Vector3? _forcedWay;
    private StormStrike? _strike;
    private float _age;
    private readonly List<(float Left, int Seed, ThunderKind Kind, float Gain, Vector3 Way, Sound? Recorded)> _thunder = [];

    // The frame build's own (ApplyLight, Draw): never touched by Tick.
    private Lighting? _base, _lit;
    private Particle[] _particles = new Particle[256];

    /// <param name="seed">The storm's own random numbers (null: from the clock).</param>
    public Storm(int? seed = null) => _random = seed is { } s ? new Random(s) : new Random();

    /// <summary>Plays a thunder: the sound, its place (world units, a little way towards the bolt
    /// from the eye) and its gain (0-1). Null: no thunder (the editor).</summary>
    public Action<Sound, Vector3, float>? Thunder { get; set; }

    /// <summary>The last <see cref="Tick"/>'s frame.</summary>
    public StormFrame Frame { get; private set; }

    /// <summary>Thunders queued, not yet heard.</summary>
    public int ThunderPending => _thunder.Count;

    /// <summary>Asks for a strike at the next <see cref="Tick"/> (the console's <c>storm strike</c>),
    /// within 15 degrees of <paramref name="ahead"/> when given (null: anywhere).</summary>
    public void Strike(Vector3? ahead = null) => (_forced, _forcedWay) = (true, ahead);

    /// <summary>Moves the storm on by <paramref name="dt"/> seconds (0: held, e.g. paused) over
    /// <paramref name="scene"/> (a new one starts afresh). No table, or indoors: nothing.</summary>
    public StormFrame Tick(float dt, StormTable? table, object? scene, StormSky sky, Vector3 eye)
    {
        if (table is not { On: true } || sky != StormSky.Outdoor)
        {
            Reset(scene);
            _forced = false;
            return Frame = default;
        }

        if (!ReferenceEquals(scene, _scene))
        {
            Reset(scene);
        }

        if (_wait < 0f)
        {
            _wait = NextWait(table.Interval);
        }

        HearThunder(dt, eye);
        _age += dt;
        _wait -= dt;
        if (_forced || _wait <= 0f)
        {
            _forced = false;
            _wait = NextWait(table.Interval);
            Begin(table);
        }

        return Frame = FrameOf(table);
    }

    /// <summary>The next wait (seconds): exponential with mean <paramref name="mean"/> (strikes at
    /// random, at that rate), no shorter than <see cref="MinGap"/>.</summary>
    public float NextWait(float mean) => MathF.Max(MinGap, -MathF.Log(1f - (float)_random.NextDouble()) * mean);

    /// <summary>The thunder's delay for a bolt <paramref name="units"/> world units away.</summary>
    public static float ThunderDelay(float units) => units / SoundSpeed;

    /// <summary>The gain (0-1) of a thunder <paramref name="metres"/> away at the table's <paramref name="volume"/>.</summary>
    public static float ThunderGain(float volume, float metres) => volume * Math.Clamp(MathF.Sqrt(FullMetres / metres), ThunderFloor, 1f);

    /// <summary>The flash at <paramref name="age"/>: the strongest pulse, each falling off from its start.</summary>
    public static float FlashAt(StormStrike strike, float age)
    {
        var flash = 0f;
        foreach (var (at, strength) in strike.Pulses)
        {
            if (age >= at)
            {
                flash = MathF.Max(flash, strength * MathF.Exp(-(age - at) / PulseDecay));
            }
        }

        return flash;
    }

    /// <summary>The bolt's cover: flickering with the pulses, then fading over <see cref="BoltFade"/>.</summary>
    public static float BoltAlphaAt(StormStrike strike, float age)
    {
        var hold = strike.Pulses[^1].At + BoltHold;
        if (age < hold)
        {
            return 0.55f + 0.45f * MathF.Min(1f, FlashAt(strike, age));
        }

        return Math.Clamp(1f - (age - hold) / BoltFade, 0f, 1f) * 0.55f;
    }

    /// <summary>Whether a strike is over at <paramref name="age"/>.</summary>
    public static bool Over(StormStrike strike, float age) =>
        age > strike.Pulses[^1].At + BoltHold + BoltFade && FlashAt(strike, age) < MinFlash;

    /// <summary>The flash's strength for a bolt <paramref name="metres"/> away: full up to
    /// <see cref="FullMetres"/>, weaker farther (a third at most).</summary>
    public static float DistanceScale(float metres) => Math.Clamp(FullMetres / metres, 0.35f, 1f);

    /// <summary><paramref name="lighting"/> lit by the frame's flash: the sun along the flash's way,
    /// the sky and ground brighter, the stars washed out; as it is with no flash.</summary>
    public static Lighting Lit(Lighting lighting, in StormFrame frame)
    {
        if (frame.Strike is not { } strike || frame.Flash <= MinFlash)
        {
            return lighting;
        }

        var k = frame.Flash;
        var e = FlashElevation * MathF.PI / 180f;
        var source = Vector3.Normalize(strike.Way * MathF.Cos(e) + Vector3.UnitY * MathF.Sin(e));
        return lighting with
        {
            SunDirection = -source,
            Sun = lighting.Sun + FlashLight * (k * SunGain),
            Sky = lighting.Sky + FlashSky * (k * SkyGain),
            Ground = lighting.Ground + FlashSky * (k * GroundGain),
            Stars = lighting.Stars is { } stars ? stars with { Brightness = stars.Brightness * MathF.Max(0f, 1f - k) } : null,
        };
    }

    /// <summary>The clear sky's colour in a flash.</summary>
    public static Vector4 SkyOf(Vector4 sky, in StormFrame frame) =>
        frame.Flash <= MinFlash ? sky : Vector4.Min(sky + SkyFlash * MathF.Min(1f, frame.Flash), Vector4.One);

    /// <summary>Sets the renderer's light for the frame: the level's own (whatever was last set from
    /// outside) with the flash on it. On the thread that builds the frame.</summary>
    public void ApplyLight(Renderer renderer, in StormFrame frame)
    {
        if (renderer.Lighting is not { } now)
        {
            (_base, _lit) = (null, null);
            return;
        }

        // A lighting set from outside (a look change, a new map) is the new base.
        if (!ReferenceEquals(now, _lit))
        {
            _base = now;
        }

        _lit = Lit(_base!, frame);
        renderer.Lighting = _lit;
    }

    /// <summary>Queues the bolt seen from <paramref name="eye"/> as additive particles, drawn
    /// <see cref="DrawDistance"/> far clips away and sized to look as far away as it is, as
    /// <paramref name="effects"/> says (null: <see cref="EffectTable.Current"/>).</summary>
    public void Draw(IFrameRecorder renderer, in StormFrame frame, Vector3 eye, float far, EffectTable? effects = null)
    {
        if (frame.Strike is not { } strike || frame.BoltAlpha <= 0f)
        {
            return;
        }

        var distance = far * DrawDistance;
        var elevation = Math.Clamp(MathF.Atan(CloudBase / strike.Metres), 6f * MathF.PI / 180f, 40f * MathF.PI / 180f);
        var height = distance * MathF.Tan(elevation);
        var way = strike.Way;
        var across = Vector3.Normalize(Vector3.Cross(way, Vector3.UnitY));
        // The foot a little under the horizon: behind the far hills.
        var foot = eye + way * distance - Vector3.UnitY * (height * 0.15f);
        var row = (effects ?? EffectTable.Current)[EffectTable.Lightning];
        var core = distance * CoreAngle * (float)row.Size;
        Vector3 coreColour = CoreColour * EffectTable.Light(row), glowColour = GlowColour * EffectTable.Light(row);
        var n = 0;
        foreach (var stroke in strike.Bolt)
        {
            var size = core * stroke.Width;
            var step = size * 0.5f;
            var light = frame.BoltAlpha * stroke.Brightness;
            var travelled = 0f;
            for (var i = 1; i < stroke.Points.Length && n < MaxParticles; i++)
            {
                var a = foot + across * (stroke.Points[i - 1].X * height) + Vector3.UnitY * (stroke.Points[i - 1].Y * height);
                var b = foot + across * (stroke.Points[i].X * height) + Vector3.UnitY * (stroke.Points[i].Y * height);
                var length = Vector3.Distance(a, b);
                for (var t = 0f; t < length && n < MaxParticles; t += step, travelled += step)
                {
                    var at = Vector3.Lerp(a, b, t / length);
                    Push(ref n, new Particle(at, size, new Vector4(coreColour * light, 1f), 0f, 0f));
                    // The glow round it, sparser (every few core steps).
                    if ((int)(travelled / step) % 5 == 0)
                    {
                        Push(ref n, new Particle(at, size * GlowScale, new Vector4(glowColour * light, 1f), 0f, 0f));
                    }
                }
            }
        }

        renderer.DrawParticles(_particles.AsSpan(0, n), ParticleBlend.Add, 1000f, false);
    }

    private void Push(ref int n, in Particle p)
    {
        if (n == _particles.Length)
        {
            Array.Resize(ref _particles, Math.Min(_particles.Length * 2, MaxParticles));
        }

        _particles[n++] = p;
    }

    /// <summary>A new scene (or the storm stopping): no strike, nothing to hear, a fresh wait.</summary>
    private void Reset(object? scene)
    {
        (_scene, _strike, _age, _wait) = (scene, null, 0f, -1f);
        _thunder.Clear();
    }

    /// <summary>Starts a strike: its place, its pulses, its bolt and its thunder.</summary>
    private void Begin(StormTable table)
    {
        var seed = _random.Next();
        var r = new Random(seed);
        var metres = table.NearMetres + (table.FarMetres - table.NearMetres) * (float)r.NextDouble();
        var azimuth = (float)(r.NextDouble() * Math.Tau);
        if (_forcedWay is { } ahead)
        {
            const float Spread = 15f * MathF.PI / 180f;
            azimuth = MathF.Atan2(ahead.X, ahead.Z) + ((float)r.NextDouble() * 2f - 1f) * Spread;
            _forcedWay = null;
        }
        _strike = new StormStrike(seed, metres, azimuth, Pulses(r), Bolt.Make(r));
        _age = 0f;
        var gain = ThunderGain(table.ThunderVolume, metres);
        if (gain > 0f)
        {
            _thunder.Add((ThunderDelay(metres * UnitsPerMetre), seed, metres < NearMetres ? ThunderKind.Near : ThunderKind.Far, gain, _strike.Way, table.Recorded));
        }
    }

    /// <summary>Two or three flashes: at once, then 0.06-0.16 s apart, each weaker or as strong.</summary>
    private static (float, float)[] Pulses(Random r)
    {
        var count = 2 + r.Next(2);
        var pulses = new (float, float)[count];
        var at = 0f;
        for (var i = 0; i < count; i++)
        {
            pulses[i] = (at, i == 0 ? 1f : 0.45f + 0.5f * (float)r.NextDouble());
            at += 0.06f + 0.1f * (float)r.NextDouble();
        }

        return pulses;
    }

    private StormFrame FrameOf(StormTable table)
    {
        if (_strike is not { } strike)
        {
            return default;
        }

        if (Over(strike, _age))
        {
            _strike = null;
            return default;
        }

        var flash = FlashAt(strike, _age) * table.Brightness * DistanceScale(strike.Metres);
        return new StormFrame(strike, _age, flash, BoltAlphaAt(strike, _age) * MathF.Min(table.Brightness, 1f));
    }

    /// <summary>Counts the queued thunders down; one that is due is made (or the mod's own) and played.</summary>
    private void HearThunder(float dt, Vector3 eye)
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
            Thunder?.Invoke(t.Recorded ?? Audio.Thunder.Make(t.Seed, t.Kind), eye + t.Way * ThunderOffset + Vector3.UnitY * ThunderHeight, t.Gain);
        }
    }

    /// <summary>The bolt's shape: a jagged channel from the cloud to the ground and a few branches
    /// off its upper part, each made by midpoint displacement.</summary>
    public static class Bolt
    {
        /// <summary>Halvings of the main channel and of a branch (2^n segments).</summary>
        public const int MainDepth = 7, BranchDepth = 5;
        /// <summary>How far a midpoint moves sideways, per unit of its segment's length.</summary>
        public const float Jag = 0.22f;

        public static BoltStroke[] Make(Random r)
        {
            var top = new Vector2(((float)r.NextDouble() - 0.5f) * 0.2f, 1f);
            var bottom = new Vector2(top.X + ((float)r.NextDouble() - 0.5f) * 0.5f, 0f);
            var main = Jagged(top, bottom, MainDepth, r);
            List<BoltStroke> strokes = [new BoltStroke(main, 1f, 1f)];
            var branches = 3 + r.Next(4);
            for (var i = 0; i < branches; i++)
            {
                // From the upper 70 % of the channel (index 0 is the cloud), down and out to one side.
                var from = main[r.Next(1, main.Length * 7 / 10)];
                var side = r.Next(2) == 0 ? -1f : 1f;
                var length = 0.12f + 0.28f * (float)r.NextDouble();
                var angle = (25f + 40f * (float)r.NextDouble()) * MathF.PI / 180f;
                var to = from + new Vector2(side * MathF.Sin(angle), -MathF.Cos(angle)) * length;
                strokes.Add(new BoltStroke(Jagged(from, to, BranchDepth, r), 0.6f, 0.45f));
            }

            return [.. strokes];
        }

        /// <summary>A line from <paramref name="a"/> to <paramref name="b"/> broken into 2^depth
        /// segments, each midpoint pushed sideways by up to <see cref="Jag"/> of its segment.</summary>
        public static Vector2[] Jagged(Vector2 a, Vector2 b, int depth, Random r)
        {
            var points = new Vector2[(1 << depth) + 1];
            points[0] = a;
            points[^1] = b;
            for (var span = points.Length - 1; span > 1; span /= 2)
            {
                for (var i = 0; i + span < points.Length; i += span)
                {
                    var (p, q) = (points[i], points[i + span]);
                    var normal = new Vector2(-(q.Y - p.Y), q.X - p.X);
                    var shift = ((float)r.NextDouble() - 0.5f) * 2f * Jag;
                    points[i + span / 2] = (p + q) * 0.5f + normal * shift;
                }
            }

            return points;
        }
    }
}
