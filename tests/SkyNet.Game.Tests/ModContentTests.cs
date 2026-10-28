using System.Buffers.Binary;
using System.IO.Compression;
using System.Numerics;
using System.Text;
using RetroEng.Diagnostics;
using RetroEng.Models;
using SkyNet.Game.Mods;

namespace SkyNet.Game.Tests;

/// <summary>ModContent and GameTables.Mods are process-wide: while a test has mods mounted, every
/// level, texture, model and table lookup of a test running beside it would see them (and a table
/// read could reach the mod's file system as the unmount disposes it). These tests run alone.</summary>
[CollectionDefinition(Name, DisableParallelization = true)]
public sealed class ModsCollection
{
    public const string Name = "Mounted mods";
}

[Collection(ModsCollection.Name)]
public class ModContentTests
{
    private static readonly GltfModel Unit = Gltf.Load(Gltf.WriteGlb(
        [new GltfPrimitive([new(-0.5f, 0, -0.5f), new(0.5f, 0, -0.5f), new(0.5f, 2, 0.5f)], null, null, [0, 1, 2], -1)], [], []));

    private static readonly (Vector3, Vector3) DosBox = (new Vector3(10, -20, 30), new Vector3(30, 20, 70));

    [Fact]
    public void The_default_sidecar_fits_the_height_and_stands_on_the_box_bottom()
    {
        var m = ModelSidecar.Default.Placement(Unit, DosBox);

        // Height 2 -> 40: scale 20; the bottom centre (0, 0, 0) onto (20, -20, 50).
        Assert.Equal(new Vector3(20, -20, 50), Vector3.Transform(Vector3.Zero, m));
        Assert.Equal(new Vector3(20, 20, 50), Vector3.Transform(new Vector3(0, 2, 0), m));
    }

    [Fact]
    public void A_sidecar_turns_stretches_and_offsets()
    {
        var s = ModelSidecar.Parse(Encoding.UTF8.GetBytes(
            """{"rotation":[0,90,0],"fit":"stretch","align":"centre","offset":[1,2,3],"frames":[0,1],"comment":"x"}"""));

        Assert.Equal(ModelFit.Stretch, s.Fit);
        Assert.Equal(ModelAlign.Centre, s.Align);
        Assert.Equal([0, 1], s.Frames);
        var m = s.Placement(Unit, DosBox);
        // Turned 90 degrees the model's box is still 1 x 2 x 1; stretched onto 20 x 40 x 40.
        var corners = new[] { new Vector3(-0.5f, 0, -0.5f), new Vector3(0.5f, 2, 0.5f) }.Select(c => Vector3.Transform(c, m)).ToArray();
        var (min, max) = (Vector3.Min(corners[0], corners[1]), Vector3.Max(corners[0], corners[1]));
        Assert.Equal(new Vector3(11, -18, 33), min, new Vec3Comparer());
        Assert.Equal(new Vector3(31, 22, 73), max, new Vec3Comparer());
    }

    [Fact]
    public void Fit_none_takes_the_scale_as_units_per_metre()
    {
        var s = ModelSidecar.Parse(Encoding.UTF8.GetBytes("""{"fit":"none","scale":40,"align":"origin"}"""));

        Assert.Equal(new Vector3(0, 80, 0), Vector3.Transform(new Vector3(0, 2, 0), s.Placement(Unit, DosBox)));
    }

    [Fact]
    public void Frames_take_ranges_all_and_frame_models()
    {
        var s = ModelSidecar.Parse(Encoding.UTF8.GetBytes(
            """{"frames":[0,"2-4"],"frameModels":[{"model":"FIRE","frames":["5-6"]},{"model":"DEAD","frames":"all"}]}"""));

        Assert.Equal([0, 2, 3, 4], s.Frames);
        Assert.False(s.AllFrames);
        Assert.Equal(["FIRE", "DEAD"], s.FrameModels.Select(f => f.Model));
        Assert.True(s.FrameModels[0].Covers(6));
        Assert.False(s.FrameModels[0].Covers(4));
        Assert.True(s.FrameModels[1].Covers(99));
        Assert.True(ModelSidecar.Parse(Encoding.UTF8.GetBytes("""{"frames":"all"}""")).AllFrames);
    }

    [Fact]
    public void A_frame_picks_the_first_frame_model_that_covers_it_else_the_main_model_else_none()
    {
        var root = Directory.CreateTempSubdirectory("skynet-mods-").FullName;
        try
        {
            var models = Path.Combine(root, "m", "models");
            Directory.CreateDirectory(models);
            foreach (var name in new[] { "ROBOT", "ROBOT_FIRE" })
            {
                File.WriteAllBytes(Path.Combine(models, name + ".glb"), Gltf.WriteGlb(Unit.Primitives, [], []));
            }

            // ROBOT_FIRE's own sidecar is not read: ROBOT's places it. MISSING is skipped with a warning.
            File.WriteAllBytes(Path.Combine(models, "ROBOT.json"), Encoding.UTF8.GetBytes(
                """{"frames":"0-3","fit":"box","frameModels":[{"model":"MISSING","frames":[1]},{"model":"ROBOT_FIRE","frames":[2,7]}]}"""));
            File.WriteAllBytes(Path.Combine(models, "ROBOT_FIRE.json"), Encoding.UTF8.GetBytes("""{"fit":"none"}"""));
            ModContent.Mount(root, ["m"]);

            var robot = ModContent.Model("robot")!;
            var fire = Assert.Single(robot.FrameModels).Model;
            Assert.Equal(ModelFit.Box, fire.Sidecar.Fit);
            Assert.Same(robot, robot.ForFrame(0));
            Assert.Same(robot, robot.ForFrame(1));
            Assert.Same(fire, robot.ForFrame(2));
            Assert.Same(fire, robot.ForFrame(7));
            Assert.Null(robot.ForFrame(5));
        }
        finally
        {
            ModContent.Unmount();
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public void Frames_with_the_main_look_share_its_surfaces()
    {
        var root = Directory.CreateTempSubdirectory("skynet-mods-").FullName;
        try
        {
            var models = Path.Combine(root, "m", "models");
            Directory.CreateDirectory(models);
            // One material, one image: an animated avatar's frames each naming the same atlas.
            GltfMaterial skin = new("skin", Vector4.One, 0, GltfAlphaMode.Opaque, 0.5f, false);
            GltfImage atlas = new([1, 2, 3, 4], "image/png");
            var primitives = new[] { Unit.Primitives[0] with { Material = 0 } };
            foreach (var name in new[] { "AV", "AV_F01" })
            {
                File.WriteAllBytes(Path.Combine(models, name + ".glb"), Gltf.WriteGlb(primitives, [skin], [atlas]));
            }

            File.WriteAllBytes(Path.Combine(models, "AV_F02.glb"), Gltf.WriteGlb(primitives, [skin with { DoubleSided = true }], [atlas]));
            File.WriteAllBytes(Path.Combine(models, "AV.json"), Encoding.UTF8.GetBytes(
                """{"frameModels":[{"model":"AV_F01","frames":[1]},{"model":"AV_F02","frames":[2]}]}"""));
            ModContent.Mount(root, ["m"]);

            var av = ModContent.Model("AV")!;
            Assert.Same(av.Surfaces, av.ForFrame(1)!.Surfaces);
            Assert.NotSame(av.Surfaces, av.ForFrame(2)!.Surfaces);
            Assert.True(av.ForFrame(2)!.Surfaces[0].DoubleSided);
        }
        finally
        {
            ModContent.Unmount();
            Directory.Delete(root, recursive: true);
        }
    }

    [Theory]
    [InlineData("""{"fit":"huge"}""")]
    [InlineData("""{"scale":0}""")]
    [InlineData("""{"rotation":[1,2]}""")]
    [InlineData("""{"colour":"red"}""")]
    [InlineData("""{"frames":"some"}""")]
    [InlineData("""{"frames":["4-2"]}""")]
    [InlineData("""{"frames":["0-5000"]}""")]
    [InlineData("""{"frames":[-1]}""")]
    [InlineData("""{"frameModels":[{"frames":[1]}]}""")]
    [InlineData("""{"frameModels":[{"model":"A"}]}""")]
    [InlineData("""{"frameModels":[{"model":"../A","frames":[1]}]}""")]
    [InlineData("""{"frameModels":[{"model":"A","frames":[1],"rotation":[0,0,0]}]}""")]
    public void A_bad_sidecar_is_refused(string json) =>
        Assert.ThrowsAny<Exception>(() => ModelSidecar.Parse(Encoding.UTF8.GetBytes(json)));

    [Fact]
    public void Mods_are_read_from_a_zip_and_a_folder_in_load_order_and_only_when_named()
    {
        var root = Directory.CreateTempSubdirectory("skynet-mods-").FullName;
        try
        {
            // "a" (a ZIP) has a crate and a texture; "b" (a folder) a crate of its own; "c" is not named.
            using (var zip = ZipFile.Open(Path.Combine(root, "a.zip"), ZipArchiveMode.Create))
            {
                Add(zip, "models/CRATE01.glb", Gltf.WriteGlb(Unit.Primitives, [], []));
                Add(zip, "models/BARREL.glb", Gltf.WriteGlb(Unit.Primitives, [], []));
                Add(zip, "models/BARREL.json", Encoding.UTF8.GetBytes("""{"fit":"box"}"""));
                Add(zip, "textures/TEXTURE.210/003.png", Png(2, 2, [255, 0, 0, 128]));
            }

            Directory.CreateDirectory(Path.Combine(root, "b", "models"));
            File.WriteAllBytes(Path.Combine(root, "b", "models", "crate01.json"), Encoding.UTF8.GetBytes("""{"fit":"width"}"""));
            Directory.CreateDirectory(Path.Combine(root, "c", "models"));
            File.WriteAllBytes(Path.Combine(root, "c", "models", "CAR.glb"), Gltf.WriteGlb(Unit.Primitives, [], []));

            ModContent.Mount(root, ["a", "b", "missing"]);

            Assert.Equal(["a", "b"], ModContent.Enabled);
            Assert.True(ModContent.HasModels);
            Assert.True(ModContent.HasTextures);
            // b's sidecar lies over a's model (later wins); names ignore case.
            Assert.Equal(ModelFit.Width, ModContent.Model("crate01")!.Sidecar.Fit);
            Assert.Equal(ModelFit.Box, ModContent.Model("BARREL")!.Sidecar.Fit);
            Assert.Null(ModContent.Model("CAR"));
            Assert.Null(ModContent.Model("NOTHING"));
            // The default material is added last.
            Assert.Single(ModContent.Model("BARREL")!.Surfaces);

            ModContent.Prefetch([(210, 3, false, 0), (210, 4, false, 0)]);
            var sprite = ModContent.Texture(210, 3, opaque: false)!;
            Assert.Equal((2, 2), (sprite.Width, sprite.Height));
            // Premultiplied: 255 * 128 / 255.
            Assert.Equal(new byte[] { 128, 0, 0, 128 }, sprite.Rgba[..4]);
            Assert.Equal(255, ModContent.Texture(210, 3, opaque: true)!.Rgba[3]);
            Assert.Null(ModContent.Texture(210, 4, opaque: false));

            // Handed to the GPU for good: let go at the load's end, decoded again when asked.
            Assert.Same(sprite, ModContent.Texture(210, 3, opaque: false));
            ModContent.Handed(210, 3, opaque: false);
            ModContent.TrimHanded();
            var again = ModContent.Texture(210, 3, opaque: false)!;
            Assert.NotSame(sprite, again);
            Assert.Equal(sprite.Rgba, again.Rgba);
            // One not handed stays.
            var opaque = ModContent.Texture(210, 3, opaque: true);
            ModContent.TrimHanded();
            Assert.Same(opaque, ModContent.Texture(210, 3, opaque: true));
        }
        finally
        {
            ModContent.Unmount();
            Directory.Delete(root, recursive: true);
        }

        Assert.Null(ModContent.Texture(210, 3, opaque: false));
        Assert.Null(ModContent.Model("crate01"));
    }

    [Fact]
    public void A_corrupt_mod_is_skipped_and_a_broken_model_leaves_the_dos_one()
    {
        var root = Directory.CreateTempSubdirectory("skynet-mods-").FullName;
        try
        {
            // "bad" is a ZIP in name only; "b" is a folder with a good crate and a broken barrel.
            File.WriteAllBytes(Path.Combine(root, "bad.zip"), Encoding.UTF8.GetBytes("this is not a zip file at all, not even close"));
            Directory.CreateDirectory(Path.Combine(root, "b", "models"));
            File.WriteAllBytes(Path.Combine(root, "b", "models", "CRATE01.glb"), Gltf.WriteGlb(Unit.Primitives, [], []));
            File.WriteAllBytes(Path.Combine(root, "b", "models", "BARREL.glb"), [.. "glTF"u8, 2, 0, 0, 0, 255, 255, 255, 127]);

            ModContent.Mount(root, ["bad", "b"]);

            Assert.Equal(["b"], ModContent.Enabled);
            Assert.NotNull(ModContent.Model("CRATE01"));
            Assert.Null(ModContent.Model("BARREL"));

            // Only the corrupt one named: nothing is mounted, and nothing throws.
            ModContent.Mount(root, ["bad"]);
            Assert.Empty(ModContent.Enabled);
            Assert.False(ModContent.HasModels);
        }
        finally
        {
            ModContent.Unmount();
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public void A_mod_with_tables_hands_them_to_the_game_tables_until_unmounted()
    {
        var root = Directory.CreateTempSubdirectory("skynet-mods-").FullName;
        try
        {
            // The shipped bytes, so other tests reading the tables meanwhile see no change.
            using (var zip = ZipFile.Open(Path.Combine(root, "t.zip"), ZipArchiveMode.Create))
            {
                Add(zip, SkyNet.Data.GameTables.PathOf(SkyNet.Data.TableNames.Locations), SkyNet.Data.GameTables.EmbeddedBytes(SkyNet.Data.TableNames.Locations)!);
            }

            ModContent.Mount(root, ["t"]);
            Assert.True(ModContent.HasTables);
            Assert.Same(ModContent.Files, SkyNet.Data.GameTables.Mods);
            Assert.Equal("t:tables/location_names.json", SkyNet.Data.GameTables.SourceOf(SkyNet.Data.TableNames.Locations));
            Assert.Equal("embedded", SkyNet.Data.GameTables.SourceOf(SkyNet.Data.TableNames.Weapons));
        }
        finally
        {
            ModContent.Unmount();
            Directory.Delete(root, recursive: true);
        }

        Assert.Null(SkyNet.Data.GameTables.Mods);
    }

    [Theory]
    [InlineData("ai", """{ "lineTests": 10, "aim": { "lead": 0.85 } }""")]
    [InlineData("hazards", """{ "table": "hazards", "fire": { "on": true, "perSecond": 60, "sprites": ["206/1", "218/0"] } }""")]
    public void A_mod_with_a_retired_table_loads_and_logs_it_once(string name, string json)
    {
        var root = Directory.CreateTempSubdirectory("skynet-mods-").FullName;
        var sink = new WarnLines();
        Log.AddSink(sink, LogLevel.Warn);
        try
        {
            // A removed mod layer's file (smart robots, burning fires), as an old mod still carries it.
            var tables = Path.Combine(root, "old", "tables");
            Directory.CreateDirectory(tables);
            File.WriteAllText(Path.Combine(tables, name + ".json"), json);

            ModContent.Mount(root, ["old"]);

            Assert.Single(sink.Lines, l => l.Contains($"tables/{name}.json", StringComparison.Ordinal));
            Assert.NotEmpty(SkyNet.Data.GameTables.Ais.Types);
            Assert.NotEmpty(SkyNet.Data.GameTables.Weapons);
        }
        finally
        {
            Log.RemoveSink(sink);
            ModContent.Unmount();
            Directory.Delete(root, recursive: true);
        }
    }

    private sealed class WarnLines : ILogSink
    {
        public List<string> Lines { get; } = [];

        public void Write(in LogEntry entry) => Lines.Add(entry.ToString());

        public void Flush(bool durable = false)
        {
        }
    }

    [Fact]
    public void A_mount_without_tables_changes_the_textures_and_models_and_keeps_the_tables()
    {
        var root = Directory.CreateTempSubdirectory("skynet-mods-").FullName;
        try
        {
            using (var zip = ZipFile.Open(Path.Combine(root, "t.zip"), ZipArchiveMode.Create))
            {
                Add(zip, SkyNet.Data.GameTables.PathOf(SkyNet.Data.TableNames.Locations), SkyNet.Data.GameTables.EmbeddedBytes(SkyNet.Data.TableNames.Locations)!);
            }

            Directory.CreateDirectory(Path.Combine(root, "m", "models"));
            File.WriteAllBytes(Path.Combine(root, "m", "models", "CRATE01.glb"), Gltf.WriteGlb(Unit.Primitives, [], []));

            ModContent.Mount(root, ["t"]);
            var tables = SkyNet.Data.GameTables.Mods;
            // The MODS screen's change in game: t off, m on; the tables wait for the next game.
            ModContent.Mount(root, ["m"], tables: false);

            Assert.Equal(["m"], ModContent.Enabled);
            Assert.Equal(["t"], ModContent.TablesFrom);
            Assert.True(ModContent.HasModels);
            Assert.True(ModContent.HasTables);
            Assert.Same(tables, SkyNet.Data.GameTables.Mods);
            Assert.Equal("t:tables/location_names.json", SkyNet.Data.GameTables.SourceOf(SkyNet.Data.TableNames.Locations));

            // A game starts: the tables follow the list.
            ModContent.Mount(root, ["m"]);
            Assert.False(ModContent.HasTables);
            Assert.Null(SkyNet.Data.GameTables.Mods);
            Assert.NotNull(ModContent.Model("CRATE01"));
        }
        finally
        {
            ModContent.Unmount();
            Directory.Delete(root, recursive: true);
        }

        Assert.Null(SkyNet.Data.GameTables.Mods);
        Assert.Empty(ModContent.TablesFrom);
    }

    [Fact]
    public void Detail_shrinks_a_record_texture_and_its_relief_by_a_whole_scale()
    {
        var root = Directory.CreateTempSubdirectory("skynet-mods-").FullName;
        try
        {
            var folder = Path.Combine(root, "hd", "textures", "TEXTURE.210");
            Directory.CreateDirectory(folder);
            File.WriteAllBytes(Path.Combine(folder, "003.png"), Png(8, 8, [255, 0, 0, 255]));
            File.WriteAllBytes(Path.Combine(folder, "003" + HiresTextures.NormalSuffix + ".png"), Png(8, 8, [128, 128, 255, 255]));
            File.WriteAllBytes(Path.Combine(folder, "003" + HiresTextures.RoughnessSuffix + ".png"), Png(8, 8, [200, 200, 200, 255]));
            ModContent.Mount(root, ["hd"]);

            // x4 over a 2-wide record: MED halves it, LOW quarters it, the relief alike.
            ModContent.UseDetail(Detail.Medium);
            var med = ModContent.Texture(210, 3, opaque: false, width: 2)!;
            Assert.Equal((4, 4, 4 * 4 * 4), (med.Width, med.Height, med.Relief!.Length));
            Assert.Equal(new byte[] { 255, 0, 0, 255 }, med.Rgba[..4]);
            Assert.Equal(200, med.Relief[3]);

            ModContent.UseDetail(Detail.Low);
            var low = ModContent.Texture(210, 3, opaque: false, width: 2)!;
            Assert.Equal((2, 2), (low.Width, low.Height));
            // x2 over a 4-wide record: LOW can only halve it.
            Assert.Equal(4, ModContent.Texture(210, 3, opaque: true, width: 4)!.Width);

            ModContent.UseDetail(Detail.High);
            Assert.Equal(8, ModContent.Texture(210, 3, opaque: false, width: 2)!.Width);
        }
        finally
        {
            ModContent.UseDetail(Detail.High);
            ModContent.Unmount();
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public void A_surface_is_made_again_at_a_new_detail()
    {
        try
        {
            var material = new GltfMaterial("m", Vector4.One, 0, GltfAlphaMode.Opaque, 0.5f, false);
            var surface = ReplacementSurface.Build("test.glb", [new GltfImage(Png(8, 4, [10, 20, 30, 255]), "image/png")], material);

            ModContent.UseDetail(Detail.Low);
            Assert.Equal((2, 1), (surface.Colour.Width, surface.Colour.Height));
            Assert.Equal(new byte[] { 10, 20, 30, 255 }, surface.Colour.Rgba[..4]);

            ModContent.UseDetail(Detail.High);
            Assert.False(surface.Made);
            Assert.Equal((8, 4), (surface.Colour.Width, surface.Colour.Height));
        }
        finally
        {
            ModContent.UseDetail(Detail.High);
        }
    }

    private static void Add(ZipArchive zip, string name, byte[] bytes)
    {
        using var s = zip.CreateEntry(name).Open();
        s.Write(bytes);
    }

    /// <summary>A straight RGBA PNG of one colour.</summary>
    private static byte[] Png(int w, int h, byte[] rgba)
    {
        var raw = new MemoryStream();
        for (var y = 0; y < h; y++)
        {
            raw.WriteByte(0);
            for (var x = 0; x < w; x++)
            {
                raw.Write(rgba);
            }
        }

        var idat = new MemoryStream();
        using (var z = new ZLibStream(idat, CompressionLevel.Optimal, leaveOpen: true))
        {
            raw.Position = 0;
            raw.CopyTo(z);
        }

        var png = new MemoryStream();
        png.Write([0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A]);
        var ihdr = new byte[13];
        BinaryPrimitives.WriteInt32BigEndian(ihdr, w);
        BinaryPrimitives.WriteInt32BigEndian(ihdr.AsSpan(4), h);
        (ihdr[8], ihdr[9]) = (8, 6);
        Chunk(png, "IHDR", ihdr);
        Chunk(png, "IDAT", idat.ToArray());
        Chunk(png, "IEND", []);
        return png.ToArray();
    }

    private static void Chunk(Stream s, string type, byte[] data)
    {
        var head = new byte[4];
        BinaryPrimitives.WriteInt32BigEndian(head, data.Length);
        s.Write(head);
        var body = Encoding.ASCII.GetBytes(type).Concat(data).ToArray();
        s.Write(body);
        BinaryPrimitives.WriteUInt32BigEndian(head, Crc(body));
        s.Write(head);
    }

    private static uint Crc(byte[] data)
    {
        var c = 0xFFFFFFFFu;
        foreach (var b in data)
        {
            c ^= b;
            for (var k = 0; k < 8; k++)
            {
                c = (c & 1) != 0 ? 0xEDB88320u ^ (c >> 1) : c >> 1;
            }
        }

        return c ^ 0xFFFFFFFFu;
    }

    private sealed class Vec3Comparer : IEqualityComparer<Vector3>
    {
        public bool Equals(Vector3 a, Vector3 b) => Vector3.Distance(a, b) < 1e-3f;
        public int GetHashCode(Vector3 v) => 0;
    }
}
