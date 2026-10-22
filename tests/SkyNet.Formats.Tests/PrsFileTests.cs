using System.Text;

namespace SkyNet.Formats.Tests;

public class PrsFileTests
{
    static byte[] Ascii(string s) => Encoding.Latin1.GetBytes(s);

    [Fact]
    public void First_definition_wins_and_comments_sections_and_junk_are_skipped()
    {
        var p = PrsFile.Parse(Ascii("; comment\r\n[strings]\r\n  PICKUP = PICKED UP A GUN \r\nPICKUP=SNAGGED A GUN\r\n=nokey\r\nnoequals\r\nEMPTY=\nA = b = c\n"));
        Assert.Equal("PICKED UP A GUN", p.Text("PICKUP"));
        Assert.Equal<string>(["PICKUP", "EMPTY", "A"], p.Keys);
        Assert.Equal("", p.Text("EMPTY", "x"));
        Assert.Equal("b = c", p.Text("A"));
        Assert.Equal("fallback", p.Text("pickup", "fallback"));
    }

    [Fact]
    public void Text_stops_at_a_nul()
    {
        var p = PrsFile.Parse(Ascii("A=1\n\0B=2\n"));
        Assert.Equal<string>(["A"], p.Keys);
    }

    [DataFact]
    public void Every_prs_parses_and_matches_godot()
    {
        var files = GodotText.Brif("PRS");
        var refs = GodotText.Lines("PRS").ToList();
        Assert.Equal(refs.Count, files.Count);
        Assert.True(files.Count >= 4, $"{files.Count} PRS files");
        foreach (var r in refs)
        {
            var p = PrsFile.Parse(files[r[1]]);
            Assert.Equal(int.Parse(r[2]), p.Keys.Length);
            Assert.Equal(r[3], GodotText.Md5(string.Join('\n', p.Keys.Select(k => $"{k}={p.Values[k]}"))));
        }
        // The loose arena table parses too.
        Assert.NotEmpty(PrsFile.Parse(TestData.Open().ReadLoose("NETLEVEL.PRS")!).Keys);
    }
}
