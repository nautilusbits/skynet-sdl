using System.Buffers.Binary;

namespace SkyNet.Edit;

/// <summary>The 3D view's shot: the view target's picture (RGBA8, rows top-down), made opaque and
/// written as a 32-bit BMP, as the game's --screenshot writes the whole frame.</summary>
public static class ViewShot
{
    private const int FileHeader = 14, InfoHeader = 40, BytesPerPixel = 4;

    /// <summary>RGBA pixels made opaque in place (a picture's alpha is not the shot's): <paramref name="rgba"/>.</summary>
    public static byte[] Opaque(byte[] rgba)
    {
        for (var i = BytesPerPixel - 1; i < rgba.Length; i += BytesPerPixel)
        {
            rgba[i] = byte.MaxValue;
        }

        return rgba;
    }

    /// <summary>RGBA8 pixels (rows top-down) as an uncompressed 32-bit BMP (BGRA, top-down: negative height).</summary>
    public static void WriteBmp(string path, int width, int height, ReadOnlySpan<byte> rgba)
    {
        var data = width * height * BytesPerPixel;
        var file = new byte[FileHeader + InfoHeader + data];
        var s = file.AsSpan();
        (s[0], s[1]) = ((byte)'B', (byte)'M');
        BinaryPrimitives.WriteInt32LittleEndian(s[2..], file.Length);
        BinaryPrimitives.WriteInt32LittleEndian(s[10..], FileHeader + InfoHeader);
        BinaryPrimitives.WriteInt32LittleEndian(s[14..], InfoHeader);
        BinaryPrimitives.WriteInt32LittleEndian(s[18..], width);
        BinaryPrimitives.WriteInt32LittleEndian(s[22..], -height);
        BinaryPrimitives.WriteInt16LittleEndian(s[26..], 1);
        BinaryPrimitives.WriteInt16LittleEndian(s[28..], 32);
        BinaryPrimitives.WriteInt32LittleEndian(s[34..], data);
        var pixels = s[(FileHeader + InfoHeader)..];
        for (var i = 0; i < data; i += BytesPerPixel)
        {
            // RGBA ─► BGRA.
            (pixels[i], pixels[i + 1], pixels[i + 2], pixels[i + 3]) = (rgba[i + 2], rgba[i + 1], rgba[i], rgba[i + 3]);
        }

        var dir = Path.GetDirectoryName(Path.GetFullPath(path));
        if (dir != null)
        {
            Directory.CreateDirectory(dir);
        }

        File.WriteAllBytes(path, file);
    }
}
