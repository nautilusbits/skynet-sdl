using System.Collections.Concurrent;
using System.Diagnostics;
using System.Globalization;
using System.Net;
using System.Runtime.InteropServices;
using RetroEng.Diagnostics;
using SkyNet.Formats;

namespace SkyNet.Game.Net;

/// <summary>
/// <c>SkyNet --dedicated --config server.json</c>: a dedicated server with no window, no GPU and no
/// sound (SDL is never loaded: this path touches no SDL type, and the log says so at the start and the
/// end), for a VPS or a Linux box. The log goes to stdout and to the config's <c>log_file</c>; console
/// commands come on stdin (<see cref="DmDedicated.Execute"/>). Ctrl+C or SIGTERM stops it cleanly.
/// Options: <c>--data DIR</c>, <c>--port N</c> / <c>--bind IP</c> (over the config's),
/// <c>--quit-after S</c>, <c>--smoke</c> (tools/net-smoke.ps1 -Dedicated: exit 0 once two players
/// joined, the map rotated and everybody left). <c>--password-hash TEXT</c> prints a
/// <c>password_sha256</c> for the config and exits.
/// </summary>
public static class DmDedicatedRunner
{
    public static bool Wants(string[] args) => args.Contains("--dedicated") || args.Any(a => a.StartsWith("--password-hash", StringComparison.Ordinal));

    public static int Run(string[] args)
    {
        TextLogSink.AttachParentConsole();
        var console = TextLogSink.Console(plain: true);
        Log.AddSink(console, LogLevel.Info);
        FileLogSink? file = null;
        var served = false;
        try
        {
            if (!Parse(args, out var o, out var error))
            {
                Log.Error("Server", error!);
                return 2;
            }

            if (o.PasswordHash != null)
            {
                Console.Out.WriteLine(Convert.ToHexString(DmProtocol.PasswordKey(o.PasswordHash)).ToLowerInvariant());
                return 0;
            }

            ServerConfig config;
            try
            {
                config = o.Config != null ? ServerConfig.Load(o.Config) : new ServerConfig();
            }
            catch (Exception e) when (e is RetroEng.Json.JsonDataException or IOException or UnauthorizedAccessException)
            {
                Log.Error("Server", ServerConfig.Describe(o.Config!, e));
                return 2;
            }

            config = config with { Port = o.Port ?? config.Port, Bind = o.Bind ?? config.Bind, Classes = o.Classes ?? config.Classes };
            // The master server: --master, server.json's, else masters.txt beside the exe (the repo has none).
            if (MasterAddresses.Pick(o.Master, config.Master, AppContext.BaseDirectory) is { } master)
            {
                config = config with { Master = master };
            }
            if (config.LogFile.Length > 0)
            {
                var path = config.Resolve(config.LogFile);
                var name = Path.GetFileNameWithoutExtension(path);
                file = FileLogSink.Open(Path.GetDirectoryName(path)!, name.Length > 0 ? name : "server", keep: 5, out var logError);
                if (file != null)
                {
                    Log.AddSink(file, LogLevel.Info);
                }
                else
                {
                    Log.Warn("Server", $"no log file ({logError})");
                }
            }

            Log.Info("Server", $"SkyNET dedicated server, {RuntimeInformation.OSDescription} {RuntimeInformation.ProcessArchitecture}, .NET {Environment.Version}");
            Log.Info("Server", $"config: {(o.Config != null ? Path.GetFullPath(o.Config) : "none (the defaults)")}");
            Log.Info("Server", $"SDL: {SdlState()}");
            var dir = GameDataLocator.Locate(o.Data ?? Environment.GetEnvironmentVariable("SKYNET_DATA"), null,
                [AppContext.BaseDirectory, Environment.CurrentDirectory]);
            var data = dir != null ? GameData.Open(dir) : null;
            if (data == null)
            {
                Log.Error("Server", "No game data found: pass --data <dir> or set SKYNET_DATA.");
                return 2;
            }

            if (DmDedicated.Start(data, config, out var startError) is not { } server)
            {
                Log.Error("Server", startError ?? "the server did not start");
                return 1;
            }

            served = true;
            using (server)
            {
                return Loop(server, o);
            }
        }
        finally
        {
            if (served)
            {
                Log.Info("Server", $"SDL at exit: {SdlState()}");
            }

            Log.RemoveSink(console);
            if (file != null)
            {
                Log.RemoveSink(file);
                file.Dispose();
            }
        }
    }

    private static int Loop(DmDedicated server, Options o)
    {
        var lines = new ConcurrentQueue<string>();
        var stop = 0;
        Console.CancelKeyPress += (_, e) =>
        {
            e.Cancel = true;
            Interlocked.Exchange(ref stop, 1);
        };
        using var term = PosixSignalRegistration.Create(PosixSignal.SIGTERM, ctx =>
        {
            ctx.Cancel = true;
            Interlocked.Exchange(ref stop, 1);
        });
        // stdin: a terminal or a pipe. On Windows the game is a GUI program, whose parent console
        // keeps its own keyboard; there the commands come only through a redirected stdin.
        if (!OperatingSystem.IsWindows() || Console.IsInputRedirected)
        {
            var reader = new Thread(() =>
            {
                try
                {
                    while (Console.In.ReadLine() is { } line)
                    {
                        lines.Enqueue(line);
                    }
                }
                catch (IOException)
                {
                }
            }) { IsBackground = true, Name = "server console" };
            reader.Start();
            Log.Info("Server", "console: type 'help' for the commands");
        }

        var clock = Stopwatch.StartNew();
        var last = 0.0;
        while (true)
        {
            var now = clock.Elapsed.TotalSeconds;
            server.Update(now - last);
            last = now;
            while (lines.TryDequeue(out var line))
            {
                var answer = server.Execute(line);
                if (answer.Length > 0)
                {
                    foreach (var l in answer.Split('\n'))
                    {
                        Log.Info("Server", l);
                    }
                }
            }

            if (o.Smoke && server.Joins >= 2 && server.Rotations >= 1 && server.Server.Humans == 0)
            {
                Log.Info("Server", $"SMOKE dedicated: PASS ({server.Joins} joins, {server.Rotations} rotation(s), {server.Rounds} round(s), everybody left)");
                server.Stop("The smoke test is over.");
                return 0;
            }

            if (Volatile.Read(ref stop) == 1)
            {
                server.Stop("The server is shutting down.");
                return 0;
            }

            if (server.Stopped)
            {
                return 0;
            }

            if (o.QuitAfter > 0 && now >= o.QuitAfter)
            {
                server.Stop("The server is shutting down.");
                if (o.Smoke)
                {
                    Log.Error("Server", $"SMOKE dedicated: FAIL ({server.Joins} joins, {server.Rotations} rotation(s), {server.Rounds} round(s), {server.Server.Humans} still in)");
                    return 1;
                }

                return 0;
            }

            Thread.Sleep(TimeSpan.FromSeconds(DmHeadless.TickSeconds));
        }
    }

    /// <summary>Whether SDL's native library is in this process (it must not be on this path).</summary>
    public static string SdlState()
    {
        try
        {
            using var me = Process.GetCurrentProcess();
            // The native library (SDL3.dll, libSDL3.so.0, libSDL3.0.dylib), not the managed binding
            // (SDL3-CS.dll), which a JIT run may map without calling into SDL.
            var sdl = me.Modules.Cast<ProcessModule>().FirstOrDefault(m =>
                m.ModuleName.StartsWith("SDL3.", StringComparison.OrdinalIgnoreCase)
                || m.ModuleName.StartsWith("libSDL3", StringComparison.OrdinalIgnoreCase));
            return sdl == null ? "native SDL3 library not loaded (no window, GPU or audio)" : $"native SDL3 LOADED ({sdl.FileName})";
        }
        catch (Exception e) when (e is InvalidOperationException or System.ComponentModel.Win32Exception or NotSupportedException or PlatformNotSupportedException)
        {
            return $"unknown ({e.GetType().Name})";
        }
    }

    private sealed record Options
    {
        public string? Config { get; init; }
        public string? Data { get; init; }
        public int? Port { get; init; }
        public IPAddress? Bind { get; init; }
        public double QuitAfter { get; init; }
        public bool Smoke { get; init; }
        public string? PasswordHash { get; init; }
        public string? Master { get; init; }
        public DmClassRules? Classes { get; init; }
    }

    private static bool Parse(string[] args, out Options o, out string? error)
    {
        o = new Options();
        error = null;
        var c = CultureInfo.InvariantCulture;
        for (var i = 0; i < args.Length; i++)
        {
            var a = args[i];
            string key, value;
            var eq = a.IndexOf('=');
            if (eq > 0)
            {
                key = a[..eq];
                value = a[(eq + 1)..];
            }
            else
            {
                key = a;
                value = key is "--dedicated" or "--smoke" or "--offscreen" || i + 1 >= args.Length || args[i + 1].StartsWith("--", StringComparison.Ordinal) ? "" : args[++i];
            }

            switch (key)
            {
                case "--dedicated" or "--offscreen":
                    break;
                case "--config": o = o with { Config = value }; break;
                case "--data": o = o with { Data = value }; break;
                case "--password-hash": o = o with { PasswordHash = value }; break;
                case "--master": o = o with { Master = value }; break;
                case "--smoke": o = o with { Smoke = true }; break;
                case "--classes":
                    if (DmProtocol.ParseRules(value) is not { } rules)
                    {
                        error = $"--classes takes dos or balanced, not '{value}'";
                        return false;
                    }

                    o = o with { Classes = rules };
                    break;
                case "--port":
                    if (!int.TryParse(value, NumberStyles.Integer, c, out var port) || port is < 0 or > 65535)
                    {
                        error = $"--port takes a port 0-65535, not '{value}'";
                        return false;
                    }

                    o = o with { Port = port };
                    break;
                case "--bind":
                    if (!IPAddress.TryParse(value, out var bind))
                    {
                        error = $"--bind takes an IP address, not '{value}'";
                        return false;
                    }

                    o = o with { Bind = bind };
                    break;
                case "--quit-after":
                    if (!double.TryParse(value, NumberStyles.Float, c, out var after) || !double.IsFinite(after) || after < 0)
                    {
                        error = $"--quit-after takes seconds, not '{value}'";
                        return false;
                    }

                    o = o with { QuitAfter = after };
                    break;
                default:
                    error = $"Unknown option {key} for a dedicated server (--config FILE, --data DIR, --port N, --bind IP, --master HOST:PORT, --classes dos|balanced, --quit-after S, --smoke, --password-hash TEXT)";
                    return false;
            }
        }

        if (o.Smoke && o.QuitAfter <= 0)
        {
            o = o with { QuitAfter = 60 };
        }

        return true;
    }
}
