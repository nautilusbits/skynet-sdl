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

/// <summary>The game in its window: the window, the renderer, the sound, the developer console, the
/// menus and the running game's context (GameContext), and the fixed-tick loop that hands each
/// frame's input, ticks and draw to the app mode of the moment (<see cref="Mode"/>: the title, a
/// campaign's play, a deathmatch). Split over AppHost.*.cs and the modes' files.</summary>
internal sealed partial class AppHost : IDisposable
{
    private const int TicksPerSecond = AppTools.TicksPerSecond;
    private const float FieldOfView = 70f * MathF.PI / 180f;
    private const float Near = 1f;
    private const float FarPlane = 100000f;

    // What Program.cs found before the window: the command line, the data, the log, the mods.
    private readonly Options options;
    private readonly bool interactive;
    private readonly bool tool;
    private readonly CrashHandler crash;
    private readonly LogRing ring;
    private readonly SkyNet.Game.Net.DmRunOptions? netRun;
    private readonly string modsDir;
    private readonly string[] systemMods;
    private readonly List<string>? modsOverride;
    private string[] modsMounted, tableModsMounted;
    private GameData data;
    private readonly GameData homeData;

    // The modes (Mode picks one each time it is asked).
    private readonly TitleMode titleMode;
    private readonly PlayMode playMode;
    private readonly DeathmatchMode deathmatchMode;

    private CampaignControls controls = null!;
    private ActionSet actions = null!;
    private int pause, quickSave, quickLoad;
    private string settingsPath = null!;
    private MenuSettings settings = null!;
    private WindowMode windowMode;
    private Window window = null!;

    // The menus' text fields and the chat line as one owner of the window's text input.
    private static readonly object MenuTextField = new();
    private (DisplaySetup Display, VSync VSync, bool ShowFps) applied;
    private Renderer renderer = null!;
    private Input input = null!;
    private AudioDevice audio = null!;
    private InputBindings bindings = null!;
    private KeyBinds keyBinds = null!;
    private BitmapFont? consoleFont;
    private CommandRegistry commands = null!;
    internal DevConsole devConsole = null!;
    private ConsoleView? consoleView;
    private DevDisplays displays = null!;
    private OverlayView? overlayView;
    /// <summary>--churn's worker; null without it.</summary>
    private ResourceChurn? churn;
    private ZoneTimes zoneTimes = null!;
    private FrameStats frameStats = null!;
    /// <summary>The first seconds after a map is entered, for the log.</summary>
    private readonly LoadHitches loadHitches = new();
    private Profiler sections = null!;
    private List<ZoneTime> zoneList = null!;
    private string? fpsText;
    private IReadOnlyList<string> overlayLines = [];
    // The console's `far`: a far clip for this run over RENDER DETAIL's (never saved); null: the detail's.
    private float? farOverride;
    private ObjectiveReport objectives = ObjectiveReport.None;
    private ObjectivePointer pointer = null!;
    private long overlayMade, pointerMade, allocatedAtFrame, allocatedPerFrame;
    private int uploadsAtFrame, uploadsPerFrame;
    private GameContext game = null!;
    private SoundBank? sounds;
    private MusicPlayer music = null!;
    private GameSession session = null!;
    private PlayerController player = null!;
    private Soldier soldier = null!;
    private WeaponSystem weapons = null!;
    private ViewModel viewModel = null!;
    private WeaponRenderer weaponView = null!;
    private VehicleRig vehicles = null!;
    private CockpitRenderer cockpitView = null!;
    private DosHud hud = null!;
    private BitmapFont? bannerFont;
    private EndBanner endBanner = null!;
    private MenuPainter menuPainter = null!;
    private SaveSlots slots = null!;
    private string?[] slotNames = [];
    private SoundBank.LoopHandle? engineLoop;
    private int engineSound = -1;
    private BlockStore? blocks;

    // The map being shown: built when the session enters one, its GPU resources released with it.
    internal Level? level;
    // The enhanced look's point lights (the level's built on its first enhanced frame), as the editor gathers them.
    private readonly EnhancedEffects effects = new();
    private WeatherSound weatherSound = null!;
    private EnemyRenderer? robotView;
    private Renderer.Scope levelScope;
    private int northDegrees;
    private FixedLoop? loop;
    private bool quitting;
    // The mission screen comes up at every mission start (not for scripted runs).
    private bool showBriefing;

    // The DOS menus: the title, the Esc menu, the mission screen, the RESTART box. The LOAD/SAVE bars
    // read their names from the running game's slots; the title's LOAD shows both games' (TitleSlot).
    private string? futureShockDir;
    private MenuEvents menuEvents = null!;
    private MenuSystem? menuRef;
    // The network deathmatch (--host / --join) while one runs, its HUD, and why the server ended it.
    internal SkyNet.Game.Net.DmGame? dm;
    private SkyNet.Game.Net.DmHud dmHud = null!;
    private string? netEnded;
    // NETLEVEL.PRS for the host screen (SkyNET's, read once).
    private IReadOnlyList<NetLevel>? netLevels;
    internal MenuSystem menu = null!;
    private Queue<string>? menuScript;
    private int scriptWait;
    private long scriptSleepUntil;
    private string? pendingShot;
    // --fire-demo: the ticks played so far.
    private int demoTicks;
    // Demos (docs/demos.md): the recording under way, or the demo playing (with its name, the
    // timedemo's clock and frames, the playback speed and pause).
    private CampaignRecorder? recording;
    private CampaignPlayback? playback;
    private string? playbackName;
    private long timedemoStart;
    private int timedemoFrames;
    private bool timedemoQuits;
    private bool demoPaused;
    private double demoSpeed = 1.0;
    // A network demo reached its end (held on its last frame until stop or another demo).
    private bool demoOver;
    private long lastMenuFrame;
    private bool hudArtInLevelScope;
    private SessionEvents events = null!;
    // The frames' times (stats, the last frame's mean).
    private List<double> frameTimes = null!;
    private int rendered;
    private long lastFrame;
    // --capture: the frames saved and the clock they drive; null: the wall clock.
    private readonly FrameCapture? capture;
    // --capture's sound (audio.wav beside the frames): the mixer's only consumer then.
    private AudioCapture? soundCapture;

    public AppHost(Options options, GameData data, bool interactive, bool tool, CrashHandler crash, LogRing ring,
        SkyNet.Game.Net.DmRunOptions? netRun, string modsDir, string[] systemMods, List<string>? modsOverride,
        string[] modsMounted, string[] tableModsMounted)
    {
        (this.options, this.data, this.interactive, this.tool, this.crash, this.ring, this.netRun) = (options, data, interactive, tool, crash, ring, netRun);
        (this.modsDir, this.systemMods, this.modsOverride, this.modsMounted, this.tableModsMounted) = (modsDir, systemMods, modsOverride, modsMounted, tableModsMounted);
        // SkyNET's data is home: the title is always its.
        homeData = data;
        // --fixed-step: the capture's clock without its frames (a scripted shot the same every run).
        capture = options.Capture is { } dir ? new FrameCapture(dir, options.CaptureFps)
            : options.FixedStep ? new FrameCapture(null, options.CaptureFps) : null;
        (titleMode, playMode, deathmatchMode) = (new TitleMode(this), new PlayMode(this), new DeathmatchMode(this));
    }

    /// <summary>The app mode of the moment: a deathmatch while one runs (its join screen too), the
    /// title with no map or under a menu that is not the game's, else the campaign's play.</summary>
    internal IAppMode Mode => dm != null ? deathmatchMode : level == null || menu.IsOpen && !menu.InGame ? titleMode : playMode;

    /// <summary>The window up, the game started as the command line asks (or the title), the loop run
    /// to its end: the exit code.</summary>
    public int Run()
    {
        // The game's actions (their ids are the tick input's bits, a demo's too): SkyNet.Game's CampaignControls.
        controls = CampaignControls.Create();
        actions = controls.Actions;
        (pause, quickSave, quickLoad) = (controls.Pause, controls.QuickSave, controls.QuickLoad);

        // The settings file (beside the game); the command line overrides them for this run only.
        settingsPath = SettingsStore.DefaultPath;
        if (interactive && SettingsStore.CopyFromAppData() is var moved and > 0)
        {
            Log.Info("App", $"Copied {moved} file(s) (settings, saves) from the app-data folder beside the game; the originals stay");
        }

        settings = SettingsStore.Load(settingsPath);
        settings.Detail = options.Detail ?? settings.Detail;
        settings.Difficulty = options.Difficulty ?? settings.Difficulty;
        settings.RobotAi = options.RobotAi ?? settings.RobotAi;
        settings.HudArt = options.HiresHud ? HudArtSet.HiRes : settings.HudArt;
        settings.HudStyle = options.HudStyle ?? settings.HudStyle;
        settings.Look = options.Look ?? settings.Look;
        settings.Style = options.Style ?? settings.Style;
        settings.Shadows = options.Shadows ?? settings.Shadows;
        settings.AntiAliasing = options.AntiAliasing ?? settings.AntiAliasing;
        // MODS: the settings' list, unless --mod / --no-mods gave this run's.
        ApplyMods(tables: true);


        // The window: the chosen size (not resizable by hand), or the whole desktop. The command line's
        // size and fullscreen are for this run only, never saved.
        var windowSize = new Resolution(options.Width ?? settings.WindowWidth, options.Height ?? settings.WindowHeight);
        windowMode = options.Fullscreen ? WindowMode.Fullscreen : options.Width != null || options.Height != null ? WindowMode.Windowed : settings.WindowMode;
        StartSplash(data, "Starting the renderer...");
        window = new Window("SkyNET", windowSize.Width, windowSize.Height,
            options.Offscreen ? Visibility.Hidden : UsesSplash ? Visibility.Deferred : Visibility.Shown, resizable: false);
        if (options.Touch)
        {
            window.ForceTouch();
        }

        // The DISPLAY screen's window, VSync and FPS readout are applied when they change from the file's
        // (Applied), so this run's command line holds until the player picks something else.
        applied = (Display: DisplayOf(settings), settings.VSync, settings.ShowFps);
        window.Apply(options.Fullscreen || options.Width != null || options.Height != null
            ? new DisplaySetup(windowMode == WindowMode.Fullscreen ? Fullscreen.Exclusive : Fullscreen.Off, windowSize, null)
            : applied.Display);
        Log.Info("Window", $"{windowMode} {(windowMode == WindowMode.Windowed ? windowSize.ToString() : window.Desktop.Size.ToString())}");
        renderer = new Renderer(window, options.Gpu, options.GpuDebug);
        churn = options.Churn ? ResourceChurn.Start(renderer) : null;
        // A hidden window draws offscreen at 1280 x 960, or at the size this run asked for.
        if (options.Width != null || options.Height != null)
        {
            renderer.HiddenSize = windowSize;
        }

        // .3D faces wind counter-clockwise seen from their visible side; DOS culls the back.
        renderer.FrontFace = Winding.CounterClockwise;
        renderer.VSync = options.NoVsync ? VSync.Off : settings.VSync;
        renderer.RenderScale = settings.RenderScale;
        renderer.Gamma = settings.Gamma;
        ViewStyles.Apply(renderer, settings);

        input = new Input(actions);
        Log.Info("Render", $"GPU: {renderer.Gpu.Device} on {GpuBackends.Name(renderer.Gpu.Backend)}, driver {renderer.Gpu.DriverVersion}");
        // A first start (no DETAIL saved, none asked): guessed from the GPU; phones get LOW.
        if (options.Detail == null && !SettingsStore.HasDetail(settingsPath))
        {
            settings.Detail = OperatingSystem.IsAndroid() ? Detail.Low : DetailGuess.Pick(renderer.Gpu.Device);
            Log.Info("Render", $"DETAIL {settings.Detail} (first start, guessed from the GPU)");
        }

        // A phone's first start: the MODERN HUD (the panel's text is too small there). The file writes
        // DETAIL and HUD STYLE together, so no DETAIL means no HUD STYLE saved either.
        if (OperatingSystem.IsAndroid() && options.HudStyle == null && !SettingsStore.HasDetail(settingsPath))
        {
            settings.HudStyle = HudStyle.Modern;
        }

        Log.Info("Render", $"Look: {settings.Look}{(options.Freeze ? ", frozen" : "")}");

        // Sound: the effects bank and the score on one mixer; muted when asked (the mix still runs).
        audio = new AudioDevice(options.NoSound || capture != null ? Output.Muted : Output.Speakers);
        // --capture: no device; each frame pulls its share of the mix into audio.wav (--offscreen too).
        soundCapture = capture?.Dir is { } captureDir ? new AudioCapture(Path.Combine(captureDir, "audio.wav"), audio.Mixer, capture.Fps) : null;
        // A mod's weather: the storm's thunder towards the bolt, the rain's hiss.
        weatherSound = new WeatherSound(audio.Mixer);
        effects.Storm.Thunder = weatherSound.Thunder;
        bindings = new InputBindings(input);
        Rebind.Restore(bindings, settings.Bindings);
        PadRebind.Restore(bindings, settings.PadBindings);
        keyBinds = new KeyBinds(bindings);
        keyBinds.Restore(settings.CommandBinds);
        // Every action's keys in the settings (an action the file lacks keeps its default), so the file
        // lists them all once written; a player's first run writes it at once, defaults and all.
        (settings.Bindings, settings.PadBindings) = (Rebind.Snapshot(bindings), PadRebind.Snapshot(bindings));
        if (interactive && !tool && !options.Offscreen && SettingsStore.CreateIfMissing(settingsPath, bindings))
        {
            Log.Info("App", $"Wrote the default settings to {SettingsStore.FileName}");
        }

        // What belongs to one game's data (GameContext): the session (soldier, weapons, mission), the sounds
        // and score, the HUD, the weapon and menu art, the save slots. The locals below are its parts,
        // taken again by Adopt when NEW GAME → FUTURE SHOCK (or QUIT back to SkyNET's title) switches games.
        // SkyNET's data is home: the title is always its.
        // The developer console: the log's ring and the commands' output, in the hi-res HUD's small font
        // (SkyNET's folder has it for both games), made before any scope so it lives with the renderer.
        consoleFont = HudFont.Load(data, HudFont.FileName(HudFont.HiResFont(HudFont.HudText))) ?? HudFont.Load(data, HudFont.FileName(HudFont.HudText));
        consoleFont?.Upload(renderer);
        commands = new CommandRegistry();
        devConsole = new DevConsole(commands, interactive ? new CommandHistory(ConsoleHistoryPath(settingsPath)) : new CommandHistory(), ring)
        {
            Printable = c => consoleFont == null || c == ' ' || consoleFont.TryGetGlyph(c, out _),
        };
        consoleView = consoleFont != null ? new ConsoleView(renderer, consoleFont) { Banner = BuildInfo.Banner } : null;
        // The developer displays (F11 the FPS readout, F12 the debug overlay): the settings', or this run's
        // from the command line. With the overlay on, a zone timer becomes the profiler backend.
        displays = new DevDisplays();
        // Under --freeze neither: their numbers change from run to run.
        displays.Restore(!options.Freeze && (settings.ShowFps || options.Fps), !options.Freeze && (settings.DebugOverlay || options.DebugOverlay));
        overlayView = consoleFont != null ? new OverlayView(renderer, consoleFont) : null;
        zoneTimes = new ZoneTimes();
        frameStats = new FrameStats();
        // The frame's parts for the overlay: tick (Scripts), draw (Scene), present (Render), audio (Audio).
        sections = new Profiler();
        zoneList = new List<ZoneTime>();
        pointer = new ObjectivePointer();
        allocatedAtFrame = GC.GetAllocatedBytesForCurrentThread();
        uploadsAtFrame = renderer.UploadSubmits;
        ApplyZoneTimer();
        displays.Changed += d =>
        {
            ApplyZoneTimer();
            (fpsText, overlayLines, overlayMade, pointerMade) = (null, [], 0, 0);
            if (interactive)
            {
                // Kept in the settings file; the rest of the file as it is on disk (this run's command-line
                // overrides stay out of it).
                (settings.ShowFps, settings.DebugOverlay) = (d.Fps, d.Debug);
                var stored = SettingsStore.Load(settingsPath);
                (stored.ShowFps, stored.DebugOverlay) = (d.Fps, d.Debug);
                SettingsStore.Save(stored, settingsPath);
            }
        };
        SplashStatus("Loading data...");
        game = GameContext.Create(data, renderer, audio.Mixer, settings.HudArt);
        endBanner = new EndBanner(renderer);
        Adopt(game);
        Log.Info("Audio", $"{(audio.IsOpen ? "device open" : "muted")}; effects {(sounds != null ? SoundBank.Archive : "missing")}");

        SplashStatus("Checking the map blocks...");
        blocks = OpenBlocks(data);

        levelScope = renderer.Mark();
        showBriefing = !options.NoBriefing && options.PlayScript == null;

        futureShockDir = homeData.Game == GameKind.SkyNet ? GameDataLocator.OtherGameDir(homeData.Dir) : null;
        menuEvents = new MenuEvents();
        dmHud = new SkyNet.Game.Net.DmHud(renderer);
        menu = new MenuSystem(menuEvents, settings, new MenuContext
        {
            FutureShockAvailable = futureShockDir != null,
            EnhancedAvailable = true,
            Window = OperatingSystem.IsAndroid() ? WindowKind.FixedFullscreen : WindowKind.Choosable,
            RenderScaleInUse = () => renderer.RenderScaleInUse,
            Bindings = bindings,
            Display = () => new DisplayModes(window.Desktop.Size, window.Usable, [.. window.ExclusiveModes().Select(m => m.Size)]),
            DefaultPilot = kind => kind == session.Data.Game ? session.DefaultPilot : Pilot.Default,
            SlotName = i => menuRef is { InGame: false } ? TitleSlot(i)?.Label : i >= 0 && i < slotNames.Length ? slotNames[i] : null,
            GamepadName = () => window.GamepadName,
            Rumble = window.Rumble,
            NetLevels = () => netLevels ??= NetLevelList.Load(homeData),
            NetConnecting = () => dm is { IsHost: false, LevelUp: false },
            MasterAddress = () => SkyNet.Game.Net.MasterAddresses.Pick(options.NetMaster, settings.Net.Master, AppContext.BaseDirectory),
            MasterBrowser = address => RetroEng.Net.Master.MasterBrowser.Start(address, SkyNet.Game.Net.DmProtocol.GameId(homeData.Game), SkyNet.Game.Net.DmProtocol.Version),
            ModList = () => SkyNet.Game.Mods.ModCatalog.Scan(modsDir, systemMods),
            ModsOverride = modsOverride,
            HiresTextures = () => SkyNet.Game.Mods.ModContent.HasTextures,
            GenerateHires = () => SkyNet.Game.Mods.HiresJob.Start(data, modsDir, Path.Combine(AppContext.BaseDirectory, "cache"), SkyNet.Upscale.UpscalerSource.Default),
        }, settingsPath);
        menuRef = menu;
        menuScript = options.MenuScript is { } steps
            ? new Queue<string>(steps.Split(';', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries))
            : null;
        lastMenuFrame = MenuClock();
        ApplySettings(settings);

        events = new SessionEvents
        {
            OnMapEntered = EnterLevel,
            OnMissionStarted = (mission, briefing, restart) =>
            {
                hud.ClearMessages();
                // A demo playing goes on through the mission screen (its ticks never stood still for it).
                if (showBriefing && playback == null)
                {
                    menu.OpenMission(new MissionBriefing(data.Game, mission.Number, mission.StartMap, briefing, restart)
                    {
                        StatisticsHeader = session.StatsHeader(session.BriefingStats),
                        Pilot = session.Pilot,
                        Statistics = session.StatsRows(session.BriefingStats),
                    });
                    ReleaseMouse();
                }
            },
            OnRestartOffered = _ =>
            {
                if (playback != null)
                {
                    return; // the demo's own restart event comes
                }

                menu.OpenRestart();
                ReleaseMouse();
            },
            OnHint = text => hud.ShowHint(text),
            OnObjective = text => hud.ShowObjective(text),
            OnStatus = text => hud.ShowStatus(text),
            OnColouredStatus = (text, colour) => hud.ShowStatus(text, colour),
        };

        menuEvents.OnSettingsChanged = ApplySettings;
        menuEvents.OnResume = ResumeGame;
        menuEvents.OnBeginMission = ResumeGame;
        menuEvents.OnQuit = () => quitting = true;
        menuEvents.OnRestartMission = () =>
        {
            ApplyMods(tables: true);
            recording?.Restart();
            session.RestartMission();
        };
        menuEvents.OnQuitToTitle = () =>
        {
            var watching = dm?.Replay != null;
            StopDemos("the game was left");
            if (watching)
            {
                menu.OpenTitle();
                ReleaseMouse();
                PlayTitleMusic();
                return;
            }

            // QUIT in a deathmatch leaves it (the host's closes it for everybody) and goes back to the
            // network screen it came from.
            if (dm != null)
            {
                var joined = !dm.IsHost;
                dm.Dispose();
                dm = null;
                menu.OpenNetwork(joined);
                ReleaseMouse();
                PlayTitleMusic();
                return;
            }

            // The title is SkyNET's: Future Shock's context goes, SkyNET's comes back.
            if (data.Game != homeData.Game)
            {
                SwitchGame(homeData.Game);
            }

            menu.OpenTitle();
            ReleaseMouse();
            PlayTitleMusic();
        };
        menuEvents.OnStartGame = (kind, number, pilot) =>
        {
            StopDemos("a new game started");
            if (kind != data.Game && !SwitchGame(kind))
            {
                menu.OpenTitle();
                menu.ShowToast($"{(kind == GameKind.FutureShock ? "FUTURE SHOCK" : "SKYNET")} DATA DOES NOT OPEN");
                return;
            }

            if (session.Table.ByNumber(number) is { } mission)
            {
                ApplyMods(tables: true);
                // A new game: the start kit, full health, the dialog's name and call sign.
                weapons.Inventory.Reset();
                session.Pilot = pilot;
                session.StartMission(mission);
            }
        };
        menuEvents.OnLoadGame = slot =>
        {
            if (!menu.InGame && TitleSlot(slot) is { } titleSlot && titleSlot.Game != data.Game && !SwitchGame(titleSlot.Game))
            {
                menu.OpenTitle();
                menu.ShowToast("LOAD FAILED");
                return;
            }

            ApplyMods(tables: true);
            LoadGame(slot);
        };
        menuEvents.OnSaveGame = SaveGame;
        // START on the network screens: the same start as --host / --join.
        menuEvents.OnStartNetGame = o => StartNet(options.NetBind is { } bind ? o with { Bind = bind } : o, fromMenu: true);
        menuEvents.OnStopNetGame = () =>
        {
            dm?.Dispose();
            dm = null;
            netEnded = null;
        };
        menuEvents.OnSound = name => sounds?.PlayInterface(name);
        // START with DEDICATED on the host screen: a server with no local player in this process, stepped
        // by the SERVER STATUS screen (its own GameSession: no window, renderer or sound of its own).
        menuEvents.OnStartDedicated = o =>
        {
            if (data == null)
            {
                return (null, "NO GAME DATA");
            }

            var config = SkyNet.Game.Net.ServerConfig.FromOptions(options.NetBind is { } bind ? o with { Bind = bind } : o, AppContext.BaseDirectory);
            var server = SkyNet.Game.Net.DmDedicated.Start(data, config, out var why);
            return (server, why);
        };
        menuEvents.OnStopDedicated = () => Log.Info("Net", "the dedicated server stopped");

        session.Listener = events;
        // The frames' times (stats, the last frame's mean).
        frameTimes = new List<double>();
        SessionCommands.Register(commands, () => session, ConsoleStarted);
        commands.Executed += (command, words) => recording?.Command(command, words);
        DevDisplays.Register(commands, displays);
        RegisterAppCommands();
        KeyBinds.Register(commands, keyBinds);
        keyBinds.Changed += SaveKeyBinds;

        // The deathmatch: the session on the arena (the host's at once, a client's on the welcome).
        // The console's session-changing commands refuse while it runs.
        SkyNet.Game.Net.DmGame.Guard(commands, () => dm != null, () => dm?.Settings.Cheats == true);
        if (netRun != null)
        {
            if (StartNet(netRun, fromMenu: false) is { } startError)
            {
                return Fatal(startError);
            }

            if (options.Record is { } netRecordName && StartNetRecording(netRecordName) is { } netRecordError)
            {
                return Fatal(netRecordError);
            }
        }
        else if (options.PlayDemo is { } demoName)
        {
            if (StartPlayback(demoName, options.TimeDemo) is { } demoError)
            {
                return Fatal(demoError);
            }

            timedemoQuits = options.TimeDemo;
        }
        else if (options.Record is { } recordName)
        {
            // --mission N from its first map; --map N in the mission of its decade (a side map of it, or a
            // loose map with no mission).
            var (number, side) = options.Mission is { } n ? (n, (int?)null)
                : session.Table.ForMap(options.MapOrDefault) is { } m ? (m.Number, m.StartMap == options.MapOrDefault ? null : options.MapOrDefault)
                : (0, options.MapOrDefault);
            if (StartRecording(recordName, number, side) is { } recordError)
            {
                return Fatal(recordError);
            }
        }
        else if (options.Mission != null || options.Map != null)
        {
            SplashStatus("Loading the mission...");
            try
            {
                if (options.Mission is { } number)
                {
                    session.StartMission(session.Table.ByNumber(number) ?? throw new InvalidDataException($"No mission {number} in the table."));
                }
                else
                {
                    session.StartAt(options.MapOrDefault);
                }
            }
            catch (Exception e) when (e is FileNotFoundException or InvalidDataException)
            {
                return Fatal(e.Message);
            }

            if (level == null)
            {
                return Fatal($"MAP.{options.MapOrDefault:D3} is not in {data.MapArchive} or does not parse.");
            }

            if (options.Position != null || options.Yaw != null || options.Pitch != null)
            {
                SpawnPlayer(player, session.World, level.Map, level.Wld, options);
            }
        }
        else
        {
            menu.OpenTitle();
            PlayTitleMusic();
        }

        if (options.PlayScript is { } windowedScript)
        {
            // A scripted run with a screenshot: play it through, then show where it ended.
            var failures = PlayScript.Load(windowedScript).Run(session, line => Log.Info("Script", line));
            Log.Info("Script", failures.Count == 0 ? "Script: every check held" : $"Script: {failures.Count} failed\n{string.Join('\n', failures)}");
        }

        Log.Info("App", FormattableString.Invariant(
            $"Player: feet {player.Position.X:0},{player.Position.Y:0},{player.Position.Z:0} yaw {player.Yaw * 180f / MathF.PI:0.#}{(player.Noclip ? " (noclip)" : "")}"));

        if (options.Console is { } consoleLines)
        {
            devConsole.Open();
            devConsole.Run(consoleLines);
        }

        // Play takes the mouse (and shows the touch controls): a player's run, or --touch's checks.
        if ((interactive || options.Touch) && !menu.IsOpen && !devConsole.IsOpen)
        {
            window.CaptureMouse(Capture.On);
        }

        rendered = 0;
        lastFrame = Stopwatch.GetTimestamp();
        loop = capture == null
            ? new FixedLoop(input, Tick, FrameCallback(), ticksPerSecond: TicksPerSecond)
            : new FixedLoop(input, Tick, FrameCallback(), ticksPerSecond: TicksPerSecond, clock: () => capture.Timestamp, clockFrequency: Stopwatch.Frequency);
        if (playback != null && options.TimeDemo)
        {
            // --timedemo: one tick a frame from the first frame on.
            loop.OneTickPerFrame = true;
            renderer.VSync = VSync.Off;
            timedemoStart = Stopwatch.GetTimestamp();
        }

        ApplyDemoClock();
        try
        {
            RunLoop(new LoopHost
            {
                Pump = () => window.PumpEvents(input) && FrameMenus() && !quitting,
                Wait = () => window.WaitEvents(),
                Paused = () => window.Minimised,
                Presented = () => renderer.Presented,
            });
        }
        catch (Exception e) when (!Debugger.IsAttached)
        {
            // The window is still up: the mouse goes back to the player before the message box.
            ReleaseMouse();
            crash.Report(e);
            return CrashHandler.ExitCode;
        }

        if (capture != null)
        {
            soundCapture?.Dispose();
            Log.Info("App", $"Capture: {capture.Taken} frames at {capture.Fps} fps and {soundCapture?.Written} sound frames at {audio.Mixer.SampleRate} Hz in {capture.Dir}");
        }

        // A recording is written as the game ends.
        StopDemos("the game ended");

        // A deathmatch leaves the game (the host closes it for everyone).
        dm?.Dispose();

        // The first frames build pipelines and fill caches: the mean is over the later half.
        var steady = frameTimes.Skip(frameTimes.Count / 2).DefaultIfEmpty(0).Average();
        Log.Info("App", $"Last frame: {renderer.Stats.DrawCalls} draw calls, {renderer.Stats.Triangles} triangles; " +
            $"{rendered} frames, {steady:0.###} ms per frame, {renderer.PresentMilliseconds:0.###} ms present (means of the later half); " +
            $"GPU {renderer.Memory.TextureBytes >> 20} MB textures + {renderer.Memory.MeshBytes >> 20} MB meshes, DETAIL {settings.Detail}");
        return 0;
    }

    // The loop's ticks and draws: the mode's.
    private void Tick(in TickInput latched, int number) => Mode.Tick(latched, number);

    private void Render(float alpha) => Mode.Render(alpha);

    public void Dispose()
    {
        splash?.Dispose();
        editorLink?.Dispose();
        endBanner?.Dispose();
        audio?.Dispose();
        churn?.Dispose();
        renderer?.Dispose();
        window?.Dispose();
    }

    // An error that stops the program before the game starts: logged, and shown to a player.
    private int Fatal(string message)
    {
        // The splash stays on top: gone before the message box.
        EndSplash();
        return AppTools.Fatal(message, interactive && !tool);
    }

    // A game context's parts as the app's locals, its events wired to the app.
    void Adopt(GameContext g)
    {
        game = g;
        data = g.Data;
        sounds = g.Sounds;
        g.Session.Blocks = n => blocks?.Load(n);
        g.Session.Missions = m => blocks?.LoadMission(m.Number);
        music = g.Music;
        session = g.Session;
        player = session.Player;
        soldier = session.Soldier;
        weapons = session.Weapons;
        viewModel = g.ViewModel;
        weaponView = g.WeaponView;
        vehicles = g.Vehicles;
        cockpitView = g.CockpitView;
        hud = g.Hud;
        bannerFont = g.BannerFont;
        menuPainter = g.MenuPainter;
        slots = g.Slots;
        slotNames = g.SlotNames;

        var w = weapons;
        w.Reported += r => Log.Info("Weapons", Describe(r));
        w.Fired += (record, thrown) =>
        {
            if (thrown)
            {
                Log.Info("Weapons", $"{w.Table.Weapons[record].Name}: thrown, {w.Inventory.AmmoFor(record)} left");
            }
        };
        var h = sounds;
        hud.SoundRequested += id => h?.Play2D(id);
    }

    // NEW GAME → FUTURE SHOCK (and QUIT back to the title): the running game's map, sounds, art and
    // session go, the other game's data opens and its context takes over, as DOS SKYNET.EXE plays Future
    // Shock inside SkyNET. False (and nothing changed) when the other game's data does not open.
    bool SwitchGame(GameKind kind)
    {
        var dir = kind == homeData.Game ? homeData.Dir : kind == GameKind.FutureShock ? futureShockDir : null;
        var next = kind == homeData.Game ? homeData : dir != null ? GameData.Open(dir, homeData.Dir) : null;
        if (next == null || next.Game != kind)
        {
            Log.Error("App", $"{kind}: no data to switch to");
            return false;
        }

        using var zone = Prof.Zone(AppZones.SwitchGame);
        WaitForFrames();
        var clock = Stopwatch.StartNew();
        engineLoop?.Stop();
        engineLoop = null;
        engineSound = -1;
        renderer.Release(levelScope);
        // The map's scene state goes with it (no map until the new game enters one).
        renderer.ShadeTable = default;
        renderer.Fog = null;
        renderer.PlaneRemap = null;
        renderer.PlaneGrade = null;
        level = null;
        robotView = null;
        automap.Close();
        automapView = null;
        hudArtInLevelScope = false;
        var old = game;
        GameContext created;
        try
        {
            old.Dispose();
            created = GameContext.Create(next, renderer, audio.Mixer, settings.HudArt);
        }
        catch (Exception e) when (e is IOException or InvalidDataException or UnauthorizedAccessException)
        {
            // The new game's data broke halfway: home again (SkyNET's data opened once already).
            Log.Error("App", $"{kind}: {e.Message}");
            created = GameContext.Create(homeData, renderer, audio.Mixer, settings.HudArt);
            next = homeData;
        }

        Adopt(created);
        blocks?.Dispose();
        blocks = OpenBlocks(next);
        session.Listener = events;
        ApplySettings(settings);
        levelScope = renderer.Mark();
        loop?.Reset();
        Log.Info("App", $"Data: {data.Game} in a folder named {LogHeader.FolderName(data.Dir)} ({clock.Elapsed.TotalMilliseconds:0} ms to switch)");
        return next.Game == kind;
    }

    // The session entered a map: the old level's GPU resources go, the new one is built and warmed
    // behind the same frame, and the loop forgets the time the load took (no catch-up burst).
    void EnterLevel(GameSession s)
    {
        WaitForFrames();
        GcPacing.Loading();
        // The MODS screen's textures and models (its tables wait for a game's start).
        ApplyMods(tables: false);
        vehicles.MapEntered();
        automap.Close();
        viewModel.Animation.Reset();
        var clock = Stopwatch.StartNew();
        renderer.Release(levelScope);
        weaponView.MapReleased();
        if (hudArtInLevelScope)
        {
            // The panel art changed on the last map went with its scope: made again outside the next.
            hud.ReloadArt();
            viewModel.ReloadArt();
            hudArtInLevelScope = false;
        }

        // DETAIL's HD texture size, then a mod's surfaces outlive the map (made before its mark).
        SkyNet.Game.Mods.ModContent.UseDetail(settings.Detail);
        game.Kept.BeforeMap();
        levelScope = renderer.Mark();
        using var zone = Prof.Zone(AppZones.LevelLoad);
        using (Prof.Zone(AppZones.LevelBuild))
        {
            level = Level.Load(renderer, data, s.MapNumber, staticRobots: false, block: blocks?.Load(s.BlockMap), kept: game.Kept);
        }

        var levelMs = clock.Elapsed.TotalMilliseconds;
        using (Prof.Zone(AppZones.RobotMeshes))
        {
            robotView = new EnemyRenderer(renderer, new TextureStore(renderer, data), level.Palette, level, game.Kept);
        }

        level.Detail = settings.Detail;
        level.FarOverride = farOverride;
        level.ClockMs = options.Freeze ? Options.FrozenClockMs : null;
        level.Poses = s.Behaviour;
        level.HiddenSprites = s.TakenPickups;
        level.Moon = s.Moon;
        level.Floors = s.World;
        var robotsMs = clock.Elapsed.TotalMilliseconds;
        using (Prof.Zone(AppZones.WarmUp))
        {
            SplashStatus("Compiling shaders...");
            // The look's light, haze and sprite materials, the colour textures asked for, and the pipelines.
            ApplyLook(settings);
        }

        var warmMs = clock.Elapsed.TotalMilliseconds;
        using (Prof.Zone(AppZones.UploadFlush))
        {
            renderer.FlushUploads();
        }

        var uploadMs = clock.Elapsed.TotalMilliseconds;

        northDegrees = NorthOffset(level.Map);
        hud.ClearMessages();
        // DOS queues it as the level starts, after the status lines were emptied (FUN_00136768).
        if (s.EnteredLine is { } entered)
        {
            hud.ShowStatus(entered, StatusQueue.WhiteColour);
        }

        if (!options.NoMusic && !options.NoSound)
        {
            using var musicZone = Prof.Zone(AppZones.MusicStart);
            music.Play(s.InstanceMusic ?? MusicTracks.ForMap(level.Map));
        }

        (objectives, pointerMade) = (ObjectiveReport.None, 0);
        frameStats.Clear();

        Log.Info("Level", $"MAP.{s.MapNumber:D3}: {(level.Outdoor ? "outdoor" : "indoor")}, {level.Map.Entities.Length} entities, " +
            $"{level.ModelCount} models, {level.SpriteCount} sprites ({level.PickupCount} pickups); collision {s.CollisionStats.Triangles} triangles; " +
            $"built in {clock.Elapsed.TotalMilliseconds:0} ms ({level.LoadTime.TotalMilliseconds:0} ms level, " +
            (level.BakeTime > TimeSpan.Zero ? $"its block baked in {level.BakeTime.TotalMilliseconds:0} ms)" : "from its block)") +
            $"; steps: level {levelMs:0} ms, robots {robotsMs - levelMs:0} ms, warm-up {warmMs - robotsMs:0} ms, uploads {uploadMs - warmMs:0} ms");
        if (level.Missing.Count > 0)
        {
            Log.Warn("Level", $"Missing: {string.Join(", ", level.Missing)}");
        }

        game.Kept.Trim();
        // The HD textures now on the GPU alone (HdPixels.Released) are let go before the collection below.
        SkyNet.Game.Mods.ModContent.TrimHanded();

        // The load's garbage collected now, not in play's first seconds.
        var gc = GcPacing.Playing();
        Log.Info("Level", $"GC after the load: {gc.TotalMilliseconds:0} ms, heap {GC.GetTotalMemory(false) >> 20} MB");
        loop?.Reset();
        loadHitches.Start($"MAP.{s.MapNumber:D3}", Stopwatch.GetTimestamp());
    }

    // Once a frame, before the ticks: the menu script's step, the developer displays' keys, the
    // console, then the mode's keys or the open menu. Always true (the loop's pump goes on).
    private bool FrameMenus()
    {
        var now = MenuClock();
        var seconds = (float)Stopwatch.GetElapsedTime(lastMenuFrame, now).TotalSeconds;
        lastMenuFrame = now;
        // The editor's live edits may change what a frame build reads.
        if (options.EditorLink != null)
        {
            WaitForFrames();
        }

        StepMenuScript();
        NetFrame(seconds);
        EditorLinkFrame();
        // The developer displays' keys work over everything (the console, the menus, the title).
        if (input.WasPressed(RawKey.F11))
        {
            displays.ToggleFps();
        }

        if (input.WasPressed(RawKey.F12))
        {
            displays.ToggleDebug();
        }

        if (ConsoleFrame(seconds))
        {
            return true;
        }

        Mode.Pump(seconds);
        return true;
    }

    // The game's keys with the menus shut (Esc opens the game menu, F6 / F7 over a map), else the
    // open menu fed this frame's input.
    internal void GameKeysOrMenu(float seconds)
    {
        if (!menu.IsOpen)
        {
            if (AutomapFrame(seconds))
            {
                return;
            }

            if (input.WasPressed(controls.Automap) && OpenAutomap())
            {
                return;
            }

            if (input.WasPressed(pause) && level != null)
            {
                // This frame's Esc opened the menu; the menu reads input from the next frame on.
                menu.OpenPause();
                ReleaseMouse();
            }
            else if (input.WasPressed(quickSave) && level != null)
            {
                hud.ShowStatus(SaveGame(SaveSlots.QuickSlot) ? "GAME SAVED" : "CANNOT SAVE NOW");
            }
            else if (input.WasPressed(quickLoad) && level != null)
            {
                LoadGame(SaveSlots.QuickSlot);
            }
            else if (level != null && keyBinds.Run(input.LastControl, commands) is { } said)
            {
                // A bound key's console line, run as if typed (a network game's guards hold).
                foreach (var line in said.Where(l => l.Length > 0))
                {
                    Log.Info("Bind", line);
                }
            }

            return;
        }

        var (width, height) = FrameSize();
        var menuInput = MenuInput.From(input, renderer.CanvasPoint) with { Held = input.IsHeld(Pointer.Left) };
        menu.Update(menuInput, MenuFrame.Fit(width, height), seconds);
        if (menuInput.Wheel != 0f)
        {
            // The menu had the wheel: it adds up until a latch, which menus do not make.
            input.ClearMouse();
        }
    }

    // The console's keys, before the game's and the menus': true when it had this frame's input. The
    // mouse is free while it is down; closed over a running map, play resumes.
    bool ConsoleFrame(float seconds)
    {
        var wasOpen = devConsole.IsOpen;
        var capturing = menu.Current is ControlsScreen { Capturing: >= 0 };
        var typing = menu.IsOpen && menu.TakesText;
        var chatting = dm is { ChatOpen: true };
        // A menu's text field or the chat line wants typed text as the console does (claimed first,
        // so a hand-over between them in one frame keeps the text input on).
        window.TextInputs.Want(MenuTextField, typing || chatting);
        if (!ConsoleInput.Update(devConsole, input, window, seconds, enabled: !capturing && !typing && !chatting))
        {
            return false;
        }

        if (devConsole.IsOpen && !wasOpen)
        {
            ReleaseMouse();
        }
        else if (!devConsole.IsOpen && wasOpen && level != null && !menu.IsOpen)
        {
            ResumeGame();
        }

        return true;
    }

    // bind / unbind: the key binds and every action's keys into the settings file; the rest of the file
    // as it is on disk.
    void SaveKeyBinds()
    {
        settings.CommandBinds = new Dictionary<string, string>(keyBinds.Lines, StringComparer.OrdinalIgnoreCase);
        settings.Bindings = Rebind.Snapshot(bindings);
        if (!interactive)
        {
            return;
        }

        var stored = SettingsStore.Load(settingsPath);
        (stored.CommandBinds, stored.Bindings) = (settings.CommandBinds, settings.Bindings);
        SettingsStore.Save(stored, settingsPath);
    }

    // A console command started a map or a mission: the menus (the title, the mission screen) close and
    // play goes on once the console is up.
    void ConsoleStarted()
    {
        menu.Close();
        if (!devConsole.IsOpen)
        {
            ResumeGame();
        }
    }

    // MODS: the settings' list mounted when it differs from what is (never under --mod / --no-mods):
    // the textures and models, and with tables the tables too.
    void ApplyMods(bool tables)
    {
        WaitForFrames();
        if (modsOverride != null)
        {
            return;
        }

        var want = SkyNet.Game.Mods.ModOrder.Effective(settings.Mods, null, systemMods);
        var files = !want.SequenceEqual(modsMounted, StringComparer.OrdinalIgnoreCase);
        if (files || (tables && !want.SequenceEqual(tableModsMounted, StringComparer.OrdinalIgnoreCase)))
        {
            SkyNet.Game.Mods.ModContent.Mount(modsDir, want, tables);
            modsMounted = want;
            if (tables)
            {
                tableModsMounted = want;
            }
        }
    }

    // The settings, applied: the panel art, DIFFICULTY, the two volumes, RENDER DETAIL and DISPLAY.
    void ApplySettings(MenuSettings s)
    {
        // The settings change the level, the drawers and the renderer's scene state a frame build reads.
        WaitForFrames();
        var display = DisplayOf(s);
        if (display != applied.Display)
        {
            window.Apply(display);
            windowMode = s.WindowMode;
            Log.Info("Window", $"{windowMode} {(windowMode == WindowMode.Windowed ? s.WindowSize.ToString() : (s.WindowMode == WindowMode.Fullscreen ? s.FullscreenSize : null)?.ToString() ?? window.Desktop.Size.ToString())}");
        }

        if (s.VSync != applied.VSync)
        {
            renderer.VSync = s.VSync;
        }

        if (s.ShowFps != applied.ShowFps && displays.Fps != s.ShowFps)
        {
            displays.SetFps(s.ShowFps);
        }

        applied = (display, s.VSync, s.ShowFps);
        // A MODS change on the title (no game running) comes in at once, tables and all.
        if (menuRef is { InGame: false } && dm == null)
        {
            ApplyMods(tables: true);
        }

        renderer.RenderScale = s.RenderScale;
        renderer.Gamma = s.Gamma;
        ViewStyles.Apply(renderer, s);

        hudArtInLevelScope |= level != null && hud.ArtSet != s.HudArt;
        hud.ArtSet = s.HudArt;
        hud.Style = s.HudStyle;
        viewModel.ArtSet = s.HudArt;
        // A demo playing keeps the DIFFICULTY and ROBOT AI it was recorded with (its events change them).
        if (playback == null)
        {
            session.Difficulty = Difficulty.Of(s.Difficulty);
            session.RobotAi = s.RobotAi;
        }
        // MOUSE and GAMEPAD: the look of every mover (on foot, the turret, the HK) reads Input's counts.
        s.ApplyLook(input);
        // A hit shakes the pad by what it took (a 1000-point soldier: 250 points is a full shake).
        soldier.Wounded = s.PadVibration
            ? taken => window.Rumble(Math.Clamp(taken / 250f, 0.25f, 1f), Math.Clamp(taken / 400f, 0.1f, 0.8f), (int)Math.Clamp(120f + taken, 120f, 400f))
            : null;
        audio.Mixer.SetBusGain(Bus.Effects, s.SoundGain);
        music.Volume = MusicPlayer.DefaultVolume * s.MusicGain;
        if (level != null)
        {
            level.Detail = s.Detail;
            level.FarOverride = farOverride;
        }

        ApplyStereoSettings(s);
        ApplyLook(s);
    }

    // The DISPLAY screen's LOOK on the map shown (live): ORIGINAL keeps the palette look; ENHANCED
    // lights the map with its preset (LookLighting) and the ENHANCED OPTIONS (shadows, anti-aliasing,
    // light budget, ambient, occlusion, glow), BRIGHTNESS as the exposure (the gamma pass stays off), the depth cue as a linear haze, the world's colour textures asked for and the pipelines
    // made. The HUD, menus, view-model and underwater fill stay palette-exact.
    void ApplyLook(MenuSettings s)
    {
        WaitForFrames();
        var enhanced = s.Look == Look.Enhanced && level != null;
        renderer.Lighting = enhanced ? LookLighting.For(level!, s) : null;
        renderer.Gamma = enhanced ? Renderer.DefaultGamma : s.Gamma;
        // ENHANCED OPTIONS: anti-aliasing (the original look stays unsmoothed) and the light budget.
        renderer.AntiAliasing = enhanced ? s.AntiAliasing : AntiAliasing.Off;
        if (level != null)
        {
            EnhancedEffects.Apply(level, s, weaponView);
        }

        weaponView.Apply(renderer.Lighting);
        if (level == null)
        {
            return;
        }

        level.Apply(s.Look);
        // What the first frames would make on first sight: the robots' meshes, the guns in hand.
        if (session.Robots is { } robots)
        {
            robotView?.Prepare(robots);
        }

        var guns = viewModel.Prepare(session.Weapons.Inventory, level.Enhanced ? Look.Enhanced : Look.Original);
        if (renderer.Lighting is { } lit)
        {
            Log.Info("Render", string.Create(CultureInfo.InvariantCulture,
                $"Enhanced: {LookLighting.PresetOf(level.Number, level.Outdoor)}, light along {lit.SunDirection:F2}, haze {level.HazeColour:F2}; " +
                $"shadows {lit.ShadowQuality}, AA {renderer.AntiAliasing}, lights {level.LightBudget}, ambient {s.Ambient}%, particles {s.Particles}, AO {(s.AmbientOcclusion ? "on" : "off")}, glow {(s.Glow ? "on" : "off")}"));
        }
        List<Material> materials = [.. level.Materials(), .. weaponView.Preload(session.Weapons.Table),
            .. robotView?.Materials() ?? [], .. cockpitView.Materials(), .. guns];
        if (renderer.Lighting != null)
        {
            foreach (var m in materials)
            {
                renderer.Prepare(m);
            }
        }

        renderer.WarmUp([.. materials]);
        robotView?.WarmUp();
    }

    // The menu closed onto the game: the input gathered under it is dropped (a click on RETURN does
    // not fire), the clock owes nothing, the mouse looks again.
    void ResumeGame()
    {
        input.Latch();
        loop?.Reset();
        if (interactive)
        {
            window.CaptureMouse(Capture.On);
        }
    }

    void ReleaseMouse() => window.CaptureMouse(Capture.Off);

    // SAVE on a slot (or F6): the session written to the slot's file; false (and why, on the open
    // menu's toast) when the game cannot be saved now or the file was not written.
    bool SaveGame(int slot)
    {
        using var zone = Prof.Zone(AppZones.SaveGame);
        if (dm != null)
        {
            // main.gd save_to_slot under Net.
            menu.ShowToast("NO SAVING IN A NETWORK GAME");
            return false;
        }

        if (!session.CanSave)
        {
            Log.Info("Save", $"Save to slot {slot}: not now ({session.Phase})");
            menu.ShowToast("CANNOT SAVE NOW");
            return false;
        }

        var save = session.Capture(DateTime.Now);
        if (slots.Write(slot, save) is { } error)
        {
            Log.Error("Save", $"Save to slot {slot} failed: {error}");
            menu.ShowToast("SAVE FAILED");
            return false;
        }

        slotNames[slot] = save.Label;
        Log.Info("Save", $"Saved slot {slot}: {save.Label}");
        return true;
    }

    // LOAD on a slot (or F7): the saved game restored and play resumed on its map; a slot that does not
    // read leaves the game (or the title) as it was, with the reason on the toast or the status line.
    void LoadGame(int slot)
    {
        using var zone = Prof.Zone(AppZones.LoadGame);
        StopDemos("a game was loaded");
        if (dm != null)
        {
            // main.gd load_from_slot under Net: a save would leave the arena under the wire.
            menu.ShowToast("NO LOADING IN A NETWORK GAME");
            hud.ShowStatus("NO LOADING IN A NETWORK GAME");
            return;
        }

        try
        {
            session.Load(slots.Read(slot));
        }
        catch (SaveFormatException e)
        {
            Log.Error("Save", $"Load slot {slot}: {e.Message}");
            if (level == null)
            {
                if (data.Game != homeData.Game)
                {
                    // A Future Shock save from the title that does not read: SkyNET's title again.
                    SwitchGame(homeData.Game);
                }

                menu.OpenTitle();
                menu.ShowToast("LOAD FAILED");
            }
            else
            {
                hud.ShowStatus("LOAD FAILED");
            }

            return;
        }

        hud.ShowStatus("GAME LOADED");
        ResumeGame();
    }

    // The size the HUD and menus are laid out for: the frame's, in stereo an eye's (the full side by side
    // and the phone viewer give an eye half the width at its own aspect; the canvas follows it, unsqueezed).
    (int Width, int Height) FrameSize()
    {
        var (width, height) = (renderer.Target.Width, renderer.Target.Height);
        (width, height) = width > 0 && height > 0 ? (width, height) : window.Size;
        var stereo = StereoNow();
        return stereo.EyeAspect != 1f ? ((int)MathF.Round(width * stereo.EyeAspect), height) : (width, height);
    }

    // The zone timer is the profiler backend while the overlay is on.
    void ApplyZoneTimer() =>
        Prof.SetBackend(displays.Debug ? zoneTimes : null);

    // The menus' and the network's clock (Stopwatch ticks): the wall's, or --capture's.
    long MenuClock() => capture?.Timestamp ?? Stopwatch.GetTimestamp();

    // The draws' animation clock (ms): the system's, a fixed one under --freeze, or --capture's.
    long AnimationMs() => options.Freeze ? Options.FrozenClockMs : capture?.Milliseconds ?? Environment.TickCount64;

    // No ticks run while this holds (always under --freeze), so Render must not keep interpolating between the last two:
    // the loop's alpha still sweeps 0..1 and the view would shake between them.
    // The engine's loop is kept by the ticks (PlayMode.EngineLoop): a paused game or the menus stop
    // it, and the next tick riding starts it again.
    internal void SilenceEngineWhenFrozen()
    {
        // Every other game sound (3D loops, one-shots in flight) holds on its paused bus and goes on
        // where it stopped; the music and the menus' sounds play on.
        if (sounds != null)
        {
            sounds.Paused = Frozen();
        }

        if (engineLoop != null && Frozen())
        {
            engineLoop.Stop();
            engineLoop = null;
            engineSound = -1;
        }
    }

    internal bool Frozen() => options.Freeze || demoPaused && playback != null || level == null || menu.IsOpen && !menu.InGame || dm == null && (menu.PausesGame || devConsole.IsOpen || automap.IsOpen);

    // The data set's map blocks: the missing and stale ones baked first ("PREPARING GAME DATA n/N"
    // over the title), then the mods folder mounted. Null (raw builds) when the mods folder cannot be written.
    BlockStore? OpenBlocks(GameData d)
    {
        var store = new BlockStore(modsDir, d);
        try
        {
            var clock = Stopwatch.StartNew();
            var stale = store.Stale();
            Log.Info("Bake", $"{Path.GetFileName(store.ZipPath)}: {stale.Count} map blocks to bake (checked in {clock.Elapsed.TotalMilliseconds:0} ms)");
            if (stale.Count > 0)
            {
                store.Bake(stale, (done, total) =>
                {
                    Log.Info("Bake", $"PREPARING GAME DATA {done}/{total}");
                    DrawPreparing(done, total);
                });
            }

            var missions = store.StaleMissions();
            Log.Info("Bake", $"{Path.GetFileName(store.ZipPath)}: {missions.Count} mission files to generate");
            store.BakeMissions(missions);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            Log.Warn("Bake", $"The map blocks cannot be written ({e.Message}): maps are built from the raw files");
        }

        store.Mount();
        return store;
    }

    // One frame of the bake's progress over the title.
    void DrawPreparing(int done, int total)
    {
        // Behind the splash the window is hidden: the count goes on the splash.
        if (splash != null)
        {
            splash.Status = $"Preparing game data {done}/{total}";
            return;
        }

        window.PumpEvents(input);
        var (width, height) = (renderer.Target.Width, renderer.Target.Height);
        menuPainter.Use(null);
        menuPainter.FillWindow(MenuPainter.Black);
        menuPainter.Image(MenuLayout.TitleImage, MenuLayout.TitlePalette, 0, 0);
        DrawBanner($"PREPARING GAME DATA {done}/{total}", width, height);
        renderer.Present(new View(Matrix4x4.Identity, Matrix4x4.Identity, Vector3.Zero), new Vector4(0f, 0f, 0f, 1f));
    }
}

/// <summary>The session's events, handed to the app's closures.</summary>
internal sealed class SessionEvents : ISessionListener
{
    public Action<GameSession>? OnMapEntered;
    public Action<Mission, Briefing?, bool>? OnMissionStarted;
    public Action<Mission?>? OnRestartOffered;
    public Action<string>? OnHint, OnObjective, OnStatus;
    public Action<string, byte>? OnColouredStatus;

    public void MapEntered(GameSession session) => OnMapEntered?.Invoke(session);
    public void MissionStarted(Mission mission, Briefing? briefing, bool restart) => OnMissionStarted?.Invoke(mission, briefing, restart);
    public void RestartOffered(Mission? mission) => OnRestartOffered?.Invoke(mission);
    public void Hint(string text) => OnHint?.Invoke(text);
    public void Objective(string text) => OnObjective?.Invoke(text);
    public void Status(string text) => OnStatus?.Invoke(text);
    public void Status(string text, byte colour) => OnColouredStatus?.Invoke(text, colour);
}
