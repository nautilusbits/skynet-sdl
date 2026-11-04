using System.Drawing;
using System.Numerics;
using RetroEng.Models;
using RetroEng.Physics;
using RetroEng.Render;
using RetroEng.Render.Lights;
using SkyNet.Game.Enhanced;
using SkyNet.Game.Menu;
using SkyNet.Game.Weapons;

namespace SkyNet.Game.Tests.Enhanced;

/// <summary>The muzzle flash: the DOS flare in the original look, particles (and a 3D gun's
/// triangles) in the enhanced one, tinted by the shot, turned per shot, burning out in a frame or two.</summary>
public class MuzzleFlashesTests
{
    private static readonly WeaponTable Table = WeaponTable.Shipped;
    private static readonly Vector3 Eye = new(0f, 40f, 300f);

    private sealed class Target(Vector3 centre) : IDamageable
    {
        public Vector3 Centre => centre;
        public float Radius => 40f;
        public bool Alive => true;
        public void TakeDamage(in Damage damage) { }
    }

    private static AmmoRecord Bullet => Table.Ammo.First(a => a.Family == AmmoFamily.Hitscan);
    private static AmmoRecord Laser(string model) => Table.Ammo.First(a => a.Family == AmmoFamily.Bolt && a.Model.StartsWith(model, StringComparison.OrdinalIgnoreCase));

    [Fact]
    public void Original_look_keeps_the_DOS_flare_enhanced_draws_particles()
    {
        Assert.Equal(219, WeaponSystem.FlashBank);
        Assert.Equal(FlashArt.Dos, MuzzleFlashes.ArtOf(Look.Original));
        Assert.Equal(FlashArt.Particles, MuzzleFlashes.ArtOf(Look.Enhanced));
    }

    [Fact]
    public void A_shots_flash_is_the_flare_bank_with_its_way_seed_and_smoke()
    {
        var weapons = new WeaponSystem(Table, new CollisionWorld());
        var robot = new Target(new Vector3(0f, 40f, -300f));
        weapons.EnemyFire(robot, Bullet, new Vector3(0f, 40f, -300f), Vector3.UnitZ, 0f);
        weapons.EnemyFire(robot, Bullet, new Vector3(0f, 40f, -300f), Vector3.UnitZ, 0f);

        var flashes = weapons.Effects.Where(e => e.Kind == EffectKind.Flash).ToList();
        Assert.Equal(2, flashes.Count);
        Assert.All(flashes, f => Assert.Equal(WeaponSystem.FlashBank, f.Bank));
        Assert.All(flashes, f => Assert.Equal(Vector3.UnitZ, f.Direction));
        Assert.NotEqual(flashes[0].Seed, flashes[1].Seed);
        Assert.Equal(2, weapons.Smokes.Count);
    }

    [Fact]
    public void Bullets_flash_fire_orange_lasers_their_bolts_colour()
    {
        var fire = MuzzleFlashes.ColourOf(Bullet);
        Assert.True(fire.X > fire.Y && fire.Y > fire.Z, $"bullet {fire}");
        Assert.Equal(fire, MuzzleFlashes.ColourOf(null));

        var red = MuzzleFlashes.ColourOf(Laser("LASER1"));
        var blue = MuzzleFlashes.ColourOf(Laser("LASER2"));
        Assert.True(red.X > red.Z * 3f, $"laser {red}");
        Assert.True(blue.Z > blue.X, $"plasma {blue}");
        Assert.NotEqual(fire, red);
        Assert.NotEqual(red, blue);
    }

    [Fact]
    public void Each_shot_turns_its_petals_its_own_way()
    {
        var turns = Enumerable.Range(0, 8).Select(s => MuzzleFlashes.ShapeOf(s).Turn).ToList();
        Assert.Equal(8, turns.Distinct().Count());
        Assert.All(turns, t => Assert.InRange(t, 0f, MathF.Tau));
        Assert.Contains(Enumerable.Range(0, 32), s => MuzzleFlashes.ShapeOf(s).Variant != MuzzleFlashes.ShapeOf(0).Variant);

        // The queued petals (streaks across the view) point other ways for two seeds.
        static Vector3 FirstPetal(int seed)
        {
            var recorder = new Recorder();
            var flashes = new MuzzleFlashes();
            flashes.Add(new MuzzleShot(Vector3.Zero, -Vector3.UnitZ, 18f, Vector3.One, seed, 0f), Eye);
            flashes.Flush(recorder);
            return Vector3.Normalize(recorder.Particles.First(p => p.Shape == ParticleShape.Streak).Axis);
        }

        Assert.True(Vector3.Dot(FirstPetal(1), FirstPetal(2)) < 0.999f);
    }

    [Fact]
    public void A_flash_is_HDR_bright_tinted_and_burns_out_over_its_life()
    {
        var recorder = new Recorder();
        var flashes = new MuzzleFlashes();
        var blue = MuzzleFlashes.ColourOf(Laser("LASER2"));
        flashes.Add(new MuzzleShot(Vector3.Zero, -Vector3.UnitZ, 18f, blue, 3, 0f), Eye);
        flashes.Flush(recorder);

        Assert.Single(recorder.Batches);
        Assert.Contains(recorder.Particles, p => MathF.Max(p.Colour.X, MathF.Max(p.Colour.Y, p.Colour.Z)) > 1f);
        Assert.All(recorder.Particles.Where(p => p.Shape == ParticleShape.Streak && p.Size > 1f), p => Assert.True(p.Colour.Z > p.Colour.X));

        Assert.Equal(1f, MuzzleFlashes.HeatOf(0f));
        Assert.Equal(0f, MuzzleFlashes.HeatOf(WeaponSystem.FlashLife));
        recorder.Particles.Clear();
        flashes.Add(new MuzzleShot(Vector3.Zero, -Vector3.UnitZ, 18f, blue, 3, WeaponSystem.FlashLife), Eye);
        flashes.Flush(recorder);
        Assert.Empty(recorder.Particles);
    }

    [Fact]
    public void A_3D_guns_flash_is_soft_triangles_fading_to_black()
    {
        var g = MuzzleFlashes.GunFlashMesh();
        Assert.Equal(0, g.FanCount % 3);
        Assert.Equal(MuzzleFlashes.GunVariants, g.Variants.Length);
        Assert.Equal(Vertex.Rgba(255, 255, 255, 255), g.Vertices[0].Colour);
        Assert.Equal(Vertex.Rgba(0, 0, 0, 255), g.Vertices[1].Colour);
        Assert.All(g.Variants, v => Assert.InRange(v.Count / 3, MuzzleFlashes.MinPetals, MuzzleFlashes.MaxPetals));
        Assert.Equal(g.Vertices.Length, g.Variants[^1].First + g.Variants[^1].Count);
    }

    /// <summary>A frame recorder that keeps the particles queued (no GPU).</summary>
    private sealed class Recorder : IFrameRecorder
    {
        public List<Particle> Particles { get; } = [];
        public List<List<Particle>> Batches { get; } = [];

        public void DrawParticles(ReadOnlySpan<Particle> particles, ParticleBlend blend, float softness, bool hazed = true)
        {
            Batches.Add([.. particles]);
            Particles.AddRange(particles);
        }

        public Lighting? Lighting { get; set; } = new(-Vector3.UnitY, Vector3.One, new Vector3(0.2f), new Vector3(0.1f), ShadowQuality.Off, 0f, 0f, Vector4.Zero, 0f);
        public float CanvasWidth => 640f;
        public ReadOnlyMemory<PointLight> Lights { get; set; }
        public TextureHandle ShadeTable { get; set; }
        public Fog? Fog { get; set; }
        public PlaneRemap? PlaneRemap { get; set; }
        public PlaneGrade? PlaneGrade { get; set; }
        public bool ReversedZ { get; set; }
        public void PushClip(System.Drawing.RectangleF rect) { }
        public void PopClip() { }
        public void Draw(MeshHandle mesh, int first, int count, Material material, int frame = 0) { }
        public void Draw(MeshHandle mesh, int first, int count, Material material, int frame, Matrix4x4 world) { }
        public void Draw(MeshHandle mesh, int first, int count, Material material, int frame, Matrix4x4 world, Bounds bounds) { }
        public void Draw(MeshHandle mesh, ReadOnlyMemory<MeshChunk> chunks, Material material, int frame, Matrix4x4 world) { }
        public void DrawLines(MeshHandle mesh, int first, int count, Material material) { }
        public void DrawInstanced(MeshHandle mesh, int first, int count, Material material, ReadOnlySpan<Instance> instances, Matrix4x4 world, int frame = 0) { }
        public void DrawSkinned(MeshHandle mesh, int first, int count, Material material, int frame, Matrix4x4 world,
            ReadOnlySpan<Matrix4x4> joints, Bounds bounds, ShapeBlend shapes = default) { }
        public Span<Vertex> Stream(int count, out MeshHandle mesh, out int first) => throw new NotSupportedException();
        public void DrawImage(TextureHandle texture, TextureHandle palette, Vector2 textureSize, RectangleF source, RectangleF target, Vector4 tint) { }
        public void DrawImage(TextureHandle texture, TextureHandle palette, Vector2 textureSize, RectangleF source, RectangleF target,
            TextureHandle shadeTable, CornerShades shades, float alpha = 1f) { }
        void IFrameRecorder.DrawImage(Material material, Vector2 textureSize, RectangleF source, RectangleF target) { }
        public void FillRect(RectangleF target, Vector4 colour) { }
        public void AddRect(RectangleF target, Vector4 colour) { }
        public void FrameRect(RectangleF target, float width, Vector4 colour) { }
        public void BeginInset(View view, RectangleF area, InsetContent content, InsetLayer layer) { }
        public void EndInset() { }
        public void DrawUi(ReadOnlySpan<Vertex> vertices, ReadOnlySpan<uint> indices, ReadOnlySpan<UiDraw> draws) { }
    }
}
