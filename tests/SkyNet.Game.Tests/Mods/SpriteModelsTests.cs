using System.Numerics;
using System.Text;
using RetroEng.Content;
using RetroEng.Json;
using RetroEng.Models;
using SkyNet.Data;
using SkyNet.Game.Menu;
using SkyNet.Game.Mods;

namespace SkyNet.Game.Tests.Mods;

[Collection(ModsCollection.Name)]
public class SpriteModelsTests
{
    /// <summary>The sample mod's sprites: 200/002 (bullets, static model), 214/002 (heavy armour, animated).</summary>
    private const int Bullets = 200 << 7 | 2, Armour = 214 << 7 | 2;

    private static readonly SpriteModelRow Row = new("214/007", "medkit.glb", 0, 45, 90, 2.5, -1);

    private static byte[] Json(string text) => Encoding.UTF8.GetBytes(text);

    [Fact]
    public void A_row_writes_and_reads_back()
    {
        var file = SpriteModels.Base with { Rows = [Row, Row with { Sprite = "200/002", Scale = 12.5, Yaw = -30 }] };

        var json = TableSchemas.SpriteModels.Write(file).ToString();
        var back = TableSchemas.SpriteModels.Read(JsonItem.Parse(Encoding.UTF8.GetBytes(json)));

        Assert.Equal(file.Rows.AsEnumerable(), back.Rows.AsEnumerable());
        Assert.Contains("\"offsetY\"", json);
    }

    /// <summary>A file system of one folder mod per table text, the later over the earlier.</summary>
    private static (VirtualFileSystem Vfs, string Root) Mods(params string[] tables)
    {
        var root = Directory.CreateTempSubdirectory("skynet-mods-").FullName;
        var vfs = new VirtualFileSystem();
        for (var i = 0; i < tables.Length; i++)
        {
            var dir = Path.Combine(root, $"m{i}", "tables");
            Directory.CreateDirectory(dir);
            File.WriteAllBytes(Path.Combine(dir, "sprite_models.json"), Json(tables[i]));
            vfs.Mount(new FolderMount(Path.Combine(root, $"m{i}")), i, $"m{i}");
        }

        return (vfs, root);
    }

    [Fact]
    public void Mods_lay_rows_over_each_other_by_sprite()
    {
        var (vfs, root) = Mods(
            """{"rows":[{"sprite":"214/007","model":"a.glb","scale":0,"yaw":0,"spin":0,"bob":0,"offsetY":0},{"sprite":"200/002","model":"b.glb","scale":0,"yaw":0,"spin":0,"bob":0,"offsetY":0}]}""",
            """{"rows":[{"sprite":"214/007","model":"c.glb","scale":2,"yaw":0,"spin":0,"bob":0,"offsetY":0}]}""");
        try
        {
            Assert.Equal(["c.glb", "b.glb"], SpriteModels.Rows(vfs).Select(r => r.Model));
            Assert.Empty(SpriteModels.Rows(null));
        }
        finally
        {
            vfs.Dispose();
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public void A_row_without_a_key_does_not_read()
    {
        var (vfs, root) = Mods("""{"rows":[{"sprite":"214/007","model":"a.glb"}]}""");
        try
        {
            Assert.Throws<InvalidDataException>(() => SpriteModels.Rows(vfs));
        }
        finally
        {
            vfs.Dispose();
            Directory.Delete(root, recursive: true);
        }
    }

    [Theory]
    [InlineData("214/007", 27399)]
    [InlineData("214/7", 27399)]
    [InlineData("200/002", 25602)]
    public void A_key_is_bank_and_record(string key, int sprite)
    {
        Assert.True(SpriteModels.TryKey(key, out var s));
        Assert.Equal(sprite, s);
        Assert.Equal(key.Length == 7 ? key : "214/007", SpriteModels.KeyOf(s));
    }

    [Theory]
    [InlineData("214")]
    [InlineData("214/128")]
    [InlineData("x/1")]
    [InlineData("-1/2")]
    [InlineData("1/2/3")]
    public void A_bad_key_is_refused(string key) => Assert.False(SpriteModels.TryKey(key, out _));

    [Fact]
    public void A_row_whose_model_is_missing_or_broken_stays_a_billboard()
    {
        var root = Directory.CreateTempSubdirectory("skynet-mods-").FullName;
        try
        {
            var mod = Path.Combine(root, "m");
            Directory.CreateDirectory(Path.Combine(mod, "models", "sprites"));
            Directory.CreateDirectory(Path.Combine(mod, "tables"));
            File.WriteAllBytes(Path.Combine(mod, "models", "sprites", "broken.glb"), [1, 2, 3]);
            File.WriteAllBytes(Path.Combine(mod, "models", "sprites", "ok.glb"), Gltf.WriteGlb(Unit.Primitives, [], []));
            File.WriteAllBytes(Path.Combine(mod, "tables", "sprite_models.json"), Json(
                """{"rows":[{"sprite":"214/000","model":"missing.glb","scale":0,"yaw":0,"spin":0,"bob":0,"offsetY":0},{"sprite":"214/001","model":"broken.glb","scale":0,"yaw":0,"spin":0,"bob":0,"offsetY":0},{"sprite":"214/003","model":"../ok.glb","scale":0,"yaw":0,"spin":0,"bob":0,"offsetY":0},{"sprite":"oops","model":"ok.glb","scale":0,"yaw":0,"spin":0,"bob":0,"offsetY":0},{"sprite":"214/004","model":"ok.glb","scale":0,"yaw":0,"spin":0,"bob":0,"offsetY":0}]}"""));
            ModContent.Mount(root, ["m"]);

            Assert.Null(SpriteModels.Drawn(214 << 7, Look.Enhanced));
            Assert.Null(SpriteModels.Drawn(214 << 7 | 1, Look.Enhanced));
            Assert.Null(SpriteModels.Drawn(214 << 7 | 3, Look.Enhanced));
            Assert.NotNull(SpriteModels.Drawn(214 << 7 | 4, Look.Enhanced));

            // In models/sprites/: not a replacement of a DOS model named the same (OK.3D).
            Assert.Null(ModContent.Model("ok"));
        }
        finally
        {
            ModContent.Unmount();
            Directory.Delete(root, recursive: true);
        }
    }

    /// <summary>A 1 x 2 x 1 model, its bottom centre at the origin.</summary>
    private static readonly GltfModel Unit = Gltf.Load(Gltf.WriteGlb(
        [new GltfPrimitive([new(-0.5f, 0, -0.5f), new(0.5f, 0, 0.5f), new(0.5f, 2, 0.5f)], null, null, [0, 1, 2], -1)], [], []));

    private static SpriteModel Model(SpriteModelRow row) =>
        new() { Sprite = 0, Row = row, Box = (new Vector3(-0.5f, 0, -0.5f), new Vector3(0.5f, 2, 0.5f)) };

    [Fact]
    public void Scale_zero_fits_the_sprite_height_on_its_foot()
    {
        var m = Model(new SpriteModelRow("1/1", "a.glb", 0, 0, 0, 0, 0)).World(new Vector3(100, 50, -20), 40, 0, 0);

        Assert.Equal(new Vector3(100, 50, -20), Vector3.Transform(Vector3.Zero, m));
        Assert.Equal(new Vector3(100, 90, -20), Vector3.Transform(new Vector3(0, 2, 0), m));
    }

    [Fact]
    public void Scale_yaw_spin_bob_and_lift_place_the_model()
    {
        var model = Model(new SpriteModelRow("1/1", "a.glb", 10, 90, 90, 4, 3));
        var foot = new Vector3(0, 100, 0);

        // Seed 0: no stagger. t = 0: yaw 90 turns +X to -Z; lifted 3, the bob at 0.
        var at0 = model.World(foot, 40, 0, 0);
        Near(new Vector3(0, 103, -10), Vector3.Transform(new Vector3(1, 0, 0), at0));

        // t = 1 s: spun 90 more (+X to -X); the bob (0.5 a second) at sin(pi) = 0 again; t = 0.5 s at its top.
        Near(new Vector3(-10, 103, 0), Vector3.Transform(new Vector3(1, 0, 0), model.World(foot, 40, 1, 0)));
        Near(new Vector3(0, 107, 0), Vector3.Transform(Vector3.Zero, model.World(foot, 40, 0.5f, 0)));
    }

    private static void Near(Vector3 expected, Vector3 actual) =>
        Assert.True(Vector3.Distance(expected, actual) < 1e-3f, $"expected {expected}, got {actual}");
}
