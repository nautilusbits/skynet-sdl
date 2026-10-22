using System.Buffers.Binary;
using System.Collections.Immutable;
using System.Runtime.InteropServices;

namespace SkyNet.Formats;

/// <summary>
/// An .IMG still image (menus, briefing portraits, HUD panels): a 12-byte header of u16
/// x, y, width, height, compression, record size, then width × height palette indices, row-major.
/// Only compression 0 (raw) is decoded, which covers every image either game uses; the Godot
/// port's img_file.gd rejects the rest the same way. Index 0 is see-through only where the
/// caller says so (the cockpit panels' windscreen).
/// </summary>
public sealed class ImgFile
{
    public const int HeaderSize = 12;

    /// <summary>Header +0: screen placement x (not used by the Godot port).</summary>
    public int X { get; }
    /// <summary>Header +2: screen placement y.</summary>
    public int Y { get; }
    public int Width { get; }
    public int Height { get; }
    /// <summary>Header +8; always 0 for a parsed file.</summary>
    public int Compression { get; }
    /// <summary>Header +10: record size, meaning unknown.</summary>
    public int RecordSize { get; }
    /// <summary>Width × Height palette indices.</summary>
    public ImmutableArray<byte> Pixels { get; }

    ImgFile(int x, int y, int w, int h, int comp, int rec, byte[] px)
    {
        X = x; Y = y; Width = w; Height = h; Compression = comp; RecordSize = rec;
        Pixels = ImmutableCollectionsMarshal.AsImmutableArray(px);
    }

    /// <summary>Header fields without the pixels; null when shorter than the header.</summary>
    public static (int X, int Y, int Width, int Height, int Compression, int RecordSize)? ReadHeader(ReadOnlySpan<byte> b)
    {
        if (b.Length < HeaderSize)
            return null;
        static int U(ReadOnlySpan<byte> s, int o) => BinaryPrimitives.ReadUInt16LittleEndian(s[o..]);
        return (U(b, 0), U(b, 2), U(b, 4), U(b, 6), U(b, 8), U(b, 10));
    }

    /// <summary>Parse an .IMG; null when the header is short, a side is 0, it is compressed, or the pixels are cut short.</summary>
    public static ImgFile? Parse(ReadOnlySpan<byte> bytes)
    {
        if (ReadHeader(bytes) is not { } h || h.Width <= 0 || h.Height <= 0 || h.Compression != 0)
            return null;
        int n = h.Width * h.Height;
        if (bytes.Length < HeaderSize + n)
            return null;
        return new ImgFile(h.X, h.Y, h.Width, h.Height, h.Compression, h.RecordSize, bytes.Slice(HeaderSize, n).ToArray());
    }

    public byte this[int x, int y] => Pixels[y * Width + x];
}
