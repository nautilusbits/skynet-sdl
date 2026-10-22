using System.Text;

namespace SkyNet.Formats.Tests;

public class BriefingTests
{
    const string Sample =
        "[EN]\r\n[009]\r\nDestroy the base.\r\nGet out.\r\n####\r\n%%%%\r\n" +
        "[BR]\r\n[v00012]\r\n[001]\r\nHello there.\r\n####\r\nSecond line\r\nwrapped.\r\n####\r\n[002]\r\nReply.\r\n####\r\n[000.102.3]\r\nignored\r\n####\r\n%%%%\r\n" +
        "[M1]\r\n####\r\n####\r\nCamp defenses destroyed\r\n####\r\n%%%%\r\n" +
        "[M3]\r\nThird\r\n####\r\nTrailing\r\n%%%%\r\n" +
        "[G2]\r\n####\r\nHint two\r\n####\r\n%%%%\r\n" +
        "[TA]\r\ntachkftr tacscout\r\n####\r\ntacdrone\r\n%%%%\r\n";

    [Fact]
    public void Sections_entries_tags_and_blank_mission_entries()
    {
        var b = Briefing.Parse(Encoding.Latin1.GetBytes(Sample));
        Assert.Equal("Destroy the base. Get out.", b.Objectives);
        Assert.Equal(9, b.Picture);
        Assert.Equal<BriefingLine>([new BriefingLine(1, "Hello there."), new BriefingLine(1, "Second line wrapped."), new BriefingLine(2, "Reply.")], b.Lines);
        // Blank [M1] entries are kept: three objectives, the line shows at the last one.
        Assert.Equal<string>(["", "", "Camp defenses destroyed"], b.MissionTexts[0]);
        Assert.Empty(b.MissionTexts[1]);
        Assert.Equal<string>(["Third", "Trailing"], b.MissionTexts[2]);
        Assert.Equal(5, b.ObjectiveCount);
        Assert.Equal<string>(["Camp defenses destroyed", "", "Third", "", ""], b.Missions);
        Assert.Equal("Hint two", b.Hints[1]);
        Assert.Equal(9, b.Hints.Length);
        Assert.Equal<string>(["tachkftr", "tacscout", "tacdrone"], b.Tactical);
    }

    [Fact]
    public void No_picture_when_text_comes_first_and_the_default_speaker_is_3()
    {
        var b = Briefing.Parse(Encoding.Latin1.GetBytes("[BR]\nHi\n####\n%%%%\n[EN]\ntext\n[007]\n%%%%\n"));
        Assert.Equal(-1, b.Picture);
        Assert.Equal<BriefingLine>([new BriefingLine(3, "Hi")], b.Lines);
        Assert.Equal("text", b.Objectives);
        Assert.Equal(0, b.ObjectiveCount);
    }

    [DataFact]
    public void Every_briefing_parses_and_matches_godot()
    {
        var files = GodotText.Brif("TXT");
        var refs = GodotText.Lines("TXT").ToList();
        Assert.Equal(refs.Count, files.Count);
        Assert.True(files.Count > 20, $"{files.Count} briefings");
        foreach (var r in refs)
        {
            var b = Briefing.Parse(files[r[1]]);
            Assert.Equal((int.Parse(r[2]), int.Parse(r[3]), int.Parse(r[4]), int.Parse(r[5])),
                (b.Lines.Length, b.ObjectiveCount, b.Tactical.Length, b.Picture));
            Assert.Equal(r[6], GodotText.Md5(b.Objectives));
            Assert.Equal(r[7], GodotText.Md5(string.Join('\n', b.Lines.Select(l => $"{l.Speaker}:{l.Text}"))));
            Assert.Equal(r[8], GodotText.Md5(string.Join('\n', b.Missions)));
            Assert.Equal(r[9], GodotText.Md5(string.Join('\n', b.MissionTexts.Select(s => string.Join('/', s)))));
            Assert.Equal(r[10], GodotText.Md5(string.Join('\n', b.Hints)));
            Assert.Equal(r[11], GodotText.Md5(string.Join(' ', b.Tactical)));
        }
    }
}
