using System.Numerics;
using RetroEng.Platform;

namespace SkyNet.Game.Menu;

/// <summary>
/// One frame of menu input: the fixed menu keys (keyboard arrows / Enter / Esc and the gamepad's
/// d-pad / A / B, <see cref="MenuKey"/>), the pointer in canvas units and its clicks, and the
/// control pressed this frame (for rebinding). Built from the engine's <see cref="Input"/> by
/// <see cref="From"/>, or by hand in tests.
/// </summary>
public readonly record struct MenuInput
{
    public bool Up { get; init; }
    public bool Down { get; init; }
    public bool Left { get; init; }
    public bool Right { get; init; }
    public bool Accept { get; init; }
    public bool Back { get; init; }

    /// <summary>The pointer on the canvas (<see cref="RetroEng.Render.Renderer.CanvasPoint"/>); null when it did not move.</summary>
    public Vector2? Pointer { get; init; }

    public bool Click { get; init; }

    /// <summary>The left button (or a finger) held now: a drag goes on while it is.</summary>
    public bool Held { get; init; }

    /// <summary>Wheel notches this frame, up positive.</summary>
    public float Wheel { get; init; }

    /// <summary>Text typed this frame (SDL's text input, on while a screen takes text: <see cref="MenuScreen.TakesText"/>).</summary>
    public string Typed { get; init; }

    /// <summary>Backspace this frame (a text field's erase).</summary>
    public bool Erase { get; init; }

    /// <summary>The key or mouse button pressed this frame by its binding name (<see cref="Input.LastControl"/>), "" when none.</summary>
    public string Control { get; init; }

    /// <summary>The gamepad button pressed this frame by its binding name (<see cref="Input.LastPadControl"/>), "" when none.</summary>
    public string PadControl { get; init; }

    public MenuInput()
    {
        Control = "";
        PadControl = "";
        Typed = "";
    }

    /// <summary>This frame's menu input; <paramref name="toCanvas"/> maps window coordinates to the
    /// canvas (pass <c>renderer.CanvasPoint</c>).</summary>
    public static MenuInput From(Input input, Func<float, float, Vector2> toCanvas) => new()
    {
        Up = input.WasPressed(MenuKey.Up),
        Down = input.WasPressed(MenuKey.Down),
        Left = input.WasPressed(MenuKey.Left),
        Right = input.WasPressed(MenuKey.Right),
        Accept = input.WasPressed(MenuKey.Accept),
        Back = input.WasPressed(MenuKey.Back),
        Pointer = input.PointerMoved || input.WasClicked(RetroEng.Platform.Pointer.Left) ? toCanvas(input.PointerX, input.PointerY) : null,
        Click = input.WasClicked(RetroEng.Platform.Pointer.Left),
        Wheel = input.Wheel,
        Typed = input.Typed,
        Erase = input.WasPressed(MenuKey.Backspace),
        Control = input.LastControl,
        PadControl = input.LastPadControl,
    };
}
