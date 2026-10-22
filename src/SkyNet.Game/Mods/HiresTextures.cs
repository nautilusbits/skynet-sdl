using System.Diagnostics;
using System.Globalization;
using RetroEng.Diagnostics;
using RetroEng.Jobs;
using SkyNet.Upscale;
using SkyNet.Formats;
using SkyNet.Game.Menu;

namespace SkyNet.Game.Mods;

/// <summary>
/// The hi-res texture mod made in the game (OPTIONS → MODS → GENERATE HI-RES TEXTURES), as
/// <c>tools/upscale/upscale.py run --method esrgan-x4plus</c> makes it (docs/hires-textures.md):
/// every TEXTURE.NNN record (<see cref="TextureStack"/>) goes through <see cref="TexturePipeline"/>
/// (holes filled, padded by 8, wrapped for walls and edge-copied for sprites), all frames through the
/// upscaler in one batch, then cropped, alpha = the index-0 mask by nearest and the holes' RGB =
/// palette index 31. Written as <c>mods/hires-esrgan-x4plus/textures/TEXTURE.NNN/RRR.png</c> plus
/// <c>upscale-stats.json</c> (as the tool writes) and a <c>mod.json</c> for the MODS screen. Every
/// record but a sprite also gets its relief maps (<see cref="WriteRelief"/>).
/// </summary>
public static class HiresTextures
{
    public const string Method = "esrgan-x4plus";
    public const string ModName = "hires-" + Method;
    public const string Licence = "BSD-3-Clause (Real-ESRGAN)";
    /// <summary>HAZE.000 row 0: what index 0 becomes on an opaque surface.</summary>
    public const int HazeIndex = 31;

    /// <summary>Every record of the data's TEXTURE banks (<paramref name="banks"/>: only those).</summary>
    public static List<TextureStack.Record> Records(GameData data, IReadOnlySet<int>? banks = null)
    {
        List<TextureStack.Record> records = [];
        for (var bank = 0; bank <= TextureStack.MaxBank; bank++)
        {
            if ((banks == null || banks.Contains(bank)) && data.ReadLoose($"TEXTURE.{bank:D3}") is { } bytes)
            {
                records.AddRange(TextureStack.Records(bank, bytes));
            }
        }

        return records;
    }

    /// <summary>The records upscaled: <paramref name="sink"/> gets each record with its RGBA
    /// (width·factor × height·factor·frames, frames stacked downwards), from several threads.
    /// <paramref name="progress"/> gets a phase ("PREPARING", "UPSCALING", "WRITING"), the count
    /// done and the count in all. Returns the number of frames.</summary>
    public static int Upscale(IReadOnlyList<TextureStack.Record> records, ReadOnlySpan<byte> paletteRgb, IImageUpscaler upscaler,
        Action<TextureStack.Record, byte[]> sink, Action<string, int, int>? progress, CancellationToken cancel)
    {
        var palette = paletteRgb.ToArray();
        var first = new int[records.Count + 1];
        for (var r = 0; r < records.Count; r++)
        {
            first[r + 1] = first[r] + records[r].Frames;
        }

        var frames = new RgbImage[first[^1]];
        var prepared = 0;
        var jobs = JobSystem.Shared;
        jobs.ParallelFor(records.Count, 1, new ActionFor(r =>
        {
            cancel.ThrowIfCancellationRequested();
            var rec = records[r];
            var n = rec.Width * rec.Height;
            for (var k = 0; k < rec.Frames; k++)
            {
                var idx = rec.Pixels.AsSpan(k * n, n);
                var rgb = new float[n * 3];
                var mask = new bool[n];
                for (var i = 0; i < n; i++)
                {
                    mask[i] = idx[i] != 0;
                    rgb[i * 3] = palette[idx[i] * 3];
                    rgb[i * 3 + 1] = palette[idx[i] * 3 + 1];
                    rgb[i * 3 + 2] = palette[idx[i] * 3 + 2];
                }

                if (rec.Holes)
                {
                    TexturePipeline.FillHoles(rgb, mask, rec.Width, rec.Height, wrap: !rec.Sprite);
                }

                frames[first[r] + k] = TexturePipeline.Padded(rgb, rec.Width, rec.Height, wrap: !rec.Sprite);
            }

            progress?.Invoke("PREPARING", Interlocked.Increment(ref prepared), records.Count);
        }));

        var factor = upscaler.Factor;
        var hole = (palette[HazeIndex * 3], palette[HazeIndex * 3 + 1], palette[HazeIndex * 3 + 2]);
        using var scaled = upscaler.Upscale(frames, d => progress?.Invoke("UPSCALING", d, frames.Length), cancel);
        var written = 0;
        jobs.ParallelFor(records.Count, 1, new ActionFor(r =>
        {
            cancel.ThrowIfCancellationRequested();
            var rec = records[r];
            var n = rec.Width * rec.Height;
            var frameBytes = n * factor * factor * 4;
            var rgba = new byte[frameBytes * rec.Frames];
            var mask = new bool[n];
            for (var k = 0; k < rec.Frames; k++)
            {
                for (var i = 0; i < n; i++)
                {
                    mask[i] = rec.Pixels[k * n + i] != 0;
                }

                TexturePipeline.Finish(scaled[first[r] + k], factor, mask, rec.Width, rec.Height, hole, rgba.AsSpan(k * frameBytes, frameBytes));
            }

            sink(rec, rgba);
            progress?.Invoke("WRITING", Interlocked.Increment(ref written), records.Count);
        }));

        return frames.Length;
    }

    /// <summary>The mod made from <paramref name="data"/> into <paramref name="modsDir"/>/<see cref="ModName"/>:
    /// written into <paramref name="workDir"/> first and moved over only when complete (a cancelled or
    /// failed run leaves the mod as it was). Files of banks left out stay.</summary>
    public static void Generate(GameData data, string modsDir, string workDir, IImageUpscaler upscaler, IReadOnlySet<int>? banks,
        Action<string, int, int>? progress, CancellationToken cancel)
    {
        var clock = Stopwatch.StartNew();
        var palette = data.PaletteBytes() is { } p ? Palette.Parse(p) : null;
        if (palette == null)
        {
            throw new InvalidDataException("no game palette");
        }

        var records = Records(data, banks);
        if (records.Count == 0)
        {
            throw new InvalidDataException("no TEXTURE records");
        }

        var staging = Path.Combine(workDir, "mod");
        if (Directory.Exists(staging))
        {
            Directory.Delete(staging, recursive: true);
        }

        long bytes = 0;
        TimeSpan upscaleStart = TimeSpan.Zero, upscaleEnd = TimeSpan.Zero;
        var frames = Upscale(records, palette.Rgb.AsSpan(), upscaler, (rec, rgba) =>
        {
            var factor = upscaler.Factor;
            var png = PngWriter.Encode(rec.Width * factor, rec.Height * factor * rec.Frames, 4, rgba);
            var path = Path.Combine(staging, RelativePath(rec.Bank, rec.Index));
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            File.WriteAllBytes(path, png);
            Interlocked.Add(ref bytes, png.Length);
            if (!rec.Sprite)
            {
                Interlocked.Add(ref bytes, WriteRelief(path, rec.Width * factor, rec.Height * factor * rec.Frames, rec.Frames, factor, rgba));
            }
        }, (phase, done, all) =>
        {
            if (phase == "UPSCALING" && upscaleStart == TimeSpan.Zero)
            {
                upscaleStart = clock.Elapsed;
            }
            else if (phase == "WRITING" && upscaleEnd == TimeSpan.Zero)
            {
                upscaleEnd = clock.Elapsed;
            }

            progress?.Invoke(phase, done, all);
        }, cancel);

        cancel.ThrowIfCancellationRequested();
        var mod = Path.Combine(modsDir, ModName);
        foreach (var file in Directory.EnumerateFiles(staging, "*.png", SearchOption.AllDirectories))
        {
            var target = Path.Combine(mod, Path.GetRelativePath(staging, file));
            Directory.CreateDirectory(Path.GetDirectoryName(target)!);
            File.Move(file, target, overwrite: true);
        }

        Directory.Delete(staging, recursive: true);
        if (!Directory.EnumerateFileSystemEntries(workDir).Any())
        {
            Directory.Delete(workDir);
        }

        var c = CultureInfo.InvariantCulture;
        File.WriteAllText(Path.Combine(mod, ModInfo.FileName),
            "{\n \"name\": \"Hi-res textures (Real-ESRGAN x4plus)\",\n \"version\": \"1\",\n" +
            " \"description\": \"The game's own textures upscaled 4x by Real-ESRGAN x4plus (BSD-3-Clause), made by GENERATE HI-RES TEXTURES.\"\n}\n");
        File.WriteAllText(Path.Combine(mod, "upscale-stats.json"), string.Create(c,
            $"{{\n \"method\": \"{Method}\",\n \"licence\": \"{Licence}\",\n \"records\": {records.Count},\n \"frames\": {frames},\n" +
            $" \"model_load_s\": 0.0,\n \"upscale_s\": {Math.Round((upscaleEnd - upscaleStart).TotalSeconds, 2)},\n" +
            $" \"total_s\": {Math.Round(clock.Elapsed.TotalSeconds, 2)},\n \"bytes\": {bytes}\n}}"));
        Log.Info("Textures", $"{ModName}: {records.Count} records, {frames} frames, {bytes / (1 << 20)} MB in {clock.Elapsed.TotalSeconds:F0} s");
    }

    /// <summary>A record's relief maps (<see cref="ReliefMaps"/>) beside its HD texture at
    /// <paramref name="path"/>: <c>RRR_n.png</c> (the normal, RGB) and <c>RRR_r.png</c> (the
    /// roughness, grey). Returns their bytes.</summary>
    public static long WriteRelief(string path, int width, int height, int frames, int factor, ReadOnlySpan<byte> rgba)
    {
        var (normal, roughness) = ReliefMaps.Make(rgba, width, height, frames, factor, wrap: true);
        var stem = Path.Combine(Path.GetDirectoryName(path)!, Path.GetFileNameWithoutExtension(path));
        var n = PngWriter.Encode(width, height, 3, normal);
        var r = PngWriter.Encode(width, height, 1, roughness);
        File.WriteAllBytes(stem + NormalSuffix + ".png", n);
        File.WriteAllBytes(stem + RoughnessSuffix + ".png", r);
        return n.Length + r.Length;
    }

    public const string NormalSuffix = "_n";
    public const string RoughnessSuffix = "_r";

    /// <summary>The relief maps (re)made for the HD textures of an existing mod folder
    /// <paramref name="modDir"/> from its own PNGs (<paramref name="decode"/>: straight RGBA8), without
    /// upscaling again (the hidden <c>--gen-relief</c>): every record of the data's banks but sprites.
    /// Returns the records done.</summary>
    public static int GenerateRelief(GameData data, string modDir, Func<byte[], (int Width, int Height, byte[] Rgba)?> decode)
    {
        var clock = Stopwatch.StartNew();
        var records = Records(data).Where(r => !r.Sprite).ToList();
        var done = 0;
        JobSystem.Shared.ParallelFor(records.Count, 1, new ActionFor(i =>
        {
            var rec = records[i];
            var path = Path.Combine(modDir, RelativePath(rec.Bank, rec.Index));
            if (!File.Exists(path) || decode(File.ReadAllBytes(path)) is not { } image)
            {
                return;
            }

            var (w, h, rgba) = image;
            var factor = w / rec.Width;
            if (factor < 1 || w != rec.Width * factor || h != rec.Height * factor * rec.Frames)
            {
                Log.Warn("Textures", $"{path}: {w} x {h} does not scale the record's {rec.Width} x {rec.Height} x {rec.Frames}");
                return;
            }

            WriteRelief(path, w, h, rec.Frames, factor, rgba);
            Interlocked.Increment(ref done);
        }));
        Log.Info("Textures", $"{modDir}: relief maps for {done} of {records.Count} records in {clock.Elapsed.TotalSeconds:F0} s");
        return done;
    }

    /// <summary><c>textures/TEXTURE.NNN/RRR.png</c>, the layout the loader reads.</summary>
    public static string RelativePath(int bank, int record) => Path.Combine("textures", $"TEXTURE.{bank:D3}", $"{record:D3}.png");
}

/// <summary>
/// GENERATE HI-RES TEXTURES as a background job on the pool (<see cref="JobSystem.Shared"/>): Real-ESRGAN fetched and checked into
/// <c>cache/realesrgan</c> beside the game (<see cref="RealEsrgan.Install"/>), then
/// <see cref="HiresTextures.Generate"/>. <see cref="Status"/> is the MODS screen's progress line.
/// </summary>
public sealed class HiresJob : IMenuJob
{
    private readonly CancellationTokenSource _cancel = new();
    private readonly JobHandle _job;
    private volatile string _status = "STARTING";
    private volatile string? _error;
    private volatile int _state = (int)MenuJobState.Running;

    private HiresJob(Action<HiresJob, CancellationToken> work)
    {
        _job = JobSystem.Shared.Schedule(new ActionJob(() =>
        {
            try
            {
                work(this, _cancel.Token);
                _status = "HI-RES TEXTURES READY";
                _state = (int)MenuJobState.Done;
            }
            catch (OperationCanceledException)
            {
                _status = "CANCELLED";
                _state = (int)MenuJobState.Cancelled;
            }
            catch (Exception e)
            {
                Log.Warn("Textures", $"{HiresTextures.ModName} failed: {e}");
                _error = Explain(e);
                _status = _error;
                _state = (int)MenuJobState.Failed;
            }
        }), priority: JobPriority.Background);
    }

    public string Status => _status;
    public MenuJobState State => (MenuJobState)_state;
    public string? Error => _error;
    public string ModName => HiresTextures.ModName;

    public void Cancel() => _cancel.Cancel();

    /// <summary>Blocks until the job ends (the command line's run).</summary>
    public void Wait() => JobSystem.Shared.Wait(_job);

    /// <summary>The job for the game's data: the tool goes to <paramref name="cacheDir"/>, the mod to <paramref name="modsDir"/>.</summary>
    public static HiresJob Start(GameData data, string modsDir, string cacheDir, UpscalerSource source, IReadOnlySet<int>? banks = null) =>
        new((job, cancel) =>
        {
            var tool = RealEsrgan.Install(Path.Combine(cacheDir, "realesrgan"), source,
                (got, total) => job._status = total > 0 ? $"DOWNLOADING THE UPSCALER {got >> 20} / {total >> 20} MB" : $"DOWNLOADING THE UPSCALER {got >> 20} MB",
                cancel);
            var upscaler = new RealEsrganUpscaler(tool, Path.Combine(cacheDir, "hires-work", "frames"));
            HiresTextures.Generate(data, modsDir, Path.Combine(cacheDir, "hires-work"), upscaler, banks,
                (phase, done, all) => job._status = $"{phase} {done} / {all}", cancel);
        });

    /// <summary>What the MODS screen says when it fails (the log has the whole story).</summary>
    private static string Explain(Exception e) => e switch
    {
        HttpRequestException => "DOWNLOAD FAILED: SEE LOGS/SKYNET.LOG",
        InvalidDataException when e.Message.Contains("SHA-256", StringComparison.Ordinal) => "THE DOWNLOAD DID NOT VERIFY",
        InvalidOperationException or System.ComponentModel.Win32Exception => "NO VULKAN GPU OR THE UPSCALER FAILED: SEE LOGS",
        _ => "FAILED: SEE LOGS/SKYNET.LOG",
    };
}
