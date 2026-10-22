using System.Globalization;
using RetroEng.Diagnostics;
using RetroEng.Platform;
using RetroEng.Render;
using SkyNet.Edit;
using SkyNet.Formats;
using SkyNet.Game.Baking;
using SkyNet.Game.Campaign;
using SkyNet.Game.Menu;

// SkyNetEdit: the mission editor, a program of its own beside the game (docs/plans/editor-vr-menus.md,
// section 5: the game's exe carries no ImGui). It opens one mission's world as the game loads it, in
// the ORIGINAL or ENHANCED look, with a fly camera and Dear ImGui panels (outliner, properties, log).
// The command line is EditorOptions.Help (--help prints it).
if (args.Any(a => a is "--help" or "-h" or "-?" or "/?"))
{
    if (TextLogSink.AttachParentConsole())
    {
        Console.WriteLine(EditorOptions.Help);
    }
    else
    {
        SystemDialog.Info("SkyNetEdit", EditorOptions.Help);
    }

    return 0;
}

var o = EditorOptions.Parse(args, out var bad);
var ring = new LogRing(2000);
var errors = new ErrorCount();
Log.AddSink(ring);
Log.AddSink(errors);
if (o?.ConsoleLog == true || o?.CheckMissions == true || o?.ExportGltf != null)
{
    Log.AddSink(TextLogSink.Console(plain: true));
}

var logFile = FileLogSink.Open(Path.Combine(AppContext.BaseDirectory, "logs"), "skynetedit", keep: 5, out _);
if (logFile != null)
{
    Log.AddSink(logFile);
}

try
{
    if (o == null)
    {
        Log.Error("Edit", (bad ?? "Bad command line") + " (--help lists the arguments)");
        return 2;
    }

    SdlLog.Install(verbose: false);
    // The preferences (SkyNetEdit.json beside the exe, or --prefs); the command line overrides them.
    // --check-missions and --cycle-missions read them but never write them.
    var prefsPath = o.Prefs ?? EditorPrefs.DefaultPath;
    var prefs = EditorPrefs.Load(prefsPath);
    // An offscreen run without its own --prefs (agents, screenshots) borrows the user's file: it neither
    // takes the settings nor writes them, only the folders, mods and panels to start with.
    var borrowed = o.Offscreen && o.Prefs == null;
    if (borrowed)
    {
        prefs = prefs.Reset();
        prefs.Place = null;
    }

    var dataDir = GameDataLocator.Locate(o.Data ?? prefs.DataDir ?? Environment.GetEnvironmentVariable("SKYNET_DATA"), null,
        [AppContext.BaseDirectory, Environment.CurrentDirectory]);
    if ((dataDir != null ? GameData.Open(dataDir) : null) is not { } data)
    {
        Log.Error("Edit", "No game data found: pass --data <dir> or set SKYNET_DATA to the folder holding MDMDMAP2.BSA.");
        return 2;
    }

    // --game: the other data set when the folder found is the other game's.
    if (o.Game is { } game && game != data.Game)
    {
        if (EditorApp.OtherData(data) is not { } other)
        {
            Log.Error("Edit", $"--game {(game == GameKind.FutureShock ? "shock" : "skynet")}: that game's data is not beside {data.Dir}");
            return 2;
        }

        data = other;
    }

    var systemMods = new[] { GameKind.SkyNet, GameKind.FutureShock }.Select(BlockStore.SystemModName).ToArray();
    var modsDir = Path.GetFullPath(o.ModsDir ?? prefs.ModsDir ?? Path.Combine(AppContext.BaseDirectory, "mods"));
    if (SkyNet.Game.Mods.ModOrder.Effective(prefs.Mods, o.Mod != null ? SkyNet.Game.Mods.ModOrder.Parse(o.Mod) : null, systemMods) is { Length: > 0 } mods)
    {
        SkyNet.Game.Mods.ModContent.Mount(modsDir, mods);
    }

    // --export-gltf: models to .glb files (after the mods: their robot table hangs the segments), no window.
    if (o.ExportGltf != null)
    {
        return GltfExport.Run(data, o.ExportGltf, o.ExportDir ?? GltfExport.DefaultDir);
    }

    // The splash: its own small window from here until the editor's first frame (nothing offscreen).
    var art = SplashScreen.Shows(o.SplashRequest, prefs) ? TitleArt.Splash(data) : null;
    using var splashWindow = SplashScreen.Shows(o.SplashRequest, prefs) ? new SplashWindow(art, SplashScreen.Title, SplashScreen.Version(data.Game)) : null;
    var splash = splashWindow == null ? null : new SplashScreen(status => splashWindow.Status = status, () =>
    {
        if (o.SplashShot is { } shot)
        {
            SplashScreen.SaveShot(shot, art);
        }

        splashWindow.Dispose();
    });
    splash?.Step(SplashScreen.Data);

    var batch = o.CheckMissions || o.CycleMissions != null;
    // The editor owns the blocks (a game switch replaces them).
    using var app = new EditorApp(o, data, EditorApp.OpenBlocks(modsDir, data), ring, prefs, batch || borrowed ? null : prefsPath, modsDir, systemMods, splash);
    var code = o.CheckMissions ? app.CheckMissions() : o.CycleMissions != null ? app.CycleMissions(o.CycleMissions) : app.Run();
    if (batch)
    {
        Log.Info("Edit", errors.Count == 0 ? "Every mission opened without errors" : $"{errors.Count} error(s) logged");
        code = code == 0 && errors.Count == 0 ? 0 : 1;
    }

    return code;
}
catch (Exception e)
{
    Log.Error("Edit", e.ToString());
    return 1;
}
finally
{
    Log.Shutdown();
}

/// <summary>The command line.</summary>
internal sealed record EditorOptions(int? Mission = null, string? Instance = null, string? Data = null, string? Mod = null, string? ModsDir = null,
    Look? Look = null, System.Numerics.Vector3? Pos = null, float? Yaw = null, float? Pitch = null, int Width = 1600, int Height = 900,
    bool Offscreen = false, string? Screenshot = null, int Frames = int.MaxValue, bool CheckMissions = false, bool ImGuiDemo = false,
    bool ConsoleLog = false, System.Numerics.Vector2? Click = null, string? Prefs = null, int[]? CycleMissions = null, string? OpenMenu = null,
    (int Key, int X, int Y, int Z)? Move = null, string? SaveAs = null, string? Place = null,
    int? Arena = null, GameKind? Game = null, int? Select = null, bool? Preview = null,
    string[]? Dock = null, string? Float = null, bool ResetLayout = false, string? Command = null,
    string? ExportGltf = null, string? ExportDir = null, bool Mcp = false, int? McpPort = null, string? WindowScreenshot = null,
    string? Capture = null, int CaptureFps = SkyNet.Game.DevTools.FrameCapture.DefaultFps, string? Script = null,
    bool Splash = false, string? SplashShot = null)
{
    public static readonly string Help = "SkyNetEdit - the SkyNET mission editor\n" + AboutBox.Notice + "\n" + """
        Usage: SkyNetEdit [arguments]

          --mission N                 the mission to open (default where the editor was when it closed, else 1)
          --game skynet|shock         the data set whose missions open (default the data folder's; Future Shock's
                                      data lives beside SkyNET's, as the installer puts it)
          --arena N                   opens deathmatch arena MAP.N (a NETLEVEL.PRS level) instead of a mission
          --instance ID               the instance shown first (default the mission's start)
          --data <dir>                the game's data folder (else SKYNET_DATA, else the usual places)
          --mod <name>[,name]         mods in load order (later wins), as the game's --mod
          --mods-dir <dir>            the mods folder holding the baked map blocks (default mods\ beside the editor)
          --look original|enhanced    the viewport's look (default the preferences' default look)
          --pos x,y,z                 the camera position (world units; default above the player start)
          --yaw deg  --pitch deg      the camera direction
          --width N  --height N       the window's size (default 1600 x 900)
          --offscreen                 a hidden window (checks, screenshots); SKYNET_OFFSCREEN=1 does the same
          --screenshot file.bmp       saves the 3D view (the Viewport window's picture) of frame N (--frames,
                                      default 3) and quits
          --window-screenshot file.bmp  the whole window (panels included) of that frame too
          --frames N                  quits after N frames
          --capture <dir>             a video's frames: every frame one fixed step of 1/fps s (not the wall clock)
                                      and the whole window saved as <dir>\frame_000000.bmp, ...; --capture-fps N
                                      (default 60), --capture-frames N quits after N frames (as --frames). docs/video.md
          --script <file|steps>       a scripted take: steps run a frame at a time, wait N between them (a file's lines,
                                      else ';' between steps): commands, cam, tool, mouse, key, preview... docs/video.md
          --click x,y                 a left click at that pixel of the view (the Viewport window's picture,
                                      from its top-left) once the camera is set and the window has its size
          --check-missions            opens every mission (of --game), each instance drawn once: exit 1 on an error
          --cycle-missions N,M,...    opens the missions in turn: exit 1 on an error or a leaked texture or mesh
          --prefs <file>              the preferences file (default SkyNetEdit.json beside the editor; an
                                      --offscreen run without it neither takes the user's settings nor writes them)
          --open-menu <name>          that main menu shown open (screenshots)
          --move key,dx,dy,dz         moves entity key (its MAP offset) of the shown level by dx, dy, dz once open
          --select key                selects entity key (its MAP offset) of the shown level and flies to it
          --preview, --preview-camera plays the shown instance (from its start or the camera) once open; Esc returns
          --place kind:text           arms the first item of that Place palette (prop, robot, pickup, marker, light, sprite)
                                      whose label holds the text; --click then places it at that pixel
          --save-as <name>            when the frames run out, saves the layers to the folder mod <name> (exit 1 if not)
          --dock <panel>[,panel...]   docks the panels (by title) together into the main window's dock space
                                      (a layout check: exit 1 when they are not docked by frame 6)
          --float <panel>             moves that panel off the main window into a window of its own (exit 1 if not)
          --reset-layout              View > Reset layout at frame 8
          --command "<line>"          runs editor commands once open (';' between them), e.g.
                                      "asset.open texture 210/002" (the Assets panel; asset.list lists kinds:
                                      textures, sprites, models, sounds, music, tables; also asset.select, asset.close,
                                      asset.override, asset.path, asset.show; "pref showRobots false" sets a
                                      preference by its name in the file, "pref" lists them, pref.reset resets them;
                                      "table.open weapons", table.set, table.revert, table.add, table.save: the Tables window;
                                      sound.replace, sound.uses, sound.settings, music.play, music.stop;
                                      model.replace, robot.replace, model.remove, robot.revert, robot.gltf, model.sidecar,
                                      model.original: a model's or robot's replacement in the save mod;
                                      edit.copy, edit.cut, edit.paste [same|x,y], edit.duplicate, edit.drop,
                                      edit.snap, edit.align x|y|z min|centre|max, edit.distribute x|y|z: the selection)
          --export-gltf NAME|all      writes .3D model NAME (or every model) as NAME.glb (glTF 2.0: textures,
                                      frames as morph targets + an animation) and quits, no window; a robot
                                      with segments also as NAME.robot.glb (one skinned mesh, a joint per
                                      segment, a clip per AIS block; exit 1 when NAME does not load;
                                      all skips the few models the game cannot load either)
          --export-dir <dir>          where --export-gltf writes (default gltf\ in the current folder)
          --mcp [port]                serves the editor to AI agents over MCP on http://127.0.0.1:port/mcp (default
                                      the preferences' port; 0: any free one); off unless this or Preferences turn it on
          --splash [file.bmp]         runs the start-up splash's stages in an automated run too (--offscreen, --capture,
                                      --script skip it; offscreen it logs them, no window) and over a preference that
                                      turns it off; with a file, the splash's picture (title art, 640 x 360) saved there
          --imgui-demo                ImGui's demo window too
          --console-log               the log on stdout too
          --help                      this text

        Keys (Help > Keys, F1):
        """ + "\n" + EditorKeys.Text;

    public static EditorOptions? Parse(string[] args, out string? error)
    {
        error = null;
        var o = new EditorOptions(Offscreen: Environment.GetEnvironmentVariable("SKYNET_OFFSCREEN") == "1");
        for (var i = 0; i < args.Length; i++)
        {
            var value = i + 1 < args.Length ? args[i + 1] : null;
            bool Int(out int n) => int.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out n);
            bool Float(out float f) => float.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out f);
            switch (args[i])
            {
                case "--mission" when Int(out var m): o = o with { Mission = m }; i++; break;
                case "--arena" when Int(out var a): o = o with { Arena = a }; i++; break;
                case "--game" when value is "skynet" or "shock": o = o with { Game = value == "shock" ? GameKind.FutureShock : GameKind.SkyNet }; i++; break;
                case "--instance" when value != null: o = o with { Instance = value }; i++; break;
                case "--data" when value != null: o = o with { Data = value }; i++; break;
                case "--mod" when value != null: o = o with { Mod = value }; i++; break;
                case "--mods-dir" when value != null: o = o with { ModsDir = value }; i++; break;
                case "--look" when value is "original" or "enhanced": o = o with { Look = value == "enhanced" ? SkyNet.Game.Menu.Look.Enhanced : SkyNet.Game.Menu.Look.Original }; i++; break;
                case "--yaw" when Float(out var y): o = o with { Yaw = y }; i++; break;
                case "--pitch" when Float(out var p): o = o with { Pitch = p }; i++; break;
                case "--width" when Int(out var w): o = o with { Width = w }; i++; break;
                case "--height" when Int(out var h): o = o with { Height = h }; i++; break;
                case "--frames" when Int(out var f): o = o with { Frames = f }; i++; break;
                case "--screenshot" when value != null: o = o with { Screenshot = value }; i++; break;
                case "--window-screenshot" when value != null: o = o with { WindowScreenshot = value }; i++; break;
                case "--capture" when value != null: o = o with { Capture = value }; i++; break;
                case "--capture-fps" when Int(out var cf) && cf is >= SkyNet.Game.DevTools.FrameCapture.MinFps and <= SkyNet.Game.DevTools.FrameCapture.MaxFps:
                    o = o with { CaptureFps = cf }; i++; break;
                case "--script" when value != null: o = o with { Script = value }; i++; break;
                case "--capture-frames" when Int(out var cn) && cn > 0: o = o with { Frames = cn }; i++; break;
                case "--pos" when value?.Split(',') is [var x, var y, var z]
                    && float.TryParse(x, NumberStyles.Float, CultureInfo.InvariantCulture, out var px)
                    && float.TryParse(y, NumberStyles.Float, CultureInfo.InvariantCulture, out var py)
                    && float.TryParse(z, NumberStyles.Float, CultureInfo.InvariantCulture, out var pz):
                    o = o with { Pos = new(px, py, pz) }; i++; break;
                case "--click" when value?.Split(',') is [var cx, var cy]
                    && float.TryParse(cx, NumberStyles.Float, CultureInfo.InvariantCulture, out var kx)
                    && float.TryParse(cy, NumberStyles.Float, CultureInfo.InvariantCulture, out var ky):
                    o = o with { Click = new(kx, ky) }; i++; break;
                case "--prefs" when value != null: o = o with { Prefs = value }; i++; break;
                case "--open-menu" when value != null: o = o with { OpenMenu = value }; i++; break;
                case "--save-as" when value != null: o = o with { SaveAs = value }; i++; break;
                case "--select" when Int(out var sel): o = o with { Select = sel }; i++; break;
                case "--place" when value != null: o = o with { Place = value }; i++; break;
                case "--move" when value?.Split(',') is [var mk, var mx, var my, var mz]
                    && int.TryParse(mk, NumberStyles.Integer, CultureInfo.InvariantCulture, out var key)
                    && int.TryParse(mx, NumberStyles.Integer, CultureInfo.InvariantCulture, out var dx)
                    && int.TryParse(my, NumberStyles.Integer, CultureInfo.InvariantCulture, out var dy)
                    && int.TryParse(mz, NumberStyles.Integer, CultureInfo.InvariantCulture, out var dz):
                    o = o with { Move = (key, dx, dy, dz) }; i++; break;
                case "--cycle-missions" when value?.Split(',') is { Length: > 0 } list
                    && list.All(n => int.TryParse(n, NumberStyles.Integer, CultureInfo.InvariantCulture, out _)):
                    o = o with { CycleMissions = [.. list.Select(n => int.Parse(n, CultureInfo.InvariantCulture))], Offscreen = true }; i++; break;
                case "--preview": o = o with { Preview = false }; break;
                case "--preview-camera": o = o with { Preview = true }; break;
                case "--offscreen": o = o with { Offscreen = true }; break;
                case "--check-missions": o = o with { CheckMissions = true, Offscreen = true }; break;
                case "--dock" when value != null:
                    o = o with { Dock = value.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries) }; i++; break;
                case "--float" when value != null: o = o with { Float = value }; i++; break;
                case "--reset-layout": o = o with { ResetLayout = true }; break;
                case "--command" when value != null: o = o with { Command = value }; i++; break;
                case "--export-gltf" when value != null: o = o with { ExportGltf = value }; i++; break;
                case "--export-dir" when value != null: o = o with { ExportDir = value }; i++; break;
                case "--mcp" when Int(out var port) && port is >= 0 and <= ushort.MaxValue: o = o with { Mcp = true, McpPort = port }; i++; break;
                case "--mcp": o = o with { Mcp = true }; break;
                case "--splash" when value != null && value.EndsWith(".bmp", StringComparison.OrdinalIgnoreCase):
                    o = o with { Splash = true, SplashShot = value }; i++; break;
                case "--splash": o = o with { Splash = true }; break;
                case "--imgui-demo": o = o with { ImGuiDemo = true }; break;
                case "--console-log": o = o with { ConsoleLog = true }; break;
                default:
                    error = $"Unknown or incomplete argument: {args[i]}";
                    return null;
            }
        }

        if ((o.Screenshot != null || o.WindowScreenshot != null) && o.Frames == int.MaxValue)
        {
            o = o with { Frames = 3 };
        }

        return o;
    }

    /// <summary>How this run asks for the splash: --splash forces it; checks, hidden, captured and scripted runs skip it.</summary>
    public SplashRequest SplashRequest => Splash ? SplashRequest.Forced
        : Offscreen || Capture != null || Script != null || CheckMissions || CycleMissions != null ? SplashRequest.Automated
        : SplashRequest.Normal;
}

/// <summary>Counts the errors logged (--check-missions fails on any).</summary>
internal sealed class ErrorCount : ILogSink
{
    private int _count;
    public int Count => _count;

    public void Write(in LogEntry entry)
    {
        if (entry.Level >= LogLevel.Error)
        {
            Interlocked.Increment(ref _count);
        }
    }

    public void Flush(bool durable = false)
    {
    }
}
