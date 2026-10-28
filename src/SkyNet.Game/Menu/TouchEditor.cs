using System.Drawing;
using System.Numerics;
using SkyNet.Game.Hud;

namespace SkyNet.Game.Menu;

/// <summary>Editing where the on-screen controls are (CONTROLS → TOUCH LAYOUT): a control is
/// dragged by any part of it (past <see cref="DragStart"/>, so a tap only selects), SMALLER / BIGGER
/// resize the selected button (the stick keeps the engine's size), RESET brings the selected
/// control's default back (again, or with none selected: every default), SAVE keeps the edits,
/// CANCEL drops them. A tap off the controls keeps the selection. The tools' row stands at the top,
/// at the bottom while the selected control is in the top half. Controls stay in the
/// <see cref="TouchRoom"/> (above the HUD panel, off the pause button).
/// Any unit (the menu's canvas): <see cref="Measure"/> gives the room in it.
/// <code>
///    [RESET]     [SMALLER] [BIGGER] [CANCEL] [SAVE]
///   ┌ MOVE ┐                                  [||]
///   │      │                     [WPN-] [WPN+]
///   └──────┘                     [ALT ] [USE ]
///                                [JUMP] [FIRE]
/// </code></summary>
internal sealed class TouchEditor
{
    public enum Tool { Reset, Smaller, Bigger, Cancel, Save }

    public enum Outcome { Editing, Saved, Cancelled }

    private enum Edge { Top, Bottom }

    /// <summary>A finger on a control: down (a tap so far), or moving it.</summary>
    private enum Grip { None, Down, Moving }

    /// <summary>A finger moves a control only past this many button sides (a tap's wobble selects).</summary>
    private const float DragStart = 0.3f;

    /// <summary>SMALLER / BIGGER step, in buttons.</summary>
    private const float SizeStep = 0.1f;

    /// <summary>A tool's height in buttons, its width in heights, the gap in heights.</summary>
    private const float ToolHeight = 0.55f;
    private const float ToolWidth = 2.4f;
    private const float ToolGap = 0.25f;

    /// <summary>RESET stands apart from the rest by this many gaps.</summary>
    private const float ResetGaps = 3f;

    /// <summary>The row takes at most this part of the screen's width.</summary>
    private const float RowPart = 0.95f;

    private static readonly Tool[] ToolOrder = [Tool.Reset, Tool.Smaller, Tool.Bigger, Tool.Cancel, Tool.Save];

    private readonly IReadOnlyDictionary<string, string> _saved;
    private readonly (string Label, int Column, int Row)[] _buttons;
    private readonly List<(Tool Tool, RectangleF Area)> _tools = [];
    private TouchRoom _room;
    private Edge _edge;
    private Grip _grip;
    private Vector2 _grab;
    private Vector2 _down;

    public TouchEditor(IReadOnlyDictionary<string, string> saved, IEnumerable<(string Label, int Column, int Row)> buttons)
    {
        _saved = saved;
        _buttons = [.. buttons];
        Layout = new TouchLayout(saved);
    }

    /// <summary>The edited places.</summary>
    public TouchLayout Layout { get; }

    /// <summary>The controls, hit in this order (the stick under the buttons).</summary>
    public IEnumerable<string> Ids => _buttons.Select(b => b.Label).Append(TouchLayout.StickId);

    public string? Selected { get; private set; }

    public IReadOnlyList<(Tool Tool, RectangleF Area)> Tools => _tools;

    /// <summary>The places differ from the saved ones.</summary>
    public bool Changed
    {
        get
        {
            var now = Layout.Save();
            return now.Count != _saved.Count || now.Any(p => !_saved.TryGetValue(p.Key, out var text) || text != p.Value);
        }
    }

    /// <summary>Where controls may stand (the screen, a button's side, the panel, the pause button).</summary>
    public void Measure(TouchRoom room)
    {
        if (room == _room)
        {
            return;
        }

        _room = room;
        LayTools();
    }

    /// <summary>A control's square (the stick: its full push).</summary>
    public RectangleF AreaOf(string id)
    {
        var spot = SpotOf(id);
        var centre = TouchLayout.Centre(spot, _room.Screen, _room.Side);
        var half = TouchLayout.Reach(id, spot.Size) * _room.Side;
        return new RectangleF(centre.X - half, centre.Y - half, 2f * half, 2f * half);
    }

    /// <summary>A finger lands: a tool, else a control (selected and grabbed); elsewhere the
    /// selection stays.</summary>
    public Outcome Press(Vector2 at)
    {
        foreach (var (tool, area) in _tools)
        {
            if (area.Contains(at.X, at.Y))
            {
                return Use(tool);
            }
        }

        if (Ids.FirstOrDefault(id => AreaOf(id).Contains(at.X, at.Y)) is not { } hit)
        {
            return Outcome.Editing;
        }

        Selected = hit;
        (_grip, _down, _grab) = (Grip.Down, at, TouchLayout.Centre(SpotOf(hit), _room.Screen, _room.Side) - at);
        return Outcome.Editing;
    }

    /// <summary>The finger moves: past the drag start, the grabbed control follows; the row gives way.</summary>
    public void Drag(Vector2 at)
    {
        if (_grip == Grip.None || Selected is not { } part)
        {
            return;
        }

        if (_grip == Grip.Down && Vector2.Distance(at, _down) < DragStart * _room.Side)
        {
            return;
        }

        _grip = Grip.Moving;
        Place(part, at + _grab, SpotOf(part).Size);
        var edge = TouchLayout.Centre(SpotOf(part), _room.Screen, _room.Side).Y < _room.Screen.Y / 2f ? Edge.Bottom : Edge.Top;
        if (edge != _edge)
        {
            _edge = edge;
            LayTools();
        }
    }

    public void Release() => _grip = Grip.None;

    /// <summary>The next (+1) or previous (-1) control (keys, a gamepad).</summary>
    public void Select(int step)
    {
        var ids = Ids.ToList();
        var index = Selected is { } part ? ids.IndexOf(part) + step : step > 0 ? 0 : ids.Count - 1;
        Selected = ids[(index % ids.Count + ids.Count) % ids.Count];
    }

    public Outcome Use(Tool tool)
    {
        switch (tool)
        {
            case Tool.Reset:
                Reset();
                break;
            case Tool.Smaller:
                Grow(-1);
                break;
            case Tool.Bigger:
                Grow(1);
                break;
            case Tool.Cancel:
                return Outcome.Cancelled;
            case Tool.Save:
                return Outcome.Saved;
        }

        return Outcome.Editing;
    }

    /// <summary>The selected control's default; when it has it already (or none is selected), every default.</summary>
    private void Reset()
    {
        if (Selected is { } part && Layout.Reset(part))
        {
            return;
        }

        Layout.Reset();
    }

    /// <summary>The selected button grows (+1) or shrinks (-1) about its centre; the stick keeps its size.</summary>
    public void Grow(int step)
    {
        if (Selected is not { } part || part == TouchLayout.StickId)
        {
            return;
        }

        var spot = SpotOf(part);
        Place(part, TouchLayout.Centre(spot, _room.Screen, _room.Side), spot.Size + step * SizeStep);
    }

    private void Place(string id, Vector2 centre, float size)
    {
        if (_room.Side <= 0f)
        {
            return;
        }

        Layout.Set(id, TouchLayout.SpotAt(id, centre, size, _room));
    }

    /// <summary>A control's spot; a placed one kept in the room (a hand-edited file may put it off).</summary>
    private TouchSpot SpotOf(string id)
    {
        var fallback = id == TouchLayout.StickId ? TouchLayout.StickDefault : Grid(id);
        var spot = Layout.SpotOf(id, fallback);
        if (spot == fallback || _room.Side <= 0f)
        {
            return spot;
        }

        return TouchLayout.SpotAt(id, TouchLayout.Centre(spot, _room.Screen, _room.Side), spot.Size, _room);
    }

    private TouchSpot Grid(string id)
    {
        var (_, column, row) = _buttons.First(b => b.Label == id);
        return TouchLayout.Grid(column, row);
    }

    /// <summary>The tools centred in a row at the top or bottom, smaller when they don't fit.</summary>
    private void LayTools()
    {
        _tools.Clear();
        var height = ToolHeight * _room.Side;
        var width = ToolWidth * height;
        var gap = ToolGap * height;
        var total = ToolOrder.Length * width + (ToolOrder.Length - 2 + ResetGaps) * gap;
        var fit = total > 0f ? MathF.Min(1f, RowPart * _room.Screen.X / total) : 1f;
        (height, width, gap, total) = (height * fit, width * fit, gap * fit, total * fit);

        var x = (_room.Screen.X - total) / 2f;
        var y = _edge == Edge.Top ? gap : _room.Screen.Y - gap - height;
        foreach (var tool in ToolOrder)
        {
            _tools.Add((tool, new RectangleF(x, y, width, height)));
            x += width + (tool == Tool.Reset ? ResetGaps : 1f) * gap;
        }
    }
}
