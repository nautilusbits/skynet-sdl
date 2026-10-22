using System.Drawing;
using System.Numerics;

namespace SkyNet.Game.Automap;

/// <summary>MAPBAR1.IMG's buttons, left to right (MAPBTN.CFA's frames); None: off the bar.</summary>
internal enum AutomapButton { None = -1, TurnLeft, TurnRight, TiltUp, TiltDown, ZoomIn, ZoomOut, Exit }

/// <summary>One frame of the automap's input: the held controls, a close (Esc, the automap key,
/// the pad's B), and the pointer in DOS pixels with the left button.</summary>
public readonly record struct AutomapInput
{
    public AutomapKeys Keys { get; init; }
    public bool Close { get; init; }
    /// <summary>The pointer on the 320 × 200 screen; null when it is not over the window.</summary>
    public PointF? Pointer { get; init; }
    /// <summary>The left button went down this frame.</summary>
    public bool Click { get; init; }
    /// <summary>The left button is held.</summary>
    public bool Held { get; init; }
}

/// <summary>
/// The automap (SKYNET.EXE v1.01 FUN_0013dc00, the CONTROLS AUTOMAP key, Tab): a full screen that
/// pauses the game. Its bar's buttons (table 0x5f2c6, all 17 high) repeat every frame while the
/// left button holds them, as DOS's handler runs each frame; the hovered one is lit (MAPBTN.CFA).
/// Esc, the automap key or EXIT closes it.
/// <code>
///   0      47     93     139    185    231    277   320
///   │ ↺    │ ↻    │ ↑    │ ↓    │ +    │ −    │ EXIT │  MAPBAR1.IMG, y 0..17
/// </code>
/// </summary>
public sealed class AutomapScreen
{
    /// <summary>The bar's height (MAPBAR1.IMG; MAPGRID.IMG starts under it).</summary>
    internal const int BarHeight = 17;

    /// <summary>The buttons' right edges (DOS pixels).</summary>
    private static readonly int[] ButtonEnds = [47, 93, 139, 185, 231, 277, 320];

    internal AutomapCamera Camera { get; } = new();

    public bool IsOpen { get; private set; }

    internal AutomapButton Hovered { get; private set; } = AutomapButton.None;

    /// <summary>Opens on the soldier's eye and look (<see cref="AutomapCamera.Open"/>).</summary>
    public void Open(Vector3 eye, float yaw, Vector3 forward)
    {
        Camera.Open(eye, yaw, forward);
        Hovered = AutomapButton.None;
        IsOpen = true;
    }

    public void Close() => IsOpen = false;

    /// <summary>One frame: a close, the bar under the pointer, then the controls held.</summary>
    public void Update(in AutomapInput input, float seconds)
    {
        if (!IsOpen)
        {
            return;
        }

        if (input.Close)
        {
            Close();
            return;
        }

        if (input.Pointer is { } at)
        {
            Hovered = ButtonAt(at);
        }

        if (Hovered == AutomapButton.Exit && input.Click)
        {
            Close();
            return;
        }

        var keys = input.Keys;
        if (input.Held)
        {
            keys |= KeysOf(Hovered);
        }

        Camera.Step(keys, seconds);
    }

    /// <summary>The button under a DOS pixel.</summary>
    internal static AutomapButton ButtonAt(PointF at)
    {
        if (at.Y < 0 || at.Y >= BarHeight || at.X < 0)
        {
            return AutomapButton.None;
        }

        var i = Array.FindIndex(ButtonEnds, end => at.X < end);
        return i < 0 ? AutomapButton.None : (AutomapButton)i;
    }

    private static AutomapKeys KeysOf(AutomapButton b) => b switch
    {
        AutomapButton.TurnLeft => AutomapKeys.TurnLeft,
        AutomapButton.TurnRight => AutomapKeys.TurnRight,
        AutomapButton.TiltUp => AutomapKeys.TiltUp,
        AutomapButton.TiltDown => AutomapKeys.TiltDown,
        AutomapButton.ZoomIn => AutomapKeys.ZoomIn,
        AutomapButton.ZoomOut => AutomapKeys.ZoomOut,
        _ => AutomapKeys.None,
    };
}
