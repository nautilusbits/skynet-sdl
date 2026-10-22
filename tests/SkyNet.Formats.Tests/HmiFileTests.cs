using System.Text;

namespace SkyNet.Formats.Tests;

public class HmiFileTests
{
    /// <summary>A song with one track per stream; table at 0xF0, tracks back to back from 0x100, each stream at track + 0x60.</summary>
    static byte[] Song(params byte[][] streams)
    {
        var b = new Bytes().Raw(Encoding.ASCII.GetBytes(HmiFile.Magic)).PadTo(0xD2).U16(480).U16(120)
            .PadTo(0xE4).U16(streams.Length).U16(0).U32(0xF0).PadTo(0xF0);
        int off = 0x100;
        var offs = new List<int>();
        foreach (var s in streams) { offs.Add(off); off += 0x60 + s.Length; }
        foreach (var o in offs) b.U32(o);
        b.PadTo(0x100);
        foreach (var s in streams)
        {
            int start = b.Length;
            b.Raw(Encoding.ASCII.GetBytes(HmiFile.TrackMagic)).PadTo(start + 0x57).U32(0x60).PadTo(start + 0x60).Raw(s);
        }
        return b.ToArray();
    }

    [Fact]
    public void Header_and_note_on_durations_become_note_offs()
    {
        // note on ch 1 (60, vel 100, dur 0x81 0x00 = 128); +10 program 5; running-status note
        // with velocity 0 = an immediate off; end of track.
        var song = HmiFile.Parse(Song([0x00, 0x91, 60, 100, 0x81, 0x00, 0x0A, 0xC1, 5, 0x00, 0x91, 61, 0, 0, 0x00, 0xFF, 0x2F, 0]))!;
        Assert.Equal((480, 120), (song.Division, song.Rate));
        Assert.Equal(10, song.Length);
        Assert.Equal([128, 0], song.NoteDurations.ToArray());
        Assert.Equal(
            [new(0, 0x90, 1, 60, 100), new(10, 0xC0, 1, 5, 0), new(10, 0x80, 1, 61, 0), new(128, 0x80, 1, 60, 0)],
            song.Events.ToArray());
        Assert.Equal("end of track", song.Tracks[0].End);
    }

    [Fact]
    public void Private_fe_events_skip_their_fixed_lengths()
    {
        // FE 10: FE, sub, 2 bytes, then byte[4] (= 1) + 5 more counting byte[4]; FE 13: 12 bytes in all; then a bend and a tempo.
        byte[] s = [0x00, 0xFE, 0x10, 0, 0, 1, 9, 9, 9, 9, 9,
                    0x00, 0xFE, 0x13, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0,
                    0x05, 0xE2, 0x7F, 0x7F, 0x00, 0xFF, 0x51, 3, 0x07, 0xA1, 0x20, 0x00, 0xFF, 0x2F, 0];
        var song = HmiFile.Parse(Song(s))!;
        Assert.Equal(1, song.PrivateEvents[0x10]);
        Assert.Equal(1, song.PrivateEvents[0x13]);
        Assert.Equal([new(5, 0xE0, 2, 16383, 0), new(5, 0x51, 0, 500000, 0)], song.Events.ToArray());
    }

    [Fact]
    public void Track_ends_where_the_next_begins_and_duplicates_parse_once()
    {
        var b = Song([0x00, 0x90, 40, 1, 0x02], [0x03, 0xB0, 7, 100]);
        var song = HmiFile.Parse(b)!;
        Assert.Equal(2, song.Tracks.Length);
        Assert.Equal("ran off the end", song.Tracks[0].End);
        Assert.Equal([new(0, 0x90, 0, 40, 1), new(2, 0x80, 0, 40, 0), new(3, 0xB0, 0, 7, 100)], song.Events.ToArray());

        b[0xF4] = b[0xF0]; b[0xF5] = b[0xF1];   // second table entry = the first track
        Assert.Single(HmiFile.Parse(b)!.Tracks);
    }

    [Fact]
    public void Corrupt_varlen_and_bad_header_are_handled()
    {
        var song = HmiFile.Parse(Song([0xFF, 0xFF, 0xFF, 0xFF, 0x7F]))!;
        Assert.Equal("corrupt delta time", song.Tracks[0].End);
        Assert.Null(HmiFile.Parse(new byte[0x100]));
        var b = Song([0x00, 0xFF, 0x2F, 0]);
        b[0xE4] = 0;
        Assert.Null(HmiFile.Parse(b));
    }
}
