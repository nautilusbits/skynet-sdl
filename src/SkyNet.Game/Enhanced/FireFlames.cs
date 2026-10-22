using System.Numerics;
using RetroEng.Render;
using SkyNet.Data;
using SkyNet.Game.Menu;

namespace SkyNet.Game.Enhanced;

/// <summary>A fire's flame in the enhanced look: the foot of the flame, its size (units), the
/// flicker's phase (the same as its lamp's, <see cref="EmissiveLamps.PhaseOf"/>) and a seed for
/// its shape.</summary>
public readonly record struct FireFlame(Vector3 Base, float Width, float Height, float Phase, float Seed);

/// <summary>How the enhanced look draws a sprite under its flames (<see cref="FireFlames.SpriteLook"/>):
/// as DOS does, its painted flames cut out (<see cref="FireFlames.MaskPalette"/>), or not at all.</summary>
public enum FireSpriteLook { Dos, Masked, Hidden }

/// <summary>
/// The enhanced look's fires: where the original look draws a DOS fire sprite, a flame of tongues
/// standing in a volume, each tongue three upright sheets of rising noise crossed about its axis and
/// fixed in the world (<see cref="ParticleShape.Flame"/> with an axis, added to the scene): walked
/// around, the tongues shift against each other and no card turns with the eye. Its brightness
/// follows its lamp's flicker (<see cref="EmissiveLamps.FlickerScale"/>), embers rise out of it.
/// The smoke over it is <see cref="Atmosphere"/>'s. Nothing is kept: a frozen clock draws the same frame.
/// <code>
///   per fire (from above):   .  ╲│╱      tongue 0 in the middle, the tallest; the others on a ring
///                          ╲│╱ ─┼─      round it (its own angle and height per fire), shorter
///                          ─┼─ ╱│╲  .   each tongue: sheets at a, a + 60°, a + 120° (a by its seed),
///                          ╱│╲          each as bright as it faces the eye (cos²; the three sum to 1.5)
///   embers:   on a loop of EmberLife seconds, born in the flame, rising, drifting downwind,
///             wobbling, cooling (short streaks along their way)
/// </code>
/// </summary>
public sealed class FireFlames
{
    /// <summary>Tongues per fire at PARTICLES OFF (none: the DOS sprite), LOW, HIGH.</summary>
    public static readonly int[] Tongues = [0, 3, 5];
    /// <summary>Sheets per tongue, crossed at 180° / Sheets about its axis.</summary>
    public const int Sheets = 3;
    /// <summary>Embers per fire and the fires that throw them (the nearest) per level.</summary>
    public static readonly int[] EmbersPerFire = [0, 5, 12];
    public static readonly int[] EmberFires = [0, 8, 16];
    /// <summary>Fires farther than this (units) throw no embers.</summary>
    public const float EmberReach = 2400f;
    /// <summary>An ember's life (seconds).</summary>
    public const float EmberLife = 2.4f;
    /// <summary>A flame's brightness (linear) at full flicker.</summary>
    public const float Brightness = 1.5f;
    /// <summary>The ring the outer tongues stand on (its radius over the flame's width).</summary>
    public const float Ring = 0.28f;
    /// <summary>The ember's colour (linear, added).</summary>
    public static readonly Vector3 EmberColour = new(3.2f, 1.1f, 0.22f);

    private Particle[] _flames = [];
    private Particle[] _embers = [];
    private readonly List<(FireFlame Fire, float Distance)> _near = [];

    /// <summary>A palette (RGBA8, 256 entries) with its fire colours cut out (alpha 0: clear in the
    /// enhanced look's colour textures, <see cref="ColourMips.Expand(ReadOnlySpan{byte}, ReadOnlySpan{byte})"/>):
    /// a burning barrel's, camp fire's or wreck's sprite drawn through it keeps its body and loses
    /// its painted flames. A fire colour is bright, red over green over blue, and saturated.</summary>
    public static byte[] MaskPalette(ReadOnlySpan<byte> rgba)
    {
        var mask = rgba.ToArray();
        for (var i = 0; i + 3 < mask.Length; i += 4)
        {
            int r = mask[i], g = mask[i + 1], b = mask[i + 2];
            if (IsFireColour(r, g, b))
            {
                mask[i + 3] = 0;
            }
        }

        return mask;
    }

    /// <summary>Whether a palette colour (0-255) is a flame's: bright, R ≥ G > B, R − B at least half of R.</summary>
    public static bool IsFireColour(int r, int g, int b) => r >= 170 && r >= g && g > b && (r - b) * 2 >= r;

    /// <summary>Whether the enhanced look draws <paramref name="sprite"/> as a flame alone; a burning
    /// barrel keeps its sprite, its painted flames cut out (<see cref="MaskPalette"/>), under the flame.
    /// A camp fire's or a wreck's flames are drawn in dark reds too, which the mask leaves: their
    /// ragged DOS flame would show behind the new one.</summary>
    public static bool Replaces(int sprite) =>
        EmissiveLamps.IsFire(sprite) && (!EmissiveLamps.GodotFires.TryGetValue(sprite, out var f) || f.Kind != EmissiveLamps.FireKind.Barrel);

    /// <summary>Whether the enhanced look burns its flames at <paramref name="particles"/>: PARTICLES
    /// OFF keeps the DOS fire sprites, as it keeps the DOS impacts.</summary>
    public static bool Burns(ParticleLevel particles) => particles != ParticleLevel.Off;

    /// <summary>How the enhanced look draws a map's or a drop's <paramref name="sprite"/> at
    /// <paramref name="particles"/> (its flames on: the level's <c>Flames</c>).</summary>
    public static FireSpriteLook SpriteLook(int sprite, ParticleLevel particles)
    {
        if (!EmissiveLamps.IsFire(sprite) || !Burns(particles))
        {
            return FireSpriteLook.Dos;
        }

        return Replaces(sprite) ? FireSpriteLook.Hidden : FireSpriteLook.Masked;
    }

    /// <summary>The flame of a fire sprite standing on <paramref name="foot"/>, <paramref name="width"/>
    /// × <paramref name="height"/> units (null: not a fire), shaped by its kind as its lamp is
    /// (<see cref="EmissiveLamps.Fires"/>): a camp fire's and a wreck's lower, a barrel's on its top.</summary>
    public static FireFlame? Of(int sprite, Vector3 foot, float width, float height, int seed)
    {
        if (!EmissiveLamps.IsFire(sprite))
        {
            return null;
        }

        var (kind, hScale) = EmissiveLamps.GodotFires.TryGetValue(sprite, out var info) ? info : (EmissiveLamps.FireKind.Flame, 1f);
        var h = height * hScale;
        var w = width * 0.9f;
        var lift = 0f;
        switch (kind)
        {
            case EmissiveLamps.FireKind.Camp:
                h *= 0.8f;
                w *= 0.8f;
                break;
            case EmissiveLamps.FireKind.Wreck:
                h *= 0.9f;
                break;
            case EmissiveLamps.FireKind.Barrel:
                lift = h * 0.5f;
                h *= 0.7f;
                w *= 0.6f;
                break;
        }

        return new FireFlame(foot + new Vector3(0f, lift, 0f), w, h, EmissiveLamps.PhaseOf(seed), (uint)seed * 2654435761u % 997u / 97f);
    }

    /// <summary>Queues the flames and their embers (the enhanced look only: the renderer queues
    /// nothing without its lighting), drawn as <paramref name="effects"/> says (null: <see cref="EffectTable.Current"/>).</summary>
    public void Draw(IFrameRecorder renderer, IReadOnlyList<FireFlame> fires, ParticleLevel level, Vector3 eye, float seconds, bool outdoor,
        EffectTable? effects = null)
    {
        if (fires.Count == 0)
        {
            return;
        }

        effects ??= EffectTable.Current;
        var row = effects[EffectTable.Fire];
        var tongues = EffectTable.Count(row, level);
        if (tongues == 0)
        {
            return;
        }

        if (_flames.Length < fires.Count * tongues * Sheets)
        {
            _flames = new Particle[fires.Count * tongues * Sheets];
        }

        // The noise's clock, wrapped before the shader's hash loses its fraction.
        var clock = seconds % 600f * (float)row.Speed;
        float size = (float)row.Size, spread = (float)row.Spread, flickers = (float)row.Flicker;
        var light = EffectTable.Light(row);
        // A sheet's share: the three of a tongue sum to 1.5 times one facing the eye.
        const float sheetShare = 2f / 3f;
        var n = 0;
        foreach (var f in fires)
        {
            // The row's flicker scales the swing round 1 (0: steady).
            var flicker = EmissiveLamps.FlickerScale(seconds, f.Phase) / 0.82f * flickers + (1f - flickers);
            var seed = (int)(f.Seed * 97f);
            for (var t = 0; t < tongues; t++)
            {
                // Tongue 0 in the middle; the others round it, each at its own angle, distance and height.
                var j = Atmosphere.Hash3(t + seed * 7, 12);
                var place = Vector3.Zero;
                if (t > 0)
                {
                    var angle = MathF.Tau * ((t - 1f) / (tongues - 1f) + j.X * 0.25f + f.Seed);
                    var radius = f.Width * Ring * (0.7f + 0.5f * j.Y) * spread;
                    place = new Vector3(MathF.Cos(angle) * radius, 0f, MathF.Sin(angle) * radius);
                }

                var w = f.Width * (t == 0 ? 0.75f : 0.42f + 0.12f * j.Z) * size;
                var h = f.Height * (t == 0 ? 1.15f : 0.65f + 0.35f * j.Z) * (0.94f + 0.06f * flicker) * size;
                var brightness = Brightness * flicker * (t == 0 ? 1f : 0.7f) * sheetShare;
                var turn = MathF.Tau * j.Z;
                for (var k = 0; k < Sheets; k++)
                {
                    var a = turn + MathF.PI * k / Sheets;
                    _flames[n++] = new Particle(f.Base + place + new Vector3(0f, h * 0.5f, 0f), w,
                        new Vector4(light * brightness, 1f), Spin: clock * (1f + 0.11f * k + 0.07f * t) + f.Phase,
                        Seed: f.Seed + k * 1.618f + t * 2.414f, Shape: ParticleShape.Flame, Stretch: h / MathF.Max(w, 1e-3f),
                        Axis: new Vector3(MathF.Cos(a), 0f, MathF.Sin(a)));
                }
            }
        }

        renderer.DrawParticles(_flames.AsSpan(0, n), ParticleBlend.Add, 14f);
        DrawEmbers(renderer, fires, level, eye, seconds, outdoor, effects[EffectTable.Embers]);
    }

    private void DrawEmbers(IFrameRecorder renderer, IReadOnlyList<FireFlame> fires, ParticleLevel level, Vector3 eye, float seconds,
        bool outdoor, EffectRow row)
    {
        var per = EffectTable.Count(row, level);
        if (per == 0)
        {
            return;
        }

        _near.Clear();
        foreach (var f in fires)
        {
            var d = Vector3.Distance(f.Base, eye);
            if (d < EmberReach)
            {
                _near.Add((f, d));
            }
        }

        _near.Sort((a, b) => a.Distance.CompareTo(b.Distance));
        var count = Math.Min(_near.Count, EmberFires[(int)level]);
        if (_embers.Length < count * per)
        {
            _embers = new Particle[count * per];
        }

        var wind = outdoor ? Atmosphere.OutdoorWind : Atmosphere.IndoorWind;
        float emberLife = (float)row.Life, size = (float)row.Size, spread = (float)row.Spread;
        var colour = EmberColour * EffectTable.Light(row);
        var n = 0;
        for (var s = 0; s < count; s++)
        {
            var f = _near[s].Fire;
            var rise = f.Height * 1.9f / emberLife * (float)row.Speed;
            for (var k = 0; k < per; k++)
            {
                var j = Atmosphere.Hash3(k + (int)(f.Seed * 97f) * 31, 11);
                var life = emberLife * (0.6f + 0.5f * j.Z);
                var age = Fract(seconds / life + j.X + f.Phase);
                var t = age * life;
                var wobble = new Vector3(MathF.Sin(t * 3.1f + j.Y * 20f), 0f, MathF.Cos(t * 2.7f + j.X * 20f)) * (f.Width * 0.12f * age) * spread;
                var start = new Vector3((j.Y - 0.5f) * f.Width * 0.5f * spread, f.Height * 0.25f, (j.Z - 0.5f) * f.Width * 0.5f * spread);
                var speed = rise * (0.7f + 0.6f * j.Y);
                var velocity = new Vector3(0f, speed * (1f - 0.35f * age), 0f) + wind * 1.4f;
                var position = f.Base + start + new Vector3(0f, speed * t * (1f - 0.17f * age), 0f) + wind * (1.4f * t) + wobble;
                var heat = MathF.Pow(1f - age, 1.5f) * SmoothStep(0f, 0.06f, age);
                // Twinkling as it turns.
                heat *= 0.7f + 0.3f * MathF.Sin(t * 17f + j.X * 40f);
                _embers[n++] = new Particle(position, (1.1f + 0.9f * j.Z) * size, new Vector4(colour, MathF.Max(heat, 0f)),
                    Shape: ParticleShape.Streak, Axis: velocity * 0.05f);
            }
        }

        renderer.DrawParticles(_embers.AsSpan(0, n), ParticleBlend.Add, 6f);
    }

    private static float Fract(float x) => x - MathF.Floor(x);

    private static float SmoothStep(float a, float b, float x)
    {
        var t = Math.Clamp((x - a) / (b - a), 0f, 1f);
        return t * t * (3f - 2f * t);
    }
}
