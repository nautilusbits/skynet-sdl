using System.Text;

namespace SkyNet.Formats.Tests;

/// <summary>
/// Every IMG and CFA in the player's archives and every loose HMI, compared with the Godot
/// port's decode (GodotReference/godot_dump_media.txt: sizes and MD5s of palette indices and
/// events, no game data). Skipped without SKYNET_DATA.
/// </summary>
public class MediaRealDataTests
{
    static readonly string[] Reference = File.ReadAllLines(Path.Combine(AppContext.BaseDirectory, "GodotReference", "godot_dump_media.txt"));

    static IEnumerable<string[]> Lines(string tag) =>
        Reference.Where(l => l.StartsWith(tag + " ", StringComparison.Ordinal)).Select(l => l.Split(' '));

    static IEnumerable<string> Archives(GameData gd) =>
        Directory.GetFiles(gd.Dir).Select(Path.GetFileName).OfType<string>()
            .Where(n => n.EndsWith(".BSA", StringComparison.OrdinalIgnoreCase)).Order(StringComparer.Ordinal);

    [DataFact]
    public void Every_img_and_cfa_parses_and_matches_godot()
    {
        var gd = TestData.Open();
        var refs = Lines("IMG").Concat(Lines("CFA")).ToDictionary(l => l[0] + " " + l[1] + " " + l[2]);
        int img = 0, cfa = 0, hires = 0, hiresFrames = 0;
        foreach (var arc in Archives(gd))
        {
            using var a = gd.OpenArchive(arc);
            foreach (var e in a.Entries)
            {
                bool isImg = e.Name.EndsWith(".IMG", StringComparison.Ordinal);
                if (!isImg && !e.Name.EndsWith(".CFA", StringComparison.Ordinal))
                    continue;
                var r = refs[(isImg ? "IMG " : "CFA ") + arc + " " + e.Name];
                var bytes = a.Read(e)!;
                if (isImg)
                {
                    var m = ImgFile.Parse(bytes);
                    Assert.NotNull(m);
                    Assert.Equal(string.Join(' ', r[3..]), $"{m.Width} {m.Height} {TestData.Md5(m.Pixels.AsSpan())}");
                    img++;
                }
                else
                {
                    var c = CfaFile.Parse(bytes);
                    Assert.NotNull(c);
                    var all = c.Frames.SelectMany(f => f).ToArray();
                    Assert.Equal(string.Join(' ', r[3..]),
                        $"{(c.Layout == CfaLayout.Hires ? 1 : 0)} {c.Width} {c.Height} {c.Frames.Length} {TestData.Md5(all)}");
                    if (c.Layout == CfaLayout.Hires) { hires++; hiresFrames += c.Frames.Length; }
                    cfa++;
                }
            }
        }
        Assert.Equal(refs.Count, img + cfa);
        Assert.True(img > 200 && cfa > 100, $"{img} IMG, {cfa} CFA");
        // MDMDHRES.BSA: the 14 hi-res WEAPON*.CFA, 73 frames; RADCOUNT and TNET* stay 16-bit.
        Assert.Equal((14, 73), (hires, hiresFrames));
    }

    [DataFact]
    public void Hires_weapon00_sits_on_the_640x480_corner()
    {
        var gd = TestData.Open();
        using var a = gd.OpenArchive("MDMDHRES.BSA");
        var c = CfaFile.Parse(a.Read("WEAPON00.CFA")!)!;
        Assert.Equal((640, 480), (c.X + c.Width, c.Y + c.Height));
        Assert.Equal(CfaLayout.Low, CfaFile.Parse(a.Read("RADCOUNT.CFA")!)!.Layout);
    }

    [DataFact]
    public void Every_loose_hmi_parses_and_matches_godot()
    {
        var gd = TestData.Open();
        int n = 0;
        foreach (var r in Lines("HMI"))
        {
            var song = HmiFile.Parse(gd.ReadLoose(r[1])!);
            Assert.NotNull(song);
            var recs = song.Events.Select(e => $"{e.Tick},{e.Kind},{e.Channel},{e.Data1},{e.Data2}").Order(StringComparer.Ordinal);
            Assert.Equal(string.Join(' ', r[2..]),
                $"{song.Division} {song.Rate} {song.Length} {song.Events.Length} {TestData.Md5(Encoding.UTF8.GetBytes(string.Join('|', recs)))}");
            Assert.Equal(120, song.Rate);
            Assert.Contains(song.Tracks, t => t.End == "end of track");
            Assert.DoesNotContain(song.Tracks, t => t.End.StartsWith("lost sync", StringComparison.Ordinal) || t.End.StartsWith("unknown", StringComparison.Ordinal));
            n++;
        }
        Assert.Equal(Directory.GetFiles(gd.Dir).Count(p => p.EndsWith(".HMI", StringComparison.OrdinalIgnoreCase)), n);
        Assert.True(n >= 20);
    }
}
