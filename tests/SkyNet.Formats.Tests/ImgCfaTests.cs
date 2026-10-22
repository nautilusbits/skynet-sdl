namespace SkyNet.Formats.Tests;

public class ImgCfaTests
{
    [Fact]
    public void Img_reads_header_and_raw_pixels()
    {
        var b = new Bytes().U16(5).U16(7).U16(3).U16(2).U16(0).U16(99).Raw([1, 2, 3, 4, 5, 6]).ToArray();
        var img = ImgFile.Parse(b)!;
        Assert.Equal((5, 7, 3, 2, 99), (img.X, img.Y, img.Width, img.Height, img.RecordSize));
        Assert.Equal(6, img[2, 1]);
        Assert.Equal([1, 2, 3, 4, 5, 6], img.Pixels.ToArray());
    }

    [Fact]
    public void Img_rejects_compressed_short_and_empty()
    {
        Assert.Null(ImgFile.Parse(new Bytes().U16(0).U16(0).U16(2).U16(2).U16(1).U16(0).Zeros(4).ToArray()));
        Assert.Null(ImgFile.Parse(new Bytes().U16(0).U16(0).U16(2).U16(2).U16(0).U16(0).Zeros(3).ToArray()));
        Assert.Null(ImgFile.Parse(new Bytes().U16(0).U16(0).U16(0).U16(2).U16(0).U16(0).ToArray()));
        Assert.Null(ImgFile.Parse(new byte[11]));
    }

    [Fact]
    public void Cfa_low_layout_decodes_literals_and_runs()
    {
        // 4 x 2, two frames. Frame 0: literal 3 (c = 2), run of 5 x 9 (c = 0x84). Frame 1: 8 x 7.
        var b = new Bytes().U16(4).U16(2).Zeros(7).U8(2).U16(16).U16(22)
            .U8(2).Raw([1, 2, 3]).U8(0x84).U8(9)
            .U8(0x87).U8(7)
            .ToArray();
        var c = CfaFile.Parse(b)!;
        Assert.Equal(CfaLayout.Low, c.Layout);
        Assert.Equal((4, 2, 2), (c.Width, c.Height, c.Frames.Length));
        Assert.Equal([1, 2, 3, 9, 9, 9, 9, 9], c.Frames[0].ToArray());
        Assert.Equal(Enumerable.Repeat((byte)7, 8), c.Frames[1].ToArray());
    }

    [Fact]
    public void Cfa_low_layout_leaves_unreached_pixels_zero_and_clips_runs()
    {
        var b = new Bytes().U16(3).U16(1).Zeros(7).U8(1).U16(14).U8(0xFF).U8(4).ToArray();
        Assert.Equal([4, 4, 4], CfaFile.Parse(b)!.Frames[0].ToArray());
        var cut = new Bytes().U16(3).U16(1).Zeros(7).U8(1).U16(14).U8(5).U8(6).ToArray();
        Assert.Equal([6, 0, 0], CfaFile.Parse(cut)!.Frames[0].ToArray());
    }

    static byte[] Hires(int w, int h, params byte[][] frames)
    {
        var b = new Bytes().U32(10).U32(20).U32(w).U32(h).Zeros(24).U32(frames.Length);
        int off = CfaFile.HiresHeader + 4 * frames.Length;
        foreach (var f in frames) { b.U32(off); off += f.Length; }
        foreach (var f in frames) b.Raw(f);
        return b.ToArray();
    }

    [Fact]
    public void Cfa_hires_layout_is_told_apart_by_its_first_offset()
    {
        // 3 x 2: row 0 = skip 1, two pixels; row 1 blank (width, 0).
        var b = Hires(3, 2, [1, 2, 5, 6, 3, 0]);
        Assert.Equal(1, CfaFile.HiresFrameCount(b));
        var c = CfaFile.Parse(b)!;
        Assert.Equal((CfaLayout.Hires, 10, 20), (c.Layout, c.X, c.Y));
        Assert.Equal([0, 5, 6, 0, 0, 0], c.Frames[0].ToArray());

        b[CfaFile.HiresHeader] ^= 4;   // first offset no longer 44 + 4n
        Assert.False(CfaFile.IsHires(b));
    }

    [Fact]
    public void Cfa_hires_frames_stop_at_the_next_offset_and_reject_huge_sizes()
    {
        var c = CfaFile.Parse(Hires(2, 2, [0, 1, 7], [0, 2, 8, 9, 0, 2, 1, 2]))!;
        Assert.Equal([7, 0, 0, 0], c.Frames[0].ToArray());
        Assert.Equal([8, 9, 1, 2], c.Frames[1].ToArray());
        Assert.Null(CfaFile.Parse(Hires(4096, 4096, [0, 0])));
        Assert.Null(CfaFile.Parse(new Bytes().U16(2048).U16(2048).Zeros(7).U8(9).Zeros(20).ToArray()));
    }
}
