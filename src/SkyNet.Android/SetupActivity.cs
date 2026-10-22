using Android.App;
using Android.Content;
using Android.Content.PM;
using Android.OS;
using Android.Provider;
using Android.Views;
using Android.Widget;
using AUri = Android.Net.Uri;

namespace SkyNet.Android;

/// <summary>The launcher: finds the game's data (the DOS SKYNET folder, MDMDMAP2.BSA and the rest)
/// in the app's external files folder, <c>Android/data/&lt;package&gt;/files/gamedata</c>, and
/// starts the game. Without it, the player picks the folder holding the data (Android's folder
/// picker, SAF) and it is copied there once; <c>adb push gamedata/. &lt;that folder&gt;</c> works
/// too. The settings, saves, logs and baked maps live in the app's internal storage.
/// <para>Then, optionally, the player's mods folder (picked the same way): mirrored into the game's
/// mods folder at every start (<see cref="ModSync"/>). Both picks keep a persistable permission. The
/// launcher icon's Setup shortcut (long-press) opens this screen: the folders and SYNC MODS NOW.</para></summary>
[Activity(Label = "@string/app_name", Icon = "@mipmap/ic_launcher", RoundIcon = "@mipmap/ic_launcher", MainLauncher = true, Theme = "@android:style/Theme.Material.NoActionBar.Fullscreen",
    ScreenOrientation = ScreenOrientation.SensorLandscape, Exported = true)]
public sealed class SetupActivity : Activity
{
    private const int PickFolder = 1;
    private const int PickMods = 2;
    private const string Prefs = "setup";
    private const string DataTreeKey = "dataTree";
    private const string ModsTreeKey = "modsTree";
    private const string SetupExtra = "setup";
    private const string SetupShortcut = "setup";
    private TextView _text = null!;
    private Button _pick = null!;
    private Button _mods = null!;
    private Button _sync = null!;
    private Button _play = null!;

    /// <summary>Whether the buttons can be used: not while a copy or a sync runs.</summary>
    private enum Buttons { Off, Ready }

    /// <summary>Where a mods sync leads: into the game, or back to this screen.</summary>
    private enum AfterSync { Play, Setup }

    /// <summary>Where the game's data is kept on the device.</summary>
    public static string DataRoot(Context context) =>
        Path.Combine((context.GetExternalFilesDir(null) ?? context.FilesDir!).AbsolutePath, "gamedata");

    /// <summary>The game's mods folder: the user mods mirrored from the picked folder and the system
    /// mods' baked maps (skynet.zip, shock.zip), which the mirror never touches.</summary>
    internal static string ModsRoot(Context context) => Path.Combine(context.FilesDir!.AbsolutePath, "mods");

    /// <summary>The folder under <paramref name="root"/> holding MDMDMAP2.BSA, or null.</summary>
    public static string? FindData(string root)
    {
        try
        {
            return Directory.EnumerateFiles(root, "*", new EnumerationOptions { RecurseSubdirectories = true, IgnoreInaccessible = true })
                .Where(f => Path.GetFileName(f).Equals("MDMDMAP2.BSA", StringComparison.OrdinalIgnoreCase))
                .Select(Path.GetDirectoryName)
                .FirstOrDefault();
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    protected override void OnCreate(Bundle? savedInstanceState)
    {
        base.OnCreate(savedInstanceState);
        // The app makes the folder: one adb made is not the app's to read.
        try
        {
            Directory.CreateDirectory(DataRoot(this));
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
        }

        AddShortcut();
        if (FindData(DataRoot(this)) == null)
        {
            ShowSetup("SkyNET needs the data of the DOS game (the folder holding MDMDMAP2.BSA).\n\n"
                + "Pick that folder (copied once, about 270 MB), or copy it with a cable or adb to\n"
                + DataRoot(this) + "\nand start again.");
            return;
        }

        if (Intent?.GetBooleanExtra(SetupExtra, false) == true)
        {
            ShowSetup(Status());
            return;
        }

        // A normal start: sync the mods (when a folder was picked), then play.
        if (Tree(ModsTreeKey) is not { } mods)
        {
            StartGame();
            return;
        }

        _text = new TextView(this) { TextSize = 18 };
        _text.SetPadding(48, 48, 48, 48);
        SetContentView(_text);
        Sync(mods, AfterSync.Play);
    }

    // The screen: what is set up, and the buttons to change it.
    private void ShowSetup(string message)
    {
        var layout = new LinearLayout(this) { Orientation = Orientation.Vertical };
        layout.SetPadding(48, 48, 48, 48);
        layout.SetGravity(GravityFlags.Center);
        _text = new TextView(this) { TextSize = 18, Text = message };
        _pick = new Button(this) { Text = "Choose the game's folder" };
        _pick.Click += (_, _) => Pick(PickFolder);
        _mods = new Button(this) { Text = "Choose your mods folder (optional)" };
        _mods.Click += (_, _) => Pick(PickMods);
        _sync = new Button(this) { Text = "Sync mods now" };
        _sync.Click += (_, _) => Sync(Tree(ModsTreeKey)!, AfterSync.Setup);
        _play = new Button(this) { Text = "Play" };
        _play.Click += (_, _) => StartGame();
        layout.AddView(_text);
        layout.AddView(_pick);
        layout.AddView(_mods);
        layout.AddView(_sync);
        layout.AddView(_play);

        var scroll = new ScrollView(this);
        scroll.AddView(layout);
        SetContentView(scroll);
        SetButtons(Buttons.Ready);
    }

    // What is set up, for the screen.
    private string Status()
    {
        var mods = Tree(ModsTreeKey) is { } tree ? DocumentsContract.GetTreeDocumentId(tree) : "none (skip it to play without)";
        return "The game's data is in place.\n\n"
            + $"Your mods folder: {mods}\n"
            + "It is copied into the game's mods and synced at every start; OPTIONS > MODS turns them on.";
    }

    // The data and mods buttons wait for the data; SYNC for a picked mods folder.
    private void SetButtons(Buttons state)
    {
        var ready = state == Buttons.Ready;
        var data = ready && FindData(DataRoot(this)) != null;
        _pick.Enabled = ready;
        _play.Enabled = data;
        _mods.Enabled = data;
        _sync.Enabled = data && Tree(ModsTreeKey) != null;
    }

    private void Pick(int request)
    {
        var intent = new Intent(Intent.ActionOpenDocumentTree)
            .AddFlags(ActivityFlags.GrantReadUriPermission | ActivityFlags.GrantPersistableUriPermission);
        StartActivityForResult(intent, request);
    }

    // A picked folder, kept: its read permission survives restarts, its address is saved.
    private void Keep(AUri tree, string key)
    {
        try
        {
            ContentResolver!.TakePersistableUriPermission(tree, ActivityFlags.GrantReadUriPermission);
        }
        catch (Java.Lang.SecurityException)
        {
        }

        GetSharedPreferences(Prefs, FileCreationMode.Private)!.Edit()!.PutString(key, tree.ToString())!.Apply();
    }

    private AUri? Tree(string key) =>
        GetSharedPreferences(Prefs, FileCreationMode.Private)!.GetString(key, null) is { } s ? AUri.Parse(s) : null;

    // Mirrors the mods folder off the UI thread, the progress on the screen. A folder that cannot
    // be read leaves the last copy in place, and says so.
    private void Sync(AUri tree, AfterSync after)
    {
        if (after == AfterSync.Setup)
        {
            SetButtons(Buttons.Off);
        }

        Task.Run(() =>
        {
            var sync = new ModSync(this, s => RunOnUiThread(() => _text.Text = s));
            var outcome = sync.Run(tree, ModsRoot(this));
            var message = outcome switch
            {
                SyncOutcome.Done => "Your mods are synced.",
                SyncOutcome.NoAccess => "Your mods folder cannot be read any more: the game keeps the last copy. "
                    + "Choose it again in Setup (long-press the game's icon).",
                _ => $"Syncing the mods failed ({sync.Error}): the game keeps the last copy.",
            };
            RunOnUiThread(() => Synced(outcome, message, after));
        });
    }

    private void Synced(SyncOutcome outcome, string message, AfterSync after)
    {
        if (after == AfterSync.Setup)
        {
            ShowSetup(message + "\n\n" + Status());
            return;
        }

        if (outcome != SyncOutcome.Done)
        {
            Toast.MakeText(this, message, ToastLength.Long)!.Show();
        }

        StartGame();
    }

    // The launcher icon's long-press Setup: this screen with the folders and SYNC MODS NOW.
    private void AddShortcut()
    {
        try
        {
            var manager = (ShortcutManager?)GetSystemService(Java.Lang.Class.FromType(typeof(ShortcutManager)));
            var intent = new Intent(this, typeof(SetupActivity)).SetAction(Intent.ActionMain)!.PutExtra(SetupExtra, true)!;
            var shortcut = new ShortcutInfo.Builder(this, SetupShortcut).SetShortLabel("Setup")!
                .SetLongLabel("Game folder and mods")!.SetIntent(intent)!.Build()!;
            manager?.SetDynamicShortcuts([shortcut]);
        }
        catch (Java.Lang.Exception)
        {
        }
    }

    protected override void OnActivityResult(int requestCode, Result resultCode, Intent? data)
    {
        base.OnActivityResult(requestCode, resultCode, data);
        if (resultCode != Result.Ok || data?.Data is not { } tree)
        {
            return;
        }

        if (requestCode == PickMods)
        {
            Keep(tree, ModsTreeKey);
            Sync(tree, AfterSync.Setup);
            return;
        }

        if (requestCode != PickFolder)
        {
            return;
        }

        Keep(tree, DataTreeKey);
        SetButtons(Buttons.Off);
        var root = DataRoot(this);
        Task.Run(() =>
        {
            try
            {
                var copied = 0;
                Copy(tree, DocumentsContract.GetTreeDocumentId(tree)!, root, ref copied);
                RunOnUiThread(() =>
                {
                    if (FindData(root) != null)
                    {
                        ShowSetup("The game's data is copied. Choose your mods folder (optional), or play.\n\n" + Status());
                        return;
                    }

                    _text.Text = "That folder has no MDMDMAP2.BSA: pick the folder of the DOS game.";
                    SetButtons(Buttons.Ready);
                });
            }
            catch (Exception e)
            {
                RunOnUiThread(() =>
                {
                    _text.Text = $"Copying failed: {e.Message}";
                    SetButtons(Buttons.Ready);
                });
            }
        });
    }

    /// <summary>Copies a picked folder's documents (and its folders) into <paramref name="to"/>.</summary>
    private void Copy(AUri tree, string documentId, string to, ref int copied)
    {
        Directory.CreateDirectory(to);
        var children = DocumentsContract.BuildChildDocumentsUriUsingTree(tree, documentId)!;
        string[] columns = [DocumentsContract.Document.ColumnDocumentId, DocumentsContract.Document.ColumnDisplayName, DocumentsContract.Document.ColumnMimeType];
        using var cursor = ContentResolver!.Query(children, columns, null, null, null);
        while (cursor != null && cursor.MoveToNext())
        {
            var id = cursor.GetString(0)!;
            var name = cursor.GetString(1)!;
            var target = Path.Combine(to, name);
            if (cursor.GetString(2) == DocumentsContract.Document.MimeTypeDir)
            {
                Copy(tree, id, target, ref copied);
                continue;
            }

            using (var from = ContentResolver.OpenInputStream(DocumentsContract.BuildDocumentUriUsingTree(tree, id)!)!)
            using (var file = File.Create(target))
            {
                from.CopyTo(file);
            }

            var n = ++copied;
            RunOnUiThread(() => _text.Text = $"Copying the game's data… {n} files ({name})");
        }
    }

    private void StartGame()
    {
        StartActivity(new Intent(this, typeof(GameActivity)));
        Finish();
    }
}
