using Android.App;
using Android.Content;
using Android.Content.PM;
using Android.Net.Wifi;
using Android.Views;
using Org.Libsdl.App;

// Multiplayer: sockets, and the LAN game list's broadcasts (received only under a multicast lock).
[assembly: UsesPermission(global::Android.Manifest.Permission.Internet)]
[assembly: UsesPermission(global::Android.Manifest.Permission.AccessWifiState)]
[assembly: UsesPermission(global::Android.Manifest.Permission.ChangeWifiMulticastState)]

namespace SkyNet.Android;

/// <summary>The game: SDL's activity (window, input, audio, the Vulkan surface) running SkyNet.App's
/// Program on SDL's main thread with the files <see cref="DataSetup"/> found. The user folder plays
/// the desktop install folder's part: settings, saves, mods/ (baked maps too) and logs/ there.</summary>
[Activity(Label = "@string/app_name", Icon = "@mipmap/ic_launcher", RoundIcon = "@mipmap/ic_launcher", Theme = "@android:style/Theme.Material.NoActionBar.Fullscreen",
    ScreenOrientation = ScreenOrientation.SensorLandscape, LaunchMode = LaunchMode.SingleInstance, Exported = false,
    ConfigurationChanges = ConfigChanges.Orientation | ConfigChanges.ScreenSize | ConfigChanges.ScreenLayout | ConfigChanges.Keyboard
        | ConfigChanges.KeyboardHidden | ConfigChanges.Navigation | ConfigChanges.UiMode | ConfigChanges.SmallestScreenSize | ConfigChanges.Density)]
public sealed class GameActivity : SDLActivity
{
    private const string MulticastTag = "SkyNET LAN games";
    private const string DataExtra = "data";
    private const string RootExtra = "root";
    /// <summary>.NET's override of AppContext.BaseDirectory: the game keeps its user files there
    /// when it can write it (SettingsStore.UserFolder), as beside the exe on the desktop.</summary>
    private const string BaseDirectoryKey = "APP_CONTEXT_BASE_DIRECTORY";

    // Held while the game runs: without it many phones drop the LAN list's broadcasts.
    private WifiManager.MulticastLock? _multicast;

    // Setup's backdrop over SDL's surface until the game's first frame: the start-up was ~4 s of black.
    private Backdrop? _backdrop;

    protected override string[] GetLibraries() => ["SDL3"];

    /// <summary>The intent that plays <paramref name="files"/>.</summary>
    internal static Intent For(Context context, GameFiles files) =>
        new Intent(context, typeof(GameActivity)).PutExtra(DataExtra, files.DataDir)!.PutExtra(RootExtra, files.UserRoot)!;

    protected override void OnCreate(global::Android.OS.Bundle? savedInstanceState)
    {
        base.OnCreate(savedInstanceState);

        // The screen stays on while the game shows (SDL sets it too, until a screensaver is allowed):
        // a phone in a headset gets no touches to keep it awake.
        Window?.AddFlags(WindowManagerFlags.KeepScreenOn);

        _multicast = (GetSystemService(WifiService) as WifiManager)?.CreateMulticastLock(MulticastTag);
        _multicast?.Acquire();

        _backdrop = new Backdrop(this);
        var full = ViewGroup.LayoutParams.MatchParent;
        AddContentView(_backdrop, new ViewGroup.LayoutParams(full, full));
    }

    // The game drew: the backdrop goes (UI thread).
    private void HideBackdrop() => RunOnUiThread(() =>
    {
        if (_backdrop?.Parent is not ViewGroup parent)
        {
            return;
        }

        parent.RemoveView(_backdrop);
        _backdrop = null;
    });

    protected override void OnDestroy()
    {
        if (_multicast is { IsHeld: true })
        {
            _multicast.Release();
        }

        base.OnDestroy();
    }

    protected override void Main()
    {
        // Started by SetupActivity, which always passes both.
        var root = Intent?.GetStringExtra(RootExtra) ?? (GetExternalFilesDir(null) ?? FilesDir!).AbsolutePath;
        var data = Intent?.GetStringExtra(DataExtra) ?? root;
        Environment.CurrentDirectory = root;
        AppContext.SetData(BaseDirectoryKey, root + Path.DirectorySeparatorChar);
        string[] args = ["--data", data, "--mods-dir", DataSetup.Mods(root), "--log-dir", DataSetup.Logs(root)];
        PlatformHooks.WindowBrightness = SetBrightness;
        PlatformHooks.FirstFrame = HideBackdrop;
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
