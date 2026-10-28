using SkyNet.Game.Demos;
using SkyNet.Game.Menu;

namespace SkyNet.Game.Storage;

/// <summary>Android: moving the player's files between storage places. Settings, console history,
/// saves and demos follow the player to a new user folder (a switch, an older build's place);
/// an older build's mods folder moves to the new one.</summary>
public static class StorageMigration
{
    private const string History = "console-history.txt";
    private const string Saves = "saves";
    /// <summary>Links followed when comparing folders: a loop ends.</summary>
    private const int MaxLinks = 16;
    private const string PartSuffix = ".part";

    /// <summary>Copies the user files of <paramref name="from"/> into <paramref name="to"/> where
    /// the target is missing or older; the old files stay. Returns how many were copied.</summary>
    public static int CopyUserFiles(string from, string to)
    {
        if (!Directory.Exists(from) || SameFolder(from, to))
        {
            return 0;
        }

        var copied = 0;
        foreach (var name in new[] { SettingsStore.FileName, History })
        {
            copied += CopyNewer(Path.Combine(from, name), Path.Combine(to, name));
        }

        foreach (var folder in new[] { Saves, CampaignDemo.FolderName })
        {
            var dir = Path.Combine(from, folder);
            if (!Directory.Exists(dir))
            {
                continue;
            }

            foreach (var file in Directory.EnumerateFiles(dir, "*", SearchOption.AllDirectories))
            {
                copied += CopyNewer(file, Path.Combine(to, folder, Path.GetRelativePath(dir, file)));
            }
        }

        return copied;
    }

    /// <summary>Moves the files of <paramref name="from"/> into <paramref name="to"/> (e.g. baked
    /// maps across volumes), keeping files already there with the same size. Each source file goes
    /// once its copy is complete; a move cut short (killed, a failure thrown) is finished by the next.
    /// Returns how many files were copied.</summary>
    public static int MoveTree(string from, string to)
    {
        if (!Directory.Exists(from) || SameFolder(from, to))
        {
            return 0;
        }

        var copied = 0;
        foreach (var file in Directory.GetFiles(from, "*", SearchOption.AllDirectories))
        {
            var target = Path.Combine(to, Path.GetRelativePath(from, file));
            var size = new FileInfo(file).Length;
            if (!File.Exists(target) || new FileInfo(target).Length != size)
            {
                CopyWhole(file, target);
                copied++;
            }

            // The source file goes only when its copy is all there.
            if (new FileInfo(target).Length == size)
            {
                File.Delete(file);
            }
        }

        DeleteEmpty(from);
        return copied;
    }

    // Through a temporary file: a copy cut short never leaves half a file at the target.
    private static void CopyWhole(string from, string to)
    {
        var part = to + PartSuffix;
        Directory.CreateDirectory(Path.GetDirectoryName(to)!);
        File.Copy(from, part, overwrite: true);
        File.Move(part, to, overwrite: true);
    }

    // The folder and its folders, where no file is left.
    private static void DeleteEmpty(string dir)
    {
        foreach (var sub in Directory.GetDirectories(dir))
        {
            DeleteEmpty(sub);
        }

        if (!Directory.EnumerateFileSystemEntries(dir).Any())
        {
            Directory.Delete(dir);
        }
    }

    private static int CopyNewer(string from, string to)
    {
        if (!File.Exists(from) || File.Exists(to) && File.GetLastWriteTimeUtc(to) >= File.GetLastWriteTimeUtc(from))
        {
            return 0;
        }

        Directory.CreateDirectory(Path.GetDirectoryName(to)!);
        File.Copy(from, to, overwrite: true);
        return 1;
    }

    /// <summary>True when both paths name one folder, however spelled: "a/b/", "a/x/../b", a link
    /// on the way (Android's /sdcard ─► /storage/emulated/0).</summary>
    public static bool SameFolder(string a, string b) =>
        string.Equals(Canonical(a, MaxLinks), Canonical(b, MaxLinks), StringComparison.Ordinal);

    // The full path with the deepest link on it resolved, again until none is left.
    private static string Canonical(string path, int links)
    {
        var full = Path.TrimEndingDirectorySeparator(Path.GetFullPath(path));
        for (var dir = full; dir != null && links > 0; dir = Path.GetDirectoryName(dir))
        {
            FileSystemInfo? target;
            try
            {
                target = new DirectoryInfo(dir).ResolveLinkTarget(returnFinalTarget: true);
            }
            catch (IOException)
            {
                continue;
            }

            if (target != null)
            {
                return Canonical(Path.Combine(target.FullName, Path.GetRelativePath(dir, full)), links - 1);
            }
        }

        return full;
    }
}
