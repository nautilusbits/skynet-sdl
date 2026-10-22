using System.Numerics;
using Hexa.NET.ImGui;
using RetroEng.Diagnostics;
using RetroEng.Editor;

namespace SkyNet.Edit;

/// <summary>The panels' layout: a dock space over the main window between the menu bar and the status
/// bar (the Viewport window, the 3D view, docked in its middle), panels placed by default where they always
/// were, View > Reset layout, and the main window's placement kept in the preferences. ImGuiHost keeps
/// the panel layout itself (SkyNetEdit.imgui.ini beside the preferences). --dock and --float check
/// docking and a panel's own window in an offscreen run.</summary>
internal sealed partial class EditorApp
{
    private const string DockSpaceName = "EditorDockSpace";
    /// <summary>The frame --dock and --float act on (the panels exist by then) and the one they check on.</summary>
    private const int LayoutActFrame = 3, LayoutCheckFrame = 6, ResetFrame = 8;

    private WindowTracker _tracker = null!;
    private uint _dockSpace;
    private int _layoutFrame;
    private bool _layoutChecked, _layoutFailed;

    /// <summary>The layout file beside the preferences; none for a batch run, nor for an offscreen one
    /// without its own --prefs (it never touches the user's layout).</summary>
    private static string? LayoutFile(EditorOptions options, string? prefsPath) =>
        prefsPath != null && (!options.Offscreen || options.Prefs != null) ? Path.ChangeExtension(prefsPath, ".imgui.ini") : null;

    /// <summary>The main window where it was last time (a shown window only).</summary>
    private void RestoreWindow()
    {
        _tracker = new WindowTracker(_window);
        if (!_options.Offscreen && _prefs.Window is { } saved)
        {
            var placed = _tracker.Apply(saved);
            if (placed != saved)
            {
                Log.Info("Edit", $"The window's last place is off the displays there are now: it opens on {placed.Display}");
            }
        }
    }

    /// <summary>On exit: the editor's state, where it is and the main window's placement (a shown window) into
    /// the preferences, written (with a preferences file).</summary>
    private void KeepWindow()
    {
        if (_prefsPath == null)
        {
            return;
        }

        CapturePrefs();
        CapturePlace();
        if (!_options.Offscreen)
        {
            _prefs.Window = _tracker.Current;
        }

        _prefs.Save(_prefsPath);
    }

    /// <summary>A panel's default place (from the main window's top-left) and size: its first time, or
    /// again after View > Reset layout.</summary>
    private void PanelAt(Vector2 at, Vector2 size)
    {
        var cond = _imgui.LayoutReset ? ImGuiCond.Always : ImGuiCond.FirstUseEver;
        var main = ImGui.GetMainViewport();
        ImGui.SetNextWindowPos(main.Pos + at, cond);
        ImGui.SetNextWindowSize(size, cond);
        if (_imgui.LayoutReset)
        {
            ImGui.SetNextWindowViewport(main.ID);
        }
    }

    /// <summary>The dock space over the main window from <paramref name="top"/> to <paramref name="bottom"/>
    /// above its foot; <paramref name="keepAlive"/>: only kept (the preview shows no panels).</summary>
    private void DockSpace(float top, float bottom, bool keepAlive = false)
    {
        var main = ImGui.GetMainViewport();
        ImGui.SetNextWindowPos(main.Pos + new Vector2(0f, top));
        ImGui.SetNextWindowSize(Vector2.Max(main.Size - new Vector2(0f, top + bottom), Vector2.One));
        ImGui.SetNextWindowViewport(main.ID);
        ImGui.PushStyleVar(ImGuiStyleVar.WindowRounding, 0f);
        ImGui.PushStyleVar(ImGuiStyleVar.WindowBorderSize, 0f);
        ImGui.PushStyleVar(ImGuiStyleVar.WindowPadding, Vector2.Zero);
        const ImGuiWindowFlags flags = ImGuiWindowFlags.NoTitleBar | ImGuiWindowFlags.NoCollapse | ImGuiWindowFlags.NoResize
            | ImGuiWindowFlags.NoMove | ImGuiWindowFlags.NoDocking | ImGuiWindowFlags.NoBringToFrontOnFocus | ImGuiWindowFlags.NoNavFocus
            | ImGuiWindowFlags.NoBackground | ImGuiWindowFlags.NoSavedSettings | ImGuiWindowFlags.NoScrollbar;
        ImGui.Begin("##dockhost", flags);
        ImGui.PopStyleVar(3);
        _dockSpace = ImGui.GetID(DockSpaceName);
        // Passthru fills the dock space with the window colour once the Viewport is in its middle: none (the
        // window's clear colour shows); docked panels fill their own.
        ImGui.PushStyleColor(ImGuiCol.WindowBg, 0u);
        ImGui.DockSpace(_dockSpace, Vector2.Zero, keepAlive ? ImGuiDockNodeFlags.KeepAliveOnly : ImGuiDockNodeFlags.PassthruCentralNode);
        ImGui.PopStyleColor();
        ImGui.End();
    }

    /// <summary>View > Reset layout: the panels back where they start, undocked, in the main window, the default ones shown;
    /// the Viewport docked in the middle again.</summary>
    private void ResetLayout()
    {
        _imgui.ResetLayout();
        var defaults = new EditorPrefs();
        (_prefs.ShowOutliner, _prefs.ShowProperties, _prefs.ShowLog, _prefs.ShowPlace) =
            (defaults.ShowOutliner, defaults.ShowProperties, defaults.ShowLog, defaults.ShowPlace);
        (_prefs.ShowTriggers, _prefs.ShowMission, _prefs.ShowValidation) = (defaults.ShowTriggers, defaults.ShowMission, defaults.ShowValidation);
        _prefs.ShowAssets = defaults.ShowAssets;
        _prefsChanged = true;
        Log.Info("Edit", "Layout reset");
    }

    /// <summary>--dock, --float and --reset-layout, after the panels of a frame.</summary>
    private unsafe void LayoutActions()
    {
        _layoutFrame++;
        if (_layoutFrame == LayoutActFrame)
        {
            if (_options.Dock is [var first, .. var others])
            {
                uint left, rest;
                ImGuiP.DockBuilderSplitNode(_dockSpace, ImGuiDir.Left, 0.25f, &left, &rest);
                ImGuiP.DockBuilderDockWindow(first, left);
                foreach (var other in others)
                {
                    ImGuiP.DockBuilderDockWindow(other, left);
                }

                ImGuiP.DockBuilderFinish(_dockSpace);
            }

            // A docked panel (the Viewport) leaves its node first; it moves the frame after.
            var window = _options.Float is { } name ? ImGuiP.FindWindowByName(name) : default;
            if (window.Handle != null && window.Handle->DockNode != null)
            {
                ImGuiP.DockContextQueueUndockWindow(ImGui.GetCurrentContext(), window);
            }
        }

        if (_layoutFrame == LayoutActFrame + 1 && _options.Float is { } floating)
        {
            // Beside the main window, off it: a window of its own.
            var main = ImGui.GetMainViewport();
            ImGui.SetWindowPos(floating, main.Pos + new Vector2(main.Size.X + 40f, 40f));
        }

        if (_layoutFrame == LayoutCheckFrame)
        {
            _layoutChecked = true;
            CheckLayout();
        }

        if (_layoutFrame == ResetFrame && _options.ResetLayout)
        {
            ResetLayout();
        }
    }

    private unsafe void CheckLayout()
    {
        foreach (var name in _options.Dock ?? [])
        {
            var window = ImGuiP.FindWindowByName(name);
            var root = window.Handle == null || window.Handle->DockNode == null ? null : window.Handle->DockNode;
            while (root != null && root->ParentNode != null)
            {
                root = root->ParentNode;
            }

            if (root == null || root->ID != _dockSpace)
            {
                Log.Error("Edit", $"--dock: {name} is not docked in the main dock space");
                _layoutFailed = true;
            }
            else
            {
                Log.Info("Edit", $"--dock: {name} docked in the main dock space (node 0x{window.Handle->DockNode->ID:X8})");
            }
        }

        if (_options.Float is { } floating)
        {
            var window = ImGuiP.FindWindowByName(floating);
            var main = ImGui.GetMainViewport();
            if (window.Handle == null || window.Handle->Viewport == null || window.Handle->ViewportId == main.ID
                || _imgui.ViewportWindows == 0 || _renderer.UiWindowCount == 0)
            {
                Log.Error("Edit", $"--float: {floating} has no window of its own ({_imgui.ViewportWindows} panel windows, {_renderer.UiWindowCount} claimed)");
                _layoutFailed = true;
            }
            else
            {
                Log.Info("Edit", $"--float: {floating} in a window of its own ({_imgui.ViewportWindows} panel windows, {_renderer.UiWindowCount} claimed by the renderer)");
            }
        }
    }

    /// <summary>Whether --dock or --float failed, or the run ended before their check.</summary>
    private bool LayoutFailed()
    {
        if ((_options.Dock != null || _options.Float != null) && !_layoutChecked)
        {
            Log.Error("Edit", $"--dock/--float: the run ended before frame {LayoutCheckFrame} (their check)");
            return true;
        }

        return _layoutFailed;
    }
}
