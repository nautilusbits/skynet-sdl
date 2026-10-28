namespace SkyNet.Game.Storage;

/// <summary>Android: the file path of a folder picked with the system's folder picker (a tree URI's
/// authority and document id), for the game to use it in place with All files access.
/// <code>
///   externalstorage  primary:SkyNET     ─► /storage/emulated/0/SkyNET
///                    home:SkyNET        ─► /storage/emulated/0/Documents/SkyNET
///                    1234-ABCD:SkyNET   ─► /storage/1234-ABCD/SkyNET (SD card)
///   downloads        raw:/storage/...   ─► /storage/...
///   anything else (cloud, msf:42)       ─► null
/// </code></summary>
public static class TreePaths
{
    public const string ExternalStorage = "com.android.externalstorage.documents";
    public const string Downloads = "com.android.providers.downloads.documents";
    private const string PrimaryVolume = "primary";
    private const string HomeVolume = "home";
    private const string HomeFolder = "Documents";
    private const string Raw = "raw:";
    private const string Volumes = "/storage";
    /// <summary>The app's external files folder: primary storage/Android/data/package/files.</summary>
    private const string AppData = "/Android/data/";
    private const char VolumeSeparator = ':';
    private const char Separator = '/';
    private const string Up = "..";
    private const string Here = ".";

    /// <summary>The picked folder's path; null when it has none (a cloud, a Downloads list entry).</summary>
    public static string? Resolve(string authority, string documentId, string primaryRoot) =>
        Contained(PathOf(authority, documentId, primaryRoot));

    /// <summary>The path of the entry <paramref name="name"/> (a document provider's display name)
    /// in <paramref name="folder"/>; null for a name that is no single entry or would climb out
    /// (e.g. "", "..", "a/b", "/etc").</summary>
    public static string? Child(string folder, string name)
    {
        if (name is "" or Here or Up || name.IndexOfAny(['/', '\\', '\0']) >= 0 || Path.IsPathRooted(name))
        {
            return null;
        }

        var root = Path.GetFullPath(folder).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
        var child = Path.GetFullPath(Path.Combine(root, name));
        return child.StartsWith(root, StringComparison.Ordinal) ? Path.Combine(folder, name) : null;
    }

    /// <summary>The primary storage's root, from the app's external files folder; null when that
    /// is not on it.</summary>
    public static string? PrimaryRoot(string appFiles)
    {
        var at = appFiles.IndexOf(AppData, StringComparison.Ordinal);
        return at < 0 ? null : appFiles[..at];
    }

    // A path on the storage volumes only, with no .. or . part to climb out of them.
    private static string? Contained(string? path)
    {
        if (path == null || !path.StartsWith(Volumes + Separator, StringComparison.Ordinal))
        {
            return null;
        }

        return path.Split(Separator).Any(part => part is Up or Here) ? null : path;
    }

    private static string? PathOf(string authority, string documentId, string primaryRoot)
    {
        if (authority == Downloads && documentId.StartsWith(Raw, StringComparison.Ordinal))
        {
            return documentId[Raw.Length..];
        }

        var colon = documentId.IndexOf(VolumeSeparator);
        if (authority != ExternalStorage || colon < 0)
        {
            return null;
        }

        var volume = documentId[..colon];
        var root = volume switch
        {
            PrimaryVolume => primaryRoot,
            HomeVolume => primaryRoot + Separator + HomeFolder,
            _ => Volumes + Separator + volume,
        };
        var folder = documentId[(colon + 1)..].Trim(Separator);
        return folder.Length == 0 ? root : root + Separator + folder;
    }
}
