using System.Text;
using System.Text.Json;
using RetroEng.Json;
using RetroEng.Platform;
using RetroEng.Render;
using SkyNet.Game.Enemies;
using SkyNet.Game.Hud;

namespace SkyNet.Game.Menu;

/// <summary>The settings the OPTIONS, DETAIL, CONTROLS, MOUSE, GAMEPAD, DISPLAY, ENHANCED OPTIONS and MODS screens change.</summary>
public sealed class MenuSettings
{
    /// <summary>Volume steps of the SOUND and MUSIC bars (0 = silent).</summary>
    public const int VolumeSteps = 16;

    /// <summary>RENDER DETAIL: the haze distance (<see cref="Level.Detail"/>) and the HD textures' size (<see cref="Mods.HdDetail"/>).</summary>
    public Detail Detail { get; set; } = Detail.High;

    /// <summary>0..<see cref="VolumeSteps"/>.</summary>
    public int SoundVolume { get; set; } = VolumeSteps;

    public int MusicVolume { get; set; } = VolumeSteps * 3 / 4;

    public bool ReverseStereo { get; set; }

    /// <summary>DISPLAY's HUD ART: the 640 × 480 set (MDMDHRES.BSA) or DOS's 320 × 200 art.</summary>
    public HudArtSet HudArt { get; set; } = HudArtSet.HiRes;

    /// <summary>DISPLAY's HUD STYLE: DOS's panel (CLASSIC) or the corner widgets over a full-frame view (MODERN).</summary>
    public HudStyle HudStyle { get; set; } = HudStyle.Classic;

    /// <summary>DISPLAY's UI SCALE (<see cref="UiMetrics"/>).</summary>
    public UiScale UiScale { get; set; } = UiScale.Auto;

    public DifficultyLevel Difficulty { get; set; } = DifficultyLevel.Medium;

    /// <summary>ROBOT AI: DOS's robots (CLASSIC) or smarter movement and fire (IMPROVED, the default;
    /// the console's <c>robotai</c>, <c>--robot-ai</c>).</summary>
    public RobotAi RobotAi { get; set; } = RobotAi.Improved;

    /// <summary>The window's size when <see cref="WindowMode"/> is <see cref="WindowMode.Windowed"/>
    /// (the Godot port's first offered size, 1280 × 720); the window cannot be resized by hand.</summary>
    public int WindowWidth { get; set; } = DefaultWindowWidth;

    public int WindowHeight { get; set; } = DefaultWindowHeight;

    /// <summary>WINDOW / BORDERLESS / FULLSCREEN, as the Godot port's settings.gd window_mode.</summary>
    public WindowMode WindowMode { get; set; } = WindowMode.Windowed;

    /// <summary>The display mode of exclusive fullscreen (<see cref="WindowMode.Fullscreen"/>); 0 × 0:
    /// the desktop's own.</summary>
    public int FullscreenWidth { get; set; }

    public int FullscreenHeight { get; set; }

    /// <summary>The exclusive fullscreen size, null for the desktop's.</summary>
    public Resolution? FullscreenSize
    {
        get => FullscreenWidth > 0 && FullscreenHeight > 0 ? new Resolution(FullscreenWidth, FullscreenHeight) : null;
        set => (FullscreenWidth, FullscreenHeight) = value is { } r ? (r.Width, r.Height) : (0, 0);
    }

    public Resolution WindowSize
    {
        get => new(WindowWidth, WindowHeight);
        set => (WindowWidth, WindowHeight) = (value.Width, value.Height);
    }

    /// <summary>How frames meet the display's refresh (<see cref="Renderer.VSync"/>; the renderer
    /// falls back to a mode the window supports).</summary>
    public VSync VSync { get; set; } = VSync.On;

    /// <summary>The 3D frame's share of the window in % (<see cref="Renderer.Scales"/>), or
    /// <see cref="Renderer.AutoScale"/> (by the window's pixels; Android's default: tablets have many).</summary>
    public int RenderScale { get; set; } = OperatingSystem.IsAndroid() ? Renderer.AutoScale : Renderer.FullScale;

    /// <summary>Brightness in % of the plain picture: the renderer's gamma × 100
    /// (<see cref="Renderer.MinGamma"/>..<see cref="Renderer.MaxGamma"/>).</summary>
    public int Brightness { get; set; } = DefaultBrightness;

    public const int DefaultBrightness = 100;
    public const int MinBrightness = (int)(Renderer.MinGamma * 100);
    public const int MaxBrightness = (int)(Renderer.MaxGamma * 100);
    public const int BrightnessStep = 10;

    public float Gamma => Math.Clamp(Brightness, MinBrightness, MaxBrightness) / 100f;

    /// <summary>ORIGINAL (DOS's palette look) or ENHANCED (the engine's lit pipeline; its options below).</summary>
    public Look Look { get; set; } = Look.Original;

    /// <summary>DISPLAY's RENDERER: the world drawn as is, as ASCII glyphs or as a point cloud (<see cref="ViewStyles"/>).</summary>
    public RenderStyle Style { get; set; } = RenderStyle.Default;

    /// <summary>DISPLAY's ASCII COLOUR: RETRO ASCII's glyphs in the scene's colours (FULL COLOUR), green or amber.</summary>
    public AsciiColour AsciiColour { get; set; } = AsciiColour.Source;

    /// <summary>ENHANCED OPTIONS' SHADOWS: the sun's and moon's shadows outdoors (interiors have none).</summary>
    public ShadowQuality Shadows { get; set; } = ShadowQuality.High;

    /// <summary>ENHANCED OPTIONS' ANTI-ALIASING (the enhanced look only; ORIGINAL stays unsmoothed).</summary>
    public AntiAliasing AntiAliasing { get; set; } = AntiAliasing.Fxaa;

    /// <summary>ENHANCED OPTIONS' LIGHTS: the most point lights gathered a frame,
    /// <see cref="LowLightBudget"/> or <see cref="HighLightBudget"/> (<see cref="Level.LightBudget"/>).</summary>
    public int LightBudget { get; set; } = HighLightBudget;

    public const int LowLightBudget = 64;
    public const int HighLightBudget = 256;

    /// <summary>ENHANCED OPTIONS' AMBIENT in %: the sky / ground light all around scaled
    /// (<see cref="MinAmbient"/>..<see cref="MaxAmbient"/>); how dark walls facing away from the sun are.</summary>
    public int Ambient { get; set; } = DefaultAmbient;

    public const int DefaultAmbient = 100;
    public const int MinAmbient = 50;
    public const int MaxAmbient = 200;
    public const int AmbientStep = 10;

    public float AmbientScale => Math.Clamp(Ambient, MinAmbient, MaxAmbient) / 100f;

    /// <summary>ENHANCED OPTIONS' AMBIENT OCCLUSION: corners and creases darkened. Off by default on
    /// Android: phones are short of memory bandwidth and its passes cost most there.</summary>
    public bool AmbientOcclusion { get; set; } = !OperatingSystem.IsAndroid();

    /// <summary>ENHANCED OPTIONS' GLOW: bright light bleeding into its surroundings.</summary>
    public bool Glow { get; set; } = true;

    /// <summary>ENHANCED OPTIONS' SURFACE RELIEF: how strongly HD textures with relief maps are lit by
    /// their normal and roughness (off: as without them).</summary>
    public ReliefLevel Relief { get; set; } = ReliefLevel.Low;

    /// <summary>ENHANCED OPTIONS' PARTICLES: the dust and ash in the air and the smoke over fires and
    /// burning wrecks (<see cref="Enhanced.Atmosphere"/>); off: none.</summary>
    public ParticleLevel Particles { get; set; } = ParticleLevel.High;

    public const int DefaultWindowWidth = 1280;
    public const int DefaultWindowHeight = 720;
    /// <summary>The window sizes the settings file accepts.</summary>
    public const int MinWindowSide = 320;
    public const int MaxWindowSide = 16384;

    /// <summary>The last new game's name and call sign (capitalised, as DOS keeps them): what the
    /// new-game dialog offers next time; empty before the first.</summary>
    public string PlayerName { get; set; } = "";

    public string CallSign { get; set; } = "";

    /// <summary>The network screens' last choices (<see cref="NetMenuSettings"/>): the host's game
    /// and the address last joined.</summary>
    public NetMenuSettings Net { get; set; } = new();

    /// <summary>MODS: the user mods turned on, in load order (later wins; <see cref="SkyNet.Game.Mods.ModOrder"/>).
    /// <c>--mod</c> replaces the list for one run without changing it.</summary>
    public List<string> Mods { get; set; } = [];

    /// <summary>The HI-RES TEXTURES notice (the enhanced look with no texture mod on) was shown and
    /// dismissed: it comes up once.</summary>
    public bool HiresNoticeSeen { get; set; }

    /// <summary>The FPS readout in the corner (F11, the console's <c>fps</c>); a port-only aid.</summary>
    public bool ShowFps { get; set; }

    /// <summary>The stereo output (<see cref="RetroEng.Render.StereoOutput.Names"/>: off, sbs, sbsr, sbsfull,
    /// crossview, tab, tabr, int, intr, viewer; the console's <c>stereo</c>, 3D VIEW's MODE); a port-only aid.</summary>
    public string Stereo { get; set; } = "off";

    /// <summary>The depth the HUD and menus show at in stereo (world units); 0: the convergence depth (zero parallax).</summary>
    public int CanvasDepth { get; set; }

    /// <summary>The phone viewer's lens (<see cref="RetroEng.Render.LensProfile.Names"/>: a preset, or custom
    /// with <see cref="LensK1"/> and <see cref="LensK2"/>).</summary>
    public string Viewer { get; set; } = "generic";

    /// <summary>The custom lens's coefficients, in hundredths.</summary>
    public int LensK1 { get; set; } = 44;
    public int LensK2 { get; set; } = 16;

    /// <summary>What turns the head (3D VIEW's HEAD TRACKING): off, or the phone's gyroscope.</summary>
    public RetroEng.Platform.HeadTracking HeadTracking { get; set; }

    /// <summary>Whether the head turns the aim too (3D VIEW's AIM: coupled), or looks on its own (decoupled).</summary>
    public RetroEng.Platform.AimCoupling Aim { get; set; }

    /// <summary>The phone screen's brightness while the game shows (3D VIEW's BACKLIGHT).</summary>
    public ScreenBrightness ScreenBrightness { get; set; }

    public const int MaxCanvasDepth = 10000;
    public const int MaxLensK = 200;

    /// <summary>The viewer's lens from <see cref="Viewer"/> (custom: <see cref="LensK1"/>, <see cref="LensK2"/>).</summary>
    public RetroEng.Render.LensProfile Lens() =>
        RetroEng.Render.LensProfile.Preset(Viewer) ?? new RetroEng.Render.LensProfile(LensK1 / 100f, LensK2 / 100f);

    /// <summary>The stereo eyes' distance apart in world units (the soldier's eye is 75 high: 3 ≈ 6.4 cm).</summary>
    public int StereoSeparation { get; set; } = DefaultStereoSeparation;

    /// <summary>The stereo convergence depth in world units: what lies there shows at the screen's depth.</summary>
    public int StereoDepth { get; set; } = DefaultStereoDepth;

    public const int DefaultStereoSeparation = 3;
    public const int MaxStereoSeparation = 40;
    public const int DefaultStereoDepth = 200;
    public const int MinStereoDepth = 10;
    public const int MaxStereoDepth = 10000;

    /// <summary>The debug overlay (F12, the console's <c>debug</c>); a port-only aid.</summary>
    public bool DebugOverlay { get; set; }

    /// <summary>The job pool's worker threads (the console's <c>workers</c>, <c>--workers</c>; read at
    /// start): <see cref="AutoWorkers"/> lets the engine pick (cores − 1, 2..8), 0 runs every job inline.</summary>
    public int Workers { get; set; } = AutoWorkers;

    public const int AutoWorkers = -1;

    /// <summary>MOUSE's SENSITIVITY HORIZONTAL and VERTICAL as DOS keeps them (CONTROLS.DAT +0x44 / +0x48):
    /// a divisor 1 (fastest) .. <see cref="MouseSteps"/> (slowest), <see cref="MouseDefault"/> at first;
    /// the default is the port's 0.176° a pixel (<see cref="MouseScaleX"/>).</summary>
    public int MouseH { get; set; } = MouseDefault;

    public int MouseV { get; set; } = MouseDefault;

    /// <summary>MOUSE's REVERSE VERTICAL (CONTROLS.DAT +0x60 bit 0): moving the mouse forward looks down.</summary>
    public bool MouseInvertY { get; set; }

    public const int MouseSteps = 11;
    public const int MouseDefault = 3;

    /// <summary>What DOS's bar shows of a divisor: <see cref="MouseSteps"/> + 1 − value lit segments (more is faster).</summary>
    public static int MouseLit(int value) => Math.Clamp(MouseSteps + 1 - value, 1, MouseSteps);

    /// <summary>The divisor of a bar of <paramref name="lit"/> segments.</summary>
    public static int MouseValue(int lit) => MouseSteps + 1 - Math.Clamp(lit, 1, MouseSteps);

    /// <summary>The look per mouse count against the default (DOS's gain is 4096 / value a mickey).</summary>
    public float MouseScaleX => (float)MouseDefault / Math.Clamp(MouseH, 1, MouseSteps);

    public float MouseScaleY => (float)MouseDefault / Math.Clamp(MouseV, 1, MouseSteps);

    /// <summary>GAMEPAD's LOOK SENSITIVITY across and up / down in % of the right stick's rate.</summary>
    public int PadH { get; set; } = DefaultPadLook;

    public int PadV { get; set; } = DefaultPadLook;

    public const int DefaultPadLook = 100;
    public const int MinPadLook = 25;
    public const int MaxPadLook = 300;
    public const int PadLookStep = 25;

    /// <summary>GAMEPAD's INVERT Y: pushing the right stick up looks down.</summary>
    public bool PadInvertY { get; set; }

    /// <summary>GAMEPAD's DEADZONE: the right stick's, in % of its reach.</summary>
    public int PadDeadZone { get; set; } = DefaultPadDeadZone;

    public const int DefaultPadDeadZone = 15;
    public const int MinPadDeadZone = 0;
    public const int MaxPadDeadZone = 50;
    public const int PadDeadZoneStep = 5;

    /// <summary>GAMEPAD's VIBRATION: the pad shakes when the soldier is hit (off at first: the port
    /// never shook before).</summary>
    public bool PadVibration { get; set; }

    /// <summary>GAMEPAD BUTTONS' MOVE / LOOK: which stick walks; the other looks.</summary>
    public PadSticks PadSticks { get; set; }

    /// <summary>MOUSE's and GAMEPAD's look on <paramref name="input"/>: every mover (on foot, the
    /// jeep's turret, the HK) turns by its counts, so this is all the wiring they need.</summary>
    public void ApplyLook(Input input)
    {
        input.MouseScaleX = MouseScaleX;
        input.MouseScaleY = MouseScaleY;
        input.InvertMouse = MouseInvertY;
        input.PadScaleX = Math.Clamp(PadH, MinPadLook, MaxPadLook) / 100f;
        input.PadScaleY = Math.Clamp(PadV, MinPadLook, MaxPadLook) / 100f;
        input.InvertPad = PadInvertY;
        input.PadDeadZone = Math.Clamp(PadDeadZone, MinPadDeadZone, MaxPadDeadZone) / 100f;
        input.PadSticks = PadSticks;
    }

    /// <summary>Action name → its keys and mouse buttons (the app fills in every action; an action
    /// absent here keeps its default).</summary>
    public Dictionary<string, string[]> Bindings { get; set; } = new(StringComparer.Ordinal);

    /// <summary>Action name → its gamepad buttons ("A", "RT"); as <see cref="Bindings"/>.</summary>
    public Dictionary<string, string[]> PadBindings { get; set; } = new(StringComparer.Ordinal);

    /// <summary>The console's bind: key name (SDL's, "F9") → the console line it runs in play
    /// (<see cref="DevTools.KeyBinds"/>).</summary>
    public Dictionary<string, string> CommandBinds { get; set; } = new(StringComparer.OrdinalIgnoreCase);

    public float SoundGain => Math.Clamp(SoundVolume, 0, VolumeSteps) / (float)VolumeSteps;

    public float MusicGain => Math.Clamp(MusicVolume, 0, VolumeSteps) / (float)VolumeSteps;

    public MenuSettings Clone()
    {
        var c = (MenuSettings)MemberwiseClone();
        c.Bindings = new Dictionary<string, string[]>(Bindings, StringComparer.Ordinal);
        c.PadBindings = new Dictionary<string, string[]>(PadBindings, StringComparer.Ordinal);
        c.CommandBinds = new Dictionary<string, string>(CommandBinds, StringComparer.OrdinalIgnoreCase);
        c.Net = Net.Clone();
        c.Mods = [.. Mods];
        return c;
    }
}

/// <summary>What the network screens remember (written when a game is hosted or joined): the
/// player's name and class, the host screen's fields (the Godot port's menu.gd defaults until the
/// first game: 8 players, 20 minutes, 20 kills, 3 bots at NORMAL, port 27015, BALANCED classes)
/// and the last address joined. No password is kept (the host's and the joiner's PASSWORD boxes
/// last as long as their screen).</summary>
public sealed class NetMenuSettings
{
    public const int DefaultMaxPlayers = 8;
    public const int DefaultTime = 20;
    public const int DefaultFrags = 20;
    public const int DefaultBots = 3;
    public const int DefaultBotSkill = 1;
    public const int DefaultPort = 27015;
    public const int MaxBots = 12;
    public const int MaxTime = 180;
    public const int MaxFrags = 99999;
    public const int MaxItems = 99;

    /// <summary>The name the others see; empty: the new-game dialog's call sign or name.</summary>
    public string Name { get; set; } = "";
    public bool Terminator { get; set; }
    /// <summary>TERMINATOR VISION: a TERMINATOR's machine vision draws the world as a point cloud
    /// (<see cref="ViewStyles.MachineCloud"/>) instead of DISPLAY's RENDERER; off by default, this
    /// machine's own (never sent).</summary>
    public bool TerminatorVision { get; set; }
    /// <summary>The avatar of the host screen's left box (DOS's 1..13, <see cref="Net.DmAvatarTable"/>;
    /// 7 and 13 are the machines, <see cref="Terminator"/>); 0: none chosen yet (the class's first).</summary>
    public int Avatar { get; set; }
    /// <summary>The arena's map number; 0: NETLEVEL.PRS's first.</summary>
    public int Arena { get; set; }
    public int MaxPlayers { get; set; } = DefaultMaxPlayers;
    public int TimeLimit { get; set; } = DefaultTime;
    /// <summary>The limit of the GOAL box (kills, points, lives or minutes for its goal).</summary>
    public int FragLimit { get; set; } = DefaultFrags;
    /// <summary>The GOAL chosen; null: the arena's (NETLEVEL.PRS).</summary>
    public Net.DmGoal? Goal { get; set; }
    /// <summary>CHEATS: the console's cheats work in a game hosted from the menu.</summary>
    public bool Cheats { get; set; }
    /// <summary>CLASSES: DOS or BALANCED (the default) for a game hosted from the menu.</summary>
    public Net.DmClassRules Classes { get; set; } = Net.DmClassRules.Balanced;
    public int Bots { get; set; } = DefaultBots;
    /// <summary>0 EASY, 1 NORMAL, 2 HARD (the SKILL LEVEL box shows 1-3).</summary>
    public int BotSkill { get; set; } = DefaultBotSkill;
    public int Port { get; set; } = DefaultPort;
    /// <summary>REPLENISH and the item counts as last set for <see cref="Arena"/>; null: the arena's own.</summary>
    public bool? Replenish { get; set; }
    public Dictionary<string, int>? Items { get; set; }
    /// <summary>The last address joined (host or ip[:port]).</summary>
    public string Address { get; set; } = "";
    /// <summary>The master server (host[:port]) of the join screen's INTERNET tab and of PUBLIC games;
    /// empty by default (the repo holds no address: <see cref="Net.MasterAddresses"/> says where else it
    /// comes from).</summary>
    public string Master { get; set; } = "";
    /// <summary>The host screen's PUBLIC: announce a hosted game to <see cref="Master"/>.</summary>
    public bool Public { get; set; }
    /// <summary>The host screen's DEDICATED as last chosen.</summary>
    public bool Dedicated { get; set; }

    /// <summary>Whether <paramref name="master"/> names a master server (host[:port]).</summary>
    public static bool MasterSet(string? master) => Net.MasterAddresses.Usable(master);

    public NetMenuSettings Clone()
    {
        var c = (NetMenuSettings)MemberwiseClone();
        c.Items = Items == null ? null : new Dictionary<string, int>(Items, StringComparer.Ordinal);
        return c;
    }
}

/// <summary>How the game's window shows (the Godot port's WIN_WINDOWED / WIN_BORDERLESS /
/// WIN_FULLSCREEN): a window of the chosen size, a borderless window over the whole desktop, or
/// exclusive fullscreen in the desktop's own resolution.</summary>
public enum WindowMode { Windowed, Borderless, Fullscreen }

/// <summary>The picture: DOS's own, or the engine's enhanced lighting.</summary>
public enum Look { Original, Enhanced }

/// <summary>ENHANCED OPTIONS' SURFACE RELIEF (<see cref="Enhanced.LookLighting.ReliefOf"/>).</summary>
public enum ReliefLevel { Off, Low, Medium, High }

/// <summary>ENHANCED OPTIONS' PARTICLES (<see cref="Enhanced.Atmosphere"/>).</summary>
public enum ParticleLevel { Off, Low, High }

/// <summary>Why a settings file was not written.</summary>
public enum SaveResult { Saved, ForeignFile, Failed }

/// <summary>
/// The settings file of the C# port: JSON in the user's application-data folder
/// (<see cref="Folder"/>/<see cref="FileName"/>), its own name and folder, apart from the Godot
/// port's <c>user://</c> files. The file carries <see cref="Format"/>; a file at the path without
/// it belongs to someone else and is neither read nor overwritten.
/// </summary>
public static class SettingsStore
{
    public const string Folder = "SkyNET-CSharp";
    public const string FileName = "skynet-csharp-settings.json";
    public const string Format = "skynet-csharp-settings";
    public const int Version = 2;

    /// <summary>Files before this version saved LowRes as the default: they get HiRes once.</summary>
    private const int HiResHudVersion = 2;

    /// <summary>The player's files - the settings, the console history, the saves - beside the game
    /// (the exe's folder), as the logs are; where that cannot be written (Program Files, Android),
    /// <see cref="AppDataFolder"/>.</summary>
    public static string UserFolder => _userFolder ??= PickUserFolder();

    /// <summary>%APPDATA%/SkyNET-CSharp (XDG config on Linux): the fallback, and where builds before
    /// the files moved beside the game kept them.</summary>
    public static string AppDataFolder =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData, Environment.SpecialFolderOption.DoNotVerify),
            Folder);

    /// <summary>The settings file in <see cref="UserFolder"/>.</summary>
    public static string DefaultPath => Path.Combine(UserFolder, FileName);

    /// <summary>The app-data logs folder: the log's fallback when the game's folder cannot be written.</summary>
    public static string LogFolder => Path.Combine(AppDataFolder, "logs");

    private static string? _userFolder;

    private static string PickUserFolder()
    {
        var game = AppContext.BaseDirectory;
        try
        {
            var probe = Path.Combine(game, $".write-test-{Environment.ProcessId}");
            File.WriteAllBytes(probe, []);
            File.Delete(probe);
            return game;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            return AppDataFolder;
        }
    }

    /// <summary>Copies the settings, the console history and the saves an older build kept in
    /// <see cref="AppDataFolder"/> into <see cref="UserFolder"/>, each only where the new place has
    /// none yet; the old files stay. Returns how many files were copied.</summary>
    public static int CopyFromAppData()
    {
        var from = AppDataFolder;
        var to = UserFolder;
        if (string.Equals(Path.GetFullPath(from).TrimEnd(Path.DirectorySeparatorChar), Path.GetFullPath(to).TrimEnd(Path.DirectorySeparatorChar),
                StringComparison.OrdinalIgnoreCase) || !Directory.Exists(from))
        {
            return 0;
        }

        var copied = 0;
        foreach (var name in new[] { FileName, "console-history.txt" })
        {
            copied += CopyIfAbsent(Path.Combine(from, name), Path.Combine(to, name));
        }

        var saves = Path.Combine(from, "saves");
        if (Directory.Exists(saves))
        {
            foreach (var file in Directory.EnumerateFiles(saves, "*.sav", SearchOption.AllDirectories))
            {
                copied += CopyIfAbsent(file, Path.Combine(to, "saves", Path.GetRelativePath(saves, file)));
            }
        }

        return copied;
    }

    private static int CopyIfAbsent(string from, string to)
    {
        if (!File.Exists(from) || File.Exists(to))
        {
            return 0;
        }

        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(to)!);
            File.Copy(from, to);
            return 1;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            return 0;
        }
    }

    /// <summary>Is the file at <paramref name="path"/> ours (or absent)?</summary>
    public static bool IsOwnOrAbsent(string path)
    {
        if (!File.Exists(path))
        {
            return true;
        }

        try
        {
            return JsonItem.Parse(File.ReadAllBytes(path))["format"] is { } f && f.TryString(out var format) && format == Format;
        }
        catch (Exception e) when (e is JsonDataException or IOException or UnauthorizedAccessException)
        {
            return false;
        }
    }

    /// <summary>Whether the file at <paramref name="path"/> (ours) holds a DETAIL; none: a first
    /// start, which guesses one (<see cref="DetailGuess"/>).</summary>
    public static bool HasDetail(string path)
    {
        if (!File.Exists(path) || !IsOwnOrAbsent(path))
        {
            return false;
        }

        try
        {
            return JsonItem.Parse(File.ReadAllBytes(path))["detail"] is not null;
        }
        catch (Exception e) when (e is JsonDataException or IOException or UnauthorizedAccessException)
        {
            return false;
        }
    }

    /// <summary>The settings at <paramref name="path"/>; the defaults when it is absent, foreign or
    /// unreadable. A value out of range falls back to its default.</summary>
    public static MenuSettings Load(string path)
    {
        var s = new MenuSettings();
        if (!File.Exists(path) || !IsOwnOrAbsent(path))
        {
            return s;
        }

        try
        {
            var root = JsonItem.Parse(File.ReadAllBytes(path));
            s.Detail = Enum(root, "detail", s.Detail);
            s.HudArt = Int(root, "version", 1, 1, int.MaxValue) >= HiResHudVersion ? Enum(root, "hudArt", s.HudArt) : s.HudArt;
            s.HudStyle = Enum(root, "hudStyle", s.HudStyle);
            s.UiScale = Enum(root, "uiScale", s.UiScale);
            s.Difficulty = Enum(root, "difficulty", s.Difficulty);
            s.RobotAi = Enum(root, "robotAi", s.RobotAi);
            s.SoundVolume = Int(root, "soundVolume", s.SoundVolume, 0, MenuSettings.VolumeSteps);
            s.MusicVolume = Int(root, "musicVolume", s.MusicVolume, 0, MenuSettings.VolumeSteps);
            s.WindowMode = Enum(root, "windowMode", s.WindowMode);
            var w = Int(root, "windowWidth", -1, MenuSettings.MinWindowSide, MenuSettings.MaxWindowSide);
            var h = Int(root, "windowHeight", -1, MenuSettings.MinWindowSide, MenuSettings.MaxWindowSide);
            if (w > 0 && h > 0)
            {
                (s.WindowWidth, s.WindowHeight) = (w, h);
            }
            var fw = Int(root, "fullscreenWidth", -1, MenuSettings.MinWindowSide, MenuSettings.MaxWindowSide);
            var fh = Int(root, "fullscreenHeight", -1, MenuSettings.MinWindowSide, MenuSettings.MaxWindowSide);
            if (fw > 0 && fh > 0)
            {
                (s.FullscreenWidth, s.FullscreenHeight) = (fw, fh);
            }

            s.VSync = Enum(root, "vsync", s.VSync);
            var scale = Int(root, "renderScale", s.RenderScale, Renderer.AutoScale, int.MaxValue);
            s.RenderScale = scale == Renderer.AutoScale || Array.IndexOf(Renderer.Scales, scale) >= 0 ? scale : s.RenderScale;
            s.Brightness = Int(root, "brightness", s.Brightness, MenuSettings.MinBrightness, MenuSettings.MaxBrightness);
            s.Look = Enum(root, "look", s.Look);
            s.Style = Enum(root, "renderer", s.Style);
            s.AsciiColour = Enum(root, "asciiColour", s.AsciiColour);
            s.Shadows = Enum(root, "shadows", s.Shadows);
            s.AntiAliasing = Enum(root, "antiAliasing", s.AntiAliasing);
            var budget = Int(root, "lightBudget", s.LightBudget, 1, int.MaxValue);
            s.LightBudget = budget is MenuSettings.LowLightBudget or MenuSettings.HighLightBudget ? budget : s.LightBudget;
            s.Ambient = Int(root, "ambient", s.Ambient, MenuSettings.MinAmbient, MenuSettings.MaxAmbient);
            s.AmbientOcclusion = Bool(root, "ambientOcclusion", s.AmbientOcclusion);
            s.Glow = Bool(root, "glow", s.Glow);
            // Before the levels: an on / off "surfaceRelief" (on: MEDIUM, the strength it had then).
            s.Relief = root["relief"] is not null ? Enum(root, "relief", s.Relief)
                : root["surfaceRelief"] is { Kind: JsonKind.True or JsonKind.False } old ? (old.AsBool() ? ReliefLevel.Medium : ReliefLevel.Off)
                : s.Relief;
            s.Particles = Enum(root, "particles", s.Particles);
            s.PlayerName = Campaign.Pilot.Capitalise(Campaign.Pilot.Clean(Str(root, "playerName"), Campaign.Pilot.NameLength));
            s.CallSign = Campaign.Pilot.Capitalise(Campaign.Pilot.Clean(Str(root, "callSign"), Campaign.Pilot.CallSignLength));
            s.ReverseStereo = Bool(root, "reverseStereo", s.ReverseStereo);
            s.ShowFps = Bool(root, "showFps", s.ShowFps);
            s.Workers = Int(root, "workers", s.Workers, MenuSettings.AutoWorkers, RetroEng.Jobs.JobSystem.MaxWorkers);
            s.DebugOverlay = Bool(root, "debugOverlay", s.DebugOverlay);
            s.Stereo = RetroEng.Render.StereoOutput.Parse(Str(root, "stereo")) is { } stereo ? stereo.Name : s.Stereo;
            s.StereoSeparation = Int(root, "stereoSeparation", s.StereoSeparation, 0, MenuSettings.MaxStereoSeparation);
            s.StereoDepth = Int(root, "stereoDepth", s.StereoDepth, MenuSettings.MinStereoDepth, MenuSettings.MaxStereoDepth);
            s.CanvasDepth = Int(root, "canvasDepth", s.CanvasDepth, 0, MenuSettings.MaxCanvasDepth);
            s.Viewer = Array.IndexOf(RetroEng.Render.LensProfile.Names, Str(root, "viewer")) >= 0 ? Str(root, "viewer") : s.Viewer;
            s.LensK1 = Int(root, "lensK1", s.LensK1, 0, MenuSettings.MaxLensK);
            s.LensK2 = Int(root, "lensK2", s.LensK2, 0, MenuSettings.MaxLensK);
            s.HeadTracking = Enum(root, "headTracking", s.HeadTracking);
            s.Aim = Enum(root, "aim", s.Aim);
            s.ScreenBrightness = Enum(root, "screenBrightness", s.ScreenBrightness);
            s.MouseH = Int(root, "mouseH", s.MouseH, 1, MenuSettings.MouseSteps);
            s.MouseV = Int(root, "mouseV", s.MouseV, 1, MenuSettings.MouseSteps);
            s.MouseInvertY = Bool(root, "mouseInvertY", s.MouseInvertY);
            s.PadH = Int(root, "padH", s.PadH, MenuSettings.MinPadLook, MenuSettings.MaxPadLook);
            s.PadV = Int(root, "padV", s.PadV, MenuSettings.MinPadLook, MenuSettings.MaxPadLook);
            s.PadInvertY = Bool(root, "padInvertY", s.PadInvertY);
            s.PadDeadZone = Int(root, "padDeadZone", s.PadDeadZone, MenuSettings.MinPadDeadZone, MenuSettings.MaxPadDeadZone);
            s.PadVibration = Bool(root, "padVibration", s.PadVibration);
            s.PadSticks = Enum(root, "padSticks", s.PadSticks);

            s.HiresNoticeSeen = Bool(root, "hiresNoticeSeen", s.HiresNoticeSeen);
            if (root["mods"] is { Kind: JsonKind.Array } mods)
            {
                s.Mods = SkyNet.Game.Mods.ModOrder.Clean(mods.Items.Where(e => e.Kind == JsonKind.String).Select(e => e.AsString()));
            }

            if (root["net"] is { Kind: JsonKind.Object } net)
            {
                s.Net = LoadNet(net);
            }

            ReadBindings(root["bindings"], s.Bindings);
            ReadBindings(root["padBindings"], s.PadBindings);
            ReadCommandBinds(root["commandBinds"], s.CommandBinds);
        }
        catch (Exception e) when (e is JsonDataException or IOException or UnauthorizedAccessException)
        {
            return new MenuSettings();
        }

        return s;
    }

    /// <summary>Writes the settings (through a temporary file, so a crash leaves the old one);
    /// refuses to replace a file that is not ours.</summary>
    public static SaveResult Save(MenuSettings s, string path)
    {
        if (!IsOwnOrAbsent(path))
        {
            return SaveResult.ForeignFile;
        }

        try
        {
            var dir = Path.GetDirectoryName(Path.GetFullPath(path));
            if (dir != null)
            {
                Directory.CreateDirectory(dir);
            }

            var temp = path + ".tmp";
            File.WriteAllBytes(temp, Serialize(s));
            File.Move(temp, path, overwrite: true);
            return SaveResult.Saved;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            return SaveResult.Failed;
        }
    }

    /// <summary>A first run's file: every setting at its default and every action's keys and
    /// buttons from <paramref name="bindings"/>, so all of it can be read and edited. False when a
    /// file is there already (left as it is) or cannot be written.</summary>
    public static bool CreateIfMissing(string path, IBindings bindings)
    {
        if (File.Exists(path))
        {
            return false;
        }

        var s = new MenuSettings { Bindings = Rebind.Snapshot(bindings), PadBindings = PadRebind.Snapshot(bindings) };
        return Save(s, path) == SaveResult.Saved;
    }

    public static byte[] Serialize(MenuSettings s)
    {
        using var ms = new MemoryStream();
        using (var w = new Utf8JsonWriter(ms, new JsonWriterOptions { Indented = true }))
        {
            w.WriteStartObject();
            w.WriteString("format", Format);
            w.WriteNumber("version", Version);
            w.WriteString("detail", s.Detail.ToString());
            w.WriteNumber("soundVolume", s.SoundVolume);
            w.WriteNumber("musicVolume", s.MusicVolume);
            w.WriteBoolean("reverseStereo", s.ReverseStereo);
            w.WriteString("hudArt", s.HudArt.ToString());
            w.WriteString("hudStyle", s.HudStyle.ToString());
            w.WriteString("uiScale", s.UiScale.ToString());
            w.WriteString("difficulty", s.Difficulty.ToString());
            w.WriteString("robotAi", s.RobotAi.ToString());
            w.WriteNumber("windowWidth", s.WindowWidth);
            w.WriteNumber("windowHeight", s.WindowHeight);
            w.WriteString("windowMode", s.WindowMode.ToString());
            w.WriteNumber("fullscreenWidth", s.FullscreenWidth);
            w.WriteNumber("fullscreenHeight", s.FullscreenHeight);
            w.WriteString("vsync", s.VSync.ToString());
            w.WriteNumber("renderScale", s.RenderScale);
            w.WriteNumber("brightness", s.Brightness);
            w.WriteString("look", s.Look.ToString());
            w.WriteString("renderer", s.Style.ToString());
            w.WriteString("asciiColour", s.AsciiColour.ToString());
            w.WriteString("shadows", s.Shadows.ToString());
            w.WriteString("antiAliasing", s.AntiAliasing.ToString());
            w.WriteNumber("lightBudget", s.LightBudget);
            w.WriteNumber("ambient", s.Ambient);
            w.WriteBoolean("ambientOcclusion", s.AmbientOcclusion);
            w.WriteBoolean("glow", s.Glow);
            w.WriteString("relief", s.Relief.ToString());
            w.WriteString("particles", s.Particles.ToString());
            w.WriteString("playerName", s.PlayerName);
            w.WriteString("callSign", s.CallSign);
            w.WriteBoolean("showFps", s.ShowFps);
            w.WriteNumber("workers", s.Workers);
            w.WriteBoolean("debugOverlay", s.DebugOverlay);
            w.WriteString("stereo", s.Stereo);
            w.WriteNumber("stereoSeparation", s.StereoSeparation);
            w.WriteNumber("stereoDepth", s.StereoDepth);
            w.WriteNumber("canvasDepth", s.CanvasDepth);
            w.WriteString("viewer", s.Viewer);
            w.WriteNumber("lensK1", s.LensK1);
            w.WriteNumber("lensK2", s.LensK2);
            w.WriteString("headTracking", s.HeadTracking.ToString().ToLowerInvariant());
            w.WriteString("aim", s.Aim.ToString().ToLowerInvariant());
            w.WriteString("screenBrightness", s.ScreenBrightness.ToString().ToLowerInvariant());
            w.WriteNumber("mouseH", s.MouseH);
            w.WriteNumber("mouseV", s.MouseV);
            w.WriteBoolean("mouseInvertY", s.MouseInvertY);
            w.WriteNumber("padH", s.PadH);
            w.WriteNumber("padV", s.PadV);
            w.WriteBoolean("padInvertY", s.PadInvertY);
            w.WriteNumber("padDeadZone", s.PadDeadZone);
            w.WriteBoolean("padVibration", s.PadVibration);
            w.WriteString("padSticks", s.PadSticks.ToString().ToLowerInvariant());
            w.WriteBoolean("hiresNoticeSeen", s.HiresNoticeSeen);
            w.WriteStartArray("mods");
            foreach (var mod in s.Mods)
            {
                w.WriteStringValue(mod);
            }

            w.WriteEndArray();
            WriteNet(w, s.Net);
            WriteBindings(w, "bindings", s.Bindings);
            WriteBindings(w, "padBindings", s.PadBindings);
            WriteCommandBinds(w, s.CommandBinds);
            w.WriteEndObject();
        }

        return ms.ToArray();
    }

    private static NetMenuSettings LoadNet(JsonItem e)
    {
        var n = new NetMenuSettings
        {
            Name = Net.DmProtocol.CleanText(Str(e, "name"), Net.DmProtocol.MaxNameLength),
            Terminator = Bool(e, "terminator", false),
            TerminatorVision = Bool(e, "terminatorVision", false),
            Avatar = Int(e, "avatar", 0, 0, Net.DmAvatarTable.Count),
            Arena = Int(e, "arena", 0, 0, 999),
            MaxPlayers = Int(e, "maxPlayers", NetMenuSettings.DefaultMaxPlayers, 0, Net.DmProtocol.MaxPeers),
            TimeLimit = Int(e, "timeLimit", NetMenuSettings.DefaultTime, 0, NetMenuSettings.MaxTime),
            FragLimit = Int(e, "fragLimit", NetMenuSettings.DefaultFrags, 0, NetMenuSettings.MaxFrags),
            Goal = Int(e, "goal", -1, -1, 3) is var g and >= 0 ? (Net.DmGoal)g : null,
            Cheats = Bool(e, "cheats", false),
            Classes = e["classes"] is { Kind: JsonKind.String } cr && Net.DmProtocol.ParseRules(cr.AsString()) is { } rules ? rules : Net.DmClassRules.Balanced,
            Bots = Int(e, "bots", NetMenuSettings.DefaultBots, 0, NetMenuSettings.MaxBots),
            BotSkill = Int(e, "botSkill", NetMenuSettings.DefaultBotSkill, 0, 2),
            Port = Int(e, "port", NetMenuSettings.DefaultPort, 1024, 65535),
            Address = Net.DmProtocol.CleanText(Str(e, "address"), 64),
            Master = e["master"] is { Kind: JsonKind.String } m ? Net.DmProtocol.CleanText(m.AsString(), 128) : "",
            Public = Bool(e, "public", false),
            Dedicated = Bool(e, "dedicated", false),
        };
        if (e["replenish"] is { Kind: JsonKind.True or JsonKind.False } r)
        {
            n.Replenish = r.AsBool();
        }

        if (e["items"] is { Kind: JsonKind.Object } items)
        {
            n.Items = new Dictionary<string, int>(StringComparer.Ordinal);
            foreach (var key in Formats.NetLevelList.ItemKeys)
            {
                var v = Int(items, key, -1, 0, NetMenuSettings.MaxItems);
                if (v >= 0)
                {
                    n.Items[key] = v;
                }
            }
        }

        return n;
    }

    private static void WriteNet(Utf8JsonWriter w, NetMenuSettings n)
    {
        w.WriteStartObject("net");
        w.WriteString("name", n.Name);
        w.WriteBoolean("terminator", n.Terminator);
        w.WriteBoolean("terminatorVision", n.TerminatorVision);
        w.WriteNumber("avatar", n.Avatar);
        w.WriteNumber("arena", n.Arena);
        w.WriteNumber("maxPlayers", n.MaxPlayers);
        w.WriteNumber("timeLimit", n.TimeLimit);
        w.WriteNumber("fragLimit", n.FragLimit);
        if (n.Goal is { } goal)
        {
            w.WriteNumber("goal", (int)goal);
        }

        w.WriteBoolean("cheats", n.Cheats);
        w.WriteString("classes", n.Classes == Net.DmClassRules.Dos ? "dos" : "balanced");
        w.WriteNumber("bots", n.Bots);
        w.WriteNumber("botSkill", n.BotSkill);
        w.WriteNumber("port", n.Port);
        if (n.Replenish is { } r)
        {
            w.WriteBoolean("replenish", r);
        }

        if (n.Items != null)
        {
            w.WriteStartObject("items");
            foreach (var key in Formats.NetLevelList.ItemKeys)
            {
                if (n.Items.TryGetValue(key, out var v))
                {
                    w.WriteNumber(key, v);
                }
            }

            w.WriteEndObject();
        }

        w.WriteString("address", n.Address);
        w.WriteString("master", n.Master);
        w.WriteBoolean("public", n.Public);
        w.WriteBoolean("dedicated", n.Dedicated);
        w.WriteEndObject();
    }

    /// <summary>An object of action name → control names into <paramref name="into"/> (absent: nothing).</summary>
    private static void ReadBindings(JsonItem? e, Dictionary<string, string[]> into)
    {
        if (e is not { Kind: JsonKind.Object })
        {
            return;
        }

        foreach (var (name, value) in e.Members)
        {
            if (value.Kind == JsonKind.Array)
            {
                into[name] = [.. value.Items.Where(c => c.Kind == JsonKind.String).Select(c => c.AsString())];
            }
        }
    }

    private static void WriteBindings(Utf8JsonWriter w, string key, Dictionary<string, string[]> bindings)
    {
        w.WriteStartObject(key);
        foreach (var (name, controls) in bindings.OrderBy(p => p.Key, StringComparer.Ordinal))
        {
            w.WriteStartArray(name);
            foreach (var c in controls)
            {
                w.WriteStringValue(c);
            }

            w.WriteEndArray();
        }

        w.WriteEndObject();
    }

    /// <summary>An object of key name → console line into <paramref name="into"/> (absent: nothing).</summary>
    private static void ReadCommandBinds(JsonItem? e, Dictionary<string, string> into)
    {
        if (e is not { Kind: JsonKind.Object })
        {
            return;
        }

        foreach (var (key, value) in e.Members)
        {
            if (value.TryString(out var line) && line.Trim().Length > 0)
            {
                into[key] = line.Trim();
            }
        }
    }

    private static void WriteCommandBinds(Utf8JsonWriter w, Dictionary<string, string> binds)
    {
        w.WriteStartObject("commandBinds");
        foreach (var (key, line) in binds.OrderBy(p => p.Key, StringComparer.OrdinalIgnoreCase))
        {
            w.WriteString(key, line);
        }

        w.WriteEndObject();
    }

    private static T Enum<T>(JsonItem root, string name, T fallback) where T : struct, System.Enum =>
        root[name] is { } v && v.TryString(out var text) &&
        System.Enum.TryParse<T>(text, ignoreCase: true, out var parsed) && System.Enum.IsDefined(parsed)
            ? parsed
            : fallback;

    private static bool Bool(JsonItem root, string name, bool fallback) =>
        root[name] is { Kind: JsonKind.True or JsonKind.False } v ? v.AsBool() : fallback;

    private static string Str(JsonItem root, string name) =>
        root[name] is { } v && v.TryString(out var text) ? text : "";

    private static int Int(JsonItem root, string name, int fallback, int min, int max) =>
        root[name] is { } v && v.TryInt(out var i) && i >= min && i <= max
            ? i
            : fallback;

    /// <summary>For messages: the settings text as UTF-8 (tests).</summary>
    public static string Text(MenuSettings s) => Encoding.UTF8.GetString(Serialize(s));
}
