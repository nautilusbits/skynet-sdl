using System.Buffers.Binary;

namespace SkyNet.Formats;

/// <summary>A WLD byte layer (docs/formats/wld.md, "Layers").</summary>
public enum WldLayer
{
    /// <summary>Bits 0-6: height curve index; bit 7: the "non-planar" flag.</summary>
    Height,
    /// <summary>Bits 2-7: decoration spawn type (not ported).</summary>
    Decoration,
    /// <summary>Bits 0-5: TEXTURE.302 tile; bit 6: turn 90°, bit 7: turn 180°.</summary>
    Tile,
    /// <summary>Unknown; zero in every shipped file.</summary>
    Unknown,
}

/// <summary>
/// WLD.NNN as a container, for editing: the four raw byte layers (layer 2 as stored, before the
/// load-time smoothing <see cref="WldTerrain.Parse"/> runs) and the file they came from.
/// <see cref="Write"/> patches the layers back into that file, so every byte the format does not
/// explain (header, the block at 0xA0, chunk headers, trailing bytes) survives: an unedited file
/// writes back byte for byte. The size is a property: <see cref="Width"/> × <see cref="Height"/>
/// corners, any multiple of <see cref="ChunkCells"/> (DOS and <see cref="WldTerrain"/> take 256 × 256).
/// <code>
///   0x00 header (cx at +4, cy at +8)   0x90 u32 chunk offsets, row-major
///   chunk: 22-byte header | layer 0 | layer 1 | layer 2 | layer 3   (128 × 128 bytes each)
/// </code>
/// </summary>
public sealed class WldFile
{
    public const int ChunkCells = WldTerrain.ChunkCells;
    public const int LayerCount = 4;
    private const int ChunkLayerBytes = ChunkCells * ChunkCells;
    private const int ChunkBytes = WldTerrain.ChunkHeaderSize + LayerCount * ChunkLayerBytes;
    private const int ChunksXOffset = 4;
    private const int ChunksYOffset = 8;

    // A new file's constants, as every shipped file without a trailer has them (WLD.601).
    private const int FirstChunkOffset = 0x4A0;
    private const int MarkOffset = 0xA0;
    private const uint Magic = 0x10;
    private const uint MarkValue = 0x7F501E19;
    private static ReadOnlySpan<ushort> ChunkTags => [0x868, 0x238, 0x51C, 0x00A];

    private readonly byte[] _source;
    private readonly int[] _chunkOffsets;
    private readonly byte[][] _layers;

    private WldFile(byte[] source, int chunksX, int chunksY, int[] chunkOffsets, byte[][] layers)
    {
        _source = source;
        ChunksX = chunksX;
        ChunksY = chunksY;
        _chunkOffsets = chunkOffsets;
        _layers = layers;
    }

    public int ChunksX { get; }
    public int ChunksY { get; }

    /// <summary>Corners per row (columns).</summary>
    public int Width => ChunksX * ChunkCells;

    /// <summary>Rows of corners; row 0 is the north edge.</summary>
    public int Height => ChunksY * ChunkCells;

    /// <summary>
    /// Reads a WLD; null when it is shorter than its chunk table, a chunk count is 0 or above 64, or a
    /// chunk lies past the end of the file.
    /// </summary>
    public static WldFile? Read(ReadOnlySpan<byte> bytes)
    {
        const int maxChunks = 64;
        if (bytes.Length < WldTerrain.ChunkTableOffset)
        {
            return null;
        }

        var cx = BinaryPrimitives.ReadUInt32LittleEndian(bytes[ChunksXOffset..]);
        var cy = BinaryPrimitives.ReadUInt32LittleEndian(bytes[ChunksYOffset..]);
        if (cx is 0 or > maxChunks || cy is 0 or > maxChunks)
        {
            return null;
        }

        var (chunksX, chunksY) = ((int)cx, (int)cy);
        var offsets = new int[chunksX * chunksY];
        for (var i = 0; i < offsets.Length; i++)
        {
            var at = WldTerrain.ChunkTableOffset + i * 4;
            if (at + 4 > bytes.Length)
            {
                return null;
            }

            var off = BinaryPrimitives.ReadUInt32LittleEndian(bytes[at..]);
            if (off + (long)ChunkBytes > bytes.Length)
            {
                return null;
            }

            offsets[i] = (int)off;
        }

        var file = new WldFile(bytes.ToArray(), chunksX, chunksY, offsets, NewLayers(chunksX, chunksY));
        file.Unpack();
        return file;
    }

    /// <summary>A new, flat file of <paramref name="width"/> × <paramref name="height"/> corners
    /// (multiples of <see cref="ChunkCells"/>), laid out as the shipped files are.</summary>
    public static WldFile Create(int width = WldTerrain.GridW, int height = WldTerrain.GridH)
    {
        if (width <= 0 || height <= 0 || width % ChunkCells != 0 || height % ChunkCells != 0)
        {
            throw new ArgumentException($"{width} x {height}: not a multiple of {ChunkCells}");
        }

        var (chunksX, chunksY) = (width / ChunkCells, height / ChunkCells);
        var count = chunksX * chunksY;

        // Header (+0x10 points at the mark), chunk table, the mark at 0xA0 (after the table when the table
        // reaches it), chunks.
        var tableEnd = WldTerrain.ChunkTableOffset + count * 4;
        var mark = Math.Max(MarkOffset, tableEnd);
        var first = Math.Max(FirstChunkOffset, mark + 4);
        var bytes = new byte[first + count * ChunkBytes];
        BinaryPrimitives.WriteUInt32LittleEndian(bytes, Magic);
        BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(ChunksXOffset), (uint)chunksX);
        BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(ChunksYOffset), (uint)chunksY);
        BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(0x10), (uint)mark);
        BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(0x14), 1);
        BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(0x18), WldTerrain.ChunkHeaderSize);
        BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(mark), MarkValue);

        var offsets = new int[count];
        for (var i = 0; i < count; i++)
        {
            offsets[i] = first + i * ChunkBytes;
            BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(WldTerrain.ChunkTableOffset + i * 4), (uint)offsets[i]);

            // Chunk header: a tag, 0x12E at +6, 1 at +8 (meaning unknown; copied from WLD.601).
            BinaryPrimitives.WriteUInt16LittleEndian(bytes.AsSpan(offsets[i]), ChunkTags[i % ChunkTags.Length]);
            BinaryPrimitives.WriteUInt16LittleEndian(bytes.AsSpan(offsets[i] + 6), 0x12E);
            bytes[offsets[i] + 8] = 1;
        }

        return new WldFile(bytes, chunksX, chunksY, offsets, NewLayers(chunksX, chunksY));
    }

    /// <summary>A copy that shares nothing with this one.</summary>
    public WldFile Clone() =>
        new(_source, ChunksX, ChunksY, _chunkOffsets, [.. _layers.Select(l => (byte[])l.Clone())]);

    /// <summary>The byte of <paramref name="layer"/> at corner / cell (col, row); 0 outside.</summary>
    public byte Get(WldLayer layer, int col, int row) =>
        Inside(col, row) ? _layers[(int)layer][row * Width + col] : (byte)0;

    /// <summary>Sets the byte of <paramref name="layer"/> at (col, row); ignored outside.</summary>
    public void Set(WldLayer layer, int col, int row, byte value)
    {
        if (Inside(col, row))
        {
            _layers[(int)layer][row * Width + col] = value;
        }
    }

    /// <summary>A whole layer, row-major (row · <see cref="Width"/> + col), live: writes go into the file.</summary>
    public Span<byte> Layer(WldLayer layer) => _layers[(int)layer];

    /// <summary>The file: the source bytes with the layers written into their chunks.</summary>
    public byte[] Write()
    {
        var bytes = (byte[])_source.Clone();
        ForEachChunkRow((layer, file, grid) => _layers[layer].AsSpan(grid, ChunkCells).CopyTo(bytes.AsSpan(file, ChunkCells)));
        return bytes;
    }

    private bool Inside(int col, int row) => col >= 0 && col < Width && row >= 0 && row < Height;

    private static byte[][] NewLayers(int chunksX, int chunksY)
    {
        var size = chunksX * chunksY * ChunkLayerBytes;
        return [new byte[size], new byte[size], new byte[size], new byte[size]];
    }

    private void Unpack()
    {
        var source = _source;
        ForEachChunkRow((layer, file, grid) => source.AsSpan(file, ChunkCells).CopyTo(_layers[layer].AsSpan(grid, ChunkCells)));
    }

    // Every 128-byte row of every chunk layer: (layer, offset in the file, offset in the layer grid).
    private void ForEachChunkRow(Action<int, int, int> row)
    {
        for (var layer = 0; layer < LayerCount; layer++)
        {
            for (var cy = 0; cy < ChunksY; cy++)
            {
                for (var cx = 0; cx < ChunksX; cx++)
                {
                    var data = _chunkOffsets[cy * ChunksX + cx] + WldTerrain.ChunkHeaderSize + layer * ChunkLayerBytes;
                    for (var r = 0; r < ChunkCells; r++)
                    {
                        row(layer, data + r * ChunkCells, (cy * ChunkCells + r) * Width + cx * ChunkCells);
                    }
                }
            }
        }
    }
}
