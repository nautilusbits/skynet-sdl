namespace SkyNet.Formats.Tests;

public class TextureBankTests
{
    /// <summary>Header (count, 16-byte name, 8 bytes) and the outer table; each record = (byte +0, byte +1, u32 descriptor offset, 14 bytes).</summary>
    static Bytes Header(string name, params (int Byte1, long Desc)[] recs)
    {
        var b = new Bytes().U16(recs.Length).Raw(System.Text.Encoding.ASCII.GetBytes(name.PadRight(16)));
        b.PadTo(26);
        foreach (var r in recs) b.U8(0).U8(r.Byte1).U32(r.Desc).Zeros(14);
        return b;
    }

    static Bytes Descriptor(Bytes b, int w, int h, long pixRel, int rowGap, int depth, int ms = 0) =>
        b.U32(0).U16(w).U16(h).U16(0).Zeros(4).U32(pixRel).U16(rowGap).U16(depth).U16(ms).Zeros(4);

    [Fact]
    public void Flat_colour_reads_the_records_own_byte_and_the_table_may_end_at_the_file_end()
    {
        var file = Header("Solid", (5, 0), (9, 0), (77, 0)).ToArray();
        Assert.Equal(26 + 60, file.Length);   // last record ends exactly at ro + 18
        var bank = TextureBank.Parse(file)!;
        Assert.Equal("Solid", bank.Name);
        Assert.Equal([5, 9, 77], bank.Records.Select(r => (int)r.Pixels[0]));
        Assert.All(bank.Records, r => Assert.Equal((1, 1), (r.Width, r.Height)));

        var cut = TextureBank.Parse(file.AsSpan(0, file.Length - 1))!;
        Assert.Equal(2, cut.Records.Length);  // ro + 18 > size drops only the last record
    }

    [Fact]
    public void Four_wall_records_interleave_at_stride_w_plus_row_gap()
    {
        const int w = 4, h = 3, gap = 12, stride = w + gap;
        int descBase = 26 + 4 * 20;
        int buf = descBase + 4 * 28;
        var b = Header("walls", (0, descBase), (0, descBase + 28), (0, descBase + 56), (0, descBase + 84));
        for (int k = 0; k < 4; k++)
            Descriptor(b, w, h, buf + k * w - (descBase + 28 * k), gap, 1);
        for (int i = 0; i < h * stride; i++) b.U8(i);
        var bank = TextureBank.Parse(b.ToArray())!;
        Assert.Equal(4, bank.Records.Length);
        for (int k = 0; k < 4; k++)
        {
            var r = bank.Records[k];
            Assert.Equal((w, h), (r.Width, r.Height));
            for (int y = 0; y < h; y++)
                for (int x = 0; x < w; x++)
                    Assert.Equal(y * stride + k * w + x, r.Pixels[y * w + x]);
        }
    }

    [Fact]
    public void Sprite_transparency_runs_leave_index_zero_and_drop_pixels_past_w()
    {
        int desc = 26 + 20;
        var b = Header("sprite", (0, desc));
        Descriptor(b, 9, 9, 28, 0, 1, ms: 100);
        b.U32(4);                         // frame table: frame 0 at +4
        b.U16(4).U16(2);                  // W 4, H 2
        b.U8(1).U8(2).U8(11).U8(12).U8(1).U8(0);   // row 0: . 11 12 .
        b.U8(2).U8(3).U8(21).U8(22).U8(23);        // row 1: . . 21 22 (23 is past W)
        b.Zeros(40);                      // the last frame runs to the end of the file
        var r = TextureBank.Parse(b.ToArray())!.Records[0];
        Assert.Equal((4, 2), (r.Width, r.Height));
        Assert.Equal(new byte[] { 0, 11, 12, 0, 0, 0, 21, 22 }, r.Pixels.ToArray());
        Assert.Equal((1, 100), (r.Depth, r.MsPerFrame));
    }

    [Fact]
    public void Uncompressed_sprite_frames_and_frame_list()
    {
        int desc = 26 + 20;
        var b = Header("anim", (0, desc));
        Descriptor(b, 2, 2, 28, 0, 2, ms: 50);
        int f0 = 8, f1 = f0 + 4 + 2 * 4;          // each frame: header + H*(W+2)
        b.U32(f0).U32(f1);
        b.U16(2).U16(2).U8(0).U8(0).U8(1).U8(2).U8(0).U8(0).U8(3).U8(4);
        b.U16(2).U16(2).U8(0).U8(0).U8(5).U8(6).U8(0).U8(0).U8(7).U8(8);
        var file = b.ToArray();
        var r = TextureBank.Parse(file)!.Records[0];
        Assert.Equal(new byte[] { 1, 2, 3, 4 }, r.Pixels.ToArray());
        Assert.Equal(2, r.Depth);
        var frames = TextureBank.ParseRecordFrames(file, 0);
        Assert.Equal(2, frames.Length);
        Assert.Equal(new byte[] { 5, 6, 7, 8 }, frames[1].Pixels.ToArray());
    }

    [Fact]
    public void A_bad_record_keeps_its_slot()
    {
        var b = Header("mixed", (0, 100000), (42, 0), (0, 0));
        var bank = TextureBank.Parse(b.ToArray())!;
        Assert.True(bank.Records[0].IsEmpty);
        Assert.Equal(42, bank.Records[1].Pixels[0]);
    }

    [Fact]
    public void A_256_wide_record_without_a_sane_frame_table_is_a_wall()
    {
        int desc = 26 + 20;
        var b = Header("wide", (0, desc));
        Descriptor(b, 256, 2, 28, 0, 1);
        for (int i = 0; i < 512; i++) b.U8(i < 4 ? 0 : i & 0xFF);   // first u32 = 0 < depth*4
        var r = TextureBank.Parse(b.ToArray())!.Records[0];
        Assert.Equal((256, 2), (r.Width, r.Height));
        Assert.Equal(5, r.Pixels[5]);
        Assert.Equal(0, r.Pixels[256]);
    }

    [Fact]
    public void Rejects_bad_headers()
    {
        Assert.Null(TextureBank.Parse(new byte[10]));
        Assert.Null(TextureBank.Parse(new byte[100]));          // count 0
        var many = new byte[100]; many[0] = 1; many[1] = 1;     // 257 records
        Assert.Null(TextureBank.Parse(many));
    }

    [Fact]
    public void Sparse_rows_underrun_fails() =>
        Assert.Null(TextureBank.DecodeSparseRows([0, 2, 1], 4, 1));
}
