using System.Diagnostics;
using System.Net.Sockets;
using Hexa.NET.ImGui;
using RetroEng.Commands;
using RetroEng.Diagnostics;
using RetroEng.Editor;
using RetroEng.Json;
using RetroEng.Loop;
using SkyNet.Game.Campaign;
using SkyNet.Game.Mods;

namespace SkyNet.Edit;

/// <summary>
/// AI agents drive the editor over MCP (editor-tools plan, T9; docs/mcp.md): the engine's
/// <see cref="McpServer"/> on 127.0.0.1, opt-in (Preferences or --mcp). Its tools: the typed SkyNET ones
/// (EditorApp.McpWorld.cs, EditorApp.McpData.cs: thin wrappers over the editor's services), the screenshot, and every console
/// command. Each typed edit is one step of Edit > Undo's stack, which the undo and redo tools walk.
/// <code>
///   agent ──POST /mcp──► McpServer (pool thread) ──queue──► McpFrame (Panels, main thread)
///                                                              │ tool ► EditDocument / TableGrid / terrain ► EditHistory
///   agent ◄──────────────── JSON result ◄──────────────────────┘
/// </code>
/// </summary>
internal sealed partial class EditorApp
{
    // A typed call waits this long for the main thread (a play-test or a mission opening takes seconds).
    private static readonly TimeSpan McpTimeout = TimeSpan.FromMinutes(2);
    // A play-test ticks this long a frame, so the editor and other calls go on meanwhile; its answer waits up to PlayTimeout.
    private static readonly TimeSpan PlaySlice = TimeSpan.FromMilliseconds(20);
    private static readonly TimeSpan PlayTimeout = TimeSpan.FromMinutes(5);

    private McpServer? _mcp;
    // The port the running server was asked for; a port that failed is not tried again until the setting changes.
    private int _mcpPort;
    private int? _mcpFailedPort;
    // The play-test running, ticked a slice a frame (McpFrame).
    private McpPlay? _play;

    private bool McpWanted => _options.Mcp || _prefs.Mcp;

    private int McpPortWanted => _options.McpPort ?? _prefs.McpPort;

    /// <summary>The status bar's part: the endpoint while it listens.</summary>
    private string McpStatus => _mcp?.Url is { } url ? " | MCP " + url : "";

    /// <summary>Once a frame (main thread): the server follows the setting, then runs the calls waiting.</summary>
    private void McpFrame()
    {
        McpFollowSetting();
        if (_mcp == null)
        {
            return;
        }

        PlayStep();
        _mcp.Pump();
    }

    // On when the setting or --mcp asks; started again when the port changes.
    private void McpFollowSetting()
    {
        if (!McpWanted)
        {
            StopMcp();
            _mcpFailedPort = null;
            return;
        }

        var port = McpPortWanted;
        if ((_mcp != null && _mcpPort == port) || _mcpFailedPort == port)
        {
            return;
        }

        StopMcp();
        StartMcp(port);
    }

    private void StartMcp(int port)
    {
        // The asset and table commands are registered with the Assets panel's model: made now.
        AssetsFor(_data);
        // Undo and redo are the server's, on the open mission's stack; a tool's bug answers as a tool error (logged).
        var server = new McpServer(() => EditHistory, _commands, _renderer) { Timeout = McpTimeout, ToolErrors = ToolErrors.Report };
        AddMcpTools(server);
        try
        {
            var url = server.Start(port);
            (_mcp, _mcpPort, _mcpFailedPort) = (server, port, null);
            Log.Info("Edit", $"MCP: {url} (claude mcp add --transport http skynet-edit {url})");
        }
        catch (SocketException e)
        {
            server.Dispose();
            _mcpFailedPort = port;
            Log.Error("Edit", $"MCP: port {port} does not open ({e.Message}); Preferences picks another");
        }
    }

    private void StopMcp()
    {
        EndPlay();
        _mcp?.Dispose();
        _mcp = null;
    }

    // Preferences > AI agents (MCP).
    private void McpSettings(EditorPrefs p)
    {
        ImGui.SeparatorText("AI agents (MCP)");
        var (on, port) = (p.Mcp, p.McpPort);
        var changed = ImGui.Checkbox("Serve the editor's tools on localhost", ref on);
        changed |= ImGui.InputInt("Port (0: any free)", ref port);
        if (changed)
        {
            (p.Mcp, p.McpPort, _prefsChanged) = (on, Math.Clamp(port, 0, ushort.MaxValue), true);
        }

        var state = _mcp?.Url is { } url ? $"Listening: claude mcp add --transport http skynet-edit {url}"
            : _options.Mcp ? "--mcp: starting" : "Off";
        ImGui.TextDisabled(state);
    }

    // ---- the tools' plumbing ----

    /// <summary>Adds a typed tool whose expected failures (a bad argument, an edit that does not fit) answer as errors;
    /// --script's tool step runs it too.</summary>
    private void Tool(McpServer server, string name, string description, McpArg[] args, Func<JsonItem, McpResult> run)
    {
        Func<JsonItem, McpResult> guarded = a => Guard(a, run, McpResult.Error);
        _scriptTools[name] = guarded;
        server.Add(name, description, McpArgs.Schema(args), guarded);
    }

    /// <summary>Adds a deferred tool: <paramref name="run"/> starts on main, the agent is answered when its task ends.</summary>
    private static void ToolLater(McpServer server, string name, string description, McpArg[] args,
        Func<JsonItem, Task<McpResult>> run, TimeSpan timeout) =>
        server.Add(name, description, McpArgs.Schema(args), a => Guard(a, run, e => Task.FromResult(McpResult.Error(e))), timeout);

    private static T Guard<T>(JsonItem a, Func<JsonItem, T> run, Func<string, T> failed)
    {
        try
        {
            return run(a);
        }
        catch (Exception e) when (e is MapLayerException or JsonDataException or ArgumentException or InvalidOperationException
            or KeyNotFoundException or IOException or InvalidDataException or UnauthorizedAccessException or CommandUsageException
            or RetroEng.World.WorldFormatException)
        {
            return failed(e.Message);
        }
    }

    /// <summary>Runs <paramref name="edit"/> as one step of the undo stack (no merge with the step before or after):
    /// its JSON, with the step's label as "undo", or "changed": false when it recorded nothing.</summary>
    private McpResult Edited(Func<JsonItem> edit)
    {
        var history = EditHistory;
        history.EndMerge();
        var steps = history.UndoCount;
        var result = edit();
        history.EndMerge();
        var changed = !ReferenceEquals(history, EditHistory) || history.UndoCount != steps;
        result.Add("changed", changed);
        if (changed && EditHistory.UndoLabel is { } label)
        {
            result.Add("undo", label);
        }

        return McpResult.Json(result);
    }

    private static JsonItem Said(string text) => JsonItem.NewObject().Add("result", text);

    // ---- save, play-test (undo and redo: the server's) ----

    private void AddSessionTools(McpServer s)
    {
        Tool(s, "save_target", "Sets the folder mod (mods/<name>/) that save, new maps and table edits go to, as File > Save As picks it, "
            + "without writing. Pick it before editing tables: a table's rows are read for one mod.",
            [new("mod", "string", "a folder mod's name (not a system or ZIP mod)", true)], a => UseSaveMod(a.Text("mod"), out var error) is { } folder
                ? McpResult.Json(JsonItem.NewObject().Add("mod", _saveMod).Add("folder", folder)) : McpResult.Error(error!));
        Tool(s, "save", "File > Save: the mission's layers, terrains and changed tables into the save mod (save_target, or mod here), then mounted on top.",
            [new("mod", "string", "the folder mod to save to (default the current save mod)"),
             new("all", "boolean", "every non-empty layer, not only the changed ones (default: true when mod names another mod)")], SaveTool);
        ToolLater(s, "playtest", "Plays the shown instance headless in the game's own session for some game seconds (the edits as they are, unsaved ones too) "
            + "and reports what happened: phase, health, robots, objectives, the session's log. The editor goes on while it plays.",
            [new("seconds", "number", $"game seconds to run (default {PlayDefault}, at most {PlayMax})"), new("walk", "boolean", "the soldier walks forward the whole time"),
             new("instance", "string", "the instance to play (default the shown one)")], PlayTool, PlayTimeout);
        ToolLater(s, "screenshot", "The 3D view as the Viewport window shows it (the next frame), as a PNG.", [], ShotTool, McpTimeout);
    }

    /// <summary>Makes <paramref name="mod"/> the mod a save writes to (Save As's choice): its folder, null with the reason.</summary>
    private string? UseSaveMod(string mod, out string? error)
    {
        if (SaveTarget.Folder(_modsDir, mod, _systemMods, out error) is not { } folder)
        {
            return null;
        }

        _saveMod = mod.Trim();
        return folder;
    }

    private McpResult SaveTool(JsonItem a)
    {
        var mod = a.OptText("mod");
        var other = mod != null && !string.Equals(mod.Trim(), _saveMod, StringComparison.OrdinalIgnoreCase);
        if (mod != null && UseSaveMod(mod, out var error) == null)
        {
            return McpResult.Error(error!);
        }

        if (_saveMod == null || _doc == null)
        {
            return McpResult.Error(_doc == null ? "Nothing open to save" : "No save mod: pass mod (a folder mod's name) or call save_target");
        }

        // As File > Save: the tables first, then the layers and terrains.
        var tables = _tables?.Dirty == true ? _tables.Save() : "Tables: nothing changed";
        if (!SaveTo(_saveMod, a.Opt("all")?.AsBool() ?? other))
        {
            return McpResult.Error($"Not saved to {_saveMod} (see the log)");
        }

        return McpResult.Json(JsonItem.NewObject().Add("mod", _saveMod).Add("folder", SaveFolder()).Add("tables", tables).Add("dirty", IsDirty));
    }

    private const float PlayDefault = 3f, PlayMax = 120f;

    /// <summary>A play-test under way: its session, the soldier's input, the log so far, the agent's answer, the ticks left.</summary>
    private sealed record McpPlay(PreviewSession Preview, ulong Walk, List<string> Lines, TaskCompletionSource<McpResult> Done)
    {
        public int TicksLeft { get; set; }
    }

    // Starts the play-test; PlayStep ticks it a slice a frame and answers when its seconds are played.
    private Task<McpResult> PlayTool(JsonItem a)
    {
        if (_preview != null || _play != null)
        {
            return Task.FromResult(McpResult.Error(_play != null ? "A play-test is running" : "A preview is running (Esc ends it)"));
        }

        _ = McpLayer(a);
        var mission = _arena == null ? MissionTable.For(_data.Game).ByNumber(_world!.Mission.Number) : null;
        var arrival = _levelInstance == _doc!.Mission!.World.Start.Instance ? _doc.Mission.World.Start.Arrival : 0;
        var seconds = Math.Clamp(a.OptFloat("seconds") ?? PlayDefault, GameSession.Dt, PlayMax);
        var lines = new List<string>();
        var preview = new PreviewSession(_data, _doc, mission, _levelInstance!, arrival, PreviewBlock, terrains: EditedWld);
        preview.Session.Logger = line =>
        {
            lines.Add(line);
            Log.Info("Play-test", line);
        };

        // The fixed ticks of the game's loop, the soldier walking or standing.
        var walk = a.Flag("walk") ? TickInput.Bit(preview.Controls.Forward) : 0UL;
        _play = new McpPlay(preview, walk, lines, new TaskCompletionSource<McpResult>(TaskCreationOptions.RunContinuationsAsynchronously))
        {
            TicksLeft = (int)MathF.Ceiling(seconds * GameSession.TicksPerSecond),
        };
        return _play.Done.Task;
    }

    // Once a frame: the play-test's ticks for one slice; after the last, its report.
    private void PlayStep()
    {
        if (_play is not { } play)
        {
            return;
        }

        var slice = Stopwatch.StartNew();
        try
        {
            while (play.TicksLeft > 0 && slice.Elapsed < PlaySlice)
            {
                play.Preview.Tick(new TickInput(play.Walk, 0UL, 0UL, 0f, 0f, 0f, 0f, 0f));
                play.TicksLeft--;
            }

            if (play.TicksLeft > 0)
            {
                return;
            }

            play.Done.TrySetResult(PlayReport(play));
        }
        catch (Exception e)
        {
            // The game's bug: the server answers it as the tool's error and logs it (ToolErrors.Report).
            play.Done.TrySetException(e);
        }

        EndPlay();
    }

    // The play-test ends: its session goes (maps load as the editor reads them again); an unanswered call is cancelled.
    private void EndPlay()
    {
        if (_play is not { } play)
        {
            return;
        }

        _play = null;
        play.Preview.Dispose();
        play.Done.TrySetCanceled();
    }

    private static McpResult PlayReport(McpPlay play)
    {
        const int LogLines = 40;
        var session = play.Preview.Session;
        var robots = session.Robots?.All;
        var p = session.Player.Position;
        return McpResult.Json(JsonItem.NewObject()
            .Add("instance", session.Instance?.Id).Add("map", session.MapNumber).Add("phase", session.Phase.ToString())
            .Add("seconds", (int)MathF.Round(session.Clock))
            .Add("health", (int)session.Soldier.Health).Add("alive", session.Soldier.Alive)
            .Add("position", JsonItem.NewArray([(int)p.X, (int)p.Y, (int)p.Z]))
            .Add("robots", robots?.Count ?? 0).Add("robotsAlive", robots?.Count(r => r.Alive) ?? 0)
            .Add("objectivesLeft", session.Progress.Left).Add("objectives", session.Progress.Total)
            .Add("complete", session.Progress.Complete).Add("failed", session.Progress.Failed)
            .Add("log", JsonItem.NewArray(play.Lines.TakeLast(LogLines), l => l)));
    }
}
