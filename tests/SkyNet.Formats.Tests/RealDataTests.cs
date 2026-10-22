using System.Globalization;

namespace SkyNet.Formats.Tests;

/// <summary>
/// Decodes of the player's data compared with the Godot port's decode of the same files
/// (GodotReference/godot_dump.txt: MD5s and samples, no game data). Skipped without SKYNET_DATA.
/// </summary>
public class RealDataTests
{
    /// <summary>A fact skipped unless Future Shock's data sits beside SKYNET_DATA's.</summary>
    private sealed class ShockDataFactAttribute : FactAttribute
    {
        public ShockDataFactAttribute()
        {
            if (TestData.Dir is not { } dir || GameDataLocator.OtherGameDir(dir) == null)
            {
                Skip = $"no Future Shock data beside {TestData.EnvVar}'s SkyNET folder";
            }
        }
    }

    static readonly string[] Reference = File.ReadAllLines(Path.Combine(AppContext.BaseDirectory, "GodotReference", "godot_dump.txt"));

    static IEnumerable<string[]> Lines(string tag) =>
        Reference.Where(l => l.StartsWith(tag + " ", StringComparison.Ordinal)).Select(l => l.Split(' '));

    [DataFact]
    public void Archive_toc_and_decryption_match_godot()
    {
        var gd = TestData.Open();
        Assert.Equal(GameKind.SkyNet, gd.Game);
        using var a = gd.OpenArchive("MDMDIMGS.BSA");
        Assert.Equal(int.Parse(Lines("BSA").Single()[2]), a.Entries.Length);
        foreach (var l in Lines("BSAENT"))
        {
            Assert.True(a.TryFind(l[1], out var e));
            Assert.Equal((int.Parse(l[2]), uint.Parse(l[3]), long.Parse(l[4])), (e.Flags, e.Size, e.Offset));
        }
        var col = Lines("COL").Single();
        var bytes = a.Read(col[1])!;
        Assert.Equal(int.Parse(col[2]), bytes.Length);
        Assert.Equal(col[3], TestData.Md5(bytes));
    }

    [DataFact]
    public void Texture_records_keep_dos_flat_colour()
    {
        // Outer record byte +1: the colour DOS's flat face mode (the automap at LOW / MED) fills with.
        var bank = TestData.Open().LoadTextureBank(13)!;
        Assert.Equal(new byte[] { 139, 139, 141, 140, 124, 94, 141, 121 }, bank.Records.Take(8).Select(r => r.Colour));
    }

    [DataFact]
    public void Palette_matches_godot()
    {
        var p = TestData.Open().LoadPalette()!;
        Assert.Equal(Lines("PAL").Single()[1], TestData.Md5(p.Rgb.AsSpan()));
    }

    [DataFact]
    public void Haze_row_zero_is_the_haze_colour()
    {
        var h = TestData.Open().LoadHaze()!;
        Assert.All(h.Row(0).ToArray(), b => Assert.Equal(HazeTable.HazeIndex, b));
    }

    [ShockDataFact]
    public void Future_shock_haze_lands_on_the_colours_it_was_made_for()
    {
        // Future Shock draws with SkyNET's palette, whose 0x11-0x1B (blood reds) are greys in SHOCK.COL.
        var fs = GameData.Open(GameDataLocator.OtherGameDir(TestData.Dir!)!, TestData.Dir)!;
        Assert.Equal(GameKind.FutureShock, fs.Game);
        var made = Palette.Parse(fs.ReadLoose("SHOCK.COL")!)!;
        var used = fs.LoadPalette()!;
        var haze = fs.LoadHaze()!;
        for (int step = 0; step < HazeTable.Steps; step++)
            for (int i = 0; i < 256; i++)
                Assert.Equal(made[haze.Map(step, i)], used[haze.Map(step, i)]);
    }

    [DataFact]
    public void Texture_banks_match_godot()
    {
        var gd = TestData.Open();
        int compared = 0;
        foreach (var t in Lines("TEX"))
        {
            int bank = int.Parse(t[1]);
            var tb = gd.LoadTextureBank(bank)!;
            Assert.Equal(int.Parse(t[2]), tb.Records.Length);
            Assert.Equal(string.Join(' ', t[3..]), tb.Name);
            foreach (var r in Lines("REC").Where(r => int.Parse(r[1]) == bank))
            {
                var rec = tb.Records[int.Parse(r[2])];
                Assert.Equal((int.Parse(r[3]), int.Parse(r[4])), (rec.Width, rec.Height));
                Assert.Equal(r[5], TestData.Md5(rec.Pixels.AsSpan()));
                compared++;
            }
        }
        Assert.True(compared > 300);
    }

    [DataFact]
    public void Sprite_frames_match_godot()
    {
        var gd = TestData.Open();
        foreach (var f in Lines("FRAMES"))
        {
            int bank = int.Parse(f[1]);
            var frames = TextureBank.ParseRecordFrames(gd.ReadLoose($"TEXTURE.{bank:D3}"), 0);
            Assert.Equal(int.Parse(f[2]), frames.Length);
            foreach (var r in Lines("FRM").Where(r => int.Parse(r[1]) == bank))
            {
                var fr = frames[int.Parse(r[2])];
                Assert.Equal((int.Parse(r[3]), int.Parse(r[4])), (fr.Width, fr.Height));
                Assert.Equal(r[5], TestData.Md5(fr.Pixels.AsSpan()));
            }
        }
    }

    [DataFact]
    public void Terrain_layers_and_heights_match_godot()
    {
        var gd = TestData.Open();
        var cache = new Dictionary<int, WldTerrain>();
        WldTerrain Get(int m) => cache.TryGetValue(m, out var w) ? w : cache[m] = gd.LoadWld(m)!;
        foreach (var l in Lines("WLD"))
            Assert.Equal(l[3], TestData.Md5(Get(int.Parse(l[1])).Layer(int.Parse(l[2])).AsSpan()));
        foreach (var h in Lines("H"))
        {
            var w = Get(int.Parse(h[1]));
            double x = double.Parse(h[2], CultureInfo.InvariantCulture);
            double z = double.Parse(h[3], CultureInfo.InvariantCulture);
            Assert.Equal(double.Parse(h[4], CultureInfo.InvariantCulture), w.HeightAt(x, z), 3);
            Assert.Equal(bool.Parse(h[5]), w.IsWaterAt(x, z));
        }
    }

    [DataFact]
    public void Every_shipped_bank_and_terrain_parses()
    {
        var gd = TestData.Open();
        int banks = 0, wlds = 0;
        foreach (var path in Directory.EnumerateFiles(gd.Dir))
        {
            string n = Path.GetFileName(path).ToUpperInvariant();
            if (n.StartsWith("TEXTURE.", StringComparison.Ordinal))
            {
                Assert.NotNull(TextureBank.Parse(File.ReadAllBytes(path)));
                banks++;
            }
            else if (n.StartsWith("WLD.", StringComparison.Ordinal))
            {
                var w = WldTerrain.Parse(File.ReadAllBytes(path));
                Assert.NotNull(w);
                Assert.Equal(0, w.MissingChunkLayers);
                wlds++;
            }
        }
        Assert.True(banks > 200 && wlds > 10, $"{banks} banks, {wlds} WLDs");
    }

    [DataFact]
    public void Every_archive_opens_with_the_skynet_key()
    {
        var gd = TestData.Open();
        foreach (var name in new[] { "MDMDBRIF.BSA", "MDMDENMS.BSA", "MDMDHRES.BSA", "MDMDIMGS.BSA", "MDMDMAP2.BSA", "MDMDMUSC.BSA", "MDMDOBJS.BSA", "MDMDSFXS.BSA" })
        {
            using var a = gd.OpenArchive(name);
            Assert.Equal(0, a.DroppedEntries);
            Assert.NotEmpty(a.Entries);
        }
        var (hires, key) = gd.HiresArchive();
        Assert.EndsWith("MDMDHRES.BSA", hires, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(BsaVariant.SkyNetFull, key);
    }
}
