using Android.App;
using Android.Content;
using Android.Content.PM;
using Android.OS;

namespace SkyNet.Android;

/// <summary>The launcher: the setup screen (<see cref="Backdrop"/> with <see cref="Dialogs"/>
/// beside it) while <see cref="DataSetup"/> finds the game's files on a worker thread, then the
/// game. A normal start with everything in place only syncs the mods (copy mode) and goes on. The
/// launcher icon's Setup shortcut (long-press) opens the setup menu: the folder, in place or copy,
/// the mods.</summary>
[Activity(Label = "@string/app_name", Icon = "@mipmap/ic_launcher", RoundIcon = "@mipmap/ic_launcher", MainLauncher = true, Theme = "@android:style/Theme.Material.NoActionBar.Fullscreen",
    ScreenOrientation = ScreenOrientation.SensorLandscape, Exported = true,
    ConfigurationChanges = ConfigChanges.Orientation | ConfigChanges.ScreenSize | ConfigChanges.ScreenLayout | ConfigChanges.Keyboard
        | ConfigChanges.KeyboardHidden | ConfigChanges.Navigation | ConfigChanges.UiMode | ConfigChanges.SmallestScreenSize | ConfigChanges.Density)]
public sealed class SetupActivity : Activity
{
    private const string SetupExtra = "setup";
    private const string SetupShortcut = "setup";
    private const string LogTag = "SkyNET";
    private static readonly Prompt SetupFailed = new("Setup failed",
        "Something went wrong. Open Setup (long-press the game's icon) and try again.");

    private DataSetup? _setup;

    protected override void OnCreate(Bundle? savedInstanceState)
    {
        base.OnCreate(savedInstanceState);
        AddShortcut();
        SetContentView(new Backdrop(this));

        var mode = Intent?.GetBooleanExtra(SetupExtra, false) == true ? SetupMode.Menu : SetupMode.Start;
        var setup = new DataSetup(this);
        _setup = setup;
        Task.Run(() => Run(setup, mode));
    }

    // The worker thread: the files, then the game (or quit).
    private void Run(DataSetup setup, SetupMode mode)
    {
        GameFiles? files;
        try
        {
            files = setup.Get(mode);
        }
        catch (Exception e)
        {
            // The details go to the log (logcat), the user gets a plain message.
            global::Android.Util.Log.Error(LogTag, e.ToString());
            Dialogs.Tell(this, SetupFailed);
            files = null;
        }

        RunOnUiThread(() =>
        {
            if (files != null)
            {
                StartActivity(GameActivity.For(this, files));
            }

            Finish();
        });
    }

    // The launcher icon's long-press Setup: the setup menu.
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
        if (_setup?.OnResult(requestCode, resultCode, data) == true)
        {
            return;
        }

        base.OnActivityResult(requestCode, resultCode, data);
    }

    public override void OnRequestPermissionsResult(int requestCode, string[] permissions, Permission[] grantResults)
    {
        base.OnRequestPermissionsResult(requestCode, permissions, grantResults);
        _setup?.OnPermissions(requestCode);
    }
}
