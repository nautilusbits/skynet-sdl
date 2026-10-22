// The master server (docs/multiplayer.md "Master server"): lists public game servers for the join
// screens' INTERNET tab, checks that each answers where it says, and introduces players to servers
// behind a NAT. No SDL, window or sound: a console program for a VPS or a Linux box.
//   SkyNet.Master [--config master.json] [--port N] [--bind IP] [--quit-after S]
// The log goes to stdout (and to the config's log_file); "listening on port N" comes first.
// Console (stdin): status, list, quit. Ctrl+C or SIGTERM stops it. Exit codes: 0 stopped, 1 the
// port did not open, 2 a bad command line or config.
using System.Collections.Concurrent;
using System.Diagnostics;
using System.Globalization;
using System.Net;
using System.Runtime.InteropServices;
using RetroEng.Diagnostics;
using RetroEng.Net.Master;
using SkyNet.Master;

var console = TextLogSink.Console(plain: false);
Log.AddSink(console, LogLevel.Info);
FileLogSink? file = null;
try
{
    string? configPath = null;
    int? port = null;
    IPAddress? bind = null;
    var quitAfter = 0.0;
    for (var i = 0; i < args.Length; i++)
    {
        var a = args[i];
        var eq = a.IndexOf('=');
        var key = eq > 0 ? a[..eq] : a;
        string Value() => eq > 0 ? a[(eq + 1)..] : i + 1 < args.Length ? args[++i] : "";
        switch (key)
        {
            case "--config": configPath = Value(); break;
            case "--port":
                port = int.TryParse(Value(), NumberStyles.None, CultureInfo.InvariantCulture, out var p) && p <= 65535 ? p : Fail("--port takes 0-65535");
                break;
            case "--bind":
                bind = IPAddress.TryParse(Value(), out var b) ? b : throw new ArgumentException("--bind takes an IP address");
                break;
            case "--quit-after":
                quitAfter = double.TryParse(Value(), NumberStyles.Float, CultureInfo.InvariantCulture, out var q) && q >= 0 ? q : Fail("--quit-after takes seconds");
                break;
            case "--help" or "-h" or "/?":
                Console.Out.WriteLine("SkyNet.Master [--config master.json] [--port N] [--bind IP] [--quit-after S]");
                return 0;
            default:
                Log.Error("Master", $"unknown option {key} (--config FILE, --port N, --bind IP, --quit-after S)");
                return 2;
        }
    }

    MasterConfig config;
    var logFile = "";
    try
    {
        (config, logFile) = configPath != null ? MasterSettings.Load(configPath) : (new MasterConfig(), "");
    }
    catch (Exception e) when (e is RetroEng.Json.JsonDataException or IOException or UnauthorizedAccessException)
    {
        Log.Error("Master", $"{configPath}: {e.Message}");
        return 2;
    }

    config = config with { Port = port ?? config.Port, Bind = bind ?? config.Bind };
    if (logFile.Length > 0)
    {
        var name = Path.GetFileNameWithoutExtension(logFile);
        file = FileLogSink.Open(Path.GetDirectoryName(logFile)!, name.Length > 0 ? name : "master", keep: 5, out var why);
        if (file != null)
        {
            Log.AddSink(file, LogLevel.Info);
        }
        else
        {
            Log.Warn("Master", $"no log file ({why})");
        }
    }

    Log.Info("Master", $"master server, {RuntimeInformation.OSDescription} {RuntimeInformation.ProcessArchitecture}, .NET {Environment.Version}");
    if (MasterServer.Start(config, out var error) is not { } master)
    {
        Log.Error("Master", error ?? "did not start");
        return 1;
    }

    using (master)
    {
        master.Log += line => Log.Info("Master", line);
        Log.Info("Master", $"listening on port {master.Port}{(config.Bind != null ? $" ({config.Bind})" : "")}, direct check from port {master.ProbePort}; " +
            $"heartbeat {config.HeartbeatSeconds:0} s, entries go after {config.EntryTtlSeconds:0} s, " +
            $"games {(config.Games.IsEmpty ? "any" : string.Join(", ", config.Games))}");
        return Loop(master, quitAfter);
    }
}
catch (ArgumentException e)
{
    Log.Error("Master", e.Message);
    return 2;
}
finally
{
    Log.RemoveSink(console);
    if (file != null)
    {
        Log.RemoveSink(file);
        file.Dispose();
    }
}

static int Fail(string message) => throw new ArgumentException(message);

static int Loop(MasterServer master, double quitAfter)
{
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
    var lines = new ConcurrentQueue<string>();
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
    }) { IsBackground = true, Name = "master console" };
    reader.Start();

    var clock = Stopwatch.StartNew();
    while (Volatile.Read(ref stop) == 0)
    {
        var now = clock.Elapsed.TotalSeconds;
        master.Poll(now);
        while (lines.TryDequeue(out var line))
        {
            switch (line.Trim().ToLowerInvariant())
            {
                case "":
                    break;
                case "status":
                    Log.Info("Master", $"{master.Listed} listed, {master.Pending} being checked, {master.PagesSent} pages sent, " +
                        $"{master.Introductions} introductions, {master.RateLimited} rate-limited, up {TimeSpan.FromSeconds(now):hh\\:mm\\:ss}");
                    break;
                case "list":
                    foreach (var game in master.Games())
                    {
                        foreach (var s in master.Listings(game.GameId, game.Protocol))
                        {
                            Log.Info("Master", $"{s.Info.GameId}/{s.Info.Protocol} {s.Address} {(s.Nat ? "NAT" : "direct")} '{s.Info.Name}' {s.Info.Map} " +
                                $"{s.Info.Players}/{s.Info.MaxPlayers} +{s.Info.Bots} bots{(s.Info.Password ? " password" : "")}");
                        }
                    }

                    break;
                case "quit" or "stop" or "exit":
                    Interlocked.Exchange(ref stop, 1);
                    break;
                default:
                    Log.Info("Master", "commands: status, list, quit");
                    break;
            }
        }

        if (quitAfter > 0 && now >= quitAfter)
        {
            break;
        }

        Thread.Sleep(10);
    }

    Log.Info("Master", "stopping");
    return 0;
}
