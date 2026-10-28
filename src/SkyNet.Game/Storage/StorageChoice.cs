using SkyNet.Formats;

namespace SkyNet.Game.Storage;

/// <summary>Whether the game may read and write shared storage by path (Android's All files
/// access; the storage permission before Android 11).</summary>
public enum FolderAccess
{
    Granted,
    /// <summary>Not granted, but the user can be asked.</summary>
    Askable,
    /// <summary>A store build: never asked, the files are copied.</summary>
    Unavailable,
}

/// <summary>What a start does with the game's files (<see cref="StorageChoice.Plan"/>).</summary>
public enum StorageKind
{
    /// <summary>The chosen folder, read and written in place.</summary>
    InPlace,
    /// <summary>The copy in the app's folder.</summary>
    Copied,
    /// <summary>The chosen folder is gone or unreadable: say so, forget it, plan again.</summary>
    Lost,
    /// <summary>Nothing yet: ask for access to play in place (declined: copy).</summary>
    AskAccess,
    /// <summary>Nothing yet and no access to ask for: copy.</summary>
    CopyFlow,
}

/// <summary>A start's plan; <see cref="DataDir"/> the data folder for InPlace and Copied.</summary>
public readonly record struct StoragePlan(StorageKind Kind, string? DataDir = null);

/// <summary>Android: the SkyNET folder the user chose to play from in place, remembered in a file
/// of the app's folder and checked each start. The folder is laid out as on the desktop: data in
/// gamedata/ (Future Shock in shock/GAMEDATA), mods/, logs/, settings and saves beside them.
/// <code>
///   chosen? ─yes─► access, data, writable? ─yes─► InPlace
///      │                   └no─► Lost (forget, plan again)
///      └no─► copied before? ─yes─► Copied
///               └no─► store build? ─yes─► CopyFlow
///                        └no─► AskAccess
/// </code></summary>
public sealed class StorageChoice(string appFolder)
{
    private const string FileName = "data-folder.txt";
    private const string Probe = ".write-test";

    private string ChoiceFile => Path.Combine(appFolder, FileName);

    /// <summary>The chosen folder, or null.</summary>
    public string? Folder
    {
        get
        {
            if (!File.Exists(ChoiceFile))
            {
                return null;
            }

            var folder = File.ReadAllText(ChoiceFile).Trim();
            return folder.Length == 0 ? null : folder;
        }
    }

    public void Choose(string folder)
    {
        Directory.CreateDirectory(appFolder);
        File.WriteAllText(ChoiceFile, folder);
    }

    public void Forget() => File.Delete(ChoiceFile);

    /// <summary>What this start does, given the access and the copy's folder.</summary>
    public StoragePlan Plan(FolderAccess access, string copyRoot)
    {
        if (Folder is { } folder)
        {
            var data = access == FolderAccess.Granted ? GameDataLocator.ResolveDataDir(folder) : null;
            return data != null && IsWritable(folder) ? new(StorageKind.InPlace, data) : new(StorageKind.Lost);
        }

        if (FindCopy(copyRoot) is { } copy)
        {
            return new(StorageKind.Copied, copy);
        }

        return new(access == FolderAccess.Unavailable ? StorageKind.CopyFlow : StorageKind.AskAccess);
    }

    /// <summary>The data folder in a copy: the desktop's places, else at any depth (a picked parent
    /// folder, e.g. gamedata/gamedata). Null when there is none.</summary>
    public static string? FindCopy(string copyRoot)
    {
        if (!Directory.Exists(copyRoot))
        {
            return null;
        }

        if (GameDataLocator.ResolveDataDir(copyRoot) is { } dir)
        {
            return dir;
        }

        try
        {
            return Directory.EnumerateFiles(copyRoot, "*", new EnumerationOptions { RecurseSubdirectories = true, IgnoreInaccessible = true })
                .Where(f => Path.GetFileName(f).Equals(GameDataLocator.ProbeSkyNet, StringComparison.OrdinalIgnoreCase))
                .Select(Path.GetDirectoryName)
                .FirstOrDefault();
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    /// <summary>Writes and deletes a probe file: settings, saves, mods and logs go there.</summary>
    public static bool IsWritable(string folder)
    {
        var probe = Path.Combine(folder, Probe);
        try
        {
            File.WriteAllBytes(probe, []);
            File.Delete(probe);
            return true;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            return false;
        }
    }
}
