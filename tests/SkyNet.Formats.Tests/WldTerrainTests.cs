namespace SkyNet.Formats.Tests;

public class WldTerrainTests
{
    /// <summary>A WLD file from four flat 256×256 layers, chunks stored in reverse order to exercise the table.</summary>
    static byte[] Build(byte[][] layers, int dropTail = 0)
    {
        const int n = 128 * 128;
        int chunkSize = 22 + 4 * n;
        var b = new Bytes().U32(0).U32(2).U32(2).PadTo(0x90);
        int dataStart = 0x90 + 16;
        for (int ci = 0; ci < 4; ci++)
            b.U32(dataStart + (3 - ci) * chunkSize);
        for (int slot = 0; slot < 4; slot++)
        {
            int ci = 3 - slot, cy = ci / 2, cx = ci % 2;
            b.Zeros(22);
            for (int l = 0; l < 4; l++)
                for (int r = 0; r < 128; r++)
                    b.Raw(layers[l].AsSpan((cy * 128 + r) * 256 + cx * 128, 128));
        }
        var a = b.ToArray();
        return a[..(a.Length - dropTail)];
    }

    static byte[][] Layers(Func<int, int, int, int> f)
    {
        var ls = new byte[4][];
        for (int l = 0; l < 4; l++)
        {
            ls[l] = new byte[65536];
            for (int r = 0; r < 256; r++)
                for (int c = 0; c < 256; c++)
                    ls[l][r * 256 + c] = (byte)f(l, c, r);
        }
        return ls;
    }

    [Fact]
    public void Reassembles_chunks_through_the_offset_table()
    {
        var src = Layers((l, c, r) => (c * 7 + r * 13 + l * 31) & 0xFF);
        var w = WldTerrain.Parse(Build(src), smoothMaterials: false)!;
        for (int l = 0; l < 4; l++)
            Assert.Equal(src[l], w.Layer(l).ToArray());
        Assert.Equal(0, w.MissingChunkLayers);
    }

    [Fact]
    public void A_cut_off_layer_stays_zero()
    {
        var src = Layers((l, c, r) => 1);
        // The file's last chunk is chunk 0 (top-left); cut one byte off its layer 3.
        var w = WldTerrain.Parse(Build(src, dropTail: 1), smoothMaterials: false)!;
        Assert.Equal(1, w.MissingChunkLayers);
        Assert.Equal(0, w.SampleByte(3, 0, 0));
        Assert.Equal(1, w.SampleByte(3, 200, 0));
        Assert.Equal(1, w.SampleByte(2, 0, 0));
    }

    [Fact]
    public void Rejects_other_grid_sizes()
    {
        var f = Build(Layers((l, c, r) => 0));
        f[4] = 1;   // chunks_x = 1
        Assert.Null(WldTerrain.Parse(f));
        Assert.Null(WldTerrain.Parse(new byte[0x90]));
    }

    static int Corner(int c, int r) => (c, r) switch
    {
        (1, 0) => 4,
        (0, 1) => 7,
        (1, 1) => 10 | 0x80,   // bit 7 is not part of the index
        _ => 0,
    };

    [Fact]
    public void Height_follows_the_nw_se_split()
    {
        // Cell (0,0): NW idx 0 (0), NE idx 4 (80), SW idx 7 (120), SE idx 10 (160).
        var src = Layers((l, c, r) => l != 0 ? 0 : Corner(c, r));
        var w = WldTerrain.Parse(Build(src))!;
        double k = WldTerrain.ZFlipK;
        Assert.Equal(160, w.CornerHeight(1, 1));
        Assert.Equal(60, w.HeightAt(128, k - 64), 6);    // u .5, v .25: NW-NE-SE plane
        Assert.Equal(70, w.HeightAt(64, k - 128), 6);    // u .25, v .5: NW-SE-SW plane
        Assert.Equal((0, 0), WldTerrain.CellForWorld(-10, k));
        Assert.Equal((0, 255), WldTerrain.CellForWorld(0, 1));   // masks into the grid
        Assert.Equal(120, w.HeightGrid()[256]);
    }

    [Fact]
    public void Smoothing_blends_a_cell_ringed_by_one_material()
    {
        var src = Layers((l, c, r) => l == 2 ? ((c, r) == (5, 5) ? 1 : 2) : 0);
        var w = WldTerrain.Parse(Build(src))!;
        var l2 = w.Layer(2);
        Assert.Equal(0x0D, l2[5 * 256 + 5]);   // BLEND[(2 & 3) * 4 + (1 & 3)]
        Assert.Equal(65535, l2.Count(x => x == 2));
        Assert.Equal(1, WldTerrain.Parse(Build(src), smoothMaterials: false)!.SampleByte(2, 5, 5));
    }

    [Fact]
    public void Coalesce_retry_cannot_loop()
    {
        // After 0xFE the cell is redone with BR = TR, so rBR == rTR: no such pattern may give 0xFE.
        for (int p = 0; p < 256; p++)
            if ((p >> 6) == (p & 3))
                Assert.NotEqual(0xFE, WldTerrain.CoalesceTile[p]);
    }

    [Fact]
    public void Water_tiles()
    {
        var src = Layers((l, c, r) => l == 2 && c == 3 && r == 0 ? 58 | 0x40 : 0);
        var w = WldTerrain.Parse(Build(src), smoothMaterials: false)!;
        Assert.True(w.IsWaterAt(3 * 256 + 10, WldTerrain.ZFlipK - 10));
        Assert.False(w.IsWaterAt(10, WldTerrain.ZFlipK - 10));
    }
}
