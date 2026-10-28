using System.Globalization;
using Android.App;
using Android.Content;
using Android.Provider;
using SkyNet.Formats;
using SkyNet.Game.Menu;
using SkyNet.Game.Storage;
using AUri = Android.Net.Uri;

namespace SkyNet.Android;

/// <summary>What the game plays with: the data folder and the user folder (settings, saves, mods/,
/// logs/), laid out as the desktop's install folder.</summary>
internal sealed record GameFiles(string DataDir, string UserRoot);

/// <summary>How the setup starts: a normal start, or the launcher's Setup shortcut (its menu).</summary>
internal enum SetupMode { Start, Menu }

/// <summary>The game's files at each start, on a worker thread of <see cref="SetupActivity"/>
/// (dialogs on the UI thread). With All files access the user picks the SkyNET folder copied to
/// the phone (as on the PC: gamedata/, shock/GAMEDATA, mods/) and the game reads and writes it in
/// place, copying nothing. Without (declined, a store build) the picked folder is copied into the
/// app's external folder, which then plays the install folder's part; its mods/ (or a mods folder
/// picked apart) is synced in at every start (<see cref="ModSync"/>).
/// <code>
///   older build's files ─► moved once (mods/, settings, saves)
///   chosen folder readable? ─yes─► in place
///      └gone─► "can't read it" ─► forget ─┐
///   copied before? ─yes─► sync mods ─► copy ◄┘
///      └no─► "play in place?" ─allow─► All files access ─► picker ─► path, data, writable? ─► remember
///              └copy instead, refused, store build ─► picker ─► data in it? ─► copy (progress)
/// </code></summary>
internal sealed class DataSetup(Activity activity)
{
    /// <summary>The folder picker's request code ("SD").</summary>
    public const int PickRequest = 0x5344;
    private const string CopyFolder = "gamedata";
    private const string ModsFolder = "mods";
    private const string LogsFolder = "logs";
    private const string Prefs = "setup";
    private const string DataTreeKey = "dataTree";
    private const string ModsTreeKey = "modsTree";
    /// <summary>Written once the older build's files have moved.</summary>
    private const string MigratedFile = "storage-moved.txt";
    /// <summary>The older build's mods manifest, moved with its mods.</summary>
    private const string OldManifest = "mods-sync.txt";
    /// <summary>The primary storage's usual root, when the app's folder does not tell.</summary>
    private const string DefaultPrimary = "/storage/emulated/0";
    /// <summary>Folders deeper than this in a picked folder are not copied (a provider's loop ends).</summary>
    private const int MaxDepth = 16;
    private const string PartSuffix = ".part";
    private const long Megabyte = 1024 * 1024;
    private const string LogTag = "SkyNET";

    private static readonly Prompt AccessIntro = new("Play from your SkyNET folder?",
        "Copy your SkyNET folder from the PC to this device (the folder holding gamedata, and shock and mods if you have them).\n\n"
        + "With All files access the game plays straight from it and keeps settings, saves and mods there. "
        + "Switch it on for SkyNET on the next screen, then come back.\n\nOr copy the game into the app instead.");
    private static readonly Prompt PickIntro = new("Pick your SkyNET folder",
        "Pick the SkyNET folder on this device's storage (the one holding gamedata). The game reads and writes it in place.\n\n"
        + PickerHint);
    private static readonly Prompt CopyIntro = new("Copy the game",
        "Copy your SkyNET folder to this device, then pick it (the folder holding gamedata, or gamedata itself).\n\n"
        + "It is copied into the app once (about 270 MB). Its mods folder is synced at every start.\n\n" + PickerHint);
    private const string PickerHint = "In the picker, open the folder, tap Use this folder, then Allow.";
    private static readonly Prompt NoData = new("Not a SkyNET folder", "That folder has no SkyNET data (MDMDMAP2.BSA). Pick the SkyNET folder.");
    private static readonly Prompt NoAccess = new("No All files access",
        "All files access is off for SkyNET, so the game cannot play from your folder.");
    private static readonly Prompt NoSpace = new("Not enough space", "Needs {0} MB free on this device. Free some space, then try again.");
    private static readonly Prompt CopyBroke = new(CopyFailed,
        "The game could not be copied (the folder could not be read, or the device is full). Try again.");
    private static readonly Prompt NoPath = new("Not on this device",
        "That folder is not on this device's storage (a cloud, the Downloads list?).\n\nIn the picker's menu, show the internal storage, then pick your folder.");
    private static readonly Prompt NotWritable = new("Folder is read-only", "The game cannot write to that folder (settings, saves, mods). Pick a folder on the internal storage.");
    private static readonly Prompt Lost = new("SkyNET folder not found",
        "The game cannot read {0} any more (moved, deleted, or All files access switched off). Your files there stay there.\n\nPick it again on the next screen.");
    private static readonly Prompt ModsLost = new("Mods not synced",
        "Your mods folder cannot be read any more: the game keeps the last copy. Pick it again in Setup (long-press the game's icon).");
    private static readonly Prompt NoMods = new("No mods folder",
        "The copied SkyNET folder has no mods folder. Choose your mods folder in this menu.");
    private const string CopyFailed = "Copying failed";
    private const string SyncFailed = "Syncing the mods failed";

    /// <summary>The Setup menu's rows.</summary>
    private enum Row { Play, ChangeFolder, ToCopy, ToInPlace, CopyAgain, PickMods, SyncNow }

    private readonly AllFilesAccess _access = new(activity);
    private readonly StorageChoice _choice = new(activity.FilesDir!.AbsolutePath);
    private TaskCompletionSource<AUri?>? _picked;

    /// <summary>The app's external folder: the copy's install folder.</summary>
    private string External => (activity.GetExternalFilesDir(null) ?? activity.FilesDir!).AbsolutePath;

    private string CopyRoot => Path.Combine(External, CopyFolder);

    /// <summary>The game's files, asked for when needed; null when the user quits.</summary>
    public GameFiles? Get(SetupMode mode)
    {
        MoveOldFiles();
        var plan = Plan();
        if (mode == SetupMode.Menu && plan.DataDir != null)
        {
            return Menu();
        }

        return Start(plan);
    }

    // The plan; a chosen folder that is gone is told and forgotten.
    private StoragePlan Plan()
    {
        var plan = _choice.Plan(_access.Current, CopyRoot);
        if (plan.Kind != StorageKind.Lost)
        {
            return plan;
        }

        Dialogs.Tell(activity, Lost with { Text = string.Format(CultureInfo.InvariantCulture, Lost.Text, _choice.Folder) });
        _choice.Forget();
        return _choice.Plan(_access.Current, CopyRoot);
    }

    private GameFiles? Start(StoragePlan plan)
    {
        if (plan.Kind == StorageKind.InPlace)
        {
            return new GameFiles(plan.DataDir!, _choice.Folder!);
        }

        if (plan.Kind == StorageKind.Copied)
        {
            SyncMods(Report.Problems);
            return new GameFiles(plan.DataDir!, External);
        }

        // Declined, refused or backed out of: the copy.
        if (plan.Kind == StorageKind.AskAccess
            && Dialogs.Ask(activity, AccessIntro, "Allow", "Copy instead") == Answer.Yes
            && ChooseInPlace(PickIntro, "Copy instead") is { } root)
        {
            return new GameFiles(GameDataLocator.ResolveDataDir(root)!, root);
        }

        if (PickAndCopy(CopyIntro, "Quit") is not { } data)
        {
            return null;
        }

        SyncMods(Report.Problems);
        return new GameFiles(data, External);
    }

    /// <summary>The Setup shortcut: change the folder, switch in place / copy, sync the mods; until Play.</summary>
    private GameFiles? Menu()
    {
        while (true)
        {
            var plan = Plan();
            if (plan.DataDir == null)
            {
                return Start(plan);
            }

            var rows = Rows(plan.Kind);
            var where = plan.Kind == StorageKind.InPlace ? $"Playing from {_choice.Folder}" : "Playing from the copy in the app";
            var picked = Dialogs.Choose(activity, where, rows.Select(Label).ToArray(), "Quit");
            if (picked < 0)
            {
                return null;
            }

            switch (rows[picked])
            {
                case Row.Play:
                    return Start(plan);
                case Row.ChangeFolder:
                    MoveTo(_choice.Folder!);
                    break;
                case Row.ToInPlace:
                    MoveTo(External);
                    break;
                case Row.ToCopy:
                    ToCopy();
                    break;
                case Row.CopyAgain:
                    PickAndCopy(CopyIntro, "Cancel");
                    break;
                case Row.PickMods:
                    PickMods();
                    break;
                case Row.SyncNow:
                    SyncMods(Report.Always);
                    break;
            }
        }
    }

    private Row[] Rows(StorageKind kind)
    {
        if (kind == StorageKind.InPlace)
        {
            return [Row.Play, Row.ChangeFolder, Row.ToCopy];
        }

        return _access.Current == FolderAccess.Unavailable
            ? [Row.Play, Row.SyncNow, Row.PickMods, Row.CopyAgain]
            : [Row.Play, Row.SyncNow, Row.PickMods, Row.ToInPlace, Row.CopyAgain];
    }

    private static string Label(Row row) => row switch
    {
        Row.Play => "Play",
        Row.ChangeFolder => "Choose another SkyNET folder",
        Row.ToCopy => "Copy the game into the app instead",
        Row.ToInPlace => "Play from your SkyNET folder in place",
        Row.CopyAgain => "Copy the game again",
        Row.PickMods => "Choose your mods folder",
        _ => "Sync mods now",
    };

    // A folder to play from in place; settings and saves follow from the current place.
    private void MoveTo(string current)
    {
        if (ChooseInPlace(PickIntro, "Cancel") is not { } chosen || StorageMigration.SameFolder(chosen, current))
        {
            return;
        }

        StorageMigration.CopyUserFiles(current, chosen);
    }

    // Back to the copy: settings and saves follow; no copy yet: pick and copy.
    private void ToCopy()
    {
        var from = _choice.Folder!;
        if (StorageChoice.FindCopy(CopyRoot) == null && PickAndCopy(CopyIntro, "Cancel") == null)
        {
            return;
        }

        _choice.Forget();
        StorageMigration.CopyUserFiles(from, External);
    }

    /// <summary>All files access, then the SkyNET folder until one has the data and can be written
    /// (remembered: its path) or the user refuses (null).</summary>
    private string? ChooseInPlace(Prompt intro, string no)
    {
        // Back without the access: try again, or leave (copy instead, cancel).
        while (!_access.Request())
        {
            if (Dialogs.Ask(activity, NoAccess, "Try again", no) == Answer.No)
            {
                return null;
            }
        }

        var primary = TreePaths.PrimaryRoot(External) ?? DefaultPrimary;
        var message = intro;
        while (Dialogs.Ask(activity, message, "Pick folder", no) == Answer.Yes)
        {
            if (Pick() is not { } uri)
            {
                message = intro;
                continue;
            }

            if (PathOf(uri, primary) is not { } path)
            {
                message = NoPath;
                continue;
            }

            if (GameDataLocator.ResolveDataDir(path) == null)
            {
                message = NoData;
                continue;
            }

            if (!StorageChoice.IsWritable(path))
            {
                message = NotWritable;
                continue;
            }

            _choice.Choose(path);
            return path;
        }

        return null;
    }

    private static string? PathOf(AUri tree, string primary)
    {
        if (tree.Authority is not { } authority || DocumentsContract.GetTreeDocumentId(tree) is not { } document)
        {
            return null;
        }

        return TreePaths.Resolve(authority, document, primary);
    }

    /// <summary>Asks for the SkyNET folder until one is copied (its data folder) or the user refuses (null).</summary>
    private string? PickAndCopy(Prompt intro, string no)
    {
        var message = intro;
        while (Dialogs.Ask(activity, message, "Pick folder", no) == Answer.Yes)
        {
            if (Pick() is not { } tree)
            {
                message = intro;
                continue;
            }

            if (!HasData(tree))
            {
                message = NoData;
                continue;
            }

            try
            {
                Keep(tree, DataTreeKey);
                if (CopyAll(tree) is { } problem)
                {
                    message = problem;
                    continue;
                }

                if (StorageChoice.FindCopy(CopyRoot) is { } data)
                {
                    return data;
                }

                message = NoData;
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException or Java.Lang.Exception)
            {
                // The details go to the log (logcat), the user gets a plain message.
                global::Android.Util.Log.Warn(LogTag, e.ToString());
                message = CopyBroke;
            }
        }

        return null;
    }

    // The probe file in the picked folder, or in its gamedata/GAMEDATA.
    private bool HasData(AUri tree)
    {
        var top = Children(tree, DocumentsContract.GetTreeDocumentId(tree)!);
        if (top.Any(c => c.Name.Equals(GameDataLocator.ProbeSkyNet, StringComparison.OrdinalIgnoreCase)))
        {
            return true;
        }

        return top.Where(c => c.Folder && c.Name.Equals(CopyFolder, StringComparison.OrdinalIgnoreCase))
            .Any(c => Children(tree, c.Id).Any(f => f.Name.Equals(GameDataLocator.ProbeSkyNet, StringComparison.OrdinalIgnoreCase)));
    }

    private readonly record struct Child(string Id, string Name, bool Folder, long Size);

    private List<Child> Children(AUri tree, string documentId)
    {
        string[] columns =
        [
            DocumentsContract.Document.ColumnDocumentId, DocumentsContract.Document.ColumnDisplayName,
            DocumentsContract.Document.ColumnMimeType, DocumentsContract.Document.ColumnSize,
        ];
        var children = new List<Child>();
        var uri = DocumentsContract.BuildChildDocumentsUriUsingTree(tree, documentId)!;
        using var cursor = activity.ContentResolver!.Query(uri, columns, null, null, null);
        while (cursor != null && cursor.MoveToNext())
        {
            var folder = cursor.GetString(2) == DocumentsContract.Document.MimeTypeDir;
            children.Add(new Child(cursor.GetString(0)!, cursor.GetString(1)!, folder, cursor.IsNull(3) ? -1 : cursor.GetLong(3)));
        }

        return children;
    }

    /// <summary>A file of the picked folder to copy: its document, name, target path and size (-1 unknown).</summary>
    private readonly record struct Pending(string Id, string Name, string Target, long Size);

    // Copies the picked folder (its mods/ aside: the sync's) into the copy folder: lists it first,
    // so the space is checked and the progress counts ("120 of 401"). Returns a problem, or null.
    private Prompt? CopyAll(AUri tree)
    {
        var progress = Dialogs.Show(activity, "Copying the game", "Reading the folder…");
        try
        {
            var files = new List<Pending>();
            ListCopy(tree, DocumentsContract.GetTreeDocumentId(tree)!, CopyRoot, files, new HashSet<string>(StringComparer.Ordinal), 0);

            Directory.CreateDirectory(CopyRoot);
            var needed = files.Sum(f => Math.Max(f.Size, 0));
            if (needed > new global::Android.OS.StatFs(CopyRoot).AvailableBytes)
            {
                var mb = (needed + Megabyte - 1) / Megabyte;
                return NoSpace with { Text = string.Format(CultureInfo.InvariantCulture, NoSpace.Text, mb) };
            }

            for (var i = 0; i < files.Count; i++)
            {
                progress.Update(files[i].Name, i + 1, files.Count);
                CopyFile(tree, files[i]);
            }

            return null;
        }
        finally
        {
            progress.Close();
        }
    }

    // The files to copy, folder by folder. A file already there with the same size is skipped:
    // copying again only takes what changed. Names that would leave the copy folder ("..", "a/b"),
    // folders seen before and folders too deep (a provider's loop) are skipped.
    private void ListCopy(AUri tree, string documentId, string to, List<Pending> into, HashSet<string> seen, int depth)
    {
        if (depth > MaxDepth || !seen.Add(documentId))
        {
            return;
        }

        foreach (var child in Children(tree, documentId))
        {
            if (TreePaths.Child(to, child.Name) is not { } target)
            {
                continue;
            }

            if (child.Folder)
            {
                if (depth == 0 && child.Name.Equals(ModsFolder, StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                ListCopy(tree, child.Id, target, into, seen, depth + 1);
                continue;
            }

            if (File.Exists(target) && new FileInfo(target).Length == child.Size)
            {
                continue;
            }

            into.Add(new Pending(child.Id, child.Name, target, child.Size));
        }
    }

    // Through a temporary file: a copy cut short never leaves half a file that looks whole.
    private void CopyFile(AUri tree, Pending file)
    {
        var part = file.Target + PartSuffix;
        Directory.CreateDirectory(Path.GetDirectoryName(file.Target)!);
        using (var from = activity.ContentResolver!.OpenInputStream(DocumentsContract.BuildDocumentUriUsingTree(tree, file.Id)!)
            ?? throw new IOException($"{file.Name} does not open"))
        using (var to = File.Create(part))
        {
            from.CopyTo(to);
        }

        File.Move(part, file.Target, overwrite: true);
    }

    /// <summary>When a sync tells the user how it went.</summary>
    private enum Report { Problems, Always }

    // Copy mode: the mods folder picked apart, else the copied folder's mods/, into the app's mods/.
    private void SyncMods(Report report)
    {
        ProgressBox? box = null;
        var sync = new ModSync(activity, Progress);
        if (Source(sync) is not { } source)
        {
            if (report == Report.Always)
            {
                Dialogs.Tell(activity, NoMods);
            }

            return;
        }

        var outcome = sync.Run(source.Tree, source.Id, Path.Combine(External, ModsFolder));
        box?.Close();

        if (outcome == SyncOutcome.Done && report == Report.Problems)
        {
            return;
        }

        Dialogs.Tell(activity, outcome switch
        {
            SyncOutcome.Done => new Prompt("Mods synced", "Your mods are up to date. OPTIONS > MODS turns them on."),
            SyncOutcome.NoAccess => ModsLost,
            _ => new Prompt(SyncFailed, $"{sync.Error}\n\nThe game keeps the last copy."),
        });

        // The progress shows once a file is copied: a start with nothing new shows nothing.
        void Progress(string file, int done, int total)
        {
            box ??= Dialogs.Show(activity, "Syncing your mods", file);
            box.Update(file, done, total);
        }
    }

    private (AUri Tree, string Id)? Source(ModSync sync)
    {
        if (Tree(ModsTreeKey) is { } mods)
        {
            return (mods, DocumentsContract.GetTreeDocumentId(mods)!);
        }

        if (Tree(DataTreeKey) is { } data && sync.Folder(data, ModsFolder) is { } id)
        {
            return (data, id);
        }

        return null;
    }

    private void PickMods()
    {
        if (Pick() is not { } tree)
        {
            return;
        }

        Keep(tree, ModsTreeKey);
        SyncMods(Report.Always);
    }

    /// <summary>An older build kept the mods (baked maps among them) in the internal folder, the
    /// settings and saves where the game chose: they move to the copy's install folder, once.</summary>
    private void MoveOldFiles()
    {
        var files = activity.FilesDir!.AbsolutePath;
        var marker = Path.Combine(files, MigratedFile);
        if (File.Exists(marker))
        {
            return;
        }

        var oldMods = Path.Combine(files, ModsFolder);
        var box = Directory.Exists(oldMods) ? Dialogs.Show(activity, "Moving your files", "Mods and baked maps…") : null;
        try
        {
            Directory.CreateDirectory(External);
            StorageMigration.MoveTree(oldMods, Path.Combine(External, ModsFolder));
            var manifest = Path.Combine(files, OldManifest);
            if (File.Exists(manifest))
            {
                File.Move(manifest, Path.Combine(External, OldManifest), overwrite: true);
            }

            // Where the older build's game kept settings and saves: its own folder when writable, else app data.
            var game = AppContext.BaseDirectory;
            StorageMigration.CopyUserFiles(StorageChoice.IsWritable(game) ? game : SettingsStore.AppDataFolder, External);
            File.WriteAllText(marker, "");
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            global::Android.Util.Log.Warn(LogTag, e.ToString());
            Dialogs.Tell(activity, new Prompt("Moving your files failed", "The game tries again at the next start."));
        }
        finally
        {
            box?.Close();
        }
    }

    /// <summary>The folder picker's or the access screen's answer (the activity passes them on).
    /// Returns whether it was ours.</summary>
    public bool OnResult(int request, Result result, Intent? data)
    {
        if (_access.OnAnswer(request))
        {
            return true;
        }

        if (request != PickRequest)
        {
            return false;
        }

        _picked?.TrySetResult(result == Result.Ok ? data?.Data : null);
        return true;
    }

    /// <summary>The storage permission's answer. Returns whether it was ours.</summary>
    public bool OnPermissions(int request) => _access.OnAnswer(request);

    // Android's folder picker; null when the user backs out.
    private AUri? Pick()
    {
        _picked = new TaskCompletionSource<AUri?>();
        var intent = new Intent(Intent.ActionOpenDocumentTree)
            .AddFlags(ActivityFlags.GrantReadUriPermission | ActivityFlags.GrantPersistableUriPermission);
        activity.RunOnUiThread(() => activity.StartActivityForResult(intent, PickRequest));
        return _picked.Task.Result;
    }

    // A picked folder, kept: its read permission survives restarts, its address is saved.
    private void Keep(AUri tree, string key)
    {
        try
        {
            activity.ContentResolver!.TakePersistableUriPermission(tree, ActivityFlags.GrantReadUriPermission);
        }
        catch (Java.Lang.SecurityException)
        {
        }

        activity.GetSharedPreferences(Prefs, FileCreationMode.Private)!.Edit()!.PutString(key, tree.ToString())!.Apply();
    }

    private AUri? Tree(string key) =>
        activity.GetSharedPreferences(Prefs, FileCreationMode.Private)!.GetString(key, null) is { } s ? AUri.Parse(s) : null;

    /// <summary>The logs' folder in a user folder.</summary>
    public static string Logs(string userRoot) => Path.Combine(userRoot, LogsFolder);

    /// <summary>The mods' folder in a user folder.</summary>
    public static string Mods(string userRoot) => Path.Combine(userRoot, ModsFolder);
}
