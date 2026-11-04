using RetroEng.Diagnostics;

namespace SkyNet.Game.Storage;

/// <summary>What a file moved into the cache is: made again when lost, or the player's own.</summary>
public enum CacheFile
{
    /// <summary>Baked from the data (the system ZIPs, their stamps): an older copy goes without a word.</summary>
    Rebuildable,
    /// <summary>Written by the player's use (the console's history, the editor's preferences): an older copy going is logged.</summary>
    Personal,
}

/// <summary>
/// Older builds wrote generated files beside the game or in the mods folder; they live in cache/ now.
/// Each is moved once, at its first use:
/// <code>
///   mods/skynet.zip(.stamp), mods/shock.zip(.stamp) ─► cache/   (BlockStore.MoveFromMods)
///   console-history.txt                             ─► cache/   (the console's history)
///   SkyNetEdit.json, SkyNetEdit.imgui.ini            ─► cache/   (the editor's preferences and layout)
/// </code>
/// </summary>
public static class CacheMigration
{
    private const string LogArea = "Storage";

    /// <summary>Moves <paramref name="from"/> to <paramref name="to"/>. When both are there the newer
    /// (last written) wins and the older goes (logged for a <see cref="CacheFile.Personal"/> file). Never
    /// throws (a failure is logged and the old file stays). True when the old file became the cache's.</summary>
    public static bool Move(string from, string to, CacheFile kind)
    {
        // Nothing to move: no file, or the file is already where it goes (it would be deleted).
        if (!File.Exists(from) || string.Equals(Path.GetFullPath(from), Path.GetFullPath(to), StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        try
        {
            if (File.Exists(to) && File.GetLastWriteTimeUtc(to) >= File.GetLastWriteTimeUtc(from))
            {
                File.Delete(from);
                LogDropped(from, kind);
                return false;
            }

            if (File.Exists(to))
            {
                LogDropped(to, kind);
            }

            Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(to))!);
            File.Move(from, to, overwrite: true);
            return true;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            Log.Warn(LogArea, $"{Path.GetFileName(from)} not moved to the cache ({e.GetType().Name})");
            return false;
        }
    }

    // The player's file lost to a newer copy: said in the log.
    private static void LogDropped(string file, CacheFile kind)
    {
        if (kind == CacheFile.Personal)
        {
            Log.Info(LogArea, $"{Path.GetFileName(file)}: an older copy dropped, the newer one kept in the cache");
        }
    }
}
