using System.Diagnostics;
using System.Drawing;
using System.Globalization;
using System.Numerics;
using RetroEng.Audio;
using RetroEng.Commands;
using RetroEng.Diagnostics;
using RetroEng.Loop;
using RetroEng.Physics;
using RetroEng.Platform;
using RetroEng.Render;
using SkyNet.Data;
using SkyNet.Formats;
using SkyNet.Game;
using SkyNet.Game.Audio;
using SkyNet.Game.Baking;
using SkyNet.Game.Campaign;
using SkyNet.Game.Collision;
using SkyNet.Game.Demos;
using SkyNet.Game.DevTools;
using SkyNet.Game.Enhanced;
using SkyNet.Game.Enemies;
using SkyNet.Game.Hud;
using SkyNet.Game.Menu;
using SkyNet.Game.Player;
using SkyNet.Game.Vehicles;
using SkyNet.Game.Weapons;
using static AppTools;

/// <summary>The program (Program.cs on the desktop, GameActivity on Android): its command line,
/// the log, the game data, then the tools or the window's game.</summary>
internal static class GameMain
{
    public static int Run(string[] args)
    {
        // --help: the options into the cmd window the game was started from, else a message box
        // (started from Explorer or a shortcut, there is no console to print to).
        if (args.Any(a => a is "--help" or "-h" or "-?" or "/?"))
        {
            if (TextLogSink.AttachParentConsole())
            {
                Console.WriteLine(Options.Help);
            }
            else
            {
                SystemDialog.Info("SkyNET", Options.Help);
            }

            return 0;
        }

        // A dedicated server (--dedicated --config server.json, docs/multiplayer.md): no window, no GPU, no
        // sound; SDL is never loaded on this path (nothing below runs).
        if (SkyNet.Game.Net.DmDedicatedRunner.Wants(args))
        {
            return SkyNet.Game.Net.DmDedicatedRunner.Run(args);
        }

        // A network deathmatch (--host / --join, docs/multiplayer.md): in the game's window, its own
        // options split off the game's; headless with --smoke (tools/net-smoke.ps1) or --headless.
        SkyNet.Game.Net.DmRunOptions? netRun = null;
        string? netError = null;
        var gameArgs = args;
        if (SkyNet.Game.Net.DmHeadless.Wants(args))
        {
            if (SkyNet.Game.Net.DmHeadless.Headless(args))
            {
                return SkyNet.Game.Net.DmHeadless.Run(args);
            }

            (var netArgs, gameArgs) = SkyNet.Game.Net.DmHeadless.Split(args);
            netRun = SkyNet.Game.Net.DmHeadless.Parse(netArgs, out netError);
        }

        var options = Options.Parse(gameArgs, out var badCommandLine);
        using var logScope = new LogScope();
        var tool = options is { WalkTest: not null } or { FireTest: true } or { MusicWav: not null } or { PlayScript: not null, Screenshot: null };
        // A player's run (no frame count, no menu script): a crash shows a message box. Automated runs
        // must not stop on one.
        var interactive = options is { Frames: int.MaxValue, MenuScript: null, PlayScript: null };
        var ring = new LogRing(500);
        var logFile = StartLog(options, tool, ring, args);
        // No message box for a hidden window (--offscreen): nobody is there to close it.
        var crash = new CrashHandler(ring, interactive && !tool && options?.Offscreen != true ? ShowCrash : null);
        crash.Install();
        // A crash in native code (SDL, the driver) leaves its last line in the log too.
        NativeCrashLog.Install();
        // SDL's own errors (and SDL_GPU's debug layer under --gpu-debug) into the log.
        SdlLog.Install(verbose: options?.GpuDebug == true);
        if (options == null)
        {
            Log.Error("App", badCommandLine ?? "Bad command line");
            return 2;
        }

        if (netError != null)
        {
            Log.Error("Net", netError);
            return 2;
        }

        // The job pool's size, before anything uses it: --workers, else the settings' "workers", else the
        // engine's pick. It cannot change while the game runs (the console's workers saves it for the next start).
        var workers = options.Workers ?? (SettingsStore.Load(SettingsStore.DefaultPath).Workers is var w and >= 0 ? w : null);
        RetroEng.Jobs.JobSystem.Configure(workers);
        if (!tool)
        {
            Log.Info("App", $"Job pool: {RetroEng.Jobs.JobSystem.Shared.Workers} workers ({(workers == null ? "auto" : options.Workers != null ? "--workers" : "settings")}, {Environment.ProcessorCount} cores)");
        }

        // The system mods (the baked maps of each data set): never in a user mod list.
        var systemMods = new[] { GameKind.SkyNet, GameKind.FutureShock }.Select(BlockStore.SystemModName).ToArray();
        // --mod a,b (or --no-mods): this run's mods in load order (later wins), over the MODS screen's list.
        var modsOverride = options.Mod != null ? SkyNet.Game.Mods.ModOrder.Parse(options.Mod) : null;
        // The list last mounted for textures and models, and for the tables (the MODS screen's changes
        // reach them at the next map and at a game's start: ApplyMods).
        string[] modsMounted = [], tableModsMounted = [];
        if (modsOverride is { Count: > 0 })
        {
            // Textures, models and tables come from all of them, folders or ZIPs, through the mods' file
            // system (SkyNet.Game.Mods.ModContent, docs/modding.md).
            modsMounted = tableModsMounted = SkyNet.Game.Mods.ModOrder.Effective([], modsOverride, systemMods);
            SkyNet.Game.Mods.ModContent.Mount(options.ModsDir ?? Path.Combine(AppContext.BaseDirectory, "mods"), modsMounted);
        }

        var dataDir = GameDataLocator.Locate(options.Data ?? Environment.GetEnvironmentVariable("SKYNET_DATA"), null,
            [AppContext.BaseDirectory, Environment.CurrentDirectory]);
        var data = dataDir != null ? GameData.Open(dataDir) : null;
        if (data == null)
        {
            return Fatal("No game data found: pass --data <dir> or set SKYNET_DATA to the folder holding MDMDMAP2.BSA.");
        }

        if (!tool)
        {
            Log.Info("App", $"Data: {data.Game} in a folder named {LogHeader.FolderName(data.Dir)}");
        }

        // The map blocks live in the mods folder beside the game (docs/formats/map-block.md).
        var modsDir = options.ModsDir ?? Path.Combine(AppContext.BaseDirectory, "mods");
        if (options.Bake)
        {
            return BakeAll(data, modsDir, options.Rebake);
        }

        if (options.GenHires is { } hiresMods)
        {
            return GenHires(data, hiresMods, options.Banks);
        }

        if (options.GenRelief is { } reliefMod)
        {
            return GenRelief(data, reliefMod);
        }

        if (options.GenMaterials is { } materialsFile)
        {
            return GenMaterials(data, materialsFile);
        }

        if (options.WalkTest is { } walkTicks)
        {
            return WalkTest(data, options, walkTicks);
        }

        if (options.MusicWav is { } wavOut)
        {
            return MusicWav(data, options, wavOut);
        }

        if (options.FireTest)
        {
            return FireTest(data, options);
        }

        if (options.PlayScript is { } scriptPath && options.Screenshot == null)
        {
            return PlayHeadless(data, scriptPath, options.Difficulty ?? DifficultyLevel.Medium);
        }

        using var host = new AppHost(options, data, interactive, tool, crash, ring, netRun, modsDir, systemMods, modsOverride, modsMounted, tableModsMounted);
        return host.Run();

        // A crash, told to the player (the details are in the log).
        void ShowCrash(Exception e) => SystemDialog.Error("SkyNET",
            $"SkyNET crashed — see logs\\skynet.log next to the game.\n\n{e.GetType().Name}: {e.Message}\n\nThe log: {logFile ?? "not written"}");

        // An error that stops the program before the game starts: logged, and shown to a player.
        int Fatal(string message) => AppTools.Fatal(message, interactive && !tool);
    }
}

/// <summary>The end of the program: every log sink flushed to disk and closed.</summary>
internal sealed class LogScope : IDisposable
{
    public void Dispose() => Log.Shutdown();
}
