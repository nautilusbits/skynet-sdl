using System.Buffers.Binary;
using System.IO.Compression;
using System.Text;
using SkyNet.Formats;

namespace SkyNet.Tables;

/// <summary>
/// Exports the TEXTURE.NNN records of a data folder as palette PNGs (8-bit indexed, the game
/// palette in PLTE, index 0 kept as index 0), the input of tools/upscale. One file per record,
/// <c>&lt;out&gt;/TEXTURE.NNN/RRR.png</c>; an animated record has its frames stacked downwards, each
/// centred in the largest frame's box, as <c>SkyNet.Game.TextureStore</c> uploads it. Flat-colour
/// records (1 x 1) are skipped. <c>&lt;out&gt;/manifest.json</c> lists every record with its frame
/// size, frame count, layout (<c>wall</c>: rows at a stride, repeats across a face; <c>sprite</c>:
/// transparency-run frames) and whether index 0 occurs (holes).
/// </summary>
internal static class TextureExport
{
    public static int Run(string dataDir, string outDir)
    {
        var data = GameData.Open(dataDir);
        if (data == null)
        {
            Console.Error.WriteLine($"no game data in {dataDir}");
            return 2;
        }

        var palette = data.PaletteBytes() is { } p ? Palette.Parse(p) : null;
        if (palette == null)
        {
            Console.Error.WriteLine("no game palette");
            return 2;
        }

        Directory.CreateDirectory(outDir);
        var manifest = new StringBuilder("{\"format\":\"skynet-texture-export\",\"version\":1,\"records\":[\n");
        var first = true;
        var count = 0;
        for (var bank = 0; bank <= TextureStack.MaxBank; bank++)
        {
            if (data.ReadLoose($"TEXTURE.{bank:D3}") is not { } bytes)
            {
                continue;
            }

            foreach (var rec in TextureStack.Records(bank, bytes))
            {
                var (r, w, h, frames) = (rec.Index, rec.Width, rec.Height, rec.Frames);
                var layout = rec.Sprite ? "sprite" : "wall";
                var dir = Path.Combine(outDir, $"TEXTURE.{bank:D3}");
                Directory.CreateDirectory(dir);
                File.WriteAllBytes(Path.Combine(dir, $"{r:D3}.png"), IndexedPng(w, h * frames, rec.Pixels, palette));
                manifest.Append(first ? "" : ",\n").Append(
                    $"{{\"bank\":{bank},\"record\":{r},\"width\":{w},\"height\":{h},\"frames\":{frames},\"ms\":{rec.MsPerFrame},\"layout\":\"{layout}\",\"holes\":{(rec.Holes ? "true" : "false")}}}");
                first = false;
                count++;
            }
        }

        manifest.Append("\n]}\n");
        File.WriteAllText(Path.Combine(outDir, "manifest.json"), manifest.ToString());
        Console.WriteLine($"{count} records written to {outDir}");
        return 0;
    }

    /// <summary>An 8-bit palette PNG (no transparency chunk: index 0 is the caller's to interpret).</summary>
    private static byte[] IndexedPng(int width, int height, byte[] pixels, Palette palette)
    {
        using var png = new MemoryStream();
        png.Write([0x89, (byte)'P', (byte)'N', (byte)'G', 0x0D, 0x0A, 0x1A, 0x0A]);
        var ihdr = new byte[13];
        BinaryPrimitives.WriteInt32BigEndian(ihdr, width);
        BinaryPrimitives.WriteInt32BigEndian(ihdr.AsSpan(4), height);
        ihdr[8] = 8;   // bit depth
        ihdr[9] = 3;   // palette colour
        Chunk(png, "IHDR", ihdr);
        Chunk(png, "PLTE", [.. palette.Rgb]);
        using (var raw = new MemoryStream())
        {
            using (var z = new ZLibStream(raw, CompressionLevel.Optimal, leaveOpen: true))
            {
                for (var y = 0; y < height; y++)
                {
                    z.WriteByte(0);   // filter: none
                    z.Write(pixels, y * width, width);
                }
            }

            Chunk(png, "IDAT", raw.ToArray());
        }

        Chunk(png, "IEND", []);
        return png.ToArray();
    }

    private static void Chunk(Stream s, string type, byte[] body)
    {
        Span<byte> u32 = stackalloc byte[4];
        BinaryPrimitives.WriteInt32BigEndian(u32, body.Length);
        s.Write(u32);
        var typed = Encoding.ASCII.GetBytes(type).Concat(body).ToArray();
        s.Write(typed);
        BinaryPrimitives.WriteUInt32BigEndian(u32, Crc32(typed));
        s.Write(u32);
    }

    private static readonly uint[] CrcTable = Enumerable.Range(0, 256).Select(n =>
    {
        var c = (uint)n;
        for (var k = 0; k < 8; k++)
        {
            c = (c & 1) != 0 ? 0xEDB88320u ^ (c >> 1) : c >> 1;
        }

        return c;
    }).ToArray();

    private static uint Crc32(byte[] data)
    {
        var c = 0xFFFFFFFFu;
        foreach (var b in data)
        {
            c = CrcTable[(c ^ b) & 0xFF] ^ (c >> 8);
        }

        return c ^ 0xFFFFFFFFu;
    }
}
