using System.Numerics;
using RetroEng.Render;
using SkyNet.Game.Menu;
using SkyNet.Game.Weapons;

namespace SkyNet.Game.Enhanced;

/// <summary>What a muzzle flash is drawn with: the DOS flare (TEXTURE.219, the original look) or
/// <see cref="MuzzleFlashes"/>' particles (the enhanced look).</summary>
public enum FlashArt { Dos, Particles }

/// <summary>A muzzle flash to draw: where, the shot's way (unit; zero: unknown), its size (world
/// units: the DOS flare's), its colour (linear), its seed and its age (seconds).</summary>
public readonly record struct MuzzleShot(Vector3 At, Vector3 Direction, float Size, Vector3 Colour, int Seed, float Age);

/// <summary>A flash's look by its seed (<see cref="MuzzleFlashes.ShapeOf"/>).</summary>
public readonly record struct FlashShape(float Turn, float Scale, int Variant);

/// <summary>A 3D gun's flash triangles (<see cref="MuzzleFlashes.GunFlashMesh"/>): the soft disc's
/// first <paramref name="FanCount"/> vertices, then each petal layout's range.</summary>
public sealed record GunFlashGeometry(Vertex[] Vertices, int FanCount, (int First, int Count)[] Variants);

/// <summary>
/// The enhanced look's muzzle flash, built from soft particles (no image), added and HDR-bright so
/// GLOW picks it up; the flash's light stays <see cref="GameLights.FlashLight"/>'s. GPU-free: the
/// weapons' view (<see cref="WeaponRenderer"/>) queues it.
/// <code>
///          \  |  /             petals: 4-6 tapered streaks across the view, turned per shot
///       --- (( @ ))=====>      core: a white-hot disc in a wider glow of the shot's colour
///          /  |  \   . '       burst: a cone along the barrel, a few sparks thrown forward
///                              then a wisp of smoke drifting off (<see cref="MuzzleSmoke"/>)
/// </code>
/// It burns out over <see cref="WeaponSystem.FlashLife"/> (a frame or two at full heat). Bullets and
/// rockets flash fire-orange, lasers and plasma in their bolt's colour (<see cref="ShotLightTable"/>).
/// </summary>
public sealed class MuzzleFlashes
{
    /// <summary>Petals at least and at most.</summary>
    public const int MinPetals = 4, MaxPetals = 6;
    /// <summary>Sparks thrown forward per flash.</summary>
    public const int Sparks = 3;

    /// <summary>A bullet's (and a rocket's) flash colour (sRGB): warm yellow-orange.</summary>
    public static readonly Vector3 FireSrgb = new(1f, 0.62f, 0.24f);
    private static readonly Vector3 Fire = LevelLights.Linear(FireSrgb);

    // Brightness (linear, over 1: GLOW's bloom) of the parts.
    private const float CoreHeat = 9f, GlowHeat = 2.2f, PetalHeat = 5f, ConeHeat = 4f, SparkHeat = 6f;
    // How deep (units) the particles fade where they meet a surface.
    private const float Softness = 6f;
    // Hash streams (Atmosphere.Hash3).
    private const int ShapeStream = 71, PetalStream = 72, SparkStream = 73, SmokeStream = 74;

    private readonly List<Particle> _flashes = [];
    private Particle[] _smoke = [];

    /// <summary>The art of a muzzle flash in <paramref name="look"/>.</summary>
    public static FlashArt ArtOf(Look look) => look == Look.Enhanced ? FlashArt.Particles : FlashArt.Dos;

    /// <summary>A flash's colour (linear) for <paramref name="ammo"/>: a laser's or plasma's bolt colour
    /// (or a mod's, <paramref name="table"/>), else fire.</summary>
    public static Vector3 ColourOf(AmmoRecord? ammo, ShotLightTable? table = null)
    {
        if (ammo == null || ammo.Family != AmmoFamily.Bolt || ShotLightTable.LaserSrgb(ammo.Model) == null)
        {
            return Fire;
        }

        return (table ?? ShotLightTable.Current).For(ammo) is { } shot ? shot.Colour : Fire;
    }

    /// <summary>A flash's look by its <paramref name="seed"/>: its petals' turn (radians, 0-2π), its
    /// size's jitter (0.85-1.15) and a 3D gun's petal layout (0 to <see cref="GunVariants"/> - 1).</summary>
    public static FlashShape ShapeOf(int seed)
    {
        var h = Atmosphere.Hash3(seed, ShapeStream);
        return new FlashShape(h.X * MathF.Tau, 0.85f + 0.3f * h.Y, Math.Min((int)(h.Z * GunVariants), GunVariants - 1));
    }

    /// <summary>How hot a flash <paramref name="age"/> seconds old still is (1 → 0 over <see cref="WeaponSystem.FlashLife"/>).</summary>
    public static float HeatOf(float age)
    {
        var u = Math.Clamp(1f - age / WeaponSystem.FlashLife, 0f, 1f);
        return u * u;
    }

    /// <summary>Petal layouts of a 3D gun's flash (<see cref="GunFlashMesh"/>): a shot picks one by its seed.</summary>
    public const int GunVariants = 4;
    // A soft disc's segments, and a petal's base (radius, half its width in radians).
    private const int FanSegments = 20;
    private const float PetalBase = 0.08f, PetalHalfWidth = 0.3f;

    /// <summary>
    /// A 3D gun's flash as triangles (its inset draws no particles), unit-sized in the XY plane, white
    /// fading to black in the vertices (added: black adds nothing; Gouraud makes it soft):
    /// <code>
    ///   Fan                 a disc 1 across: opaque at the centre, clear at the rim (core, glow)
    ///   Variants[v]         4-6 tapered petals out to radius 0.3-0.44: opaque at the base, clear at the tip
    /// </code>
    /// </summary>
    public static GunFlashGeometry GunFlashMesh()
    {
        var vertices = new List<Vertex>();
        var solid = Vertex.Rgba(255, 255, 255, 255);
        var clear = Vertex.Rgba(0, 0, 0, 255);
        Vertex At(float angle, float radius, uint colour) =>
            new(new Vector3(MathF.Cos(angle) * radius, MathF.Sin(angle) * radius, 0f), Vector2.Zero, colour);

        for (var k = 0; k < FanSegments; k++)
        {
            float a0 = k * MathF.Tau / FanSegments, a1 = (k + 1) * MathF.Tau / FanSegments;
            vertices.Add(new Vertex(Vector3.Zero, Vector2.Zero, solid));
            vertices.Add(At(a0, 0.5f, clear));
            vertices.Add(At(a1, 0.5f, clear));
        }

        var fan = vertices.Count;
        var variants = new (int First, int Count)[GunVariants];
        for (var v = 0; v < GunVariants; v++)
        {
            var first = vertices.Count;
            var shape = Atmosphere.Hash3(v, ShapeStream);
            var petals = MinPetals + (int)(shape.Z * (MaxPetals - MinPetals + 1));
            for (var k = 0; k < petals; k++)
            {
                var j = Atmosphere.Hash3(v * 13 + k, PetalStream);
                var angle = (k + 0.35f * (j.X - 0.5f)) / petals * MathF.Tau;
                var length = 0.3f + 0.14f * j.Y;
                vertices.Add(At(angle - PetalHalfWidth, PetalBase, solid));
                vertices.Add(At(angle + PetalHalfWidth, PetalBase, solid));
                vertices.Add(At(angle, length, clear));
            }

            variants[v] = (first, vertices.Count - first);
        }

        return new GunFlashGeometry([.. vertices], fan, variants);
    }

    /// <summary>Queues a flash's particles seen from <paramref name="eye"/>, for <see cref="Flush"/>.</summary>
    public void Add(in MuzzleShot shot, Vector3 eye)
    {
        var heat = HeatOf(shot.Age);
        if (heat <= 0f || shot.Size <= 0f)
        {
            return;
        }

        var shape = ShapeOf(shot.Seed);
        var size = shot.Size * shape.Scale;
        var at = shot.At;
        var back = eye - at;
        var distance = back.Length();
        back = distance > 1e-4f ? back / distance : Vector3.UnitZ;

        // A little towards the eye: not half sunk in the wall a robot stands by.
        at += back * MathF.Min(size * 0.5f, distance * 0.5f);

        // The view's plane: the petals lie in it.
        var up0 = MathF.Abs(back.Y) > 0.99f ? Vector3.UnitZ : Vector3.UnitY;
        var right = Vector3.Normalize(Vector3.Cross(up0, back));
        var up = Vector3.Cross(back, right);
        var way = shot.Direction.LengthSquared() > 1e-6f ? Vector3.Normalize(shot.Direction) : -back;
        var hot = HotOf(shot.Colour);

        // Glow and the white-hot core.
        _flashes.Add(new Particle(at, size * 1.15f, new Vector4(shot.Colour * GlowHeat * heat, heat)));
        _flashes.Add(new Particle(at, size * 0.4f, new Vector4(hot * CoreHeat * heat, heat)));

        // Petals: tapered streaks out from the core, turned and stretched by the seed.
        var petals = MinPetals + (int)(Atmosphere.Hash3(shot.Seed, PetalStream).Z * (MaxPetals - MinPetals + 1));
        var turn = shape.Turn;
        for (var k = 0; k < petals; k++)
        {
            var j = Atmosphere.Hash3(shot.Seed * 13 + k, PetalStream);
            var angle = turn + (k + 0.35f * (j.X - 0.5f)) / petals * MathF.Tau;
            var dir = right * MathF.Cos(angle) + up * MathF.Sin(angle);
            var length = size * (0.45f + 0.4f * j.Y);
            _flashes.Add(new Particle(at + dir * (length * 0.5f), size * (0.1f + 0.06f * j.Z), new Vector4(shot.Colour * PetalHeat * heat, heat),
                Shape: ParticleShape.Streak, Axis: dir * length));
        }

        // The burst along the barrel: a cone of fire ahead and a few sparks flying on.
        var cone = size * 1.3f;
        _flashes.Add(new Particle(at + way * (cone * 0.5f), size * 0.35f, new Vector4(shot.Colour * ConeHeat * heat, heat),
            Shape: ParticleShape.Streak, Axis: way * cone));
        _flashes.Add(new Particle(at + way * (size * 0.45f), size * 0.55f, new Vector4(shot.Colour * GlowHeat * heat, heat)));
        for (var k = 0; k < Sparks; k++)
        {
            var j = Atmosphere.Hash3(shot.Seed * 17 + k, SparkStream) * 2f - Vector3.One;
            var v = Vector3.Normalize(way + j * 0.35f) * (size * (14f + 6f * j.X));
            var p = at + v * shot.Age;
            _flashes.Add(new Particle(p, size * 0.06f, new Vector4(hot * SparkHeat * heat, heat), Shape: ParticleShape.Streak, Axis: v * 0.02f));
        }
    }

    /// <summary>A flash's white-hot core colour: its own, three quarters of the way to white.</summary>
    public static Vector3 HotOf(Vector3 colour) => Vector3.Lerp(colour, Vector3.One, 0.75f);

    /// <summary>Draws the flashes <see cref="Add"/>ed (added light) and forgets them.</summary>
    public void Flush(IFrameRecorder renderer)
    {
        if (_flashes.Count == 0)
        {
            return;
        }

        renderer.DrawParticles(System.Runtime.InteropServices.CollectionsMarshal.AsSpan(_flashes), ParticleBlend.Add, Softness);
        _flashes.Clear();
    }

    /// <summary>The shots' wisps of smoke (<see cref="WeaponSystem.Smokes"/>) in the light all around
    /// <paramref name="ambient"/>: two puffs each, drifting on along the shot and rising, thinning out.</summary>
    public void DrawSmoke(IFrameRecorder renderer, IReadOnlyList<SmokePose> smokes, Vector3 ambient)
    {
        const int Puffs = 2;
        if (smokes.Count == 0)
        {
            return;
        }

        if (_smoke.Length < smokes.Count * Puffs)
        {
            _smoke = new Particle[smokes.Count * Puffs];
        }

        var colour = ambient * 2.2f + new Vector3(0.04f);
        var n = 0;
        foreach (var s in smokes)
        {
            var u = s.Age / MuzzleSmoke.MaxLife;
            for (var k = 0; k < Puffs; k++)
            {
                var j = Atmosphere.Hash3(s.Seed * 19 + k, SmokeStream);
                var drift = s.Direction * (s.Size * (0.6f + 0.8f * j.X) * MathF.Sqrt(u)) + new Vector3(0f, s.Size * (0.4f + 0.5f * j.Y) * u, 0f);
                var size = s.Size * (0.5f + 1.1f * MathF.Sqrt(u)) * (0.8f + 0.4f * j.Z);
                var alpha = 0.3f * MathF.Min(s.Age * 25f, 1f) * (1f - u) * (1f - u);
                _smoke[n++] = new Particle(s.At + drift, size, new Vector4(colour, alpha), Spin: j.Z * 6f + u, Seed: 4f + k + (s.Seed & 31));
            }
        }

        renderer.DrawParticles(_smoke.AsSpan(0, n), ParticleBlend.Alpha, Softness);
    }
}
