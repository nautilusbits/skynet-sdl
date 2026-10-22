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

// The command line (Help lists the options).
internal sealed record Options(string? Data, int? Map, Vector3? Position, float? Yaw, float? Pitch, Detail? Detail,
    string? Screenshot, int Frames, GpuBackend Gpu, bool NoVsync = false,
    int? WalkTest = null, Vector2? WalkTo = null, bool WalkRun = false, int WalkEvery = 30,
    bool NoMusic = false, bool NoSound = false, bool Offscreen = false, (string Path, double Seconds)? MusicWav = null, bool FireTest = false, bool FireDemo = false, bool HiresHud = false,
    int? Mission = null, string? PlayScript = null, bool NoBriefing = false, DifficultyLevel? Difficulty = null, string? MenuScript = null,
    int? Width = null, int? Height = null, bool Fullscreen = false, bool ConsoleLog = false, string? Console = null,
    bool DebugOverlay = false, bool Fps = false, Look? Look = null, HudStyle? HudStyle = null, bool Freeze = false, bool Touch = false,
    ShadowQuality? Shadows = null, AntiAliasing? AntiAliasing = null, string? Mod = null, bool Bake = false, bool Rebake = false,
    bool GpuDebug = false, System.Net.IPAddress? NetBind = null, string? NetMaster = null, string? Record = null, string? PlayDemo = null,
    bool TimeDemo = false, string? GenHires = null, string? Banks = null, string? GenRelief = null, string? GenMaterials = null,
    string? ModsDir = null, int? EditorLink = null, StereoOutput? Stereo = null, int? Workers = null,
    RetroEng.Platform.HeadTracking? Head = null, RetroEng.Platform.AimCoupling? Aim = null, string? Viewer = null,
    int? CanvasDepth = null, bool StereoGrid = false, bool Churn = false, LoopThreads Threads = LoopThreads.Split,
    float? ScreenMm = null, RenderStyle? Style = null, string? Capture = null, int CaptureFps = FrameCapture.DefaultFps, RobotAi? RobotAi = null,
    bool FixedStep = false, string? LogDir = null)
{
    /// <summary>The animation clock (ms) under --freeze.</summary>
    public const long FrozenClockMs = 1000;

    /// <summary>The map without --map (the tools: --walk-test, --fire-test, --music-wav).</summary>
    public const int DefaultMap = 210;

    /// <summary>--map, else <see cref="DefaultMap"/>.</summary>
    public int MapOrDefault => Map ?? DefaultMap;

    // The disclaimer under the name in --help; the editor's About box says the same (AboutBox.Notice).
    private const string Disclaimer = "Unofficial fan port. Not affiliated with Bethesda. Requires the original game data.";

    /// <summary>The command line, as --help prints it.</summary>
    public const string Help = $"""
        SkyNet - SkyNET and Future Shock
        {Disclaimer}
        Usage: SkyNet [arguments]

           --data <dir>        the game's data folder (else SKYNET_DATA, else the usual places)
           --mod <name>[,name] mods beside the exe, in load order (later wins): mods/<name>/tables/<table>.json
                               replaces a shipped table; textures/ and models/ (folder or <name>.zip) change
                               the ENHANCED look (docs/modding.md). For this run only: it stands in for the
                               MODS screen's list (the settings), which it leaves as it is
           --no-mods           no user mods this run, whatever the MODS screen's list says
           --mission N         starts mission N of the mission table (its mission screen, its first map)
           --map N             starts on MAP.NNN, in the mission of its decade
           --no-briefing       no mission screen at a mission start
           --mods-dir <folder> the mods folder (default: mods beside the exe; SkyNetEdit's play-test passes its own)
           --editor-link <port> SkyNetEdit's play-test channel (127.0.0.1 UDP): its reloads rebuild the map in play
           --bind IP           a network game started from the menus serves (and answers the LAN
                               browser) on this address only (checks: 127.0.0.1)
           --master HOST:PORT  the master server of the join screen's INTERNET tab and of PUBLIC games for
                               this run (else the settings' net.master, else masters.txt beside the exe;
                               docs/multiplayer.md "Master server")
           --dedicated --config server.json   a dedicated server: no window, GPU or sound (SDL is never
                               loaded), the log on stdout and in the config's log file, commands on stdin
                               (docs/multiplayer.md "Dedicated server"); --password-hash TEXT prints a
                               password_sha256 for the config
           --menu-script "steps"   drives the menus for checks, one step a frame, ';' between steps:
                               up/down/left/right/accept/back (menu keys), esc (the Esc key), select ID
                               (highlight a menu item), wait N (frames), sleep MS (wall-clock milliseconds),
                               shot file.bmp, print, die (the
                               soldier dies: MISSION FAILED, then RESTART MISSION?), key NAME (a game
                               action by name, "Quick save" / "Quick load"), hold NAME / release NAME (a game
                               action held down across frames, "Turn left"), pad BUTTON (a gamepad button
                               pressed: "A", "RT", "D-pad up"), type TEXT (typed, as
                               SDL's text input gives it), run COMMAND (a console command, the console
                               shut: "run win" ends the mission), quit
           --play-script file  headless: plays a script of steps through the game session (see
                               SkyNet.Game.Campaign.PlayScript) and exits 1 when a check fails; with
                               --screenshot it runs windowed and shows where the script ended
           --pos x,y,z         the eye (world units, Y up, -Z north: DOS (x, -y, -z)), in noclip;
                               default the player start, walking
           --yaw deg --pitch deg   the view (yaw about +Y, 0 looking -Z / north; pitch up positive)
           --difficulty low|med|high   DIFFICULTY: the robots' fire rate and the damage both ways (else the setting)
           --robot-ai classic|improved   ROBOT AI for this run (else the setting, IMPROVED): DOS's robots, or
                               movement round obstacles, a memory of the soldier and no shots into walls
           --detail low|med|high   RENDER DETAIL: far clip, depth-cue start, HD textures' size (else the setting; first start: by GPU)
           --screenshot file.bmp   saves the last frame; --frames N quits after N frames
           --capture <dir>     a video's frames: every frame one fixed step of 1/fps s (not the wall
                               clock; one thread, no VSync) and the window, HUD included, saved as
                               <dir>\frame_000000.bmp, ... (--offscreen works); --capture-fps N (default 60),
                               --capture-frames N quits after N frames (as --frames). docs/video.md
           --fixed-step        the capture's fixed step without saving frames: a --menu-script shot
                               (hold Fire; wait 3) lands on the same tick every run
           --gpu d3d12|vulkan  the backend; --novsync draws unpaced (frame-time measurements)
           --gpu-debug         SDL_GPU's debug mode: the D3D12 debug layer (Windows' Graphics Tools) or the
                               Vulkan validation layers (the Vulkan SDK), their messages in the log
           --width N --height N    the window's size for this run (else the setting, 1280 x 720)
           --fullscreen        fullscreen in the desktop's resolution for this run (else the setting)
           --walk-test N       headless: walks forward from the start (or --pos) for N ticks at 60 Hz and
                               prints the feet every half second; --walk-to x,z steers towards a point,
                               --walk-run holds RUN, --walk-every N prints every N ticks (default 30)
           --fire-test         headless: from the start (or --pos/--yaw/--pitch) fires the UZI, swings the
                               pipe, throws a pipe bomb and fires a rocket, and prints every hit
           --fire-demo         windowed: fires the UZI for a second and throws a pipe bomb (screenshots)
           --threads single|split  single: all on main; split (default): main
                               presents, the simulation ticks and the frame build records on threads of their own
           --churn             stress: a worker thread makes, updates and destroys textures and meshes every frame
           --no-music          no score; --no-sound mutes everything
           --offscreen         a hidden window and no sound (checks and screenshots that must not take the
                               desktop's focus or make noise); the environment variable SKYNET_OFFSCREEN=1
                               does the same for every run
           --hires-hud         the 640 x 480 panel art (MDMDHRES.BSA), else the setting
           --hud classic|modern   DISPLAY's HUD STYLE for this run (else the setting)
           --music-wav out.wav [seconds]   renders the map's song (default 30 s) to a WAV file and quits
           --console "lines"   opens the developer console and runs the lines (';' between commands) once
                               the game has started (checks, screenshots); the history is not kept then
           --console-log       the log on stdout too, into the cmd window the game was started from (the
                               game has no console window of its own); the log file is always written
           --log-dir dir       the log files in dir (Android: the app's folder on the shared storage), else
                               logs\ beside the game or the app-data folder
           --debug-overlay     the debug overlay on for this run (F12 / `debug` keep it in the settings)
           --fps               the FPS readout on for this run (F11 / `fps` keep it in the settings)
           --touch             the on-screen touch controls always shown in play (Android shows them while
                               fingers play): their layout on the desktop
           --look original|enhanced   the DISPLAY screen's LOOK for this run (else the setting)
           --shadows off|low|high     ENHANCED OPTIONS' SHADOWS for this run (else the setting)
           --renderer default|ascii|pointcloud   DISPLAY's RENDERER for this run (else the setting)
           --stereo <layout>          stereo output for this run, not saved (off, sbs, sbsr, sbsfull, crossview,
                                      tab, tabr, int, intr, viewer; else the setting; the console's stereo)
           --canvas-depth <units>     the HUD's and menus' depth in stereo for this run (0: zero parallax)
           --viewer <lens>            the phone viewer's lens for this run (generic, cardboard1, cardboard2, flat)
           --stereo-grid              the stereo output shows a test grid in place of the eyes (the lens alone)
           --screen-mm <mm>           the screen's width for the phone viewer's lenses (else the phone's; desktop tests)
           --head off|gyro|scripted   head tracking for this run (scripted: a slow sweep, for desktop tests)
           --aim coupled|decoupled    whether the head turns the aim (decoupled: the body follows slowly)
           --aa off|fxaa|msaa2|msaa4  ENHANCED OPTIONS' ANTI-ALIASING for this run (else the setting)
           --bake              bakes the map blocks that are missing or stale (mods/skynet.zip, and
                               mods/shock.zip when Future Shock's data is beside) and exits; --rebake
                               bakes every map again. A normal start does the same for the game played
                               ("PREPARING GAME DATA" over the title) before the title comes up
           --record NAME       records a demo from the start of --mission / --map to demos\NAME.sndemo
                               beside the game (written when the run ends or on `stop`; docs/demos.md)
           --playdemo NAME     plays demos\NAME.sndemo (a path works too) and reports a desync;
                               --timedemo NAME plays it as fast as it renders, reports the frame rate and quits.
                               A network demo (recorded with --record NAME beside --host / --join, or the
                               console's record in a network game) plays its messages back: no desync check
           --freeze            no simulation ticks once the map is in and a fixed animation clock (the
                               level's records, the water's shimmer, the robots, the drops, the weapon):
                               two runs draw the same frame (tools/lookshots.ps1's regression shots)
           --workers N         the job pool's worker threads for this run (0: none; else the setting)
           --host=MAP.NNN | --join=HOST[:PORT] [...]   a network deathmatch in the window
                               (docs/multiplayer.md "Running it" lists its options)
           --help              this text
        """;

    /// <summary>The options, or null with the reason in <paramref name="error"/>.</summary>
    public static Options? Parse(string[] args, out string? error)
    {
        error = null;
        var o = new Options(null, null, null, null, null, null, null, int.MaxValue, GpuBackend.Auto);
        if (Environment.GetEnvironmentVariable("SKYNET_OFFSCREEN") == "1")
        {
            o = o with { Offscreen = true, NoSound = true, NoMusic = true };
        }

        var c = CultureInfo.InvariantCulture;
        try
        {
            for (var i = 0; i < args.Length; i++)
            {
                var value = i + 1 < args.Length ? args[i + 1] : null;
                switch (args[i])
                {
                    case "--data": o = o with { Data = value }; i++; break;
                    case "--mod": o = o with { Mod = value ?? throw new FormatException("--mod needs a name") }; i++; break;
                    case "--no-mods": o = o with { Mod = "" }; break;
                    case "--mods-dir": o = o with { ModsDir = Path.GetFullPath(value ?? throw new FormatException("--mods-dir needs a folder")) }; i++; break;
                    case "--editor-link": o = o with { EditorLink = int.Parse(value!, c) }; i++; break;
                    case "--map": o = o with { Map = int.Parse(value!, c) }; i++; break;
                    case "--mission": o = o with { Mission = int.Parse(value!, c) }; i++; break;
                    case "--play-script": o = o with { PlayScript = value ?? throw new FormatException("--play-script needs a file") }; i++; break;
                    case "--no-briefing": o = o with { NoBriefing = true }; break;
                    case "--menu-script": o = o with { MenuScript = value ?? throw new FormatException("--menu-script needs steps") }; i++; break;
                    case "--bind": o = o with { NetBind = System.Net.IPAddress.Parse(value ?? throw new FormatException("--bind needs an address")) }; i++; break;
                    case "--master": o = o with { NetMaster = value ?? throw new FormatException("--master needs HOST:PORT") }; i++; break;
                    case "--yaw": o = o with { Yaw = float.Parse(value!, c) }; i++; break;
                    case "--pitch": o = o with { Pitch = float.Parse(value!, c) }; i++; break;
                    case "--screenshot": o = o with { Screenshot = value }; i++; break;
                    case "--frames": o = o with { Frames = int.Parse(value!, c) }; i++; break;
                    case "--capture": o = o with { Capture = value ?? throw new FormatException("--capture needs a folder") }; i++; break;
                    case "--fixed-step": o = o with { FixedStep = true }; break;
                    case "--capture-fps": o = o with { CaptureFps = FrameCapture.ParseFps(value) }; i++; break;
                    case "--capture-frames": o = o with { Frames = FrameCapture.ParseFrames(value) }; i++; break;
                    case "--gpu": o = o with { Gpu = GpuBackends.Parse(value!) ?? GpuBackend.Auto }; i++; break;
                    case "--novsync": o = o with { NoVsync = true }; break;
                    case "--gpu-debug": o = o with { GpuDebug = true }; break;
                    case "--width": o = o with { Width = WindowSide(value) }; i++; break;
                    case "--height": o = o with { Height = WindowSide(value) }; i++; break;
                    case "--fullscreen": o = o with { Fullscreen = true }; break;
                    case "--console-log": o = o with { ConsoleLog = true }; break;
                    case "--log-dir": o = o with { LogDir = value ?? throw new FormatException("--log-dir needs a folder") }; i++; break;
                    case "--debug-overlay": o = o with { DebugOverlay = true }; break;
                    case "--threads":
                        o = o with
                        {
                            Threads = value switch
                            {
                                "single" => LoopThreads.Single,
                                "split" => LoopThreads.Split,
                                _ => throw new FormatException("--threads takes single or split"),
                            },
                        };
                        i++;
                        break;
                    case "--fps": o = o with { Fps = true }; break;
                    case "--touch": o = o with { Touch = true }; break;
                    case "--freeze": o = o with { Freeze = true }; break;
                    case "--record": o = o with { Record = value ?? throw new FormatException("--record needs a name") }; i++; break;
                    case "--playdemo": o = o with { PlayDemo = value ?? throw new FormatException("--playdemo needs a name") }; i++; break;
                    case "--timedemo": o = o with { PlayDemo = value ?? throw new FormatException("--timedemo needs a name"), TimeDemo = true }; i++; break;
                    case "--bake": o = o with { Bake = true }; break;
                    case "--workers": o = o with { Workers = Math.Clamp(int.Parse(value!, c), 0, RetroEng.Jobs.JobSystem.MaxWorkers) }; i++; break;
                    // Hidden: the MODS screen's GENERATE HI-RES TEXTURES into <mods-dir> (settings untouched).
                    case "--gen-hires": o = o with { GenHires = value ?? throw new FormatException("--gen-hires needs a mods folder") }; i++; break;
                    // Hidden: the relief maps (RRR_n.png, RRR_r.png) of an existing hi-res mod folder, from its PNGs.
                    case "--gen-relief": o = o with { GenRelief = value ?? throw new FormatException("--gen-relief needs a mod folder") }; i++; break;
                    case "--gen-materials": o = o with { GenMaterials = value ?? throw new FormatException("--gen-materials needs a file") }; i++; break;
                    case "--banks": o = o with { Banks = value ?? throw new FormatException("--banks needs a list") }; i++; break;
                    case "--rebake": o = o with { Bake = true, Rebake = true }; break;
                    case "--stereo": o = o with { Stereo = StereoOutput.Parse(value) ?? throw new FormatException("--stereo takes " + string.Join(", ", StereoOutput.Names)) }; i++; break;
                    case "--head":
                        o = o with { Head = Enum.TryParse<RetroEng.Platform.HeadTracking>(value, true, out var head) ? head : throw new FormatException("--head takes off, gyro, scripted") };
                        i++;
                        break;
                    case "--aim":
                        o = o with { Aim = Enum.TryParse<RetroEng.Platform.AimCoupling>(value, true, out var aim) ? aim : throw new FormatException("--aim takes coupled, decoupled") };
                        i++;
                        break;
                    case "--viewer":
                        o = o with { Viewer = LensProfile.Preset(value) != null ? value!.ToLowerInvariant() : throw new FormatException("--viewer takes generic, cardboard1, cardboard2, flat") };
                        i++;
                        break;
                    case "--canvas-depth": o = o with { CanvasDepth = int.Parse(value ?? throw new FormatException("--canvas-depth needs a depth"), CultureInfo.InvariantCulture) }; i++; break;
                    case "--stereo-grid": o = o with { StereoGrid = true }; break;
                    case "--screen-mm": o = o with { ScreenMm = float.Parse(value ?? throw new FormatException("--screen-mm needs a width"), CultureInfo.InvariantCulture) }; i++; break;
                    case "--hud":
                        o = o with
                        {
                            HudStyle = value switch
                            {
                                "classic" => SkyNet.Game.Hud.HudStyle.Classic,
                                "modern" => SkyNet.Game.Hud.HudStyle.Modern,
                                _ => throw new FormatException("--hud takes classic or modern"),
                            },
                        };
                        i++;
                        break;
                    case "--look":
                        o = o with
                        {
                            Look = value switch
                            {
                                "original" => SkyNet.Game.Menu.Look.Original,
                                "enhanced" => SkyNet.Game.Menu.Look.Enhanced,
                                _ => throw new FormatException("--look takes original or enhanced"),
                            },
                        };
                        i++;
                        break;
                    case "--renderer":
                        o = o with { Style = ViewStyles.Words.TryGetValue(value ?? "", out var style) ? style : throw new FormatException("--renderer takes default, ascii or pointcloud") };
                        i++;
                        break;
                    case "--shadows":
                        o = o with
                        {
                            Shadows = value switch
                            {
                                "off" => ShadowQuality.Off,
                                "low" => ShadowQuality.Low,
                                "high" => ShadowQuality.High,
                                _ => throw new FormatException("--shadows takes off, low or high"),
                            },
                        };
                        i++;
                        break;
                    case "--aa":
                        o = o with
                        {
                            AntiAliasing = value switch
                            {
                                "off" => RetroEng.Render.AntiAliasing.Off,
                                "fxaa" => RetroEng.Render.AntiAliasing.Fxaa,
                                "msaa2" => RetroEng.Render.AntiAliasing.Msaa2,
                                "msaa4" => RetroEng.Render.AntiAliasing.Msaa4,
                                _ => throw new FormatException("--aa takes off, fxaa, msaa2 or msaa4"),
                            },
                        };
                        i++;
                        break;
                    case "--console": o = o with { Console = value ?? throw new FormatException("--console needs commands") }; i++; break;
                    case "--walk-test": o = o with { WalkTest = int.Parse(value!, c) }; i++; break;
                    case "--walk-run": o = o with { WalkRun = true }; break;
                    case "--no-music": o = o with { NoMusic = true }; break;
                    case "--no-sound": o = o with { NoSound = true }; break;
                    case "--offscreen": o = o with { Offscreen = true, NoSound = true, NoMusic = true }; break;
                    case "--fire-test": o = o with { FireTest = true }; break;
                    case "--fire-demo": o = o with { FireDemo = true }; break;
                    case "--churn": o = o with { Churn = true }; break;
                    case "--hires-hud": o = o with { HiresHud = true }; break;
                    case "--music-wav":
                        var seconds = 30.0;
                        if (i + 2 < args.Length && double.TryParse(args[i + 2], NumberStyles.Float, c, out var sec))
                        {
                            seconds = sec;
                            i++;
                        }

                        o = o with { MusicWav = (value ?? throw new FormatException("--music-wav needs a file"), seconds) };
                        i++;
                        break;
                    case "--walk-every": o = o with { WalkEvery = Math.Max(1, int.Parse(value!, c)) }; i++; break;
                    case "--walk-to":
                        var w = value!.Split(',').Select(s => float.Parse(s, c)).ToArray();
                        o = o with { WalkTo = new Vector2(w[0], w[1]) };
                        i++;
                        break;
                    case "--detail":
                        o = o with { Detail = value switch { "low" => SkyNet.Game.Detail.Low, "med" or "medium" => SkyNet.Game.Detail.Medium, _ => SkyNet.Game.Detail.High } };
                        i++;
                        break;
                    case "--difficulty":
                        o = o with
                        {
                            Difficulty = value switch
                            {
                                "low" => DifficultyLevel.Low,
                                "med" or "medium" => DifficultyLevel.Medium,
                                "high" => DifficultyLevel.High,
                                _ => throw new FormatException("--difficulty takes low, med or high"),
                            },
                        };
                        i++;
                        break;
                    case "--robot-ai":
                        o = o with { RobotAi = RobotAis.Parse(value) ?? throw new FormatException("--robot-ai takes classic or improved") };
                        i++;
                        break;
                    case "--pos":
                        var p = value!.Split(',').Select(s => float.Parse(s, c)).ToArray();
                        o = o with { Position = new Vector3(p[0], p[1], p[2]) };
                        i++;
                        break;
                    default:
                        error = $"Unknown option {args[i]}";
                        return null;
                }
            }
        }
        catch (Exception e) when (e is FormatException or ArgumentNullException or IndexOutOfRangeException or OverflowException)
        {
            error = $"Bad command line: {e.Message}";
            return null;
        }

        // One side given: the other from the 16:9 the default has.
        if (o.Width is { } ow && o.Height == null)
        {
            o = o with { Height = Math.Max(MenuSettings.MinWindowSide, ow * MenuSettings.DefaultWindowHeight / MenuSettings.DefaultWindowWidth) };
        }
        else if (o.Height is { } oh && o.Width == null)
        {
            o = o with { Width = Math.Max(MenuSettings.MinWindowSide, oh * MenuSettings.DefaultWindowWidth / MenuSettings.DefaultWindowHeight) };
        }

        // A capture's frames follow one another on one thread: each is drawn from the clock it was made at.
        if (o.Capture != null || o.FixedStep)
        {
            o = o with { Threads = LoopThreads.Single, NoVsync = true };
        }

        if (o.Screenshot != null && o.Frames == int.MaxValue)
        {
            o = o with { Frames = 60 };
        }

        return o;
    }

    private static int WindowSide(string? value)
    {
        var n = int.Parse(value ?? throw new FormatException("--width / --height need a number of pixels"), CultureInfo.InvariantCulture);
        return n is >= MenuSettings.MinWindowSide and <= MenuSettings.MaxWindowSide
            ? n
            : throw new FormatException($"a window side is {MenuSettings.MinWindowSide} to {MenuSettings.MaxWindowSide} pixels");
    }
}
