using System.Security.Cryptography;
using System.Text;
using RetroEng.Diagnostics;
using RetroEng.Render;
using SkyNet.Formats;
using SkyNet.Game.Baking;

namespace SkyNet.Game.Enhanced;

/// <summary>
/// A map's captured reflection probes (<see cref="ReflectionProbe"/>) in the user folder's cache,
/// keyed so a capture of other data, another look or an older capture is never picked up:
/// <code>
///   cache/reflections/&lt;game&gt;/&lt;fingerprint&gt;-v&lt;version&gt;/&lt;look&gt;/MAP.NNN.reprobes
///     e.g. cache/reflections/skynet/cfbd571a1b412888-v1/3f9a0c21d4e5b687/MAP.231.reprobes
///     game:        skynet | shock (BlockStore.SystemModName)
///     fingerprint: DataVersions.Fingerprint of the data's archives
///     version:     CaptureVersion (placement, capture settings, this layout)
///     look:        what the capture saw besides the data: the mods in order, DETAIL, AMBIENT, SHADOWS
///   file: "SNRP" version count, then each probe's length and its "REPB" bytes (ReflectionProbe.Save)
/// </code>
/// A file that does not read is a miss (captured again), never an error.
/// </summary>
public static class ReflectionFiles
{
    public const string Folder = "reflections";
    public const string Extension = ".reprobes";
    /// <summary>Bumped when the placement, the capture or this layout changes: every map captures again.</summary>
    public const int CaptureVersion = 1;

    private const uint Magic = 0x50524E53; // "SNRP"
    private const string LogArea = "Render";

    /// <summary>The file of a map's probes under <paramref name="cache"/>.</summary>
    public static string PathOf(string cache, GameKind game, string fingerprint, string look, int map) =>
        Path.Combine(cache, Folder, BlockStore.SystemModName(game), $"{fingerprint}-v{CaptureVersion}", look, $"MAP.{map:D3}{Extension}");

    /// <summary>A short hash of what the capture saw besides the data, e.g. "3f9a0c21d4e5b687".</summary>
    public static string LookOf(IEnumerable<string> mods, string detail, int ambient, string shadows)
    {
        var text = $"mods={string.Join(',', mods)};detail={detail};ambient={ambient};shadows={shadows}";
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(text)))[..16].ToLowerInvariant();
    }

    /// <summary>The probes in <paramref name="file"/>; null when it is missing or does not read.</summary>
    public static IReadOnlyList<ReflectionProbe>? Read(string file)
    {
        if (!File.Exists(file))
        {
            return null;
        }

        try
        {
            using var stream = File.OpenRead(file);
            return Load(stream);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or InvalidDataException or EndOfStreamException)
        {
            Log.Warn(LogArea, $"{Path.GetFileName(file)}: not read ({e.GetType().Name}); captured again");
            return null;
        }
    }

    /// <summary>Writes <paramref name="probes"/> to <paramref name="file"/> (a temporary file, then moved
    /// over); false (logged) when it cannot.</summary>
    public static bool Write(string file, IReadOnlyList<ReflectionProbe> probes)
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(file)!);
            var temp = file + ".tmp";
            using (var stream = File.Create(temp))
            {
                Save(stream, probes);
            }

            File.Move(temp, file, overwrite: true);
            return true;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            Log.Warn(LogArea, $"{Path.GetFileName(file)}: not saved ({e.GetType().Name})");
            return false;
        }
    }

    /// <summary>Writes the probes: the header, then each probe's length and bytes.</summary>
    public static void Save(Stream stream, IReadOnlyList<ReflectionProbe> probes)
    {
        using var w = new BinaryWriter(stream, Encoding.UTF8, leaveOpen: true);
        w.Write(Magic);
        w.Write(CaptureVersion);
        w.Write(probes.Count);
        foreach (var probe in probes)
        {
            using var bytes = new MemoryStream();
            probe.Save(bytes);
            w.Write((int)bytes.Length);
            w.Write(bytes.GetBuffer(), 0, (int)bytes.Length);
        }
    }

    /// <summary>Reads what <see cref="Save"/> wrote; throws <see cref="InvalidDataException"/> on another
    /// file or version, a count out of range or a probe that does not read.</summary>
    public static IReadOnlyList<ReflectionProbe> Load(Stream stream)
    {
        using var r = new BinaryReader(stream, Encoding.UTF8, leaveOpen: true);
        if (r.ReadUInt32() != Magic || r.ReadInt32() != CaptureVersion)
        {
            throw new InvalidDataException("not a reflection probe file of this version");
        }

        var count = r.ReadInt32();
        if (count is < 0 or > Reflections.MaxProbes)
        {
            throw new InvalidDataException($"{count} probes");
        }

        var probes = new List<ReflectionProbe>(count);
        for (var i = 0; i < count; i++)
        {
            var length = r.ReadInt32();
            var bytes = r.ReadBytes(length);
            if (length < 0 || bytes.Length != length)
            {
                throw new InvalidDataException("a short probe");
            }

            using var probe = new MemoryStream(bytes, writable: false);
            probes.Add(ReflectionProbe.Load(probe));
        }

        return probes;
    }
}
