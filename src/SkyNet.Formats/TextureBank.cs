using System.Buffers.Binary;
using System.Collections.Immutable;
using System.Runtime.InteropServices;
using System.Text;

namespace SkyNet.Formats;

/// <summary>
/// One decoded texture: <see cref="Width"/> × <see cref="Height"/> palette indices, row-major.
/// A record that failed to decode is kept as an empty placeholder (0 × 0) so later records keep
/// their index.
/// </summary>
public sealed class TextureRecord
{
    public static readonly TextureRecord Empty = new(0, 0, [], 0, 0);

    public int Width { get; }
    public int Height { get; }
    /// <summary>Width × Height palette indices. Index 0 is transparent for sprites.</summary>
    public ImmutableArray<byte> Pixels { get; }
    /// <summary>Descriptor +20: frame count; &gt; 1 is the only "animated" flag. 0 for flat colours / placeholders.</summary>
    public int Depth { get; }
    /// <summary>Descriptor +22: milliseconds per frame (frame = clock / ms % depth).</summary>
    public int MsPerFrame { get; }

    public bool IsEmpty => Pixels.IsEmpty;

    /// <summary>Outer record byte +1: the palette index of a flat colour, and for a texture the one
    /// colour DOS's flat face mode fills it with (SKYNET.EXE v1.01 FUN_00150000; 0 in many records).</summary>
    public byte Colour { get; init; }

    internal TextureRecord(int width, int height, byte[] pixels, int depth, int msPerFrame)
    {
        Width = width;
        Height = height;
        Pixels = ImmutableCollectionsMarshal.AsImmutableArray(pixels);
        Depth = depth;
        MsPerFrame = msPerFrame;
    }
}

/// <summary>
/// A TEXTURE.NNN bank: u16 record count, char[16] name, then a 20-byte outer record per texture
/// whose u32 at +2 (file offset 28 + 20·r) is the offset of a 28-byte descriptor. Two pixel
/// layouts, chosen by the descriptor's row gap: wall rows at stride <c>width + row_gap</c>
/// (256 — four 64-wide records interleave in one buffer), or a sprite frame table with
/// transparency-run rows. Flat-colour banks (TEXTURE.000/001) have descriptor offset 0 and keep
/// the palette index in the record's byte +1 (file offset ro − 1).
/// Ported from the Godot port's texture_nnn.gd, quirks included.
/// </summary>
public sealed class TextureBank
{
    /// <summary>TEXTURE.000/001 hold 128 records each, so the cap must be at least that.</summary>
    public const int MaxRecords = 256;
    public const int MaxFilePixels = 16 * 1024 * 1024;
    public const int MaxSpriteFrames = 256;
    public const int MaxFramesPixels = 16 * 1024 * 1024;
    const int MinFileSize = 28 + 20 + 28;

    public string Name { get; }
    public ImmutableArray<TextureRecord> Records { get; }

    TextureBank(string name, ImmutableArray<TextureRecord> records)
    {
        Name = name;
        Records = records;
    }

    static int U16(ReadOnlySpan<byte> b, int off) => BinaryPrimitives.ReadUInt16LittleEndian(b[off..]);
    // Offsets are compared against int sizes: a u32 above int.MaxValue can never be in range,
    // so it is clamped to a value every bounds check rejects.
    static long U32(ReadOnlySpan<byte> b, int off) => BinaryPrimitives.ReadUInt32LittleEndian(b[off..]);

    /// <summary>Parse a bank; null when the file is too small or the count is 0 or above 256.</summary>
    public static TextureBank? Parse(ReadOnlySpan<byte> bytes)
    {
        if (bytes.Length < MinFileSize)
            return null;
        int tag = U16(bytes, 0);
        if (tag == 0 || tag > MaxRecords)
            return null;
        string name = Encoding.ASCII.GetString(TrimAtNul(bytes.Slice(2, 16))).Trim();
        var records = ImmutableArray.CreateBuilder<TextureRecord>(tag);
        long pixels = 0;
        for (int r = 0; r < tag; r++)
        {
            // The descriptor-offset field sits at +2 of the record, so the record ends at ro + 18.
            // (ro + 20 would drop the last record of TEXTURE.000/001, which ends at the file end.)
            int ro = 28 + r * 20;
            if (ro + 18 > bytes.Length)
                break;
            if (pixels > MaxFilePixels)
            {
                records.Add(TextureRecord.Empty);
                continue;
            }
            var rec = ParseRecord(bytes, ro);
            if (rec != null)
                pixels += rec.Pixels.Length;
            records.Add(rec ?? TextureRecord.Empty);
        }
        return new TextureBank(name, records.ToImmutable());
    }

    // Godot's get_string_from_ascii stops at the first NUL.
    static ReadOnlySpan<byte> TrimAtNul(ReadOnlySpan<byte> s)
    {
        int n = s.IndexOf((byte)0);
        return n >= 0 ? s[..n] : s;
    }

    static TextureRecord? ParseRecord(ReadOnlySpan<byte> bytes, int ro)
    {
        long descOff = U32(bytes, ro);
        if (descOff == 0)
        {
            // Flat colour: the palette index is the record's byte +1, i.e. ro - 1.
            return new TextureRecord(1, 1, [bytes[ro - 1]], 0, 0) { Colour = bytes[ro - 1] };
        }
        if (descOff + 28 > bytes.Length)
            return null;
        int d = (int)descOff;
        int w = U16(bytes, d + 4);
        int h = U16(bytes, d + 6);
        long pixRel = U32(bytes, d + 14);
        int rowGap = U16(bytes, d + 18);
        int depth = U16(bytes, d + 20);
        int ms = U16(bytes, d + 22);
        if (w == 0 || h == 0 || w > 1024 || h > 1024)
            return null;
        long pixOff = descOff + pixRel;
        int size = bytes.Length;

        // A 256-wide wall has row gap 0, like a sprite: it is a wall when the raw block fits and
        // the sprite frame table does not make sense (TEXTURE.266 rec 1, 256×128).
        if (rowGap == 0 && w == 256 && pixOff + (long)w * h <= size)
        {
            long f0 = pixOff + 4 <= size ? U32(bytes, (int)pixOff) : long.MaxValue;
            if (depth < 1 || f0 < depth * 4L || pixOff + f0 + 4 > size || U16(bytes, (int)(pixOff + f0)) != w)
                rowGap = -1;   // layout A at stride W
        }

        byte[] px;
        if (rowGap != 0)
        {
            // Layout A: wall / terrain rows at stride W + row_gap.
            long stride = w + Math.Max(rowGap, 0);
            long span = (h - 1) * stride + w;
            if (pixOff + span > size)
            {
                stride = w;
                span = (long)w * h;
                if (pixOff + span > size)
                    return null;
            }
            px = new byte[w * h];
            for (int y = 0; y < h; y++)
                bytes.Slice((int)(pixOff + y * stride), w).CopyTo(px.AsSpan(y * w, w));
        }
        else
        {
            // Layout B: sprite. Frame offset table, frame 0 only.
            if (depth < 1 || pixOff + depth * 4L + 4 > size)
                return null;
            int p = (int)pixOff;
            long f0Off = U32(bytes, p);
            long f1Off = depth >= 2 ? U32(bytes, p + 4) : size - pixOff;
            if (f0Off < depth * 4L || f1Off <= f0Off)
                return null;
            if (pixOff + f1Off > size)
                return null;
            var frame = DecodeFrame(bytes, pixOff + f0Off, pixOff + f1Off, out w, out h);
            if (frame == null)
                return null;
            px = frame;
        }
        return new TextureRecord(w, h, px, depth, ms) { Colour = bytes[ro - 1] };
    }

    /// <summary>
    /// Decode a sprite frame occupying [start, end): u16 W, u16 H, then either uncompressed rows
    /// (size = H·(W+2), 2-byte prefix + W pixels) or transparency-run rows.
    /// </summary>
    static byte[]? DecodeFrame(ReadOnlySpan<byte> bytes, long start, long end, out int w, out int h)
    {
        w = h = 0;
        if (start + 4 > bytes.Length)
            return null;
        int fs = (int)start;
        int fw = U16(bytes, fs);
        int fh = U16(bytes, fs + 2);
        if (fw == 0 || fh == 0 || fw > 1024 || fh > 1024)
            return null;
        long dataOff = start + 4;
        long dataSize = end - dataOff;
        if (dataOff + dataSize > bytes.Length || dataSize <= 0)
            return null;
        byte[]? px;
        if (dataSize == (long)fh * (fw + 2))
        {
            px = new byte[fw * fh];
            for (int y = 0; y < fh; y++)
                bytes.Slice((int)dataOff + y * (fw + 2) + 2, fw).CopyTo(px.AsSpan(y * fw, fw));
        }
        else
        {
            px = DecodeSparseRows(bytes.Slice((int)dataOff, (int)dataSize), fw, fh);
            if (px == null)
                return null;
        }
        w = fw;
        h = fh;
        return px;
    }

    /// <summary>
    /// Transparency-run rows: per row, (u8 transparent_run, u8 opaque_run, opaque_run literal
    /// bytes) until W columns are covered. Literals past W are dropped; gaps stay index 0.
    /// Null on a source under-run.
    /// </summary>
    internal static byte[]? DecodeSparseRows(ReadOnlySpan<byte> src, int w, int h)
    {
        var dst = new byte[w * h];
        int s = 0;
        for (int y = 0; y < h; y++)
        {
            int x = 0;
            int row = y * w;
            while (x < w)
            {
                if (s + 2 > src.Length)
                    return null;
                int trans = src[s];
                int opaque = src[s + 1];
                s += 2;
                x += trans;
                if (s + opaque > src.Length)
                    return null;
                if (opaque > 0 && x < w)
                {
                    int n = Math.Min(opaque, w - x);
                    src.Slice(s, n).CopyTo(dst.AsSpan(row + x, n));
                }
                s += opaque;
                x += opaque;
            }
        }
        return dst;
    }

    /// <summary>
    /// Every frame of a sprite-layout record (explosion banks TEXTURE.358 / 367 keep a whole cel
    /// animation in record 0). A wall-layout record gives its single image. Empty on failure;
    /// frames that fail to decode are skipped.
    /// </summary>
    /// <remarks>
    /// Faithful to the Godot port: this path needs the full 20-byte outer record (ro + 20), and
    /// it does not apply the 256-wide-wall test, so such a wall reads as a broken sprite here.
    /// </remarks>
    public static ImmutableArray<TextureRecord> ParseRecordFrames(ReadOnlySpan<byte> bytes, int recordId)
    {
        if (bytes.Length < MinFileSize)
            return [];
        int tag = U16(bytes, 0);
        if (recordId < 0 || recordId >= tag)
            return [];
        int ro = 28 + recordId * 20;
        if (ro + 20 > bytes.Length)
            return [];
        long descOff = U32(bytes, ro);
        if (descOff == 0 || descOff + 28 > bytes.Length)
            return [];
        int d = (int)descOff;
        long pixOff = descOff + U32(bytes, d + 14);
        int rowGap = U16(bytes, d + 18);
        int depth = U16(bytes, d + 20);
        int ms = U16(bytes, d + 22);
        if (rowGap != 0)
        {
            var single = ParseRecord(bytes, ro);
            return single != null ? [single] : [];
        }
        if (depth < 1 || pixOff + depth * 4L > bytes.Length)
            return [];
        var frames = ImmutableArray.CreateBuilder<TextureRecord>();
        long attempted = 0;
        int p = (int)pixOff;
        for (int k = 0; k < Math.Min(depth, MaxSpriteFrames); k++)
        {
            long fOff = U32(bytes, p + k * 4);
            long fNext = k + 1 < depth ? U32(bytes, p + (k + 1) * 4) : bytes.Length - pixOff;
            if (fOff < depth * 4L || fNext <= fOff)
                continue;
            if (pixOff + fNext > bytes.Length)
                continue;
            long start = pixOff + fOff;
            if (start + 4 > bytes.Length)
                continue;
            int fw = U16(bytes, (int)start);
            int fh = U16(bytes, (int)start + 2);
            if (fw == 0 || fh == 0 || fw > 1024 || fh > 1024)
                continue;
            long dataSize = pixOff + fNext - (start + 4);
            if (dataSize <= 0)
                continue;
            attempted += (long)fw * fh;
            if (attempted > MaxFramesPixels)
                break;
            var px = DecodeFrame(bytes, start, pixOff + fNext, out int w, out int h);
            if (px != null)
                frames.Add(new TextureRecord(w, h, px, depth, ms));
        }
        return frames.ToImmutable();
    }
}
