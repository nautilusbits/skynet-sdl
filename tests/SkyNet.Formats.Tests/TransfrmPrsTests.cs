using System.Text;

namespace SkyNet.Formats.Tests;

public class TransfrmPrsTests
{
    [Fact]
    public void Sections_keep_lower_cased_frames_in_order_and_drop_single_frame_objects()
    {
        var t = TransfrmPrs.Parse(Encoding.Latin1.GetBytes(
            "[object0]\nName = CarHip0a\ntype = 2\nframes = 3\nframe0 = CARHIP0A\nframe1 = carhip0b\nframe2=carhip0c\n" +
            "; c\n[object1]\nname = lone\nframe0 = lone\n[object2]\nframe0 = a\nframe1 = b\n"));
        Assert.Single(t.Objects);
        var o = t.Objects["carhip0a"];
        Assert.Equal<string>(["carhip0a", "carhip0b", "carhip0c"], o.Frames);
        Assert.Equal("2", o.Type);
        Assert.Equal<string>(o.Frames, t.StagesOf("CARHIP0A"));
        Assert.Empty(t.StagesOf("lone"));
    }

    [Fact]
    public void A_later_section_with_the_same_name_replaces_the_earlier()
    {
        var t = TransfrmPrs.Parse(Encoding.Latin1.GetBytes("[a]\nname=x\nframe0=1\nframe1=2\n[b]\nname=x\nframe0=3\nframe1=4\n"));
        Assert.Equal<string>(["3", "4"], t.StagesOf("x"));
    }

    [DataFact]
    public void Transfrm_prs_matches_godot()
    {
        var r = GodotText.Lines("TRANSFRM").Single();
        var t = TransfrmPrs.Parse(GodotText.Brif("PRS")["TRANSFRM.PRS"]);
        Assert.Equal(int.Parse(r[1]), t.Objects.Count);
        var objs = t.Objects.Keys.Order(StringComparer.Ordinal).Select(n => $"{n}:{string.Join(',', t.Objects[n].Frames)}");
        Assert.Equal(r[2], GodotText.Md5(string.Join('|', objs)));
    }
}
