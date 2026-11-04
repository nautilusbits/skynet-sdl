using System.Diagnostics;
using System.Globalization;
using RetroEng.Diagnostics;
using RetroEng.Render;

namespace SkyNet.Game.Enhanced;

/// <summary>
/// A map's irradiance bake on a worker thread, while the load waits for it and shows its progress:
/// <code>
///   Start ─► worker: input (IrradianceScene.Build) ─► IrradianceBake.Bake ─► Result | Failure
///   load:    Wait(100 ms) ─► Progress ─► "PREPARING LIGHTING 40%" ─► Wait ...
/// </code>
/// The engine's bake reports nothing, so the progress is an estimate: the input takes the first
/// <see cref="InputShare"/>, the bake the rest, at its work (probes × rays × passes) over the rate the
/// last bake ran at. Past <see cref="Steady"/> it slows down and never shows done before it is.
/// Any failure (out of memory included) ends the job without a volume; nothing is thrown.
/// </summary>
public sealed class IrradianceJob
{
    /// <summary>The share of the bar the input takes.</summary>
    public const float InputShare = 0.1f;
    /// <summary>What the input is guessed to take (s): measured 0.5 s on a desktop.</summary>
    public const double InputGuessSeconds = 0.5;
    /// <summary>Rays a second a thread before any bake ran: MAP.231 on a desktop, 19 M rays in 0.8 s on 20 threads.</summary>
    public const double DefaultRate = 1.2e6;
    /// <summary>The estimate is followed up to this fraction, then the bar slows towards <see cref="Ceiling"/>.</summary>
    public const float Steady = 0.9f;
    /// <summary>The bar's end while the work still runs.</summary>
    public const float Ceiling = 0.99f;

    // The rate (rays a second a thread) the last bake ran at: the next estimate's.
    private static double _rate = DefaultRate;

    /// <summary>The rate (rays a second a thread) the next estimate takes: the last bake's.</summary>
    public static double Rate => Volatile.Read(ref _rate);

    /// <summary>Takes the rate an earlier run saved (<see cref="SaveRate"/>): a phone's first bake of a
    /// session estimates at its own speed. A missing or bad file leaves the rate as it is.</summary>
    public static void LoadRate(string file)
    {
        try
        {
            if (File.Exists(file) && double.TryParse(File.ReadAllText(file).Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out var rate)
                && double.IsFinite(rate) && rate > 0.0)
            {
                Volatile.Write(ref _rate, rate);
            }
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            Log.Warn("Render", $"{Path.GetFileName(file)} not read ({e.GetType().Name}): the bake's estimate starts from the default");
        }
    }

    /// <summary>Saves <paramref name="rate"/> (one number) for the next run; a failure is logged only.</summary>
    public static void SaveRate(string file, double rate)
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(file)!);
            File.WriteAllText(file, rate.ToString("R", CultureInfo.InvariantCulture));
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            Log.Warn("Render", $"{Path.GetFileName(file)} not saved ({e.GetType().Name})");
        }
    }

    private readonly Stopwatch _clock = Stopwatch.StartNew();
    private readonly Thread _thread;
    private readonly Func<IrradianceInput?> _input;
    private readonly Func<IrradianceInput, IrradianceVolume> _bake;
    private long _bakeStart = -1;
    private double _expected;

    private IrradianceJob(Func<IrradianceInput?> input, Func<IrradianceInput, IrradianceVolume> bake)
    {
        (_input, _bake) = (input, bake);
        _thread = new Thread(Run) { Name = "Irradiance bake", IsBackground = true, Priority = ThreadPriority.BelowNormal };
    }

    /// <summary>The bake's input, once built (null: none, or not yet).</summary>
    public IrradianceInput? Input { get; private set; }
    /// <summary>The volume; null until done, or when the job failed or had no input.</summary>
    public IrradianceVolume? Result { get; private set; }
    /// <summary>Why the job ended without a volume (an exception's type, "no input"); null otherwise.</summary>
    public string? Failure { get; private set; }
    public TimeSpan InputTime { get; private set; }
    public TimeSpan BakeTime { get; private set; }
    public bool Done => !_thread.IsAlive;

    /// <summary>The bake's threads: every core but one, which the window and the load keep.</summary>
    public static int Threads => Math.Max(1, Environment.ProcessorCount - 1);

    /// <summary>Starts the job: <paramref name="input"/>, then <paramref name="bake"/> (default
    /// <see cref="IrradianceBake.Bake"/> on <see cref="Threads"/> threads), on a worker thread.</summary>
    public static IrradianceJob Start(Func<IrradianceInput?> input, Func<IrradianceInput, IrradianceVolume>? bake = null)
    {
        var job = new IrradianceJob(input, bake ?? (i => IrradianceBake.Bake(i.Meshes, i.Lamps, i.Settings)));
        job._thread.Start();
        return job;
    }

    /// <summary>Waits up to <paramref name="timeout"/>; true when the job is done.</summary>
    public bool Wait(TimeSpan timeout) => _thread.Join(timeout);

    /// <summary>The bar, 0-1: 1 only when done.</summary>
    public float Progress
    {
        get
        {
            if (Done)
            {
                return 1f;
            }

            var bakeStart = Interlocked.Read(ref _bakeStart);
            if (bakeStart < 0)
            {
                return InputShare * Estimate(_clock.Elapsed.TotalSeconds, InputGuessSeconds);
            }

            var baking = (_clock.ElapsedTicks - bakeStart) / (double)Stopwatch.Frequency;
            return Math.Min(Ceiling, InputShare + (1f - InputShare) * Estimate(baking, Volatile.Read(ref _expected)));
        }
    }

    /// <summary>The bar's fraction <paramref name="elapsed"/> seconds into work guessed at
    /// <paramref name="expected"/>: linear up to <see cref="Steady"/>, then easing towards
    /// <see cref="Ceiling"/> (a slower device never sees it stop at 100 %).</summary>
    public static float Estimate(double elapsed, double expected)
    {
        if (expected <= 0.0)
        {
            return Steady;
        }

        var f = Math.Max(0.0, elapsed / expected);
        if (f <= Steady)
        {
            return (float)f;
        }

        // Past the guess: the rest of the way to the ceiling, halved every tenth of the guess.
        var over = (f - Steady) / (1.0 - Steady);
        return (float)(Steady + (Ceiling - Steady) * (1.0 - Math.Pow(0.5, over)));
    }

    /// <summary>A bake's rays: every probe's, for the direct light and each bounce.</summary>
    public static double Work(IrradianceBakeSettings settings)
    {
        var (x, y, z) = IrradianceScene.ProbesOf(settings.Max - settings.Min, settings.Spacing);
        return (double)x * y * z * settings.Rays * (settings.Bounces + 1);
    }

    private void Run()
    {
        try
        {
            var input = _input();
            InputTime = _clock.Elapsed;
            if (input == null)
            {
                Failure = "no input";
                return;
            }

            var threads = Threads;
            input = input with { Settings = input.Settings with { MaxDegreeOfParallelism = threads } };
            Input = input;
            var work = Work(input.Settings);
            Volatile.Write(ref _expected, work / (Volatile.Read(ref _rate) * threads));
            Interlocked.Exchange(ref _bakeStart, _clock.ElapsedTicks);

            Result = _bake(input);
            BakeTime = _clock.Elapsed - InputTime;
            if (BakeTime.TotalSeconds > 0.0)
            {
                Volatile.Write(ref _rate, work / BakeTime.TotalSeconds / threads);
            }
        }
        catch (Exception e)
        {
            // Out of memory, a bad mesh, anything: the map keeps its even ambient.
            Result = null;
            Failure = e.GetType().Name;
        }
    }
}
