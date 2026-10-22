using System.Buffers.Binary;
using System.IO.Compression;

namespace SkyNet.Edit;

/// <summary>Bits per sample of a greyscale heightmap PNG.</summary>
public enum PngDepth
{
    Bits8 = 8,
    Bits16 = 16,
}

/// <summary>A decoded PNG as one grey sample per pixel, 0..65535 (8-bit samples × 257), rows top-down.</summary>
public sealed record GreyImage(int Width, int Height, ushort[] Samples, PngDepth Depth);

/// <summary>
/// Greyscale PNGs for heightmaps, without SDL (whose decoder keeps 8 bits): writes grey 8 or 16-bit;
/// reads non-interlaced grey, grey + alpha, RGB, RGBA (8 or 16-bit, the red or grey sample) and 8-bit
/// palette images (PNG specification, ISO/IEC 15948).
/// </summary>
public static class TerrainPng
{
    private const int ColourGrey = 0;
    private const int ColourRgb = 2;
    private const int ColourPalette = 3;
    private const int ColourGreyAlpha = 4;
    private const int ColourRgba = 6;
    private const int FilterPaeth = 4;
    private const int Max8 = byte.MaxValue;
    private const int Scale8To16 = 257;

    private static ReadOnlySpan<byte> Signature => [0x89, (byte)'P', (byte)'N', (byte)'G', 0x0D, 0x0A, 0x1A, 0x0A];

    /// <summary>A grey PNG of <paramref name="samples"/> (0..65535, reduced to 8 bits for <see cref="PngDepth.Bits8"/>).</summary>
    public static byte[] Encode(int width, int height, ReadOnlySpan<ushort> samples, PngDepth depth)
    {
        if (width <= 0 || height <= 0 || samples.Length < width * height)
        {
            throw new ArgumentException($"{width} x {height}: {samples.Length} samples");
        }

        var bytesPer = depth == PngDepth.Bits16 ? 2 : 1;
        var stride = width * bytesPer;
        var pixels = new byte[stride * height];
        for (var i = 0; i < width * height; i++)
        {
            if (depth == PngDepth.Bits16)
            {
                BinaryPrimitives.WriteUInt16BigEndian(pixels.AsSpan(i * 2), samples[i]);
            }
            else
            {
                pixels[i] = (byte)((samples[i] + Scale8To16 / 2) / Scale8To16);
            }
        }

        using var png = new MemoryStream();
        png.Write(Signature);
        var ihdr = new byte[13];
        BinaryPrimitives.WriteInt32BigEndian(ihdr, width);
        BinaryPrimitives.WriteInt32BigEndian(ihdr.AsSpan(4), height);
        ihdr[8] = (byte)depth;
        ihdr[9] = ColourGrey;
        Chunk(png, "IHDR", ihdr);

        // Rows Paeth-filtered (bytes, the left neighbour a whole sample back).
        using (var raw = new MemoryStream())
        {
            using (var z = new ZLibStream(raw, CompressionLevel.Optimal, leaveOpen: true))
            {
                var row = new byte[stride + 1];
                row[0] = FilterPaeth;
                for (var y = 0; y < height; y++)
                {
                    var line = pixels.AsSpan(y * stride, stride);
                    for (var i = 0; i < stride; i++)
                    {
                        int a = i >= bytesPer ? line[i - bytesPer] : 0;
                        int b = y > 0 ? pixels[(y - 1) * stride + i] : 0;
                        int c = y > 0 && i >= bytesPer ? pixels[(y - 1) * stride + i - bytesPer] : 0;
                        row[i + 1] = (byte)(line[i] - Paeth(a, b, c));
                    }

                    z.Write(row);
                }
            }

            Chunk(png, "IDAT", raw.ToArray());
        }

        Chunk(png, "IEND", []);
        return png.ToArray();
    }

    /// <summary>Reads a PNG's grey (or red) samples; <see cref="InvalidDataException"/> for what it does not read.</summary>
    public static GreyImage Decode(ReadOnlySpan<byte> png)
    {
        if (png.Length < Signature.Length || !png[..Signature.Length].SequenceEqual(Signature))
        {
            throw new InvalidDataException("not a PNG");
        }

        int width = 0, height = 0, depth = 0, colour = -1;
        byte[] palette = [];
        using var idat = new MemoryStream();
        var at = Signature.Length;
        while (at + 8 <= png.Length)
        {
            var length = BinaryPrimitives.ReadInt32BigEndian(png[at..]);
            var type = System.Text.Encoding.ASCII.GetString(png.Slice(at + 4, 4));
            if (length < 0 || at + 12 + (long)length > png.Length)
            {
                throw new InvalidDataException($"{type} chunk runs past the end");
            }

            var body = png.Slice(at + 8, length);
            at += 12 + length;
            switch (type)
            {
                case "IHDR":
                    (width, height) = (BinaryPrimitives.ReadInt32BigEndian(body), BinaryPrimitives.ReadInt32BigEndian(body[4..]));
                    (depth, colour) = (body[8], body[9]);
                    if (body[12] != 0)
                    {
                        throw new InvalidDataException("interlaced PNGs are not read");
                    }

                    break;
                case "PLTE":
                    palette = body.ToArray();
                    break;
                case "IDAT":
                    idat.Write(body);
                    break;
            }

            if (type == "IEND")
            {
                break;
            }
        }

        var channels = colour switch
        {
            ColourGrey or ColourPalette => 1,
            ColourGreyAlpha => 2,
            ColourRgb => 3,
            ColourRgba => 4,
            _ => throw new InvalidDataException($"colour type {colour} is not read"),
        };
        if (depth is not (8 or 16) || (colour == ColourPalette && depth != 8) || width <= 0 || height <= 0)
        {
            throw new InvalidDataException($"{width} x {height}, {depth}-bit colour type {colour} is not read");
        }

        var bytesPer = channels * depth / 8;
        var stride = width * bytesPer;
        var pixels = Unfilter(Inflate(idat.ToArray(), (stride + 1) * height), stride, height, bytesPer);
        var samples = new ushort[width * height];
        for (var i = 0; i < samples.Length; i++)
        {
            var p = i * bytesPer;
            samples[i] = colour == ColourPalette ? (ushort)(PaletteRed(palette, pixels[p]) * Scale8To16)
                : depth == 16 ? BinaryPrimitives.ReadUInt16BigEndian(pixels.AsSpan(p)) : (ushort)(pixels[p] * Scale8To16);
        }

        return new GreyImage(width, height, samples, depth == 16 ? PngDepth.Bits16 : PngDepth.Bits8);
    }

    private static int PaletteRed(byte[] palette, int index) => index * 3 < palette.Length ? palette[index * 3] : Max8;

    private static byte[] Inflate(byte[] data, int size)
    {
        var raw = new byte[size];
        using var z = new ZLibStream(new MemoryStream(data), CompressionMode.Decompress);
        var read = 0;
        while (read < size && z.Read(raw, read, size - read) is var n and > 0)
        {
            read += n;
        }

        if (read != size)
        {
            throw new InvalidDataException($"image data is {read} bytes, {size} expected");
        }

        return raw;
    }

    // Undoes the per-row filters (none, sub, up, average, Paeth).
    private static byte[] Unfilter(byte[] raw, int stride, int height, int bytesPer)
    {
        var pixels = new byte[stride * height];
        for (var y = 0; y < height; y++)
        {
            var filter = raw[y * (stride + 1)];
            var src = raw.AsSpan(y * (stride + 1) + 1, stride);
            for (var i = 0; i < stride; i++)
            {
                int a = i >= bytesPer ? pixels[y * stride + i - bytesPer] : 0;
                int b = y > 0 ? pixels[(y - 1) * stride + i] : 0;
                int c = y > 0 && i >= bytesPer ? pixels[(y - 1) * stride + i - bytesPer] : 0;
                var predictor = filter switch
                {
                    0 => 0,
                    1 => a,
                    2 => b,
                    3 => (a + b) / 2,
                    FilterPaeth => Paeth(a, b, c),
                    _ => throw new InvalidDataException($"row {y}: filter {filter}"),
                };
                pixels[y * stride + i] = (byte)(src[i] + predictor);
            }
        }

        return pixels;
    }

    private static int Paeth(int a, int b, int c)
    {
        var p = a + b - c;
        int pa = Math.Abs(p - a), pb = Math.Abs(p - b), pc = Math.Abs(p - c);
        return pa <= pb && pa <= pc ? a : pb <= pc ? b : c;
    }

    private static void Chunk(Stream s, string type, byte[] body)
    {
        Span<byte> u32 = stackalloc byte[4];
        BinaryPrimitives.WriteInt32BigEndian(u32, body.Length);
        s.Write(u32);
        var typed = new byte[4 + body.Length];
        System.Text.Encoding.ASCII.GetBytes(type, typed);
        body.CopyTo(typed, 4);
        s.Write(typed);
        BinaryPrimitives.WriteUInt32BigEndian(u32, Crc(typed));
        s.Write(u32);
    }

    private static readonly uint[] CrcTable = BuildCrcTable();

    private static uint[] BuildCrcTable()
    {
        const uint polynomial = 0xEDB88320u;
        var table = new uint[256];
        for (var n = 0u; n < 256; n++)
        {
            var c = n;
            for (var k = 0; k < 8; k++)
            {
                c = (c & 1) != 0 ? polynomial ^ (c >> 1) : c >> 1;
            }

            table[n] = c;
        }

        return table;
    }

    private static uint Crc(ReadOnlySpan<byte> data)
    {
        var c = uint.MaxValue;
        foreach (var b in data)
        {
            c = CrcTable[(c ^ b) & 0xFF] ^ (c >> 8);
        }

        return ~c;
    }
}
