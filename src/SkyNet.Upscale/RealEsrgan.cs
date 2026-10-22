using System.Diagnostics;
using System.Globalization;
using System.IO.Compression;
using System.Security.Cryptography;
using RetroEng.Platform;

namespace SkyNet.Upscale;

/// <summary>The upscaler's network: Real-ESRGAN x4plus (photos, 4x only), or the lighter
/// animevideov3 (drawings, 2x to 4x).</summary>
public enum UpscaleModel { General, Anime }

/// <summary>Real-ESRGAN's portable upscaler (realesrgan-ncnn-vulkan, BSD-3-Clause, with its models),
/// downloaded once from its release (<see cref="UpscalerSource"/>) into a folder of the user's,
/// checked against the archive's SHA-256. It runs on any Vulkan GPU (integrated ones too, slower);
/// without one it fails (its CPU path is far too slow to be offered).
/// <code>
///   Install: release zip ─► SHA-256 as given? ─► extract ─► folder/realesrgan-ncnn-vulkan[.exe], models/
///   Run:     folder of PNGs ─► the tool (-n model -s factor) ─► folder of PNGs (same names)
/// </code></summary>
public sealed class RealEsrgan
{
    private const string ToolName = "realesrgan-ncnn-vulkan";
    /// <summary>The archive while it's downloaded and checked.</summary>
    private const string Download = "download.zip";
    /// <summary>Written after a checked extraction: its content is the archive's SHA-256.</summary>
    private const string Marker = "installed.sha256";
    private const int PollMilliseconds = 200;
    private const int CopyBuffer = 1 << 16;
    private const int NativeFactor = 4;
    /// <summary>The tool's last lines of output kept for an error.</summary>
    private const int KeptLines = 20;
    /// <summary>Who asks for the release (GitHub refuses requests without one).</summary>
    private const string UserAgent = "skynet";

    private readonly string _exe;

    private RealEsrgan(string exe) => _exe = exe;

    private static string ExeIn(string folder) => Path.Combine(folder, OperatingSystem.IsWindows() ? ToolName + ".exe" : ToolName);

    /// <summary>Whether the folder holds the source's release, checked when it was extracted.</summary>
    public static bool IsInstalled(string folder, UpscalerSource source)
    {
        var marker = Path.Combine(folder, Marker);
        return File.Exists(ExeIn(folder)) && File.Exists(marker) && File.ReadAllText(marker).Trim() == source.Sha256;
    }

    /// <summary>The tool in <paramref name="folder"/>, downloaded first when missing
    /// (<paramref name="progress"/>: bytes so far, total or 0).</summary>
    public static RealEsrgan Install(string folder, UpscalerSource source, Action<long, long>? progress, CancellationToken cancel)
    {
        if (IsInstalled(folder, source))
        {
            return new RealEsrgan(ExeIn(folder));
        }

        // A release of the user's needs its checksum too (settings.cfg).
        if (source.Sha256.Length == 0)
        {
            throw new InvalidDataException($"upscaler_sha256 needed for {source.Url}");
        }

        Directory.CreateDirectory(folder);
        var download = Path.Combine(folder, Download);
        try
        {
            var hash = Fetch(source.Url, download, progress, cancel);
            if (hash != source.Sha256)
            {
                throw new InvalidDataException($"{source.Url}: SHA-256 {hash}, expected {source.Sha256}");
            }

            ZipFile.ExtractToDirectory(download, folder, overwriteFiles: true);
        }
        finally
        {
            File.Delete(download);
        }

        if (!OperatingSystem.IsWindows())
        {
            File.SetUnixFileMode(ExeIn(folder), File.GetUnixFileMode(ExeIn(folder)) | UnixFileMode.UserExecute);
        }

        File.WriteAllText(Path.Combine(folder, Marker), source.Sha256);
        return new RealEsrgan(ExeIn(folder));
    }

    /// <summary>Saves a URL's file (http(s), file or a local path); returns its SHA-256 (hex).</summary>
    private static string Fetch(string url, string path, Action<long, long>? progress, CancellationToken cancel)
    {
        if (Uri.TryCreate(url, UriKind.Absolute, out var uri) && !uri.IsFile)
        {
            HttpDownload.Save(url, path, UserAgent, progress, cancel);
            using var saved = File.OpenRead(path);
            return Convert.ToHexStringLower(SHA256.HashData(saved));
        }

        using var local = File.OpenRead(uri?.LocalPath ?? url);
        return Copy(local, local.Length, path, progress, cancel);
    }

    /// <summary>Copies a stream into a file, hashing it.</summary>
    private static string Copy(Stream source, long total, string path, Action<long, long>? progress, CancellationToken cancel)
    {
        using var file = File.Create(path);
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        var buffer = new byte[CopyBuffer];
        long done = 0;
        int read;
        while ((read = source.Read(buffer)) > 0)
        {
            cancel.ThrowIfCancellationRequested();
            file.Write(buffer, 0, read);
            hash.AppendData(buffer, 0, read);
            done += read;
            progress?.Invoke(done, total);
        }

        return Convert.ToHexStringLower(hash.GetHashAndReset());
    }

    /// <summary>The scale the network makes for a wanted one: x4plus only 4x (the caller shrinks).</summary>
    public static int Factor(UpscaleModel model, int wanted) => model == UpscaleModel.General ? NativeFactor : wanted;

    /// <summary>The tool's name of a model.</summary>
    public static string NameOf(UpscaleModel model) => model == UpscaleModel.General ? "realesrgan-x4plus" : "realesr-animevideov3";

    /// <summary>Upscales every PNG of <paramref name="input"/> into <paramref name="output"/> by
    /// <paramref name="factor"/> (<see cref="Factor"/>); <paramref name="done"/> gets the count of
    /// images written so far. Throws when the tool fails or leaves images out.</summary>
    public void Run(string input, string output, UpscaleModel model, int factor, Action<int>? done, CancellationToken cancel)
    {
        Directory.CreateDirectory(output);
        var expected = Directory.GetFiles(input, "*.png").Length;
        var info = new ProcessStartInfo(_exe)
        {
            ArgumentList = { "-i", input, "-o", output, "-n", NameOf(model), "-s", factor.ToString(CultureInfo.InvariantCulture), "-f", "png" },
            WorkingDirectory = Path.GetDirectoryName(_exe),
            RedirectStandardError = true,
            RedirectStandardOutput = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };

        using var process = Process.Start(info) ?? throw new InvalidOperationException($"{_exe} didn't start");
        var lines = new ToolLines();
        process.ErrorDataReceived += lines.Keep;
        process.OutputDataReceived += lines.Keep;
        process.BeginErrorReadLine();
        process.BeginOutputReadLine();

        // The game quitting doesn't leave the tool running.
        void Stop(object? sender, EventArgs e) => process.Kill(entireProcessTree: true);
        AppDomain.CurrentDomain.ProcessExit += Stop;
        try
        {
            Wait(process, output, done, cancel);
        }
        finally
        {
            AppDomain.CurrentDomain.ProcessExit -= Stop;
        }

        var written = Directory.GetFiles(output, "*.png").Length;
        done?.Invoke(written);
        if (process.ExitCode == 0 && written == expected)
        {
            return;
        }

        throw new InvalidOperationException($"{ToolName} failed (exit {process.ExitCode}, {written} of {expected} images; a Vulkan GPU is needed):\n{lines}");
    }

    /// <summary>Waits for the tool, counting its images; kills it when cancelled.</summary>
    private static void Wait(Process process, string output, Action<int>? done, CancellationToken cancel)
    {
        while (!process.WaitForExit(PollMilliseconds))
        {
            if (cancel.IsCancellationRequested)
            {
                process.Kill(entireProcessTree: true);
                process.WaitForExit();
                cancel.ThrowIfCancellationRequested();
            }

            done?.Invoke(Directory.GetFiles(output, "*.png").Length);
        }

        process.WaitForExit();
    }

    /// <summary>The tool's last lines (a percentage per tile; the last ones tell why it failed).</summary>
    private sealed class ToolLines
    {
        private readonly Queue<string> _lines = [];

        public void Keep(object sender, DataReceivedEventArgs e)
        {
            if (e.Data == null)
            {
                return;
            }

            lock (_lines)
            {
                _lines.Enqueue(e.Data);
                if (_lines.Count > KeptLines)
                {
                    _lines.Dequeue();
                }
            }
        }

        public override string ToString()
        {
            lock (_lines)
            {
                return string.Join('\n', _lines);
            }
        }
    }
}
