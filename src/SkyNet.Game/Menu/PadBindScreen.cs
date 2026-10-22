using System.Drawing;
using System.Numerics;
using RetroEng.Platform;
using static SkyNet.Game.Menu.MenuLayout;

namespace SkyNet.Game.Menu;

/// <summary>
/// GAMEPAD BUTTONS (CONTROLS → GAMEPAD → BUTTONS), the port's own screen in the DISPLAY screen's
/// make: every game action with its gamepad buttons, in a scrolling list a gamepad alone can work.
/// <code>
///   MOVE / LOOK      which stick walks (Accept swaps them)
///   FIRE ... NOCLIP  Accept, then a button or trigger: its only button (taken off any other action)
///   MENU             START (fixed: pauses and steps back in the menus)
/// </code>
/// Left (or Delete) clears a row; while a row waits, Start or Esc cancels (B binds: it is a button
/// like any other). DEFAULT puts the gamepad's defaults back. Every change applies and saves at once.
/// </summary>
public sealed class PadBindScreen : PanelScreen
{
    /// <summary>GAMEPAD's row to here.</summary>
    public const string Caption = "BUTTONS";

    public static readonly Size PanelSize = new(280, 194);
    public const int VisibleRows = 11;
    public const int RowTop = 21;
    public const int RowPitch = 12;
    public static readonly Rectangle ValueBox = new(146, 0, 126, 10);
    public static readonly Rectangle HintRect = new(6, RowTop + VisibleRows * RowPitch + 4, PanelSize.Width - 12, 12);
    public static readonly Rectangle DefaultRect = new(6, 175, 90, 18);
    public static readonly Rectangle ExitRect = new(PanelSize.Width - OptionsExit.Width, 175, OptionsExit.Width, OptionsExit.Height);

    public const string Unbound = "---";
    public const string Waiting = "PRESS A BUTTON";
    public const string Hint = "A: REBIND   LEFT: CLEAR";
    public const string WaitHint = "START: CANCEL";
    /// <summary>The gamepad's fixed menu button (<see cref="GamepadMap"/>): not rebound.</summary>
    private const string StartButton = "Start";
    /// <summary>The keyboard's Delete by its binding name: clears a row as Left does.</summary>
    private const string DeleteKey = "Delete";

    /// <summary>The actions first in the list, by name; the rest follow in the game's order.</summary>
    private static readonly string[] Leading =
    [
        "Fire", "Throw", "Jump", "Crouch", "Use", "Run", "Next weapon", "Previous weapon",
        "Thrown item 1", "Thrown item 2", "Thrown item 3", "Thrown item 4", "Thrown item 5",
    ];

    /// <summary>What a row is: the stick choice, an action to bind, or the fixed menu button.</summary>
    public enum RowKind { Move, Look, Action, Fixed }

    public readonly record struct Row(RowKind Kind, string Label, int Action);

    private readonly ListScroll _scroll = new(VisibleRows, RowPitch);
    private int _top;
    private int _selected;
    private int _capturing = -1;
    private bool _armed;

    public PadBindScreen() : base(OptionsImage, PanelSize, DetailScale)
    {
    }

    public override string Name => "padbind";

    public IReadOnlyList<Row> Rows { get; private set; } = [];

    /// <summary>The first row shown.</summary>
    public int Top => _top;

    /// <summary>The row Accept, Left act on.</summary>
    public int Selected => _selected;

    /// <summary>The row waiting for a button, -1 when none.</summary>
    public int Capturing => _capturing;

    private IBindings? Bindings => Menu.Context.Bindings;

    public static Rectangle RowRect(int i) => new(4, RowTop + i * RowPitch - 1, PanelSize.Width - 8, RowPitch - 1);

    public override void Opened()
    {
        if (Items.Count > 0)
        {
            return;
        }

        Rows = MakeRows(Bindings);
        for (var i = 0; i < Math.Min(VisibleRows, Rows.Count); i++)
        {
            var slot = i;
            Add(new MenuItem($"row{i}", At(RowRect(i))) { Activate = () => Pick(_top + slot) });
        }

        Add(new MenuItem("default", At(DefaultRect)) { Activate = ResetDefaults });
        Add(new MenuItem("exit", At(ExitRect))
        {
            Lit = new LitArt(OptionsLit, 11, new Point(Origin.X + ExitRect.X - OptionsExit.X + OptionsLitAt.X, Origin.Y + ExitRect.Y - OptionsExit.Y + OptionsLitAt.Y)),
            Activate = Back,
        });
        Highlight = 0;
    }

    /// <summary>The list: MOVE and LOOK, the leading actions, the others, the fixed ones last.</summary>
    public static IReadOnlyList<Row> MakeRows(IBindings? b)
    {
        List<Row> rows = [new(RowKind.Move, "MOVE", -1), new(RowKind.Look, "LOOK", -1)];
        if (b == null)
        {
            return rows;
        }

        var order = Leading.Select(b.Find).Where(a => a >= 0).ToList();
        order.AddRange(Enumerable.Range(0, b.Count).Where(a => !order.Contains(a)));
        var isFixed = (int a) => b.PadBindings(a).Contains(StartButton, StringComparer.Ordinal);
        rows.AddRange(order.Where(a => !isFixed(a)).Select(a => new Row(RowKind.Action, b.Name(a).ToUpperInvariant(), a)));
        rows.AddRange(order.Where(isFixed).Select(a => new Row(RowKind.Fixed, b.Name(a).ToUpperInvariant(), a)));
        return rows;
    }

    /// <summary>The text of a row's value box.</summary>
    public string Value(int row)
    {
        if (row == _capturing)
        {
            return Waiting;
        }

        var r = Rows[row];
        var rightMoves = Menu.Settings.PadSticks == PadSticks.RightMoves;
        return r.Kind switch
        {
            RowKind.Move => rightMoves ? "RIGHT STICK" : "LEFT STICK",
            RowKind.Look => rightMoves ? "LEFT STICK" : "RIGHT STICK",
            _ when Bindings is { } b && b.PadBindings(r.Action) is { Count: > 0 } pads => string.Join(", ", pads).ToUpperInvariant(),
            _ => Unbound,
        };
    }

    /// <summary>Accept on a row: MOVE / LOOK swap the sticks, an action waits for a button.</summary>
    private void Pick(int row)
    {
        Select(row);
        switch (Rows[row].Kind)
        {
            case RowKind.Move or RowKind.Look:
                var s = Menu.Settings;
                s.PadSticks = s.PadSticks == PadSticks.LeftMoves ? PadSticks.RightMoves : PadSticks.LeftMoves;
                Menu.Changed();
                break;
            case RowKind.Action:
                (_capturing, _armed) = (row, false);
                break;
            default:
                Menu.ShowToast("START OPENS THE MENU");
                break;
        }
    }

    /// <summary>Left / Delete on an action row: no button for it.</summary>
    private void Clear(int row)
    {
        if (Rows[row] is not { Kind: RowKind.Action, Action: var action } || Bindings is not { } b)
        {
            return;
        }

        b.UnbindPad(action);
        Save(b);
    }

    private void ResetDefaults()
    {
        Bindings?.ResetPadBindings();
        Menu.Settings.PadBindings = Bindings is { } b ? PadRebind.Snapshot(b) : new(StringComparer.Ordinal);
        Menu.Settings.PadSticks = PadSticks.LeftMoves;
        Menu.Changed();
    }

    /// <summary>Every action's buttons into the settings, saved and applied.</summary>
    private void Save(IBindings b)
    {
        Menu.Settings.PadBindings = PadRebind.Snapshot(b);
        Menu.Changed();
    }

    /// <summary>The row of item <paramref name="item"/>, -1 for the buttons.</summary>
    private static int Slot(MenuItem? item) =>
        item != null && item.Id.StartsWith("row", StringComparison.Ordinal) ? int.Parse(item.Id.AsSpan(3), provider: null) : -1;

    /// <summary>Selects row <paramref name="row"/>, scrolling so a row stays in view on either side of it.</summary>
    public void Select(int row)
    {
        _selected = Math.Clamp(row, 0, Math.Max(0, Rows.Count - 1));
        var maxTop = Math.Max(0, Rows.Count - VisibleRows);
        if (_selected <= _top)
        {
            _top = Math.Max(0, _selected - 1);
        }
        else if (_selected >= _top + VisibleRows - 1)
        {
            _top = Math.Min(maxTop, _selected - VisibleRows + 2);
        }

        _top = Math.Clamp(_top, 0, maxTop);
        Highlight = Items.FindIndex(i => i.Id == $"row{_selected - _top}");
    }

    /// <summary>A row waiting takes the next button; up / down walk the whole list (scrolling it);
    /// Left or Delete clears. Off the list's ends the highlight moves on as on every screen.</summary>
    public override bool Intercept(in MenuInput input)
    {
        if (_capturing >= 0)
        {
            Capture(input);
            return true;
        }

        var slot = Slot(Highlighted);
        if (slot >= 0)
        {
            _selected = Math.Min(_top + slot, Rows.Count - 1);
        }

        // Wheel and dragged rows (no scrollbar here: the marks only show more rows).
        var list = At(new Rectangle(RowRect(0).Location, new Size(RowRect(0).Width, VisibleRows * RowPitch)));
        if (_scroll.Update(input, PointerAt, ref _top, Rows.Count, list, Rectangle.Empty))
        {
            _selected = Math.Clamp(_selected, _top, Math.Min(Rows.Count, _top + VisibleRows) - 1);
            if (slot >= 0)
            {
                Highlight = Items.FindIndex(i => i.Id == $"row{_selected - _top}");
            }

            return true;
        }

        if (input.Pointer != null || slot < 0)
        {
            return false;
        }

        if (input.Left || input.Erase || input.Control == DeleteKey)
        {
            Clear(_selected);
            return true;
        }

        if (!(input.Up || input.Down))
        {
            return false;
        }

        var next = _selected + (input.Down ? 1 : -1);
        if (next < 0 || next >= Rows.Count)
        {
            return false;
        }

        Select(next);
        return true;
    }

    /// <summary>The first frame after the press that opened the wait is skipped (it carries that
    /// press); then a gamepad button binds, Start or Esc cancels. B binds rather than cancels.</summary>
    private void Capture(in MenuInput input)
    {
        if (!_armed)
        {
            _armed = true;
            return;
        }

        var pad = input.PadControl;
        if (pad.Length > 0 && pad != StartButton && Bindings is { } b)
        {
            if (PadRebind.Apply(b, Rows[_capturing].Action, pad))
            {
                Save(b);
            }

            _capturing = -1;
            return;
        }

        if (input.Back || pad == StartButton)
        {
            _capturing = -1;
        }
    }

    public override void Back()
    {
        _capturing = -1;
        Menu.Pop();
    }

    public override void Draw(MenuPainter p)
    {
        DosDialog.Panel(p, new Rectangle(Origin, Size));
        p.TextIn(DosDialog.TitleFont, "GAMEPAD BUTTONS", At(new Rectangle(0, 4, Size.Width, 14)), MenuPainter.TextPale);
        for (var i = 0; i < VisibleRows && _top + i < Rows.Count; i++)
        {
            var row = _top + i;
            var box = At(new Rectangle(ValueBox.X, RowTop + i * RowPitch, ValueBox.Width, ValueBox.Height));
            var fixedRow = Rows[row].Kind == RowKind.Fixed;
            p.Text(DosDialog.Font, Rows[row].Label, Origin.X + 10, box.Y + 2, fixedRow ? DosDialog.Light : MenuPainter.TextPale);
            DosDialog.Box(p, box);
            var colour = row == _capturing ? DosDialog.Pending : fixedRow ? DosDialog.Light : MenuPainter.TextGreen;
            p.TextIn(DosDialog.Font, Value(row), box, colour);
        }

        DrawScrollMarks(p);
        p.TextIn(DosDialog.Font, _capturing >= 0 ? WaitHint : Hint, At(HintRect), _capturing >= 0 ? DosDialog.Pending : MenuPainter.TextPale);
        DosDialog.Button(p, At(DefaultRect), "DEFAULT");
        if (!p.ImagePart(OptionsImage, MenuPalette, OptionsExit, At(ExitRect)))
        {
            DosDialog.Button(p, At(ExitRect), "EXIT");
        }

        if (Highlighted is { } h && Menu.Current == this)
        {
            p.Highlight(h);
        }
    }

    /// <summary>Small marks at the list's right edge when rows lie above or below it.</summary>
    private void DrawScrollMarks(MenuPainter p)
    {
        var mark = new Vector4(0.35f, 0.85f, 0.45f, 0.85f);
        if (_top > 0)
        {
            p.Fill(At(new Rectangle(PanelSize.Width - 5, RowTop - 1, 2, 4)), mark);
        }

        if (_top + VisibleRows < Rows.Count)
        {
            p.Fill(At(new Rectangle(PanelSize.Width - 5, RowTop + VisibleRows * RowPitch - 5, 2, 4)), mark);
        }
    }
}
