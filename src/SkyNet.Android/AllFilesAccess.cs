using System.Runtime.Versioning;
using Android.App;
using Android.Content;
using Android.Content.PM;
using SkyNet.Game.Storage;
using AndroidSettings = Android.Provider.Settings;
using AUri = Android.Net.Uri;

#if !STORE_BUILD
// Playing from the SkyNET folder in place: All files access from Android 11, the storage permission
// before (Android 10 also needs requestLegacyExternalStorage, in the manifest). A store build
// (-p:StoreBuild=true) leaves them out and always copies.
[assembly: UsesPermission(global::Android.Manifest.Permission.ManageExternalStorage)]
[assembly: UsesPermission(global::Android.Manifest.Permission.ReadExternalStorage, MaxSdkVersion = 29)]
[assembly: UsesPermission(global::Android.Manifest.Permission.WriteExternalStorage, MaxSdkVersion = 29)]
#endif

namespace SkyNet.Android;

/// <summary>Access to shared storage by path: checked, and asked for (the system's All files
/// access screen from Android 11, the storage permission dialog before). Asking runs on the
/// setup's thread and waits for the answer, which the activity passes on.</summary>
internal sealed class AllFilesAccess(Activity activity)
{
    /// <summary>The All files access screen's request code ("SA").</summary>
    public const int SettingsRequest = 0x5341;
    /// <summary>The storage permission's request code ("SP").</summary>
    public const int PermissionRequest = 0x5350;
    private const string PackageScheme = "package:";

    private static readonly string[] Permissions =
        [global::Android.Manifest.Permission.ReadExternalStorage, global::Android.Manifest.Permission.WriteExternalStorage];

    private TaskCompletionSource? _answered;

    /// <summary>All files access: Android 11 (API 30) on.</summary>
    [SupportedOSPlatformGuard("android30.0")]
    private static bool HasAllFiles => OperatingSystem.IsAndroidVersionAtLeast(30);

    public FolderAccess Current
    {
        get
        {
#if STORE_BUILD
            return FolderAccess.Unavailable;
#else
            return Granted() ? FolderAccess.Granted : FolderAccess.Askable;
#endif
        }
    }

    /// <summary>Asks for the access; returns whether it is granted once the user is back.</summary>
    public bool Request()
    {
        if (Current != FolderAccess.Askable)
        {
            return Current == FolderAccess.Granted;
        }

        _answered = new TaskCompletionSource();
        activity.RunOnUiThread(Ask);
        _answered.Task.Wait();
        return Granted();
    }

    /// <summary>The settings screen closed or the permission dialog was answered. Returns whether it was ours.</summary>
    public bool OnAnswer(int request)
    {
        if (request != SettingsRequest && request != PermissionRequest)
        {
            return false;
        }

        _answered?.TrySetResult();
        return true;
    }

    private bool Granted()
    {
        if (HasAllFiles)
        {
            return global::Android.OS.Environment.IsExternalStorageManager;
        }

        return Permissions.All(p => activity.CheckSelfPermission(p) == Permission.Granted);
    }

    private void Ask()
    {
        if (!HasAllFiles)
        {
            activity.RequestPermissions(Permissions, PermissionRequest);
            return;
        }

        OpenSettings();
    }

    // The app's All files access switch; some devices only have the list of apps.
    [SupportedOSPlatform("android30.0")]
    private void OpenSettings()
    {
        try
        {
            var app = AUri.Parse(PackageScheme + activity.PackageName);
            activity.StartActivityForResult(new Intent(AndroidSettings.ActionManageAppAllFilesAccessPermission, app), SettingsRequest);
        }
        catch (ActivityNotFoundException)
        {
            try
            {
                activity.StartActivityForResult(new Intent(AndroidSettings.ActionManageAllFilesAccessPermission), SettingsRequest);
            }
            catch (ActivityNotFoundException)
            {
                _answered?.TrySetResult();
            }
        }
    }
}
