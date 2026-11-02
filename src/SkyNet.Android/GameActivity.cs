using System.Runtime.InteropServices;
using Android.Content;
using Android.Runtime;

// Multiplayer: sockets, and the LAN game list's broadcasts (received only under a multicast lock).
[assembly: UsesPermission(global::Android.Manifest.Permission.Internet)]
[assembly: UsesPermission(global::Android.Manifest.Permission.AccessWifiState)]
[assembly: UsesPermission(global::Android.Manifest.Permission.ChangeWifiMulticastState)]

namespace SkyNet.Android;

/// <summary>The game: SDL's activity (Java/GameActivity.java: window, input, audio, the Vulkan
/// surface) runs <see cref="SdlMain"/> on SDL's main thread with the files <see cref="DataSetup"/>
/// found. The user folder plays the desktop install folder's part: settings, saves, mods/ (baked
/// maps too) and logs/ there.</summary>
internal static class GameActivity
{
    private const string ClassName = "net.skynetport.skynet.GameActivity";
    private const string JniClassName = "net/skynetport/skynet/GameActivity";
    private const string DataExtra = "data";
    private const string RootExtra = "root";
    private const string ArgsFile = "args.txt";
    /// <summary>SDL's argv: the program name, the user root, the data folder.</summary>
    private const int SdlArgs = 3;
    /// <summary>.NET's override of AppContext.BaseDirectory: the game keeps its user files there
    /// when it can write it (SettingsStore.UserFolder), as beside the exe on the desktop.</summary>
    private const string BaseDirectoryKey = "APP_CONTEXT_BASE_DIRECTORY";

    /// <summary>The intent that plays <paramref name="files"/>.</summary>
    internal static Intent For(Context context, GameFiles files) =>
        new Intent().SetClassName(context, ClassName).PutExtra(DataExtra, files.DataDir)!.PutExtra(RootExtra, files.UserRoot)!;

    /// <summary>SDL's entry: argv = program name, user root, data folder (GameActivity.java's
    /// getArguments after SDL's placeholder).</summary>
    [UnmanagedCallersOnly(EntryPoint = "SDL_main")]
    private static unsafe int SdlMain(int argc, byte** argv)
    {
        // An exception here aborts the process without a log: a missing argument quits instead.
        if (argc < SdlArgs)
        {
            return 1;
        }

        var given = Enumerable.Range(0, argc).Select(i => Marshal.PtrToStringUTF8((nint)argv[i]) ?? "").ToArray();
        var root = given[^2];
        var data = given[^1];
        Environment.CurrentDirectory = root;
        AppContext.SetData(BaseDirectoryKey, root + Path.DirectorySeparatorChar);
        string[] args = ["--data", data, "--mods-dir", DataSetup.Mods(root), "--log-dir", DataSetup.Logs(root), .. ExtraArgs(root)];
        PlatformHooks.WindowBrightness = SetBrightness;
        return GameMain.Run(args);
    }

    /// <summary>The desktop's command line for a run on the phone (a demo to profile, a map), one
    /// argument per line in args.txt in the user folder, e.g. <c>--timedemo</c> / <c>bench-m1</c>.
    /// Only adb and the user reach that folder.</summary>
    private static string[] ExtraArgs(string root)
    {
        var file = Path.Combine(root, ArgsFile);
        if (!File.Exists(file))
        {
            return [];
        }

        return File.ReadAllLines(file).Select(l => l.Trim()).Where(l => l.Length > 0).ToArray();
    }

    /// <summary>The window's own brightness (0..1; -1: the system's, adaptive included), set by
    /// GameActivity.java on the UI thread. A plain JNI call: no C# Java-callable wrapper.</summary>
    private static void SetBrightness(float level)
    {
        var type = JNIEnv.FindClass(JniClassName);
        try
        {
            var method = JNIEnv.GetStaticMethodID(type, "setBrightness", "(F)V");
            JNIEnv.CallStaticVoidMethod(type, method, [new JValue(level)]);
        }
        finally
        {
            JNIEnv.DeleteGlobalRef(type);
        }
    }
}
