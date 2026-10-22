using System.Buffers.Binary;
using System.Collections.Immutable;
using System.Runtime.InteropServices;

namespace SkyNet.Formats;

public enum CfaLayout
{
    /// <summary>320 × 200 art: u16 width/height, u8 frame count at +11, u16 offsets, byte-stream RLE frames.</summary>
    Low,
    /// <summary>640 × 480 art (MDMDHRES.BSA WEAPON*.CFA): u32 header of 44 bytes, u32 offsets, row-based frames.</summary>
    Hires,
}

/// <summary>
/// A .CFA cel animation (weapon viewmodels, RADCOUNT, the network HUD): frames of
/// <see cref="Width"/> × <see cref="Height"/> palette indices, index 0 transparent. Two layouts,
/// told apart only by the 640 × 480 one's first frame offset being exactly 44 + 4·count (both
/// occur in MDMDHRES.BSA). Ported from the Godot port's cfa_file.gd, guards included: a pixel the
/// stream does not reach stays 0.
/// </summary>
public sealed class CfaFile
{
    public const int HiresHeader = 44;
    /// <summary>Neither encoding expands a byte to more than ~128 pixels.</summary>
    public const int MaxPixelsPerByte = 128;
    public const int MaxTotalPixels = 16 * 1024 * 1024;

    public CfaLayout Layout { get; }
    public int Width { get; }
    public int Height { get; }
    /// <summary>Hi-res layout +0/+4 (WEAPON00: 37/142, its right/bottom edge on a 640 × 480 screen); 0 for the low layout.</summary>
    public int X { get; }
    public int Y { get; }
    /// <summary>Per frame, Width × Height palette indices.</summary>
    public ImmutableArray<ImmutableArray<byte>> Frames { get; }

    CfaFile(CfaLayout layout, int w, int h, int x, int y, ImmutableArray<ImmutableArray<byte>> frames)
    {
        Layout = layout; Width = w; Height = h; X = x; Y = y; Frames = frames;
    }

    static bool TooBig(long w, long h, long frames, long size)
    {
        long total = w * h * frames;
        return total > MaxTotalPixels || total > size * MaxPixelsPerByte;
    }

    /// <summary>Frame count when <paramref name="b"/> is the 640 × 480 layout, else 0.</summary>
    public static int HiresFrameCount(ReadOnlySpan<byte> b)
    {
        if (b.Length < HiresHeader + 8)
            return 0;
        uint w = U32(b, 8), h = U32(b, 12), n = U32(b, 40);
        if (w == 0 || h == 0 || w > 4096 || h > 4096 || n == 0 || n > 64)
            return 0;
        if (HiresHeader + n * 4 > b.Length)
            return 0;
        return U32(b, HiresHeader) == HiresHeader + n * 4 ? (int)n : 0;
    }

    public static bool IsHires(ReadOnlySpan<byte> b) => HiresFrameCount(b) > 0;

    static uint U32(ReadOnlySpan<byte> b, int o) => BinaryPrimitives.ReadUInt32LittleEndian(b[o..]);
    static int U16(ReadOnlySpan<byte> b, int o) => BinaryPrimitives.ReadUInt16LittleEndian(b[o..]);

    /// <summary>Parse either layout; null on a malformed or implausibly large file.</summary>
    public static CfaFile? Parse(ReadOnlySpan<byte> b)
    {
        if (IsHires(b))
            return ParseHires(b);
        if (b.Length < 14)
            return null;
        int w = U16(b, 0), h = U16(b, 2), count = b[11];
        if (w <= 0 || h <= 0 || w > 2048 || h > 2048 || count <= 0)
            return null;
        if (12 + count * 2 > b.Length || TooBig(w, h, count, b.Length))
            return null;
        int npx = w * h, size = b.Length;
        var frames = ImmutableArray.CreateBuilder<ImmutableArray<byte>>(count);
        for (int f = 0; f < count; f++)
        {
            int src = U16(b, 12 + f * 2);
            var px = new byte[npx];
            int dst = 0;
            while (dst < npx && src < size)
            {
                int c = b[src++];
                if (c < 0x80)
                {
                    for (int k = 0; k <= c && dst < npx && src < size; k++)
                        px[dst++] = b[src++];
                }
                else
                {
                    if (src >= size)
                        break;
                    byte v = b[src++];
                    int n = Math.Min(c - 0x7F, npx - dst);
                    px.AsSpan(dst, n).Fill(v);
                    dst += n;
                }
            }
            frames.Add(ImmutableCollectionsMarshal.AsImmutableArray(px));
        }
        return new CfaFile(CfaLayout.Low, w, h, 0, 0, frames.MoveToImmutable());
    }

    static CfaFile? ParseHires(ReadOnlySpan<byte> b)
    {
        int w = (int)U32(b, 8), h = (int)U32(b, 12), count = (int)U32(b, 40), size = b.Length;
        if (TooBig(w, h, count, size))
            return null;
        var frames = ImmutableArray.CreateBuilder<ImmutableArray<byte>>(count);
        for (int f = 0; f < count; f++)
        {
            long src = U32(b, HiresHeader + f * 4);
            long stop = f + 1 < count ? Math.Min(U32(b, HiresHeader + (f + 1) * 4), size) : size;
            var px = new byte[w * h];
            for (int row = 0; row < h; row++)
            {
                int x = 0, rowBase = row * w;
                while (x < w && src + 1 < stop)
                {
                    int skip = b[(int)src], n = b[(int)src + 1];
                    src += 2;
                    x += skip;
                    for (int k = 0; k < n && x < w && src < stop; k++)
                        px[rowBase + x++] = b[(int)src++];
                }
            }
            frames.Add(ImmutableCollectionsMarshal.AsImmutableArray(px));
        }
        return new CfaFile(CfaLayout.Hires, w, h, (int)U32(b, 0), (int)U32(b, 4), frames.MoveToImmutable());
    }
}
