using System.Buffers.Binary;
using System.Collections.Immutable;
using System.Runtime.InteropServices;

namespace SkyNet.Formats;

/// <summary>
/// WLD.NNN, the outdoor terrain: one 256 × 256 heightmap with four byte layers, stored as 2 × 2
/// chunks of 128 × 128 cells. u32 chunks_x at +4, chunks_y at +8, a u32 chunk offset table at
/// 0x90 (row-major), and per chunk a 22-byte header followed by its four 16 384-byte layers.
/// Layer 0 bits 0-6 index <see cref="HeightCurve"/> (bit 7: "non-planar", not a diagonal
/// selector); layer 1 &gt;&gt; 2 is a decoration id; layer 2 bits 0-5 the TEXTURE.302 tile, bit 6
/// rotate 90°, bit 7 rotate 180°; layer 3 is unknown.
/// Row 0 is the north edge (largest Z); cells are 256 units; every cell splits NW–SE.
/// Ported from the Godot port's wld_terrain.gd, including the two load-time material passes.
/// </summary>
public sealed class WldTerrain
{
    public const int ChunkTableOffset = 0x90;
    public const int ChunkHeaderSize = 22;
    public const int ChunkCells = 128;
    public const int GridW = 256;
    public const int GridH = 256;
    public const int LayerBytes = GridW * GridH;
    public const double WorldPerCell = 256.0;
    /// <summary>World Z at row 0 (row N is at Z = K − N·256).</summary>
    public const double ZFlipK = GridH * WorldPerCell;

    /// <summary>The 128-entry height curve (legacy dword_104FA8), world units, Y up.</summary>
    public static ReadOnlySpan<short> HeightCurve =>
    [
           0,   40,   40,   40,   80,   80,   80,  120,  120,  120,
         160,  160,  160,  200,  200,  200,  240,  240,  240,  280,
         280,  320,  320,  320,  360,  360,  400,  400,  400,  440,
         440,  480,  480,  480,  520,  520,  560,  560,  600,  600,
         600,  640,  640,  680,  680,  720,  720,  760,  760,  800,
         800,  840,  840,  880,  880,  920,  920,  960, 1000, 1000,
        1040, 1040, 1080, 1120, 1120, 1160, 1160, 1200, 1240, 1240,
        1280, 1320, 1320, 1360, 1400, 1440, 1440, 1480, 1520, 1560,
        1600, 1600, 1640, 1680, 1720, 1760, 1800, 1840, 1880, 1920,
        1960, 2000, 2040, 2080, 2120, 2200, 2240, 2280, 2320, 2400,
        2440, 2520, 2560, 2640, 2680, 2760, 2840, 2920, 3000, 3080,
        3160, 3240, 3360, 3440, 3560, 3680, 3800, 3960, 4080, 4280,
        4440, 4680, 4920, 5200, 5560, 6040, 6680, 7760,
    ];

    /// <summary>4 × 4 material transition matrix (legacy byte_104942), index nb*4 + centre.</summary>
    public static ReadOnlySpan<byte> MaterialBlend =>
    [
        0x00, 0x04, 0x13, 0x1D, 0x08, 0x01, 0x09, 0x1C,
        0x17, 0x0D, 0x02, 0x0E, 0x21, 0x1C, 0x12, 0x03,
    ];

    /// <summary>Material remap LUT (legacy byte_104952): 0..3 base group, 0xFF skip.</summary>
    public static ReadOnlySpan<byte> MaterialRemap =>
    [
        0x00, 0x01, 0x02, 0x03, 0x00, 0xFF, 0xFF, 0xFF,
        0x01, 0x01, 0xFF, 0xFF, 0xFF, 0x02, 0x02, 0xFF,
        0xFF, 0xFF, 0x03, 0x00, 0xFF, 0xFF, 0xFF, 0x02,
        0x01, 0xFF, 0xFF, 0xFF, 0x03, 0x00, 0xFF, 0xFF,
        0xFF, 0x03, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF,
        0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF,
        0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF,
        0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF,
    ];

    /// <summary>
    /// 2 × 2 corner coalesce LUT (legacy DAT_00116200), index (TL&lt;&lt;4)|(BL&lt;&lt;2)|TR|(BR&lt;&lt;6).
    /// As the code reads it: 0xFE = copy TR onto BR and redo the cell, 0xFF = no change.
    /// (The Godot table comment says the opposite; its code and the format notes agree with this.)
    /// </summary>
    public static ReadOnlySpan<byte> CoalesceTile =>
    [
        0xFF, 0xC5, 0xD4, 0xDE, 0x45, 0x70, 0xFE, 0xFE, 0x54, 0xFE, 0x16, 0xFE, 0x5E, 0xFE, 0xFE, 0x72,
        0x05, 0xC6, 0x22, 0x24, 0x06, 0x07, 0x0B, 0x26, 0x62, 0x0B, 0xFE, 0xFE, 0x24, 0x1A, 0xFE, 0xFE,
        0x14, 0x28, 0xD5, 0x29, 0x22, 0xFE, 0x28, 0xFE, 0x55, 0x0B, 0x16, 0x10, 0x29, 0xFE, 0x10, 0xFE,
        0x1E, 0xE4, 0xE3, 0xDF, 0x24, 0xFE, 0xFE, 0x1B, 0x23, 0xFE, 0xFE, 0xEC, 0x1F, 0x1A, 0x10, 0x20,
        0x85, 0x86, 0xD5, 0xA4, 0x46, 0x87, 0x22, 0x66, 0x15, 0x22, 0xFE, 0xFE, 0x64, 0xA6, 0xFE, 0xFE,
        0x30, 0xC7, 0xFE, 0xFE, 0x47, 0xFF, 0xCA, 0xD9, 0xFE, 0x4A, 0x73, 0xFE, 0xFE, 0x59, 0xFE, 0x74,
        0xFE, 0x25, 0x15, 0xFE, 0x22, 0x0A, 0xCB, 0x67, 0x15, 0x0B, 0x0C, 0x2A, 0xFE, 0xA7, 0x6A, 0xFE,
        0xFE, 0x26, 0xFE, 0xEB, 0x24, 0x19, 0x67, 0xDA, 0xFE, 0x27, 0xFE, 0x6D, 0x2B, 0x1A, 0x2D, 0x1B,
        0x94, 0xE2, 0xD5, 0xE3, 0x22, 0xFE, 0x55, 0xFE, 0x95, 0x28, 0x96, 0x23, 0x1F, 0xFE, 0x23, 0xFE,
        0xFE, 0xE5, 0x25, 0xFE, 0x46, 0x8A, 0x8B, 0x27, 0x22, 0x4B, 0x8C, 0x6A, 0xFE, 0x67, 0xAA, 0xFE,
        0x31, 0xFE, 0xD6, 0xFE, 0xFE, 0x33, 0xCC, 0xFE, 0x56, 0x4C, 0xFF, 0xCF, 0xFE, 0xFE, 0x4F, 0x75,
        0xFE, 0xFE, 0x29, 0xEC, 0xFE, 0xFE, 0x2A, 0xED, 0x69, 0x6A, 0x0F, 0xD0, 0x2C, 0x2D, 0x10, 0x11,
        0x9E, 0x24, 0x23, 0x9F, 0x64, 0xFE, 0xFE, 0xAB, 0x63, 0xFE, 0xFE, 0xAC, 0x5F, 0x6B, 0x6C, 0xA0,
        0xFE, 0xE6, 0xFE, 0x2B, 0x26, 0x99, 0x27, 0x9A, 0xFE, 0xE7, 0xFE, 0xAD, 0x6B, 0x5A, 0x6D, 0x9B,
        0xFE, 0xFE, 0xE9, 0x2C, 0xFE, 0xFE, 0xEA, 0x2D, 0x29, 0x2A, 0x8F, 0x90, 0x6C, 0x6D, 0x50, 0x91,
        0x32, 0xFE, 0xFE, 0xE0, 0xFE, 0x34, 0xFE, 0xDB, 0xFE, 0xFE, 0x35, 0xD1, 0x60, 0x5B, 0x51, 0xFF,
    ];

    /// <summary>TEXTURE.302 tiles that are water (outdoor lakes are painted, not marked).</summary>
    public static ReadOnlySpan<byte> WaterMaterials => [58, 59, 61];

    readonly byte[][] _layers;

    /// <summary>Layers that lay (wholly or partly) past the end of the file and were left zero.</summary>
    public int MissingChunkLayers { get; }

    WldTerrain(byte[][] layers, int missing)
    {
        _layers = layers;
        MissingChunkLayers = missing;
    }

    /// <summary>Layer <paramref name="layer"/> (0..3), 65 536 bytes, index row·256 + col. Layer 2 is smoothed when parsed so.</summary>
    public ImmutableArray<byte> Layer(int layer) => ImmutableCollectionsMarshal.AsImmutableArray(_layers[layer]);

    /// <summary>
    /// Parse and reassemble the four layers. Null when the file is shorter than 0x90 + 16, the
    /// chunk counts are not 1..4, the grid is not 256 × 256, or the chunk table is cut off. A
    /// chunk layer past the end of the file (and the ones after it) stays zero.
    /// </summary>
    /// <param name="smoothMaterials">Run DOS's two load-time passes over layer 2 (the Godot port always does).</param>
    public static WldTerrain? Parse(ReadOnlySpan<byte> bytes, bool smoothMaterials = true)
    {
        if (bytes.Length < ChunkTableOffset + 16)
            return null;
        long cxN = BinaryPrimitives.ReadUInt32LittleEndian(bytes[4..]);
        long cyN = BinaryPrimitives.ReadUInt32LittleEndian(bytes[8..]);
        if (cxN <= 0 || cyN <= 0 || cxN > 4 || cyN > 4)
            return null;
        int chunksX = (int)cxN, chunksY = (int)cyN;
        if (chunksX * ChunkCells != GridW || chunksY * ChunkCells != GridH)
            return null;

        const int chunkLayer = ChunkCells * ChunkCells;
        var dataOff = new long[chunksX * chunksY];
        var present = new int[chunksX * chunksY];
        int missing = 0;
        for (int cy = 0; cy < chunksY; cy++)
        {
            for (int cx = 0; cx < chunksX; cx++)
            {
                int ci = cy * chunksX + cx;
                int tableOff = ChunkTableOffset + ci * 4;
                if (tableOff + 4 > bytes.Length)
                    return null;
                long off = BinaryPrimitives.ReadUInt32LittleEndian(bytes[tableOff..]) + (long)ChunkHeaderSize;
                int n = 0;
                for (int l = 0; l < 4; l++)
                {
                    if (off + (long)(l + 1) * chunkLayer > bytes.Length)
                        break;
                    n++;
                }
                missing += 4 - n;
                dataOff[ci] = off;
                present[ci] = n;
            }
        }

        var layers = new byte[4][];
        for (int l = 0; l < 4; l++)
        {
            var layer = new byte[LayerBytes];
            for (int cy = 0; cy < chunksY; cy++)
            {
                for (int row = 0; row < ChunkCells; row++)
                {
                    for (int cx = 0; cx < chunksX; cx++)
                    {
                        int ci = cy * chunksX + cx;
                        if (l >= present[ci])
                            continue;   // stays zero
                        int src = (int)(dataOff[ci] + (long)l * chunkLayer + row * ChunkCells);
                        int dst = (cy * ChunkCells + row) * GridW + cx * ChunkCells;
                        bytes.Slice(src, ChunkCells).CopyTo(layer.AsSpan(dst, ChunkCells));
                    }
                }
            }
            layers[l] = layer;
        }
        if (smoothMaterials)
            SmoothMaterials(layers[2]);
        return new WldTerrain(layers, missing);
    }

    /// <summary>
    /// DOS's load-time passes on the layer-2 buffer (legacy FUN_00153144 then FUN_0015340a), both
    /// in place and in scan order, so a rewritten cell feeds the cells after it.
    /// </summary>
    internal static void SmoothMaterials(Span<byte> l2)
    {
        var remap = MaterialRemap;
        var blend = MaterialBlend;
        var coalesce = CoalesceTile;
        // Pass 1: a cell whose four orthogonal neighbours hold one same raw byte nb becomes
        // BLEND[(nb & 3) * 4 + (centre & 3)] (raw bytes masked, not remapped values).
        for (int row = 1; row < GridH - 1; row++)
        {
            for (int col = 1; col < GridW - 1; col++)
            {
                int idx = row * GridW + col;
                int centre = l2[idx];
                if (remap[centre & 0x3F] == 0xFF)
                    continue;
                int nW = l2[idx - 1], nE = l2[idx + 1], nN = l2[idx - GridW], nS = l2[idx + GridW];
                if (nW == nE && nW == nN && nW == nS)
                    l2[idx] = blend[((nW & 3) << 2) | (centre & 3)];
            }
        }
        // Pass 2: 2 × 2 corner coalesce.
        for (int row = 0; row < GridH - 1; row++)
        {
            int col = 0;
            while (col < GridW - 1)
            {
                int idx = row * GridW + col;
                int tl = l2[idx], tr = l2[idx + 1], bl = l2[idx + GridW], br = l2[idx + GridW + 1];
                int rTL = remap[tl & 0x3F], rTR = remap[tr & 0x3F], rBL = remap[bl & 0x3F], rBR = remap[br & 0x3F];
                if (rTL != 0xFF && rTR != 0xFF && rBL != 0xFF && rBR != 0xFF)
                {
                    int pat = ((rTL & 3) << 4) | ((rBL & 3) << 2) | (rTR & 3) | ((rBR & 3) << 6);
                    int o = coalesce[pat];
                    if (o == 0xFE)
                    {
                        // Copy TR onto BR and redo this cell. Then rBR == rTR, and no such pattern
                        // maps to 0xFE again (checked by a unit test), so this cannot loop.
                        l2[idx + GridW + 1] = (byte)tr;
                        continue;
                    }
                    if (o != 0xFF)
                        l2[idx] = (byte)o;
                }
                col++;
            }
        }
    }

    /// <summary>Layer byte at (col, row); 0 outside the grid or for a bad layer.</summary>
    public int SampleByte(int layer, int col, int row)
    {
        if (layer < 0 || layer > 3 || col < 0 || col >= GridW || row < 0 || row >= GridH)
            return 0;
        return _layers[layer][row * GridW + col];
    }

    /// <summary>Corner height at (col, row) in world units (Y up).</summary>
    public double CornerHeight(int col, int row) => HeightCurve[SampleByte(0, col, row) & 0x7F];

    /// <summary>
    /// Heightmap (col, row) under world (x, DOS z): truncate toward zero, then mask with 0xFF, as
    /// DOS's <c>&gt;&gt; 8 &amp; 0xFF</c> wraps positions outside the field.
    /// </summary>
    public static (int Col, int Row) CellForWorld(double wx, double wz)
    {
        int col = (int)(wx / WorldPerCell) & 0xFF;
        int row = (int)((ZFlipK - wz) / WorldPerCell) & 0xFF;
        return (col, row);
    }

    /// <summary>
    /// The drawn ground height at world (x, DOS z): two planes per cell split NW–SE; local
    /// u east, v south in [0, 1); u ≥ v takes the NW-NE-SE plane. Corners past the last row or
    /// column clamp to it.
    /// </summary>
    public double HeightAt(double wx, double wz)
    {
        var (c, r) = CellForWorld(wx, wz);
        double fx = wx / WorldPerCell;
        double fz = (ZFlipK - wz) / WorldPerCell;
        double u = fx - Math.Floor(fx);
        double v = fz - Math.Floor(fz);
        int c1 = Math.Min(c + 1, GridW - 1);
        int r1 = Math.Min(r + 1, GridH - 1);
        double hNW = CornerHeight(c, r);
        double hNE = CornerHeight(c1, r);
        double hSW = CornerHeight(c, r1);
        double hSE = CornerHeight(c1, r1);
        if (u >= v)
            return hNW + u * (hNE - hNW) + v * (hSE - hNE);
        return hNW + v * (hSW - hNW) + u * (hSE - hSW);
    }

    /// <summary>True when the tile under world (x, DOS z) is a water tile (58, 59, 61).</summary>
    public bool IsWaterAt(double wx, double wz)
    {
        var (c, r) = CellForWorld(wx, wz);
        return WaterMaterials.Contains((byte)(SampleByte(2, c, r) & 0x3F));
    }

    /// <summary>The 256 × 256 corner heights in world units, row-major (row 0 = north).</summary>
    public float[] HeightGrid()
    {
        var curve = HeightCurve;
        var l0 = _layers[0];
        var g = new float[LayerBytes];
        for (int i = 0; i < g.Length; i++)
            g[i] = curve[l0[i] & 0x7F];
        return g;
    }
}
