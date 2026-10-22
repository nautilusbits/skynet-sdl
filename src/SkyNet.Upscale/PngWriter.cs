using System.Buffers.Binary;
using System.IO.Compression;

namespace SkyNet.Upscale;

/// <summary>Writes 8-bit grey, RGB or RGBA PNGs (straight alpha), Paeth-filtered, without SDL.</summary>
public static class PngWriter
{
    private static ReadOnlySpan<byte> Signature => [0x89, (byte)'P', (byte)'N', (byte)'G', 0x0D, 0x0A, 0x1A, 0x0A];

    /// <summary>A PNG of <paramref name="pixels"/> (<paramref name="channels"/> 1 = grey, 3 = RGB, 4 = RGBA).</summary>
    public static byte[] Encode(int width, int height, int channels, ReadOnlySpan<byte> pixels, CompressionLevel level = CompressionLevel.Optimal)
    {
        if (channels is not (1 or 3 or 4) || pixels.Length < width * height * channels)
        {
            throw new ArgumentException($"{width}x{height}x{channels}: {pixels.Length} bytes");
        }

        using var png = new MemoryStream();
        png.Write(Signature);
        var ihdr = new byte[13];
        BinaryPrimitives.WriteInt32BigEndian(ihdr, width);
        BinaryPrimitives.WriteInt32BigEndian(ihdr.AsSpan(4), height);
        ihdr[8] = 8;                               // bit depth
        ihdr[9] = (byte)(channels switch { 4 => 6, 3 => 2, _ => 0 });   // RGBA / RGB / grey
        Chunk(png, "IHDR", ihdr);
        var stride = width * channels;
        using (var raw = new MemoryStream())
        {
            using (var z = new ZLibStream(raw, level, leaveOpen: true))
            {
                var row = new byte[stride + 1];
                row[0] = 4;   // filter: Paeth
                for (var y = 0; y < height; y++)
                {
                    var line = pixels.Slice(y * stride, stride);
                    var up = y > 0 ? pixels.Slice((y - 1) * stride, stride) : default;
                    for (var i = 0; i < stride; i++)
                    {
                        int a = i >= channels ? line[i - channels] : 0;
                        int b = y > 0 ? up[i] : 0;
                        int c = y > 0 && i >= channels ? up[i - channels] : 0;
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
        for (var i = 0; i < 4; i++)
        {
            typed[i] = (byte)type[i];
        }

        body.CopyTo(typed, 4);
        s.Write(typed);
        BinaryPrimitives.WriteUInt32BigEndian(u32, Crc(typed));
        s.Write(u32);
    }

    private static readonly uint[] CrcTable = BuildCrcTable();

    private static uint[] BuildCrcTable()
    {
        var table = new uint[256];
        for (var n = 0u; n < 256; n++)
        {
            var c = n;
            for (var k = 0; k < 8; k++)
            {
                c = (c & 1) != 0 ? 0xEDB88320u ^ (c >> 1) : c >> 1;
            }

            table[n] = c;
        }

        return table;
    }

    private static uint Crc(ReadOnlySpan<byte> data)
    {
        var c = 0xFFFFFFFFu;
        foreach (var b in data)
        {
            c = CrcTable[(c ^ b) & 0xFF] ^ (c >> 8);
        }

        return c ^ 0xFFFFFFFFu;
    }
}
