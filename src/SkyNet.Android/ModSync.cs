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
/// Mirrors the player's mods folder (picked with SAF) into the game's mods folder,
/// <c>files/mods</c>, next to the system mods' baked maps the game writes there. The plan is
/// <see cref="ModMirror"/>'s; this class lists the picked tree and carries the plan out.
/// The manifest (<c>files/mods-sync.txt</c>) lists the files the sync copied: it never deletes
/// anything else.
/// </summary>
internal sealed class ModSync(Context context, Action<string> progress)
{
    private const string ManifestName = "mods-sync.txt";
    private const string PartName = "mods-sync.part";

    private static readonly string[] Columns =
    [
        DocumentsContract.Document.ColumnDocumentId, DocumentsContract.Document.ColumnDisplayName,
        DocumentsContract.Document.ColumnMimeType, DocumentsContract.Document.ColumnSize,
        DocumentsContract.Document.ColumnLastModified,
    ];

    private static readonly string[] SystemMods =
        new[] { GameKind.SkyNet, GameKind.FutureShock }.Select(BlockStore.SystemModName).ToArray();

    public string? Error { get; private set; }

    /// <summary>Syncs <paramref name="tree"/> into <paramref name="modsDir"/>; call off the UI thread.</summary>
    public SyncOutcome Run(AUri tree, string modsDir)
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
            progress("Reading your mods folder…");
            List(tree, DocumentsContract.GetTreeDocumentId(tree)!, "", source, ids);
        }
        catch (Exception e) when (e is Java.Lang.SecurityException or Java.IO.FileNotFoundException or System.UnauthorizedAccessException)
        {
            return SyncOutcome.NoAccess;
        }

        var files = context.FilesDir!.AbsolutePath;
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
                progress($"Syncing your mods… {i + 1}/{plan.Copy.Count} ({file.Path})");
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

    // A folder's documents and, recursively, its folders'.
    private void List(AUri tree, string documentId, string prefix, List<MirrorFile> into, Dictionary<string, string> ids)
    {
        var children = DocumentsContract.BuildChildDocumentsUriUsingTree(tree, documentId)!;
        using var cursor = context.ContentResolver!.Query(children, Columns, null, null, null);
        while (cursor != null && cursor.MoveToNext())
        {
            var id = cursor.GetString(0)!;
            var path = prefix + cursor.GetString(1);
            if (cursor.GetString(2) == DocumentsContract.Document.MimeTypeDir)
            {
                List(tree, id, path + "/", into, ids);
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
