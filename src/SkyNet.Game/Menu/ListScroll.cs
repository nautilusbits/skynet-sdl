using System.Drawing;
using System.Numerics;

namespace SkyNet.Game.Menu;

/// <summary>
/// The pointer's ways to scroll a menu list: the wheel, the scrollbar's thumb dragged, a click on
/// its track paging, and the rows dragged (a finger on a touch screen; the mouse too).
/// <code>
///   ┌ body ─────────────┬┐ track
///   │ row  (drag: rows) ││ ← click above the thumb: a page up
///   │ row               ██ ← thumb (drag)
///   │ row               ││ ← click below: a page down
///   └───────────────────┴┘
/// </code>
/// The screen keeps its first row shown; <see cref="Update"/> moves it.
/// </summary>
public sealed class ListScroll(int visible, int rowPitch)
{
    /// <summary>Rows per wheel notch.</summary>
    public const int WheelRows = 2;

    private enum Grab
    {
        None,
        Thumb,
        Rows,
    }

    private Grab _grab;
    /// <summary>Thumb: the pointer's offset into it; rows: where the drag began.</summary>
    private float _grabY;
    private int _grabTop;
    /// <summary>Wheel notches not yet a row (a touchpad's fractions).</summary>
    private float _wheel;

    /// <summary>The thumb's rectangle in <paramref name="track"/> for <paramref name="count"/> rows from <paramref name="top"/>.</summary>
    public Rectangle Thumb(Rectangle track, int top, int count)
    {
        var h = Math.Max(4, track.Height * visible / Math.Max(1, count));
        var y = track.Y + (track.Height - h) * top / Math.Max(1, count - visible);
        return new Rectangle(track.X, y, track.Width, h);
    }

    /// <summary>The frame's pointer (<paramref name="at"/> in DOS pixels, null when it did not move)
    /// on a list of <paramref name="count"/> rows: true when it scrolled or holds a drag, so the
    /// screen takes no other input from it. A click on the rows still goes on to them.</summary>
    public bool Update(in MenuInput input, Vector2? at, ref int top, int count, Rectangle body, Rectangle track)
    {
        var maxTop = Math.Max(0, count - visible);
        if (!input.Held)
        {
            _grab = Grab.None;
        }

        if (maxTop == 0)
        {
            return false;
        }

        // The wheel: up scrolls up.
        if (input.Wheel != 0f && _grab == Grab.None)
        {
            _wheel += input.Wheel;
            var notches = (int)_wheel;
            _wheel -= notches;
            top = Math.Clamp(top - notches * WheelRows, 0, maxTop);
            return true;
        }

        if (at is not { } p)
        {
            return _grab != Grab.None;
        }

        switch (_grab)
        {
            case Grab.Thumb:
                var thumb = Thumb(track, top, count);
                var room = Math.Max(1, track.Height - thumb.Height);
                top = Math.Clamp((int)MathF.Round((p.Y - _grabY - track.Y) * maxTop / room), 0, maxTop);
                return true;
            case Grab.Rows:
                // Rows follow the finger: dragged up, later rows come into view.
                top = Math.Clamp(_grabTop + (int)MathF.Round((_grabY - p.Y) / rowPitch), 0, maxTop);
                return true;
        }

        if (!input.Click)
        {
            return false;
        }

        if (Inside(track, p))
        {
            var thumb = Thumb(track, top, count);
            if (p.Y >= thumb.Top && p.Y < thumb.Bottom)
            {
                (_grab, _grabY) = (Grab.Thumb, p.Y - thumb.Y);
                return true;
            }

            top = Math.Clamp(top + (p.Y < thumb.Top ? -1 : 1) * (visible - 1), 0, maxTop);
            return true;
        }

        if (Inside(body, p))
        {
            (_grab, _grabY, _grabTop) = (Grab.Rows, p.Y, top);
        }

        return false;
    }

    private static bool Inside(Rectangle r, Vector2 p) => p.X >= r.Left && p.X < r.Right && p.Y >= r.Top && p.Y < r.Bottom;
}
