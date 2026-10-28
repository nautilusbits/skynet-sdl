using Android.Content;
using Android.Provider;
using SkyNet.Formats;
using SkyNet.Game.Baking;
using SkyNet.Game.Mods;
using AUri = Android.Net.Uri;

namespace SkyNet.Android;

/// <summary>How a mods sync ended.</summary>
internal enum SyncOutcome
{
    Done,
    /// <summary>The picked folder can no longer be read (permission revoked, folder gone).</summary>
    NoAccess,
    Failed,
}

/// <summary>
/// Mirrors the player's mods folder (picked with SAF, or the picked SkyNET folder's mods/) into
/// the game's mods folder, next to the system mods' baked maps the game writes there. The plan is
/// <see cref="ModMirror"/>'s; this class lists the picked tree and carries the plan out.
/// The manifest (<c>mods-sync.txt</c> beside the mods folder) lists the files the sync copied: it
/// never deletes anything else.
/// </summary>
internal sealed class ModSync(Context context, Action<string, int, int> progress)
{
    private const string ManifestName = "mods-sync.txt";
    private const string PartName = "mods-sync.part";
    /// <summary>Folders deeper than this in the mods folder are not synced.</summary>
    private const int MaxDepth = 16;

    private static readonly string[] Columns =
    [
        DocumentsContract.Document.ColumnDocumentId, DocumentsContract.Document.ColumnDisplayName,
        DocumentsContract.Document.ColumnMimeType, DocumentsContract.Document.ColumnSize,
        DocumentsContract.Document.ColumnLastModified,
    ];

    private static readonly string[] SystemMods =
        new[] { GameKind.SkyNet, GameKind.FutureShock }.Select(BlockStore.SystemModName).ToArray();

    public string? Error { get; private set; }

    /// <summary>Syncs the folder <paramref name="documentId"/> of <paramref name="tree"/> into
    /// <paramref name="modsDir"/>; call off the UI thread.</summary>
    public SyncOutcome Run(AUri tree, string documentId, string modsDir)
    {
        var resolver = context.ContentResolver!;
        var granted = resolver.PersistedUriPermissions.Any(p => p.IsReadPermission && p.Uri?.Equals(tree) == true);
        if (!granted)
        {
            return SyncOutcome.NoAccess;
        }

        // The picked tree, path by path ('/'-separated), with each file's document id.
        var ids = new Dictionary<string, string>(StringComparer.Ordinal);
        var source = new List<MirrorFile>();
        try
        {
            List(tree, documentId, "", source, ids, new HashSet<string>(StringComparer.Ordinal), 0);
        }
        catch (Exception e) when (e is Java.Lang.SecurityException or Java.IO.FileNotFoundException or System.UnauthorizedAccessException)
        {
            return SyncOutcome.NoAccess;
        }

        var files = Path.GetDirectoryName(Path.GetFullPath(modsDir))!;
        var manifestPath = Path.Combine(files, ManifestName);
        var owned = File.Exists(manifestPath) ? ModMirror.Read(File.ReadAllText(manifestPath)) : [];
        var plan = ModMirror.Plan(source, owned, p => File.Exists(Local(modsDir, p)), SystemMods);

        // The manifest follows every step, so a sync cut short is finished by the next one.
        var manifest = owned.ToDictionary(f => f.Path, StringComparer.Ordinal);
        try
        {
            foreach (var path in plan.Delete)
            {
                Delete(modsDir, path);
                manifest.Remove(path);
            }

            for (var i = 0; i < plan.Copy.Count; i++)
            {
                var file = plan.Copy[i];
                progress(file.Path, i + 1, plan.Copy.Count);
                Copy(DocumentsContract.BuildDocumentUriUsingTree(tree, ids[file.Path])!, Local(modsDir, file.Path), Path.Combine(files, PartName));
                manifest[file.Path] = file;
            }

            return SyncOutcome.Done;
        }
        catch (Exception e)
        {
            Error = e.Message;
            return SyncOutcome.Failed;
        }
        finally
        {
            File.WriteAllText(manifestPath, ModMirror.Write(manifest.Values));
        }
    }

    /// <summary>The document id of the folder <paramref name="name"/> (any case) in the picked
    /// folder of <paramref name="tree"/>; null when there is none or it cannot be read.</summary>
    public string? Folder(AUri tree, string name)
    {
        try
        {
            var children = DocumentsContract.BuildChildDocumentsUriUsingTree(tree, DocumentsContract.GetTreeDocumentId(tree))!;
            using var cursor = context.ContentResolver!.Query(children, Columns, null, null, null);
            while (cursor != null && cursor.MoveToNext())
            {
                if (cursor.GetString(2) == DocumentsContract.Document.MimeTypeDir && string.Equals(cursor.GetString(1), name, StringComparison.OrdinalIgnoreCase))
                {
                    return cursor.GetString(0);
                }
            }
        }
        catch (Exception e) when (e is Java.Lang.SecurityException or Java.IO.FileNotFoundException or System.UnauthorizedAccessException)
        {
        }

        return null;
    }

    // A folder's documents and, recursively, its folders'; folders seen before or too deep (a
    // provider's loop) are skipped.
    private void List(AUri tree, string documentId, string prefix, List<MirrorFile> into, Dictionary<string, string> ids, HashSet<string> seen, int depth)
    {
        if (depth > MaxDepth || !seen.Add(documentId))
        {
            return;
        }

        var children = DocumentsContract.BuildChildDocumentsUriUsingTree(tree, documentId)!;
        using var cursor = context.ContentResolver!.Query(children, Columns, null, null, null);
        while (cursor != null && cursor.MoveToNext())
        {
            var id = cursor.GetString(0)!;
            var path = prefix + cursor.GetString(1);
            if (cursor.GetString(2) == DocumentsContract.Document.MimeTypeDir)
            {
                List(tree, id, path + "/", into, ids, seen, depth + 1);
                continue;
            }

            // Size and time may be unknown (null): 0, so only the other one tells a change.
            into.Add(new MirrorFile(path, cursor.IsNull(3) ? 0 : cursor.GetLong(3), cursor.IsNull(4) ? 0 : cursor.GetLong(4)));
            ids[path] = id;
        }
    }

    // Copies through a temporary file: a copy cut short never leaves half a file in the mods folder.
    private void Copy(AUri document, string target, string part)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(target)!);
        using (var from = context.ContentResolver!.OpenInputStream(document) ?? throw new IOException($"{document} does not open"))
        using (var file = File.Create(part))
        {
            from.CopyTo(file);
        }

        File.Move(part, target, overwrite: true);
    }

    // Deletes a copied file and the folders it leaves empty, up to the mods folder.
    private static void Delete(string modsDir, string path)
    {
        var local = Local(modsDir, path);
        File.Delete(local);
        var root = Path.GetFullPath(modsDir);
        for (var dir = Path.GetDirectoryName(local); dir != null && dir.Length > root.Length; dir = Path.GetDirectoryName(dir))
        {
            if (!Directory.Exists(dir))
            {
                continue;
            }

            if (Directory.EnumerateFileSystemEntries(dir).Any())
            {
                return;
            }

            Directory.Delete(dir);
        }
    }

    private static string Local(string modsDir, string path) => Path.Combine(modsDir, path.Replace('/', Path.DirectorySeparatorChar));
}
