using System.Runtime.InteropServices;
using System.Text;

namespace SkyNet.Formats.Tests;

/// <summary>
/// Every MAP and every .3D of the player's data compared with the Godot port's decode
/// (GodotReference/godot_dump_map3d.txt: counts, samples and MD5s, no game data). Skipped
/// without SKYNET_DATA.
/// </summary>
public class MapModelRealDataTests
{
    static readonly string[] Reference = File.ReadAllLines(Path.Combine(AppContext.BaseDirectory, "GodotReference", "godot_dump_map3d.txt"));

    static IEnumerable<string[]> Lines(string tag) =>
        Reference.Where(l => l.StartsWith(tag + " ", StringComparison.Ordinal)).Select(l => l.Split(' '));

    /// <summary>The Godot dump's entity line: map_file.gd's fields in its order.</summary>
    static string Canon(MapEntity e) => string.Join(' ', new long[]
    {
        e.FileOffset, e.CellX, e.CellZ, e.X, e.Y, e.Z, e.Flags, e.Pitch, e.Yaw, e.Roll,
        e.NameIndex, e.SpriteIndex, e.MarkerType, e.EnemyType, e.Convoy ? 1 : 0,
        e.LightIntensity, e.LightEnable, e.StateByte, e.LinkNext, e.ActType,
        e.Variant == MapEntityVariant.Mesh ? e.HitPoints : 0,
        e.Variant == MapEntityVariant.Sprite ? e.Param16 : 0,
        e.Variant == MapEntityVariant.Sprite ? e.Param8 : 0,
        e.LinkOffset, e.Link?.DestroyType ?? 0, e.Link?.DestroyParam ?? 0, e.UsesDefaults ? 1 : 0,
    });

    static Dictionary<string, MapFile?> Maps()
    {
        var gd = TestData.Open();
        using var a = gd.OpenMapArchive();
        var maps = new Dictionary<string, MapFile?>();
        foreach (var e in a.Entries)
            if (e.Name.StartsWith("MAP.", StringComparison.Ordinal) && int.TryParse(e.Name[4..], out _))
                maps[e.Name] = MapFile.Parse(a.Read(e)!);
        return maps;
    }

    [DataFact]
    public void Every_map_parses_and_matches_godot()
    {
        var maps = Maps();
        var sums = Lines("MAPSUM").ToList();
        Assert.Equal(sums.Count, maps.Count);
        Assert.True(maps.Count > 100, $"{maps.Count} maps");
        foreach (var s in sums)
        {
            var m = maps[s[1]];
            Assert.NotNull(m);
            Assert.Equal((int.Parse(s[2]), int.Parse(s[3])), (m.GridWidth, m.GridHeight));
            Assert.Equal(int.Parse(s[4]), m.Names.Length);
            Assert.Equal(int.Parse(s[5]), m.Entities.Length);
            Assert.Equal(uint.Parse(s[6]), m.OutdoorFlag);
            Assert.Equal(s[7], TestData.Md5(Encoding.UTF8.GetBytes(string.Join('\n', m.Entities.Select(Canon)))));
            Assert.Equal(s[8], TestData.Md5(Encoding.UTF8.GetBytes(string.Join('|', m.Names))));
            Assert.False(m.WalkCut);
        }
    }

    [DataFact]
    public void Map_210_211_231_entities_and_names_match_godot()
    {
        var maps = Maps();
        int ents = 0;
        foreach (var l in Lines("ENT"))
        {
            Assert.Equal(string.Join(' ', l[3..]), Canon(maps[l[1]]!.Entities[int.Parse(l[2])]));
            ents++;
        }
        foreach (var l in Lines("MAPNAME"))
            Assert.Equal(l[3] == "-" ? "" : l[3], maps[l[1]]!.Names[int.Parse(l[2])]);
        Assert.True(ents > 800);

        // MAP.231: the "PC" slot with leftover letters must not end the table.
        var m231 = maps["MAP.231"]!;
        Assert.Equal(89, m231.Names.Length);
        Assert.Contains("PC", m231.Names);
        Assert.False(m231.IsOutdoor);
        Assert.True(maps["MAP.210"]!.IsOutdoor);
        // MAP.211's truck interior: the last block is a variant-3 record heading its chain.
        Assert.Contains(maps["MAP.211"]!.Entities, e => e.Variant == MapEntityVariant.Sprite && e.FileOffset + 36 == maps["MAP.211"]!.Entities.Max(x => x.FileOffset + x.Block.Length));
    }

    [DataFact]
    public void Every_model_parses_and_matches_godot()
    {
        var gd = TestData.Open();
        int compared = 0, rejected = 0, animated = 0;
        foreach (var arcName in new[] { "MDMDOBJS.BSA", "MDMDENMS.BSA" })
        {
            using var a = gd.OpenArchive(arcName);
            var refs = Lines("MDL").Where(l => l[1] == arcName).ToList();
            Assert.Equal(refs.Count, a.Entries.Count(e => e.Name.EndsWith(".3D", StringComparison.Ordinal)));
            foreach (var r in refs)
            {
                var m = Model3D.Parse(a.Read(r[2])!);
                if (r[3] == "null")
                {
                    Assert.Null(m);
                    rejected++;
                    continue;
                }
                Assert.NotNull(m);
                Assert.Equal((int.Parse(r[3]), int.Parse(r[4]), int.Parse(r[5])), (m.VertexCount, m.Faces.Length, m.Frames.Length));
                var floats = m.Frames.SelectMany(f => f.Select(v => v.ToYUp())).ToArray();
                Assert.Equal(r[6], TestData.Md5(MemoryMarshal.AsBytes(floats.AsSpan())));
                var faces = string.Join('|', m.Faces.Select(f =>
                    $"{f.Texture};{string.Join(',', f.Indices)};{string.Join(',', f.Du)};{string.Join(',', f.Dv)}"));
                Assert.Equal(r[7], TestData.Md5(Encoding.UTF8.GetBytes(faces)));
                if (m.Frames.Length > 1) animated++;
                compared++;
            }
        }
        Assert.True(compared > 1000 && animated > 10, $"{compared} models, {animated} animated");
        Assert.True(rejected <= 4, $"{rejected} rejected");
    }

    [DataFact]
    public void Sky_dome_and_animated_models_load_through_game_data()
    {
        var gd = TestData.Open();
        var sky = gd.LoadModel("SKY_SKY.3D")!;
        Assert.Equal((72, 50, 1), (sky.VertexCount, sky.Faces.Length, sky.Frames.Length));
        // Seen from inside: every face's winding normal points at the centre.
        var c = sky.Vertices.Aggregate(System.Numerics.Vector3.Zero, (s, v) => s + v.ToYUp()) / sky.VertexCount;
        Assert.All(sky.Faces, f => Assert.True(System.Numerics.Vector3.Dot(sky.FaceNormal(f), c - sky.Vertices[f.Indices[0]].ToYUp()) > 0));

        // AVTRMHED: +0x30 is a cursor on frame 4; the frame table is the authority.
        var head = gd.LoadModel("AVTRMHED.3D")!;
        Assert.Equal(46, head.Frames.Length);
        Assert.NotEqual(head.FrameTable[0].VertexOffset, head.VertexBlockOffset);
        // ENDOSKEL v2.1: 84 frames, ÷4 vertex references.
        var endo = gd.LoadModel("ENDOSKEL.3D")!;
        Assert.Equal(("v2.1", 84), (endo.Version, endo.Frames.Length));

        var map = gd.LoadMap(210)!;
        Assert.True(map.IsOutdoor);
        Assert.All(map.Entities.Where(e => e.Variant == MapEntityVariant.Mesh && !e.IsActor),
            e => Assert.NotEqual("", map.EntityName(e)));
    }
}
