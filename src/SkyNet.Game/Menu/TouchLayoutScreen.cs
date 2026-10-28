using System.Drawing;
using System.Numerics;
using RetroEng.Render;
using SkyNet.Game.Hud;

namespace SkyNet.Game.Menu;

/// <summary>TOUCH LAYOUT (CONTROLS → TOUCH LAYOUT, a touch screen only): the on-screen controls
/// over the dimmed screen, edited by <see cref="TouchEditor"/>. A finger (the mouse) drags; keys:
/// Up / Down pick a control, Left / Right shrink or grow it, Enter saves, Back cancels.
/// Saved places go to the settings; the buttons take them at the next start (the engine keeps its
/// buttons), the stick at once.</summary>
internal sealed class TouchLayoutScreen : MenuScreen
{
    public const string Id = "touchlayout";
    public const string Caption = "TOUCH LAYOUT";

    private const string Help = "DRAG TO MOVE - SELECT, THEN SMALLER / BIGGER - RESET: SELECTED, AGAIN: ALL";
    /// <summary>The stick's caption once placed (it no longer floats to the finger).</summary>
    private const string FixedStick = "MOVE (FIXED)";
    /// <summary>The engine's pause button (drawn so the player sees it is kept clear).</summary>
    private const string PauseLabel = "||";
    private const string SavedNote = "TOUCH LAYOUT SAVED: BUTTONS MOVE AT NEXT START";
    /// <summary>The play HUD's font (the buttons' words).</summary>
    private const string ButtonFont = "FONT0000.FNT";

    private static readonly Vector4 Dim = new(0f, 0f, 0f, 0.6f);

    private TouchEditor? _editor;
    private Vector2 _pointer;

    public override string Name => "touchlayout";

    public override void Opened()
    {
        if (_editor != null || Menu.Context.Touch() is not var (controls, _))
        {
            return;
        }

        var buttons = controls.Actions.Touch.Select(s => (s.Label, s.Column, s.Row));
        _editor = new TouchEditor(Menu.Settings.TouchLayout, buttons);
    }

    /// <summary>Every input is the editor's.</summary>
    public override bool Intercept(in MenuInput input)
    {
        if (_editor is not { } editor)
        {
            Menu.Pop();
            return true;
        }

        var outcome = Keys(editor, input);
        _pointer = input.Pointer ?? _pointer;
        if (input.Click)
        {
            outcome = editor.Press(_pointer);
        }
        else if (input.Held)
        {
            editor.Drag(_pointer);
        }
        else
        {
            editor.Release();
        }

        Finish(outcome);
        return true;
    }

    private static TouchEditor.Outcome Keys(TouchEditor editor, in MenuInput input)
    {
        if (input.Up || input.Down)
        {
            editor.Select(input.Down ? 1 : -1);
        }

        if (input.Left || input.Right)
        {
            editor.Grow(input.Right ? 1 : -1);
        }

        return input.Back ? TouchEditor.Outcome.Cancelled
            : input.Accept ? TouchEditor.Outcome.Saved
            : TouchEditor.Outcome.Editing;
    }

    private void Finish(TouchEditor.Outcome outcome)
    {
        if (outcome == TouchEditor.Outcome.Editing || _editor is not { } editor)
        {
            return;
        }

        if (outcome == TouchEditor.Outcome.Saved && editor.Changed)
        {
            Menu.Settings.TouchLayout = editor.Layout.Save();
            Menu.Changed();
            Menu.ShowToast(SavedNote);
        }

        Menu.Pop();
    }

    public override void Draw(MenuPainter p)
    {
        if (_editor is not { } editor || Menu.Context.Touch() is not var (controls, screen))
        {
            return;
        }

        // The editor in canvas units: the window's pixels scaled to the canvas's height.
        var canvas = p.Frame.Window;
        var unit = canvas.Height / MathF.Max(screen.Y, 1f);
        var room = TouchLayout.Room(controls, screen, Menu.Settings.HudStyle);
        var pause = room.Pause;
        var canvasPause = pause.IsEmpty ? pause : new RectangleF(pause.X * unit, pause.Y * unit, pause.Width * unit, pause.Height * unit);
        editor.Measure(new TouchRoom(new Vector2(canvas.Width, canvas.Height), room.Side * unit, room.Floor * unit, canvasPause));

        var r = p.Renderer;
        r.FillRect(canvas, Dim);
        var font = p.Font(ButtonFont);
        var line = MathF.Max(1f, MathF.Round(2f * controls.Scale)) * unit;
        if (!canvasPause.IsEmpty)
        {
            TouchView.Button(r, canvasPause, PauseLabel, TouchView.Shade.Idle, font, line, unit);
        }

        foreach (var id in editor.Ids.Reverse())
        {
            var area = editor.AreaOf(id);
            var shade = id == editor.Selected ? TouchView.Shade.Selected : TouchView.Shade.Idle;
            if (id == TouchLayout.StickId)
            {
                var centre = new Vector2(area.X + area.Width / 2f, area.Y + area.Height / 2f);
                TouchView.Stick(r, centre, centre, area.Width / 2f, line, shade);
                Word(r, font, editor.Layout.Stick != null ? FixedStick : TouchLayout.StickId, centre + new Vector2(0f, area.Height / 2f + 2f * line), line);
                continue;
            }

            TouchView.Button(r, area, id, shade, font, line, unit);
        }

        foreach (var (tool, area) in editor.Tools)
        {
            // SAVE stands out (the pale ink of a selected control); the rest look pressed.
            var look = tool == TouchEditor.Tool.Save ? TouchView.Shade.Selected : TouchView.Shade.Held;
            TouchView.Button(r, area, tool.ToString().ToUpperInvariant(), look, font, line, unit);
        }

        var tools = editor.Tools;
        var below = tools[0].Area.Top < canvas.Height / 2f ? tools[0].Area.Bottom + 2f * line : tools[0].Area.Top - 2f * line - (font?.LineHeight ?? 0) * line;
        Word(r, font, Help, new Vector2(canvas.Width / 2f, below), line);
    }

    private static void Word(Renderer r, BitmapFont? font, string text, Vector2 at, float pixel)
    {
        if (font != null)
        {
            TextBatcher.Draw(r, font, text, at, pixel, MenuPainter.TextGreen, TextAlign.Centre);
        }
    }
}
