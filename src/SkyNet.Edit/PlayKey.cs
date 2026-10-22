using Hexa.NET.ImGui;

namespace SkyNet.Edit;

/// <summary>A viewer that plays something: a sound, a song, a sprite's frames, a model's animation, an effect.</summary>
public interface IPlayback
{
    bool Playing { get; }

    /// <summary>Play when paused (or stopped), pause (or stop) when playing.</summary>
    void TogglePlay();
}

/// <summary>What Space acts on this frame: pressed, the viewer focused (or its asset the active one), a text field being edited.</summary>
public readonly record struct PlayKeyInput(bool Space, bool Focused, bool Typing);

/// <summary>
/// Space = play/pause, the media convention, in every viewer: only the focused one (its window, or the Assets
/// panel while its asset is the one shown), never while a text field takes the keys.
/// </summary>
public static class PlayKey
{
    public const string Tip = "Space: play/pause";

    /// <summary>Whether the Assets panel had the focus last frame (its viewer's asset is then the active one).</summary>
    internal static bool AssetsFocused { get; set; }

    /// <summary>Toggles <paramref name="viewer"/> when Space is for it; true when it did.</summary>
    public static bool Handle(IPlayback viewer, PlayKeyInput key)
    {
        if (!key.Space || !key.Focused || key.Typing)
        {
            return false;
        }

        viewer.TogglePlay();
        return true;
    }

    /// <summary>Space for the current window (an asset viewer: the Assets panel's focus counts too).</summary>
    public static PlayKeyInput Read(PlayFocus focus = PlayFocus.Window)
    {
        var focused = ImGui.IsWindowFocused(ImGuiFocusedFlags.RootAndChildWindows) || (focus == PlayFocus.AssetViewer && AssetsFocused);
        return new PlayKeyInput(ImGui.IsKeyPressed(ImGuiKey.Space, false), focused, ImGui.GetIO().WantTextInput);
    }

    /// <summary>Space handled for the current window's <paramref name="viewer"/>.</summary>
    public static void Poll(IPlayback viewer, PlayFocus focus = PlayFocus.Window) => Handle(viewer, Read(focus));

    /// <summary>The key's hint on the last item (the play button).</summary>
    public static void Tooltip()
    {
        if (ImGui.IsItemHovered())
        {
            ImGui.SetTooltip(Tip);
        }
    }
}

/// <summary>Where a viewer is drawn: its own window, or the Asset Viewer (where the Assets panel's focus counts).</summary>
public enum PlayFocus
{
    Window,
    AssetViewer,
}
