namespace SkyNet.Upscale;

/// <summary>Where the upscaler's release archive comes from (an https URL, a file URL or a local
/// path) and its SHA-256 (lower-case hex). The defaults: Real-ESRGAN v0.2.5.0 (2022-04-24 build) for
/// this platform; a user's values (settings.cfg) replace them when the source moves.</summary>
public sealed record UpscalerSource(string Url, string Sha256)
{
    private const string Release = "https://github.com/xinntao/Real-ESRGAN/releases/download/v0.2.5.0/";
    private const string WindowsZip = "realesrgan-ncnn-vulkan-20220424-windows.zip";
    private const string WindowsSha256 = "abc02804e17982a3be33675e4d471e91ea374e65b70167abc09e31acb412802d";
    private const string LinuxZip = "realesrgan-ncnn-vulkan-20220424-ubuntu.zip";
    private const string LinuxSha256 = "e5aa6eb131234b87c0c51f82b89390f5e3e642b7b70f2b9bbe95b6a285a40c96";
    private const string MacZip = "realesrgan-ncnn-vulkan-20220424-macos.zip";
    private const string MacSha256 = "e0ad05580abfeb25f8d8fb55aaf7bedf552c375b5b4d9bd3c8d59764d2cc333a";

    public static UpscalerSource Default { get; } =
        OperatingSystem.IsWindows() ? new(Release + WindowsZip, WindowsSha256)
        : OperatingSystem.IsMacOS() ? new(Release + MacZip, MacSha256)
        : new(Release + LinuxZip, LinuxSha256);

    /// <summary>A user's values over the defaults (empty: the default). A URL of the user's without a
    /// checksum keeps it empty: the install then refuses it.</summary>
    public static UpscalerSource Of(string url, string sha256)
    {
        var custom = url.Length != 0;
        var hash = sha256.Length != 0 ? sha256.ToLowerInvariant() : custom ? "" : Default.Sha256;
        return new UpscalerSource(custom ? url : Default.Url, hash);
    }
}
