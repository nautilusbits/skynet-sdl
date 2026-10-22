using System.Text;

namespace SkyNet.Formats.Tests;

public class FntFontTests
{
    /// <summary>A font with an 'A' (3 wide, rows 010/101/111), a '!' and an empty 'a' slot.</summary>
    static byte[] Font(int lineHeight = 3, int widthA = 3)
    {
        var b = new Bytes().U16(4).U16(lineHeight);
        int data = FntFont.HeaderSize;
        for (int i = 0; i < FntFont.GlyphCount; i++)
        {
            int cp = FntFont.FirstCodepoint + i;
            b.U16(data + i * 32).U16(cp == 'A' ? widthA : cp == '!' ? 1 : 0);
        }
        for (int i = 0; i < FntFont.GlyphCount; i++)
        {
            int cp = FntFont.FirstCodepoint + i;
            int[] rows = cp == 'A' ? [0b010 << 13, 0b101 << 13, 0b111 << 13] : cp == '!' ? [0x8000, 0, 0x8000] : [];
            for (int r = 0; r < 16; r++)
                b.U16(r < rows.Length ? rows[r] : 0);
        }
        return b.ToArray();
    }

    [Fact]
    public void Parses_header_table_and_bits_high_bit_first()
    {
        var f = FntFont.Parse(Font())!;
        Assert.Equal((4, 3), (f.EmWidth, f.LineHeight));
        int a = FntFont.IndexOf('A');
        Assert.Equal(3, f.Glyphs[a].Width);
        Assert.Equal(new byte[] { 0, 1, 0, 1, 0, 1, 1, 1, 1 }, f.GlyphBitmap(a));
        Assert.True(f.Pixel(a, 1, 0));
        Assert.False(f.Pixel(a, 3, 2));
        Assert.Equal(8644, Font().Length);
    }

    [Fact]
    public void Lower_case_borrows_an_empty_slot_and_space_uses_the_em_width()
    {
        var f = FntFont.Parse(Font())!;
        Assert.Equal(FntFont.IndexOf('A'), f.ResolveIndex('a'));
        Assert.Equal(4, f.Advance('a'));
        Assert.Equal(4, f.Advance(' '));
        Assert.Equal(2, f.Advance('!'));
        Assert.Equal(-1, FntFont.IndexOf(0x20));
        Assert.Equal(-1, FntFont.IndexOf(0x111));
        Assert.Equal(4 + 4 + 2, f.MeasureWidth("A !"));
    }

    [Fact]
    public void Rejects_short_files_bad_line_heights_and_wide_glyphs()
    {
        Assert.Null(FntFont.Parse(new byte[100]));
        Assert.Null(FntFont.Parse(Font(lineHeight: 0)));
        Assert.Null(FntFont.Parse(Font(lineHeight: 17)));
        Assert.Null(FntFont.Parse(Font(widthA: 17)));
        Assert.NotNull(FntFont.Parse(Font(widthA: 16)));
    }

    [Fact]
    public void Rows_past_the_end_of_the_file_read_as_blank()
    {
        var f = FntFont.Parse(Font().AsSpan(0, FntFont.HeaderSize + 2))!;
        Assert.Equal(0x8000, f.RowWord(0, 0));
        Assert.Equal(0, f.RowWord(0, 1));
    }

    /// <summary>Every font of the player's data against the Godot port's atlas (GodotReference/godot_dump_text.txt).</summary>
    [DataFact]
    public void Every_font_parses_and_matches_godot()
    {
        var refs = GodotText.Lines("FNT").ToList();
        Assert.Equal(12, refs.Count);
        foreach (var r in refs)
        {
            var bytes = File.ReadAllBytes(GameDataLocator.FindFile(TestData.Dir, r[1])!);
            Assert.Equal(8644, bytes.Length);
            var f = FntFont.Parse(bytes)!;
            Assert.Equal((int.Parse(r[2]), int.Parse(r[3])), (f.EmWidth, f.LineHeight));
            // What each codepoint 0x21..0x110 draws (an empty lower-case slot shows its capital).
            var gl = Enumerable.Range(0, FntFont.GlyphCount).Select(i => f.ResolveIndex(FntFont.FirstCodepoint + i)).Select(i =>
                $"{f.Glyphs[i].Width}:{string.Concat(f.GlyphBitmap(i).Select(p => p == 1 ? '1' : '0'))}");
            Assert.Equal(r[4], GodotText.Md5(string.Join('|', gl)));
            var adv = Enumerable.Range(0x20, 0x111 - 0x20).Select(cp => f.Advance(cp).ToString());
            Assert.Equal(r[5], GodotText.Md5(string.Join(',', adv)));
        }
    }

    /// <summary>FONT0010, DOS's 640 × 480 HUD font: the same layout as the others, 12 × 11, every
    /// glyph a slot of the table in order, the "5" 10 wide with a solid two-row top bar.</summary>
    [DataFact]
    public void The_hi_res_hud_font_parses()
    {
        var bytes = File.ReadAllBytes(GameDataLocator.FindFile(TestData.Dir, "FONT0010.FNT")!);
        var f = FntFont.Parse(bytes)!;
        Assert.Equal((12, 11), (f.EmWidth, f.LineHeight));
        for (int i = 0; i < FntFont.GlyphCount; i++)
            Assert.Equal(FntFont.HeaderSize + 32 * i, f.Glyphs[i].DataOffset);
        Assert.Equal((8, 4), (f.Glyphs[FntFont.IndexOf('0')].Width, f.Glyphs[FntFont.IndexOf('1')].Width));
        int five = FntFont.IndexOf('5');
        Assert.Equal(10, f.Glyphs[five].Width);
        Assert.Equal(0xFF00, f.RowWord(five, 0) & 0xFFC0);   // ########..
        Assert.Equal(0xFF00, f.RowWord(five, 1) & 0xFFC0);
        Assert.Equal(0, f.RowWord(five, 10));                // the 11th row is blank
        Assert.Equal(12, f.SpaceAdvance);
    }
}

/// <summary>The Godot port's decode of the fonts and text formats (hashes only); dump_text.gd.txt regenerates it.</summary>
static class GodotText
{
    static readonly string[] Reference = File.ReadAllLines(Path.Combine(AppContext.BaseDirectory, "GodotReference", "godot_dump_text.txt"));

    public static IEnumerable<string[]> Lines(string tag) =>
        Reference.Where(l => l.StartsWith(tag + " ", StringComparison.Ordinal)).Select(l => l.Split(' '));

    public static string Md5(string s) => TestData.Md5(Encoding.UTF8.GetBytes(s));

    /// <summary>MDMDBRIF.BSA's files with extension <paramref name="ext"/>, decrypted.</summary>
    public static Dictionary<string, byte[]> Brif(string ext)
    {
        using var a = TestData.Open().OpenArchive("MDMDBRIF.BSA");
        return a.Entries.Where(e => e.Name.EndsWith("." + ext, StringComparison.OrdinalIgnoreCase))
            .ToDictionary(e => e.Name, e => a.Read(e)!);
    }
}
