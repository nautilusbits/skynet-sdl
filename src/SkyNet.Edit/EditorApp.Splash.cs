using System.Numerics;
using Hexa.NET.ImGui;
using RetroEng.Diagnostics;
using RetroEng.Editor;
using RetroEng.Render;
using SkyNet.Formats;
using SkyNet.Game.Menu;

namespace SkyNet.Edit;

/// <summary>
/// The start-up splash's end and Help > About. The splash is a window of its own (<see cref="SplashScreen"/>,
/// made in Program); the main window opens hidden and is shown with its first frame (<see cref="Reveal"/>),
/// the splash closed after it. About shows the game's title art (the player's data) darkened, the editor's
/// name and the versions.
/// </summary>
internal sealed partial class EditorApp
{
    // The About banner's darkening.
    private const float BannerShade = 0.72f;
    // The name's size in font heights.
    private const float TitleSize = 3.4f;
    // The About window's width; its height fits the text (0: ImGui's auto-fit).
    private const float AboutWidth = 520f;
    private const float BannerHeight = 150f;

    private readonly SplashScreen? _splash;
    private readonly AboutBox _about = new();
    private (TextureHandle Handle, int Width, int Height)? _titleArt;
    private bool _titleTried;
    private bool _revealed;
    // Frames presented hidden (dropped): the first ones are slow (pipelines) and lay out the Viewport, its scene
    // comes the next. Shown before the first after DroppedFrames with the scene, or after RevealLatest without.
    private const int DroppedFrames = 5, RevealLatest = 8;
    private int _hiddenFrames;

    /// <summary>Before a present: the hidden main window shown and raised once (a present before it is dropped).
    /// It waits <see cref="DroppedFrames"/> frames, and then while not <paramref name="ready"/> (no scene in the Viewport)
    /// up to <see cref="RevealLatest"/>.</summary>
    private void Reveal(bool ready = true)
    {
        // A hidden or automated run waits for nothing: its frames count (shots, captures).
        if (_revealed || !_options.Offscreen && _options.SplashRequest != SplashRequest.Automated && (++_hiddenFrames <= DroppedFrames || !ready && _hiddenFrames <= RevealLatest))
        {
            return;
        }

        _revealed = true;
        _window.Show();
    }

    /// <summary>After a present: the splash closed once the window is shown.</summary>
    private void CloseSplash()
    {
        if (_revealed)
        {
            _splash?.Close();
        }
    }

    /// <summary>The title art as a texture (made once; null when the data lacks it).</summary>
    private (TextureHandle Handle, int Width, int Height)? TitleTexture()
    {
        if (_titleTried)
        {
            return _titleArt;
        }

        _titleTried = true;
        if (TitleArt.Load(_data) is { } art)
        {
            _titleArt = (_renderer.CreateRgbaTexture(art.Width, art.Height, art.Rgba, scoped: false), art.Width, art.Height);
        }
        else
        {
            Log.Warn("Edit", $"{MenuLayout.TitleImage}: not in the data, no title art");
        }

        return _titleArt;
    }

    /// <summary>The title art over [min, max], cropped to keep its shape, and a black veil of <paramref name="shade"/>.</summary>
    private unsafe void TitleBanner(ImDrawListPtr list, Vector2 min, Vector2 max, float shade)
    {
        list.AddRectFilled(min, max, Colour(0f, 0f, 0f, 1f));
        if (TitleTexture() is { } art)
        {
            // Cover: the picture scaled to fill the box, its middle kept (e.g. 320 x 200 in a 16:9 window: top and bottom cut).
            var box = max - min;
            var (artAspect, boxAspect) = ((float)art.Width / art.Height, box.X / MathF.Max(box.Y, 1f));
            var (u, v) = artAspect > boxAspect ? (boxAspect / artAspect, 1f) : (1f, artAspect / boxAspect);
            var uvMin = new Vector2((1f - u) * 0.5f, (1f - v) * 0.5f);
            var picture = new ImTextureRef(null, new ImTextureID(ImGuiHost.TextureId(art.Handle)));
            list.AddImage(picture, min, max, uvMin, Vector2.One - uvMin);
        }

        list.AddRectFilled(min, max, Colour(0f, 0f, 0f, shade));
    }

    // Text centred on x at y in a font size: its height.
    private static unsafe float Centred(ImDrawListPtr list, string text, float size, float x, float y, uint colour)
    {
        var extent = TextSize(text, size);
        list.AddText(ImGui.GetFont(), size, new Vector2(x - extent.X * 0.5f, y), colour, text);
        return extent.Y;
    }

    private static Vector2 TextSize(string text, float size) => ImGui.CalcTextSize(text) * (size / MathF.Max(ImGui.GetFontSize(), 1f));

    private static uint Colour(float r, float g, float b, float a) => ImGui.ColorConvertFloat4ToU32(new Vector4(r, g, b, a));

    private void RegisterAboutCommands()
    {
        _commands.Add("help.about", "help.about", _ =>
        {
            _about.Show();
            return "About: open";
        }, "Help > About");
        _commands.Add("help.version", "help.version", _ => AboutBox.VersionInfo(_renderer.Gpu.Device, GpuBackends.Name(_renderer.Gpu.Backend)),
            "the version info Help > About copies");
    }

    /// <summary>Help > About: a small fixed window in the middle, the splash's banner and the facts a bug report needs.</summary>
    private void AboutWindow()
    {
        if (!_about.IsOpen)
        {
            return;
        }

        var main = ImGui.GetMainViewport();
        ImGui.SetNextWindowPos(main.Pos + main.Size * 0.5f, ImGuiCond.Always, new Vector2(0.5f, 0.5f));
        ImGui.SetNextWindowSize(new Vector2(AboutWidth * _prefs.UiScale, 0f), ImGuiCond.Always);
        var open = true;
        const ImGuiWindowFlags flags = ImGuiWindowFlags.NoResize | ImGuiWindowFlags.NoCollapse | ImGuiWindowFlags.NoDocking
            | ImGuiWindowFlags.NoSavedSettings | ImGuiWindowFlags.NoScrollbar;
        if (ImGui.Begin("About SkyNetEdit", ref open, flags))
        {
            AboutBody();
            if (ImGui.IsWindowFocused(ImGuiFocusedFlags.RootAndChildWindows) && ImGui.IsKeyPressed(ImGuiKey.Escape, false))
            {
                open = false;
            }
        }

        ImGui.End();
        _about.Keep(open && _about.IsOpen);
    }

    private void AboutBody()
    {
        // The banner: the art darkened, the name on it.
        var width = ImGui.GetContentRegionAvail().X;
        var min = ImGui.GetCursorScreenPos();
        var max = min + new Vector2(width, BannerHeight * _prefs.UiScale);
        var list = ImGui.GetWindowDrawList();
        TitleBanner(list, min, max, BannerShade);
        var line = ImGui.GetFontSize();
        var y = (min.Y + max.Y) * 0.5f - line * TitleSize * 0.6f;
        y += Centred(list, AboutBox.Title, line * TitleSize * 0.8f, (min.X + max.X) * 0.5f, y, Colour(1f, 1f, 1f, 1f));
        Centred(list, MainMenuScreen.VersionLabel, line, (min.X + max.X) * 0.5f, y, Colour(0.83f, 0.93f, 0.84f, 0.85f));
        ImGui.Dummy(max - min);

        var info = AboutBox.VersionInfo(_renderer.Gpu.Device, GpuBackends.Name(_renderer.Gpu.Backend));
        ImGui.TextWrapped(AboutBox.Description);
        ImGui.Separator();
        ImGui.TextUnformatted(info);
        ImGui.Separator();
        ImGui.BeginDisabled();
        ImGui.TextWrapped(AboutBox.Notice);
        ImGui.EndDisabled();
        ImGui.Spacing();
        if (ImGui.Button("Copy version info"))
        {
            ImGui.SetClipboardText(info);
            Log.Info("Edit", "About: version info copied");
        }

        ImGui.SameLine();
        if (ImGui.Button("Close"))
        {
            _about.Close();
        }
    }
}
