using Android.App;
using Android.Content.PM;
using Android.Views;
using Org.Libsdl.App;

namespace SkyNet.Android;

/// <summary>The game: SDL's activity (window, input, audio, the Vulkan surface) running SkyNet.App's
/// Program on SDL's main thread with the data <see cref="SetupActivity"/> found. The baked maps go
/// to the app's internal files folder (mods/); the settings, saves and logs to the app-data folder
/// the game picks where its own folder cannot be written.</summary>
[Activity(Label = "@string/app_name", Icon = "@mipmap/ic_launcher", RoundIcon = "@mipmap/ic_launcher", Theme = "@android:style/Theme.Material.NoActionBar.Fullscreen",
    ScreenOrientation = ScreenOrientation.SensorLandscape, LaunchMode = LaunchMode.SingleInstance, Exported = false,
    ConfigurationChanges = ConfigChanges.Orientation | ConfigChanges.ScreenSize | ConfigChanges.ScreenLayout | ConfigChanges.Keyboard
        | ConfigChanges.KeyboardHidden | ConfigChanges.Navigation | ConfigChanges.UiMode | ConfigChanges.SmallestScreenSize | ConfigChanges.Density)]
public sealed class GameActivity : SDLActivity
{
    protected override string[] GetLibraries() => ["SDL3"];

    protected override void OnCreate(global::Android.OS.Bundle? savedInstanceState)
    {
        base.OnCreate(savedInstanceState);

        // The screen stays on while the game shows (SDL sets it too, until a screensaver is allowed):
        // a phone in a headset gets no touches to keep it awake.
        Window?.AddFlags(WindowManagerFlags.KeepScreenOn);
    }

    protected override void Main()
    {
        var files = FilesDir!.AbsolutePath;
        Environment.CurrentDirectory = files;
        var data = SetupActivity.FindData(SetupActivity.DataRoot(this)) ?? SetupActivity.DataRoot(this);
        // The logs where the player can read them (Android/data/<app>/files/logs over USB).
        var logs = Path.Combine(GetExternalFilesDir(null)?.AbsolutePath ?? files, "logs");
        string[] args = ["--data", data, "--mods-dir", SetupActivity.ModsRoot(this), "--log-dir", logs];
        PlatformHooks.WindowBrightness = SetBrightness;
        GameMain.Run(args);
    }

    /// <summary>The window's own brightness (0..1; -1: the system's, adaptive included), on the UI
    /// thread. Needs no permission and leaves the system setting alone.</summary>
    private void SetBrightness(float level) => RunOnUiThread(() =>
    {
        if (Window is not { Attributes: { } attributes } window)
        {
            return;
        }

        attributes.ScreenBrightness = level;
        window.Attributes = attributes;
    });
}
