using System.Globalization;
using System.Numerics;
using System.Text;
using Hexa.NET.ImGui;
using RetroEng.Diagnostics;
using RetroEng.Editor;
using RetroEng.Json;
using RetroEng.Platform;

namespace SkyNet.Edit;

/// <summary>--script: scripted takes (docs/video.md). Each frame runs its <see cref="EditorScript"/> steps before
/// the frame is built. Verbs besides the editor's commands:
/// <code>
///   cam x,y,z yaw pitch      the camera (world units, degrees)
///   tool name {json}         an MCP tool (docs/mcp.md), run in-process
///   mouse x y | down | up    ImGui's pointer at a window pixel, the left button (right: down right)
///   key F5 | key ctrl+Z      an ImGui key pressed (and let go)
///   type text                text typed into the ImGui field with the focus
///   preview [camera] | stop  Play > Preview (from the camera), Esc
///   hold X | release X       a game key of the preview by its action name ("Forward")
///   window T x y w h         ImGui window T (title or ###id) put there, that size
///   quit
/// </code>
/// A capture also runs ImGui on the capture's clock and draws its pointer.</summary>
internal sealed partial class EditorApp
{
    private EditorScript? _script;
    // The typed MCP tools by name (Tool records them), made for --script without a server.
    private readonly Dictionary<string, Func<JsonItem, McpResult>> _scriptTools = [];
    // window steps, done in the next frame's panels (ImGui's windows exist then).
    private readonly List<(string Name, Vector2 At, Vector2 Size)> _scriptWindows = [];

    private void ScriptFrame()
    {
        if (_options.Script == null)
        {
            return;
        }

        if (_script == null)
        {
            _script = EditorScript.Parse(_options.Script);
            AssetsFor(_data);
            using var tools = new McpServer(() => EditHistory, _commands, _renderer);
            AddMcpTools(tools);
        }

        foreach (var step in _script.Due())
        {
            Log.Debug("Script", step);
            ScriptStep(step);
        }
    }

    private void ScriptStep(string step)
    {
        var space = step.IndexOf(' ', StringComparison.Ordinal);
        var (verb, arg) = space > 0 ? (step[..space], step[(space + 1)..].Trim()) : (step, "");
        var io = ImGui.GetIO();
        switch (verb)
        {
            case "cam": Camera(arg); break;
            case "tool": ScriptTool(arg); break;
            case "mouse":
                var xy = arg.Split(' ', StringSplitOptions.RemoveEmptyEntries).Select(Num).ToArray();
                // ImGui's pointer is on the desktop (its viewports): the main window's pixel from its top-left.
                var origin = ImGui.GetMainViewport().Pos;
                io.AddMousePosEvent(origin.X + xy[0], origin.Y + xy[1]);
                break;
            case "down" or "up": io.AddMouseButtonEvent(arg == "right" ? 1 : 0, verb == "down"); break;
            case "key": ScriptKey(io, arg); break;
            case "type": io.AddInputCharactersUTF8(arg); break;
            case "preview": StartPreview(fromCamera: arg == "camera"); break;
            case "stop": StopPreview(); break;
            case "hold" or "release" when _preview != null && _preview.Controls.Actions.Find(arg) is var action and >= 0:
                _playInput!.Hold(action, verb == "hold" ? Input.State.Down : Input.State.Up);
                break;
            case "window":
                var w = arg.Split(' ', StringSplitOptions.RemoveEmptyEntries);
                var n = w[^4..].Select(Num).ToArray();
                _scriptWindows.Add((string.Join(' ', w[..^4]), new Vector2(n[0], n[1]), new Vector2(n[2], n[3])));
                break;
            case "quit": _quit = true; break;
            default: RunCommand(step); break;
        }
    }

    // "x,y,z yaw pitch": the camera there, looking that way.
    private void Camera(string arg)
    {
        var parts = arg.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        var p = parts[0].Split(',').Select(Num).ToArray();
        _eye = new Vector3(p[0], p[1], p[2]);
        (_yaw, _pitch) = (Num(parts[1]) * MathF.PI / 180f, Num(parts[2]) * MathF.PI / 180f);
    }

    // "name {json}": the tool's result logged (an error as a warning).
    private void ScriptTool(string arg)
    {
        var space = arg.IndexOf(' ', StringComparison.Ordinal);
        var (name, json) = space > 0 ? (arg[..space], arg[(space + 1)..]) : (arg, "{}");
        if (!_scriptTools.TryGetValue(name, out var run))
        {
            Log.Warn("Script", $"No tool {name}");
            return;
        }

        run(JsonItem.Parse(Encoding.UTF8.GetBytes(json), lenient: false));
    }

    // "F5", "ctrl+Z", "shift+F5": the modifiers held around the key's press.
    private static void ScriptKey(ImGuiIOPtr io, string arg)
    {
        var parts = arg.Split('+');
        var mods = parts[..^1].Select(m => m.ToLowerInvariant() switch
        {
            "ctrl" => ImGuiKey.ModCtrl,
            "shift" => ImGuiKey.ModShift,
            _ => ImGuiKey.ModAlt,
        }).ToArray();
        var key = Enum.Parse<ImGuiKey>(parts[^1], ignoreCase: true);
        foreach (var m in mods)
        {
            io.AddKeyEvent(m, true);
        }

        io.AddKeyEvent(key, true);
        io.AddKeyEvent(key, false);
        foreach (var m in mods)
        {
            io.AddKeyEvent(m, false);
        }
    }

    private static float Num(string s) => float.Parse(s, NumberStyles.Float, CultureInfo.InvariantCulture);

    /// <summary>A capture's ImGui: its time on the capture's clock (the viewers, the effects, double-clicks
    /// keep pace with the frames) and the pointer drawn into the frames.</summary>
    private unsafe void CaptureImGui()
    {
        foreach (var (name, at, size) in _scriptWindows)
        {
            ImGui.SetWindowPos(name, ImGui.GetMainViewport().Pos + at);
            ImGui.SetWindowSize(name, size);
        }

        _scriptWindows.Clear();
        if (_capture == null)
        {
            return;
        }

        ImGui.GetCurrentContext().Handle->Time = _capture.Seconds;
        var io = ImGui.GetIO();
        io.DeltaTime = (float)_capture.Step;
        io.MouseDrawCursor = _options.Script != null;
    }
}
