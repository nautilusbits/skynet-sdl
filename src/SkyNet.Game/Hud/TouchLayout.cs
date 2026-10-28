using System.Drawing;
using System.Globalization;
using System.Numerics;
using RetroEng.Platform;

namespace SkyNet.Game.Hud;

/// <summary>A placed on-screen control: the engine's <see cref="TouchPlace"/> (a screen point moved
/// by button sides) and its size in buttons (<see cref="TouchSlot.Size"/>).</summary>
internal readonly record struct TouchSpot(TouchPlace Place, float Size);

/// <summary>Where controls may stand (one unit: pixels or the menu's canvas): the screen, a button's
/// side, the HUD panel's top (<paramref name="Floor"/>; the screen's height with no panel) and the
/// pause button's square (empty: none).</summary>
public readonly record struct TouchRoom(Vector2 Screen, float Side, float Floor, RectangleF Pause);

/// <summary>Where the on-screen controls are: the engine's grid by default, or the player's places
/// (settings <c>touchLayout</c>, control label → "anchor x,anchor y,offset x,offset y,size").
/// A placed control keeps its distance to the nearer screen corner in button sides, so it fits
/// another screen. The stick floats (where the finger lands) until placed.
/// <code>
///   touchLayout.FIRE = "1,1,-1.2,-2.5,1.3"
///   ┌──────────────────────────────┐
///   │                   -1.2 sides ◄┤ from the right
///   │                       (FIRE) │
///   │                  -2.5 sides ▲│ from the bottom
///   └──────────────────────────────┘
/// </code></summary>
public sealed class TouchLayout
{
    /// <summary>The stick's id (it has no action).</summary>
    internal const string StickId = "MOVE";

    /// <summary>A placed button's size in buttons.</summary>
    internal const float MinSize = 0.6f;
    internal const float MaxSize = 2f;

    /// <summary>The engine's grid (TouchControls): the gap in buttons, the radar's room under the
    /// rows (part of the height), the stick's radius in buttons.</summary>
    private const float Gap = 0.18f;
    private const float RadarRoom = 0.2f;
    internal const float StickRadius = 0.85f;

    /// <summary>Offsets past this many buttons are a broken line.</summary>
    private const float MaxOffset = 100f;
    private const int Fields = 5;
    private const char Separator = ',';

    private readonly Dictionary<string, TouchSpot> _spots = new(StringComparer.Ordinal);

    public TouchLayout(IReadOnlyDictionary<string, string> saved)
    {
        foreach (var (id, text) in saved)
        {
            if (TryParse(text, out var spot))
            {
                _spots[id] = spot;
            }
        }
    }

    /// <summary>The engine's default grid place: columns from the right edge, rows up from the radar's room.</summary>
    internal static TouchSpot Grid(int column, int row) =>
        new(new TouchPlace(new Vector2(1f, 1f - RadarRoom), new Vector2(-(Gap + 0.5f + column * (1f + Gap)), -(0.5f + row * (1f + Gap)))), 1f);

    /// <summary>Where the engine draws a floating stick while no finger walks.</summary>
    internal static TouchSpot StickDefault { get; } =
        new(new TouchPlace(new Vector2(0f, 1f - RadarRoom), new Vector2(1.6f * StickRadius, -1.3f * StickRadius)), 1f);

    /// <summary>The placed stick (the engine's FixedStick), null: floating.</summary>
    public TouchPlace? Stick => _spots.TryGetValue(StickId, out var spot) ? spot.Place : null;

    internal TouchSpot SpotOf(string id, TouchSpot fallback) => _spots.GetValueOrDefault(id, fallback);

    internal void Set(string id, TouchSpot spot) => _spots[id] = spot;

    /// <summary>Every control back to its default.</summary>
    internal void Reset() => _spots.Clear();

    /// <summary>One control back to its default; false when it was there already.</summary>
    internal bool Reset(string id) => _spots.Remove(id);

    /// <summary>The room on a screen (pixels): above the HUD panel of <paramref name="style"/>, off
    /// the engine's pause button (when its actions are set).</summary>
    public static TouchRoom Room(TouchControls touch, Vector2 screen, HudStyle style)
    {
        var (width, height) = ((int)screen.X, (int)screen.Y);
        var pause = touch.Actions is { Pause: >= 0 } actions
            ? touch.Layout(screen).FirstOrDefault(b => b.Action == actions.Pause).Area
            : RectangleF.Empty;
        return new TouchRoom(screen, touch.ButtonSide(screen), height - DosHud.PanelPixels(width, height, style), pause);
    }

    /// <summary>The placed controls kept in a room (a hand-edited file may put them off it).</summary>
    public void Fit(TouchRoom room)
    {
        if (room.Side <= 0f)
        {
            return;
        }

        foreach (var (id, spot) in _spots.ToList())
        {
            _spots[id] = SpotAt(id, Centre(spot, room.Screen, room.Side), spot.Size, room);
        }
    }

    /// <summary>The engine's button, at its place (the grid's when not moved).</summary>
    internal TouchSlot Slot(int action, string label, int column, int row)
    {
        var spot = SpotOf(label, Grid(column, row));
        return new TouchSlot(action, label, column, row) { Place = spot.Place, Size = spot.Size };
    }

    /// <summary>The placed controls as the settings keep them.</summary>
    internal Dictionary<string, string> Save() =>
        _spots.ToDictionary(p => p.Key, p => Text(p.Value), StringComparer.Ordinal);

    /// <summary>A spot's centre on a screen (pixels, or any unit the side is in).</summary>
    internal static Vector2 Centre(TouchSpot spot, Vector2 screen, float side) =>
        spot.Place.Anchor * screen + spot.Place.Offset * side;

    /// <summary>A control's half extent in button sides (the stick: its full push).</summary>
    internal static float Reach(string id, float size) => id == StickId ? StickRadius : size / 2f;

    /// <summary>The spot of a centre: its size kept in range, the control kept in the room (on the
    /// screen, above the HUD panel, off the pause button), its place from the nearer corner.</summary>
    internal static TouchSpot SpotAt(string id, Vector2 centre, float size, TouchRoom room)
    {
        var (screen, side) = (room.Screen, room.Side);
        var kept = Math.Clamp(size, MinSize, MaxSize);
        var half = MathF.Min(Reach(id, kept) * side, MathF.Min(screen.X, screen.Y) / 2f);
        var floor = MathF.Max(MathF.Min(room.Floor, screen.Y) - half, half);
        var at = OffPause(new Vector2(Math.Clamp(centre.X, half, screen.X - half), Math.Clamp(centre.Y, half, floor)), half, room.Pause, screen.X, floor);

        var anchor = new Vector2(at.X < screen.X / 2f ? 0f : 1f, at.Y < screen.Y / 2f ? 0f : 1f);
        var offset = (at - anchor * screen) / side;
        return new TouchSpot(new TouchPlace(anchor, offset), kept);
    }

    /// <summary>A centre pushed out of the pause button by the shortest way that stays in the room
    /// (x within [half, right - half], y within [half, floor]).</summary>
    private static Vector2 OffPause(Vector2 at, float half, RectangleF pause, float width, float floor)
    {
        var keep = RectangleF.Inflate(pause, half, half);
        if (pause.IsEmpty || !keep.Contains(at.X, at.Y) || at.X == keep.Left || at.Y == keep.Top)
        {
            return at;
        }

        Vector2[] ways = [new(keep.Left, at.Y), new(keep.Right, at.Y), new(at.X, keep.Top), new(at.X, keep.Bottom)];
        var fits = ways.Where(w => w.X >= half && w.X <= width - half && w.Y >= half && w.Y <= floor);
        return fits.DefaultIfEmpty(at).MinBy(w => Vector2.DistanceSquared(w, at));
    }

    private static string Text(TouchSpot s) => string.Join(Separator,
        new[] { s.Place.Anchor.X, s.Place.Anchor.Y, s.Place.Offset.X, s.Place.Offset.Y, s.Size }.Select(v => v.ToString(CultureInfo.InvariantCulture)));

    private static bool TryParse(string text, out TouchSpot spot)
    {
        spot = default;
        var parts = text.Split(Separator);
        if (parts.Length != Fields)
        {
            return false;
        }

        var v = new float[Fields];
        for (var i = 0; i < Fields; i++)
        {
            if (!float.TryParse(parts[i], NumberStyles.Float, CultureInfo.InvariantCulture, out v[i]) || !float.IsFinite(v[i]))
            {
                return false;
            }
        }

        // Anchors are screen fractions; offsets a few buttons; the size in range.
        if (v[0] is < 0f or > 1f || v[1] is < 0f or > 1f || MathF.Abs(v[2]) > MaxOffset || MathF.Abs(v[3]) > MaxOffset || v[4] is < MinSize or > MaxSize)
        {
            return false;
        }

        spot = new TouchSpot(new TouchPlace(new Vector2(v[0], v[1]), new Vector2(v[2], v[3])), v[4]);
        return true;
    }
}
