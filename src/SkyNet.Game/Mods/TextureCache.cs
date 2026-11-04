using System.Security.Cryptography;
using System.Text;
using RetroEng.Diagnostics;

namespace SkyNet.Game.Mods;

/// <summary>How a cached HD texture's pixels are stored. Only RGBA8 now; GPU-compressed blocks
/// (ASTC, BC7) join here, each its own file beside the others.</summary>
internal enum CachedFormat
{
    Rgba8,
}

/// <summary>
/// The HD textures as decoded (<see cref="ModContent"/>), kept on disk in the game's cache folder so a
/// later load reads them instead of decoding the PNGs again. One file per record, side and DETAIL; its key
/// (the PNGs' hashes, the record's width, the format, the version) must match, else it is decoded
/// again and rewritten. A file that does not read is a miss, never an error.
/// <code>
///   cache/textures/1A2B3C4D/skynet-hd/TEXTURE.210/003.c.Medium.Rgba8
///     "SNHT" version key format w h |rgba| |relief| rgba relief
/// </code>
/// 1A2B3C4D names the mods folder; each mod keeps its own entries, so two mods with the same
/// record are both cached and a new mod order only picks the other one.
/// </summary>
internal static class TextureCache
{
    // Bumped when the decode's output or this layout changes: every entry is made again.
    private const int Version = 1;
    private const uint Magic = 0x54484E53; // "SNHT"
    private const CachedFormat Format = CachedFormat.Rgba8;

    private static int _writeFailed;

    /// <summary>The cache folder of the mods folder <paramref name="modsDirectory"/> (a full path).</summary>
    internal static string RootOf(string modsDirectory)
    {
        var folder = Encoding.UTF8.GetBytes(modsDirectory.ToUpperInvariant());
        return Path.Combine(Menu.SettingsStore.CacheFolder, "textures", Hash(folder)[..8]);
    }

    /// <summary>A record's entry from mod <paramref name="mod"/>, one side at <paramref name="detail"/>.</summary>
    internal static string PathOf(string root, string mod, int bank, int record, bool opaque, Detail detail) =>
        Path.Combine(root, mod, $"TEXTURE.{bank:D3}", $"{record:D3}.{(opaque ? 'o' : 'c')}.{detail}.{Format}");


    /// <summary>What an entry is made from: the PNGs' content, the record's width and the format.</summary>
    internal static string KeyOf(byte[] colour, byte[]? normal, byte[]? rough, int width)
    {
        var key = new StringBuilder();
        key.Append($"v{Version} {Format} w{width} {Hash(colour)}");
        key.Append(normal != null && rough != null ? $" {Hash(normal)} {Hash(rough)}" : " -");
        return key.ToString();
    }

    private static string Hash(byte[] bytes) => Convert.ToHexString(SHA256.HashData(bytes));

    /// <summary>The entry at <paramref name="path"/> when it was made from <paramref name="key"/>; else null.</summary>
    internal static HdTexture? Read(string path, string key)
    {
        try
        {
            if (!File.Exists(path))
            {
                return null;
            }

            using var file = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 1 << 16);
            using var r = new BinaryReader(file);
            if (r.ReadUInt32() != Magic || r.ReadInt32() != Version || r.ReadString() != key || r.ReadByte() != (byte)Format)
            {
                return null;
            }

            var (w, h, rgbaLength, reliefLength) = (r.ReadInt32(), r.ReadInt32(), r.ReadInt32(), r.ReadInt32());
            var size = (long)w * h * 4;
            if (w <= 0 || h <= 0 || rgbaLength != size || (reliefLength != 0 && reliefLength != size)
                || file.Length - file.Position != rgbaLength + (long)reliefLength)
            {
                return null;
            }

            var rgba = new byte[rgbaLength];
            file.ReadExactly(rgba);
            byte[]? relief = null;
            if (reliefLength > 0)
            {
                relief = new byte[reliefLength];
                file.ReadExactly(relief);
            }

            return new HdTexture(w, h, rgba) { Relief = relief };
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or EndOfStreamException or ArgumentException or OverflowException)
        {
            return null;
        }
    }

    /// <summary>Writes the entry (a temporary file moved over the old one, so a cut-short write
    /// never leaves half an entry). A failure is logged once and costs only the next decode.</summary>
    internal static void Write(string path, string key, HdTexture texture)
    {
        var temp = $"{path}.{Guid.NewGuid():N}.tmp";
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            using (var file = new FileStream(temp, FileMode.CreateNew, FileAccess.Write, FileShare.None, 1 << 16))
            using (var w = new BinaryWriter(file))
            {
                w.Write(Magic);
                w.Write(Version);
                w.Write(key);
                w.Write((byte)Format);
                w.Write(texture.Width);
                w.Write(texture.Height);
                w.Write(texture.Rgba.Length);
                w.Write(texture.Relief?.Length ?? 0);
                w.Write(texture.Rgba);
                if (texture.Relief is { } relief)
                {
                    w.Write(relief);
                }
            }

            File.Move(temp, path, overwrite: true);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            TryDelete(temp);
            if (Interlocked.Exchange(ref _writeFailed, 1) == 0)
            {
                Log.Warn("Mods", $"HD texture cache not written ({e.Message}): the PNGs are decoded at every load");
            }
        }
    }

    private static void TryDelete(string path)
    {
        try
        {
            File.Delete(path);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
        }
    }
}
