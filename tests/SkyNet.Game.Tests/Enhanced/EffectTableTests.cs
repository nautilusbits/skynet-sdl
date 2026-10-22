using System.Drawing;
using System.Numerics;
using System.Text;
using RetroEng.Content;
using RetroEng.Json;
using RetroEng.Models;
using RetroEng.Render;
using SkyNet.Data;
using SkyNet.Game.Enhanced;
using SkyNet.Game.Menu;

namespace SkyNet.Game.Tests.Enhanced;

public class EffectTableTests
{
    private static readonly Vector3 Eye = new(0f, 60f, -400f);

    [Fact]
    public void Table_round_trips()
    {
        var file = EffectTable.Base with { Rows = [.. EffectTable.Base.Rows, EffectTable.Defaults[EffectTable.Fire] with { Size = 1.25, R = 0.5 }] };
        var read = TableSchemas.Effects.Read(JsonItem.Parse(Encoding.UTF8.GetBytes(TableSchemas.Effects.Write(file).ToString())));

        Assert.Equal(file.Rows.Length, read.Rows.Length);
        for (var i = 0; i < file.Rows.Length; i++)
        {
            Assert.Equal(file.Rows[i], read.Rows[i]);
        }
    }

    [Fact]
    public void Defaults_are_the_codes_numbers()
    {
        var d = EffectTable.Defaults;
        Assert.Equal(EffectTable.Names, EffectTable.Base.Rows.Select(r => r.Effect));
        Assert.Equal((FireFlames.Tongues[1], FireFlames.Tongues[2]), (d[EffectTable.Fire].Low, d[EffectTable.Fire].High));
        Assert.Equal((float)FireFlames.EmberLife, (float)d[EffectTable.Embers].Life);
        Assert.Equal(Atmosphere.SmokeLife, (float)d[EffectTable.Smoke].Life);
        Assert.Equal(ImpactEffects.BlastSparks[2], d[EffectTable.ExplosionDebris].High);
        foreach (var r in EffectTable.Base.Rows)
        {
            Assert.All([r.Size, r.Speed, r.Rise, r.Spread, r.Gravity, r.Intensity, r.Flicker, r.R, r.G, r.B], v => Assert.Equal(1.0, v));
        }
    }

    [Fact]
    public void No_mods_is_the_defaults() => Assert.Same(EffectTable.Defaults, EffectTable.Of(null));

    [Fact]
    public void Mod_row_changes_what_the_fire_draws()
    {
        var (vfs, root) = Mod("""{"rows": [{"effect": "fire", "low": 3, "high": 5, "life": 0, "size": 2, "speed": 1, "rise": 1, "spread": 1, "gravity": 1, "intensity": 1, "flicker": 1, "r": 0.5, "g": 1, "b": 1}]}""");
        try
        {
            var table = EffectTable.Of(vfs);
            Assert.Equal(2.0, table[EffectTable.Fire].Size);
            Assert.Equal(EffectTable.Defaults[EffectTable.Smoke], table[EffectTable.Smoke]);

            var own = FireParticles(EffectTable.Defaults);
            var modded = FireParticles(table);
            Assert.Equal(own.Count, modded.Count);
            Assert.Equal(own[0].Size * 2f, modded[0].Size, 3);
            Assert.Equal(own[0].Colour.X * 0.5f, modded[0].Colour.X, 4);
        }
        finally
        {
            vfs.Dispose();
            Directory.Delete(root, true);
        }
    }

    [Fact]
    public void Bad_row_throws_naming_the_table()
    {
        var (vfs, root) = Mod("""{"rows": [{"effect": "fire", "low": 3, "high": 5, "life": 0, "size": -1, "speed": 1, "rise": 1, "spread": 1, "gravity": 1, "intensity": 1, "flicker": 1, "r": 1, "g": 1, "b": 1}]}""");
        try
        {
            Assert.Contains("effects", Assert.Throws<InvalidDataException>(() => EffectTable.Of(vfs)).Message);
        }
        finally
        {
            vfs.Dispose();
            Directory.Delete(root, true);
        }
    }

    [Fact]
    public void The_sample_mod_reads()
    {
        using var vfs = new VirtualFileSystem();
        vfs.Mount(new FolderMount(Path.Combine(RepoPaths.Root(), "mods-samples", "big-fire")), 0, "big-fire");

        var fire = EffectTable.Of(vfs)[EffectTable.Fire];
        Assert.True(fire.Size > 1 && fire.B > fire.R);
    }

    [Fact]
    public void Preview_draws_every_effect_headless()
    {
        var recorder = new Recorder();
        var preview = new EffectPreview();
        foreach (var name in EffectTable.Names)
        {
            recorder.Particles.Clear();
            preview.Spawn(name, Vector3.Zero);
            preview.Advance(0.3f);
            preview.Draw(recorder, EffectTable.Current[name], Eye);
            Assert.True(recorder.Particles.Count > 0, name);
        }
    }

    [Fact]
    public void Preview_draws_the_unsaved_row()
    {
        var recorder = new Recorder();
        var preview = new EffectPreview();
        preview.Spawn(EffectTable.Explosion, Vector3.Zero);
        preview.Advance(0.1f);
        var row = EffectTable.Current[EffectTable.Explosion];

        preview.Draw(recorder, row, Eye);
        var own = recorder.Particles.Count;
        recorder.Particles.Clear();
        preview.Draw(recorder, row with { High = row.High + 4 }, Eye);

        Assert.Equal(own + 4, recorder.Particles.Count);
    }

    [Fact]
    public void One_off_preview_loops()
    {
        var preview = new EffectPreview();
        preview.Spawn(EffectTable.ImpactDust, Vector3.Zero);
        preview.Advance(EffectPreview.Duration(EffectTable.ImpactDust) + 0.25f);

        Assert.Equal(0.25f, preview.Age, 3);
    }

    // The flame particles a fire draws with table.
    private static List<Particle> FireParticles(EffectTable table)
    {
        var recorder = new Recorder();
        new FireFlames().Draw(recorder, [new FireFlame(Vector3.Zero, 60f, 100f, 0f, 1f)], ParticleLevel.High, Eye, 1f, false, table);
        return recorder.Batches[0];
    }

    private static (VirtualFileSystem Vfs, string Root) Mod(string json)
    {
        var root = Directory.CreateTempSubdirectory("skynet-effects-").FullName;
        Directory.CreateDirectory(Path.Combine(root, "tables"));
        File.WriteAllText(Path.Combine(root, "tables", "effects.json"), json);
        var vfs = new VirtualFileSystem();
        vfs.Mount(new FolderMount(root), 0, "m0");
        return (vfs, root);
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
        public void FillRect(RectangleF target, Vector4 colour) { }
        public void AddRect(RectangleF target, Vector4 colour) { }
        public void FrameRect(RectangleF target, float width, Vector4 colour) { }
        public void BeginInset(View view, RectangleF area, InsetContent content, InsetLayer layer) { }
        public void EndInset() { }
        public void DrawUi(ReadOnlySpan<Vertex> vertices, ReadOnlySpan<uint> indices, ReadOnlySpan<UiDraw> draws) { }
    }
}
