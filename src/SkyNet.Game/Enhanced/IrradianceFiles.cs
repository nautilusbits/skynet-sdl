using System.Buffers.Binary;
using System.Security.Cryptography;
using System.Text;
using RetroEng.Diagnostics;
using RetroEng.Render;
using SkyNet.Formats;
using SkyNet.Game.Baking;

namespace SkyNet.Game.Enhanced;

/// <summary>
/// A map's baked irradiance volume (<see cref="IrradianceVolume"/>) in the user folder's cache, baked
/// on the player's device at the map's first ENHANCED load (<see cref="IrradianceJob"/>) or by
/// <c>--bake-irradiance</c>, keyed so a bake of other data, an older bake or an edited map is never
/// picked up, one file a map (a new bake of it replaces the others):
/// <code>
///   cache/lighting/&lt;game&gt;/MAP.NNN/&lt;key&gt;.reiv    e.g. cache/lighting/skynet/MAP.231/9c41d07a5e2b8f13.reiv
///     game: skynet | shock (BlockStore.SystemModName)
///     key:  SHA-256 of the data fingerprint (DataVersions.Fingerprint), BakeVersion and the map's
///           block (MapHashOf), first 16 hex digits
///   file: IrradianceVolume.Save: a 36-byte header (sizes x, y, z at 24) and 25 bytes a probe
/// </code>
/// None found, or one that does not read: a miss (baked again), never an error.
/// </summary>
public static class IrradianceFiles
{
    public const string Folder = "lighting";
    public const string Extension = ".reiv";
    /// <summary>Bumped when the bake's input (IrradianceScene) or its light changes: every map bakes again.</summary>
    public const int BakeVersion = 4;
    /// <summary>Hex digits kept of a hash.</summary>
    private const int HashLength = 16;
    private const string LogArea = "Render";

    // IrradianceVolume.Save's layout: the header, the grid's sizes in it, a probe's bytes.
    private const int HeaderSize = 36;
    private const int SizesOffset = 24;
    private const int ProbeSize = 25;

    /// <summary>The key of a map's volume: its data, the bake's version and the map's block.</summary>
    public static string KeyOf(string fingerprint, string mapHash) =>
        Hash(Encoding.UTF8.GetBytes($"data={fingerprint};bake={BakeVersion};map={mapHash}"));

    /// <summary>A hash of the map's baked block (geometry, lamps, inputs): an edited map gets another.</summary>
    public static string MapHashOf(MapBlock block) => Hash(block.ToBytes());

    /// <summary>The volume's file under <paramref name="cache"/>.</summary>
    public static string PathOf(string cache, GameKind game, string key, int map) =>
        Path.Combine(cache, Folder, BlockStore.SystemModName(game), $"MAP.{map:D3}", key + Extension);

    /// <summary>The volume in <paramref name="file"/>; null when it is missing or does not read (logged):
    /// a size that does not match its probes, out of memory included.</summary>
    public static IrradianceVolume? Read(string file)
    {
        if (!File.Exists(file))
        {
            return null;
        }

        try
        {
            using var stream = File.OpenRead(file);
            if (!SizeMatches(stream))
            {
                Log.Warn(LogArea, $"{Path.GetFileName(file)}: its size does not match its probes; baked again");
                return null;
            }

            return IrradianceVolume.Load(stream);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or InvalidDataException or EndOfStreamException
                                      or OutOfMemoryException)
        {
            Log.Warn(LogArea, $"{Path.GetFileName(file)}: not read ({e.GetType().Name}); baked again");
            return null;
        }
    }

    /// <summary>Writes <paramref name="volume"/> to <paramref name="file"/> (a temporary file, then
    /// moved over) and drops the map's bakes under other keys; false (logged) when it cannot.</summary>
    public static bool Write(IrradianceVolume volume, string file)
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(file)!);
            var temp = file + ".tmp";
            using (var stream = File.Create(temp))
            {
                volume.Save(stream);
            }

            File.Move(temp, file, overwrite: true);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            Log.Warn(LogArea, $"{Path.GetFileName(file)}: not saved ({e.GetType().Name})");
            return false;
        }

        DropOthers(file);
        return true;
    }

    /// <summary>Deletes the legacy <c>lighting/</c> folder of builds before the cache (test bakes) from
    /// <paramref name="userFolder"/>, when it holds bakes only; true when it went.</summary>
    public static bool RemoveLegacy(string userFolder)
    {
        var dir = Path.Combine(userFolder, Folder);
        if (!Directory.Exists(dir))
        {
            return false;
        }

        try
        {
            if (Directory.EnumerateFiles(dir, "*", SearchOption.AllDirectories).Any(f => !IsBake(f)))
            {
                Log.Info(LogArea, $"{Folder}/ beside the game holds other files than bakes: kept");
                return false;
            }

            Directory.Delete(dir, recursive: true);
            Log.Info(LogArea, $"{Folder}/ beside the game (an older build's bakes) deleted: the cache holds the light");
            return true;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            Log.Warn(LogArea, $"{Folder}/ beside the game not deleted ({e.GetType().Name})");
            return false;
        }
    }

    private static bool IsBake(string file) =>
        file.EndsWith(Extension, StringComparison.OrdinalIgnoreCase) || file.EndsWith(Extension + ".tmp", StringComparison.OrdinalIgnoreCase);

    // The header's grid against the stream's length: a cut or bad file never allocates its grid.
    private static bool SizeMatches(Stream stream)
    {
        Span<byte> header = stackalloc byte[HeaderSize];
        if (stream.Length < HeaderSize || stream.ReadAtLeast(header, HeaderSize, throwOnEndOfStream: false) < HeaderSize)
        {
            return false;
        }

        stream.Position = 0;
        long probes = 1;
        for (var axis = 0; axis < 3; axis++)
        {
            var size = BinaryPrimitives.ReadInt32LittleEndian(header[(SizesOffset + axis * sizeof(int))..]);
            if (size is < IrradianceVolume.MinProbesPerAxis or > IrradianceVolume.MaxProbesPerAxis)
            {
                return false;
            }

            probes *= size;
        }

        return probes <= IrradianceVolume.MaxProbes && stream.Length == HeaderSize + probes * ProbeSize;
    }

    // The map's bakes under other keys (an edited map, older data): one file a map.
    private static void DropOthers(string file)
    {
        try
        {
            foreach (var other in Directory.EnumerateFiles(Path.GetDirectoryName(file)!).Where(f => !string.Equals(f, file, StringComparison.OrdinalIgnoreCase)))
            {
                File.Delete(other);
            }
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            Log.Warn(LogArea, $"{Path.GetFileName(file)}: older bakes not deleted ({e.GetType().Name})");
        }
    }

    private static string Hash(byte[] bytes) => Convert.ToHexString(SHA256.HashData(bytes))[..HashLength].ToLowerInvariant();
}
