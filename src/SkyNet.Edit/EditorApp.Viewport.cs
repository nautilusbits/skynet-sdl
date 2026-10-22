using System.Globalization;
using System.Numerics;
using Hexa.NET.ImGui;
using RetroEng.Diagnostics;
using RetroEng.Editor;
using RetroEng.Render;

namespace SkyNet.Edit;

/// <summary>
/// The 3D view as a panel: the "Viewport" window (docked in the dock space's middle by default, or anywhere,
/// a window of its own included) shows a view target the camera is drawn into at the window's size. Its
/// picture's place is the view's (<see cref="ViewArea"/>): picks, the gizmo, drops, the brush and pastes
/// take the pointer there; the keys are the view's while it is hovered or focused.
/// <code>
///   Draw ─► DrawView(target, camera at the area's aspect) ─► PresentNoScene ─► Panels: Viewport window
///            ▲                                                                   │ image of the target,
///            └──── area, hovered, focused, click (the next frame's input) ◄─────┘ its place and pointer
/// </code>
/// </summary>
internal sealed partial class EditorApp
{
    private const string ViewportTitle = "Viewport";
    // The window's own colour where no panel covers it (the view floated off, or closed in a tab).
    private static readonly Vector4 WindowClear = new(0.06f, 0.06f, 0.07f, 1f);

    private ViewTarget _viewTarget = null!;
    private ViewArea _viewArea;
    // The Viewport window drawn last frame (visible, not hidden behind a tab).
    private bool _viewShown;
    private bool _viewHovered, _viewFocused, _viewClicked, _looking;
    // --click ran (it waits for the Viewport window's size).
    private bool _clickDone;
    // Shots asked for (--screenshot, MCP): taken from the target after the next Present that draws it.
    private readonly List<Action<(int Width, int Height, byte[] Rgba)?>> _shots = [];

    /// <summary>The view's area; before the Viewport window is first drawn, the whole window's.</summary>
    private ViewArea ViewportArea => _viewArea.Size.X >= 1f ? _viewArea : new ViewArea(Vector2.Zero, new Vector2(_options.Width, _options.Height));

    /// <summary>The pointer in the view's coordinates.</summary>
    private Vector2 ViewMouse => ViewportArea.Local(ImGui.GetIO().MousePos);

    /// <summary>Whether the keys are the view's: no field takes text, and the view is hovered or focused
    /// (or no panel wants the keyboard).</summary>
    private bool ViewKeys => !_imgui.WantsText && (_viewHovered || _viewFocused || !_imgui.WantsKeyboard);

    private void MakeViewTarget() => _viewTarget = _renderer.CreateViewTarget(ViewportArea.Pixels);

    /// <summary>The camera drawn into the view target at the area's size (the window's frame has no scene of its own).</summary>
    private void DrawViewport(View camera, Vector4 sky, string? screenshot, string? windowShot)
    {
        if (_viewShown)
        {
            _viewTarget.Size = ViewportArea.Pixels;
            _renderer.DrawView(_viewTarget, camera, sky);
        }

        // The viewers' targets after the Viewport's: the point-light grid fits the frame's first target.
        _modelViewer?.Render(_level);
        EffectsRender();

        if (screenshot != null)
        {
            _shots.Add(shot => SaveShot(screenshot, shot));
        }

        Reveal(_viewShown);
        _renderer.PresentNoScene(WindowClear, windowShot ?? _captureShot);
        CloseSplash();
        SkyNet.Game.DevTools.FrameCapture.Mirror(windowShot, _captureShot);
        TakeShots();
        if (windowShot != null)
        {
            Log.Info("Edit", $"Screenshot: {windowShot} (the window)");
        }
    }

    // The Viewport window: the target's picture over the whole window; an invisible button takes the clicks
    // (no window drag from the picture) and tells where the view is.
    private unsafe void ViewportWindow()
    {
        var cond = _imgui.LayoutReset ? ImGuiCond.Always : ImGuiCond.FirstUseEver;
        ImGui.SetNextWindowDockID(CentralNode(), cond);
        ImGui.PushStyleVar(ImGuiStyleVar.WindowPadding, Vector2.Zero);
        var open = ImGui.Begin(ViewportTitle, ImGuiWindowFlags.NoScrollbar | ImGuiWindowFlags.NoScrollWithMouse | ImGuiWindowFlags.NoCollapse
            | ImGuiWindowFlags.NoBackground);
        ImGui.PopStyleVar();
        (_viewShown, _viewHovered, _viewFocused) = (open, false, false);
        if (open)
        {
            var min = ImGui.GetCursorScreenPos();
            var size = Vector2.Max(ImGui.GetContentRegionAvail(), Vector2.One);
            _viewClicked |= ImGui.InvisibleButton("##view", size, ImGuiButtonFlags.MouseButtonLeft);
            _viewHovered = ImGui.IsItemHovered(ImGuiHoveredFlags.AllowWhenBlockedByActiveItem);
            _viewFocused = ImGui.IsWindowFocused();
            var viewport = ImGui.GetWindowViewport();
            _viewArea = new ViewArea(min, size, viewport.FramebufferScale.X > 0f ? viewport.FramebufferScale.X : 1f);
            var picture = new ImTextureRef(null, new ImTextureID(ImGuiHost.TextureId(_viewTarget.Texture)));
            ImGui.GetWindowDrawList().AddImage(picture, min, min + size);
        }

        ImGui.End();
    }

    // The dock space's middle node (the dock space itself while it has none).
    private unsafe uint CentralNode()
    {
        var central = ImGuiP.DockBuilderGetCentralNode(_dockSpace);
        return central.Handle != null ? central.ID : _dockSpace;
    }

    /// <summary>The right mouse looks when it went down over the view; it goes on until released.</summary>
    private bool Looking(bool rightDown)
    {
        _looking = rightDown && (_looking || _viewHovered);
        return _looking;
    }

    // ---- the shot ----

    /// <summary>The shots asked for, from the view target's picture just presented (wherever its window is,
    /// a window of its own included); null to each when the Viewport was not drawn.</summary>
    private void TakeShots()
    {
        if (_shots.Count == 0)
        {
            return;
        }

        (int Width, int Height, byte[] Rgba)? shot = null;
        if (_viewShown)
        {
            _renderer.Capture(_viewTarget, (width, height, rgba) => shot = (width, height, ViewShot.Opaque(rgba)));
        }

        foreach (var done in _shots)
        {
            done(shot);
        }

        _shots.Clear();
    }

    private static void SaveShot(string path, (int Width, int Height, byte[] Rgba)? shot)
    {
        if (shot is not { } s)
        {
            Log.Error("Edit", $"Screenshot {path}: the Viewport is not shown");
            return;
        }

        ViewShot.WriteBmp(path, s.Width, s.Height, s.Rgba);
        Log.Info("Edit", string.Create(CultureInfo.InvariantCulture, $"Screenshot: {path} (the view, {s.Width} x {s.Height})"));
    }

    /// <summary>MCP's screenshot (the engine's built-in replaced): the view's pixels, as a PNG.</summary>
    private Task<McpResult> ShotTool(RetroEng.Json.JsonItem _)
    {
        var answer = new TaskCompletionSource<McpResult>(TaskCreationOptions.RunContinuationsAsynchronously);
        _shots.Add(shot => answer.TrySetResult(shot is { } s ? McpResult.Image(s.Width, s.Height, s.Rgba)
            : McpResult.Error("The Viewport is not shown (closed in a tab or collapsed)")));
        return answer.Task;
    }
}
