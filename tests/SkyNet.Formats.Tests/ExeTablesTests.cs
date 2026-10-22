using System.IO.Compression;
using RetroEng.Content;
using SkyNet.Data;
using SkyNet.Tables;

namespace SkyNet.Formats.Tests;

/// <summary>
/// The tables shipped in data/tables (embedded in SkyNet.Data): every one loads, writes back to the
/// same text, and, when SKYNET.EXE v1.01 is at hand (beside SKYNET_DATA or in its parent),
/// re-extracting it gives the committed files byte for byte.
/// </summary>
/// <summary>A fact that is skipped unless SKYNET.EXE v1.01 is in SKYNET_DATA or its parent.</summary>
sealed class ExeFactAttribute : FactAttribute
{
    public static string? Exe => TableFiles.FindExe(Environment.GetEnvironmentVariable(TestData.EnvVar));

    public ExeFactAttribute()
    {
        if (Exe == null)
            Skip = $"no SKYNET.EXE v1.01 in {TestData.EnvVar} or its parent";
    }
}

public class ExeTablesTests
{
    /// <summary>Every shipped table, loaded and written again by the tool's writer.</summary>
    static IEnumerable<(string Name, string Json)> Rewritten()
    {
        yield return (TableNames.EnemyTypes, TableWriter.Write(GameTables.Load(TableNames.EnemyTypes, TableSchemas.EnemyTypes), TableSchemas.EnemyTypes));
        yield return (TableNames.AisOps, TableWriter.Write(GameTables.Load(TableNames.AisOps, TableSchemas.AisOps), TableSchemas.AisOps));
        yield return (TableNames.AisAnims, TableWriter.Write(GameTables.Load(TableNames.AisAnims, TableSchemas.AisAnims), TableSchemas.AisAnims));
        yield return (TableNames.Ammo, TableWriter.Write(GameTables.Load(TableNames.Ammo, TableSchemas.Ammo), TableSchemas.Ammo));
        yield return (TableNames.Weapons, TableWriter.Write(GameTables.Load(TableNames.Weapons, TableSchemas.Weapons), TableSchemas.Weapons));
        yield return (TableNames.Pools, TableWriter.Write(GameTables.Load(TableNames.Pools, TableSchemas.Pools), TableSchemas.Pools));
        yield return (TableNames.StartLists, TableWriter.Write(GameTables.Load(TableNames.StartLists, TableSchemas.StartLists), TableSchemas.StartLists));
        yield return (TableNames.Pieces, TableWriter.Write(GameTables.Load(TableNames.Pieces, TableSchemas.Pieces), TableSchemas.Pieces));
        yield return (TableNames.Destruction, TableWriter.Write(GameTables.Load(TableNames.Destruction, TableSchemas.Destruction), TableSchemas.Destruction));
        yield return (TableNames.DropLists, TableWriter.Write(GameTables.Load(TableNames.DropLists, TableSchemas.DropLists), TableSchemas.DropLists));
        yield return (TableNames.Locations, TableWriter.Write(GameTables.Load(TableNames.Locations, TableSchemas.Locations), TableSchemas.Locations));
        yield return (TableNames.MissionEnemies, TableWriter.Write(GameTables.Load(TableNames.MissionEnemies, TableSchemas.MissionEnemies), TableSchemas.MissionEnemies));
    }

    static string Embedded(string name) =>
        TableFiles.Normalise(System.Text.Encoding.UTF8.GetString(GameTables.EmbeddedBytes(name) ?? throw new InvalidOperationException(name)));

    [Fact]
    public void Every_table_is_embedded_and_loads_back_to_the_same_text()
    {
        var rewritten = Rewritten().ToList();
        Assert.Equal(TableNames.All.Order(), rewritten.Select(r => r.Name).Order());
        foreach (var (name, json) in rewritten)
            Assert.True(Embedded(name) == json, $"{name}.json does not survive a load and write");
    }

    [Fact]
    public void Every_table_names_its_source_and_tool()
    {
        var file = GameTables.Load(TableNames.Weapons, TableSchemas.Weapons);
        Assert.Equal((Extractor.Source, Extractor.V101Md5, Extractor.ToolVersion), (file.Source, file.SourceMd5, file.Tool));
        Assert.Equal(26, file.Rows.Length);
        Assert.Equal(25, GameTables.Ammo.Length);
        Assert.Equal(140, GameTables.Destruction.Length);
    }

    // A mods folder with mod "name" as a folder (zip false) or a ZIP, holding tables/<table>.json files.
    static void WriteMod(string root, string name, bool zip, params (string Table, string Json)[] tables)
    {
        if (zip)
        {
            using var archive = ZipFile.Open(Path.Combine(root, name + ".zip"), ZipArchiveMode.Create);
            foreach (var (table, json) in tables)
            {
                using var w = new StreamWriter(archive.CreateEntry(GameTables.PathOf(table)).Open());
                w.Write(json);
            }

            return;
        }

        var dir = Directory.CreateDirectory(Path.Combine(root, name, "tables")).FullName;
        foreach (var (table, json) in tables)
            File.WriteAllText(Path.Combine(dir, table + ".json"), json);
    }

    static string Hotel(string name) =>
        Embedded(TableNames.Locations).Replace("THE TIKI GRAND HOTEL", name, StringComparison.Ordinal);

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void A_mod_table_replaces_the_embedded_one(bool zip)
    {
        var root = Directory.CreateTempSubdirectory("skynet-tables-").FullName;
        try
        {
            WriteMod(root, "hotel", zip, (TableNames.Locations, Hotel("A MODDED HOTEL")));
            using var mods = new ContentRoot(root).Mount([], ["hotel"]);
            Assert.Equal("A MODDED HOTEL", GameTables.Load(TableNames.Locations, TableSchemas.Locations, mods).Rows.Rows[0].Name);
            // A table the mod does not carry stays the shipped one; no mods means the shipped tables.
            Assert.Equal(26, GameTables.Load(TableNames.Weapons, TableSchemas.Weapons, mods).Rows.Length);
            Assert.Equal("THE TIKI GRAND HOTEL", GameTables.Load(TableNames.Locations, TableSchemas.Locations, null).Rows.Rows[0].Name);
        }
        finally
        {
            Directory.Delete(root, true);
        }
    }

    [Fact]
    public void The_later_mod_wins_table_by_table()
    {
        var root = Directory.CreateTempSubdirectory("skynet-tables-").FullName;
        try
        {
            // "a" (a ZIP) changes the hotel and the weapons; "b" (a folder) only the hotel.
            var weapons = Embedded(TableNames.Weapons).Replace("\"viewX\": 156,", "\"viewX\": 157,", StringComparison.Ordinal);
            Assert.NotEqual(Embedded(TableNames.Weapons), weapons);
            WriteMod(root, "a", zip: true, (TableNames.Locations, Hotel("HOTEL A")), (TableNames.Weapons, weapons));
            WriteMod(root, "b", zip: false, (TableNames.Locations, Hotel("HOTEL B")));

            using (var ab = new ContentRoot(root).Mount([], ["a", "b"]))
            {
                Assert.Equal("HOTEL B", GameTables.Load(TableNames.Locations, TableSchemas.Locations, ab).Rows.Rows[0].Name);
                Assert.Equal(157, GameTables.Load(TableNames.Weapons, TableSchemas.Weapons, ab).Rows[1].ViewX);
            }

            using (var ba = new ContentRoot(root).Mount([], ["b", "a"]))
            {
                Assert.Equal("HOTEL A", GameTables.Load(TableNames.Locations, TableSchemas.Locations, ba).Rows.Rows[0].Name);
            }
        }
        finally
        {
            Directory.Delete(root, true);
        }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void A_broken_mod_table_says_which_mod_file_and_where(bool zip)
    {
        var root = Directory.CreateTempSubdirectory("skynet-tables-").FullName;
        try
        {
            WriteMod(root, "broken", zip, (TableNames.Weapons, Embedded(TableNames.Weapons).Replace("\"viewX\": 156,", "\"viewX\": \"wide\",", StringComparison.Ordinal)));
            WriteMod(root, "cut", zip, (TableNames.Weapons, "{ \"table\": \"weapons\", }"));
            using (var mods = new ContentRoot(root).Mount([], ["broken"]))
            {
                var e = Assert.Throws<InvalidDataException>(() => GameTables.Load(TableNames.Weapons, TableSchemas.Weapons, mods));
                Assert.Equal("Table weapons (broken:tables/weapons.json): $.rows[1].viewX (line 9, column 27): expected an integer, found \"wide\"", e.Message);
            }

            using (var mods = new ContentRoot(root).Mount([], ["broken", "cut"]))
            {
                var e = Assert.Throws<InvalidDataException>(() => GameTables.Load(TableNames.Weapons, TableSchemas.Weapons, mods));
                Assert.StartsWith("Table weapons (cut:tables/weapons.json): $ (line 1, column 23):", e.Message);
            }
        }
        finally
        {
            Directory.Delete(root, true);
        }
    }

    [Fact]
    public void The_tool_refuses_anything_but_v101()
    {
        Assert.NotNull(Extractor.Reject("not an exe"u8));
        Assert.Throws<InvalidDataException>(() => Extractor.Extract([0x4D, 0x5A, 0, 0]));
    }

    [ExeFact]
    public void Re_extracting_skynet_exe_gives_the_committed_tables()
    {
        var exe = ExeFactAttribute.Exe!;
        var files = Extractor.Extract(File.ReadAllBytes(exe));
        Assert.Equal(TableNames.All.Order(), files.Select(f => f.Name).Order());
        var dir = TableFiles.RepoTablesDir(AppContext.BaseDirectory);
        foreach (var (name, json) in files)
        {
            Assert.True(json == Embedded(name), $"{name}: SKYNET.EXE gives other rows than the embedded table; run SkyNet.Tables");
            if (dir != null)
                Assert.True(json == TableFiles.Normalise(File.ReadAllText(Path.Combine(dir, name + ".json"))), $"{name}: data/tables differs; run SkyNet.Tables");
        }

        // The parser reads the same AIS data the game builds from the tables.
        var parsed = AisData.Parse(File.ReadAllBytes(exe))!;
        var shipped = GameTables.Ais;
        Assert.Equal(parsed.Ops.Count, shipped.Ops.Count);
        Assert.Equal(parsed.Anims.Count, shipped.Anims.Count);
        Assert.All(parsed.Ops.Values, o => Assert.Equal(o, shipped.Ops[o.Va]));
        Assert.Equal(parsed.Ammo.Length, shipped.Ammo.Length);
        Assert.All(parsed.Ammo.Zip(shipped.Ammo), p => Assert.Equal(p.First, p.Second));
    }
}
