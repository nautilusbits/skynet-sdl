using System.Drawing;
using System.Numerics;
using RetroEng.Content;
using RetroEng.Render;
using SkyNet.Game.Mods;
using static SkyNet.Game.Menu.MenuLayout;

namespace SkyNet.Game.Menu;

/// <summary>
/// MODS (OPTIONS → MODS), the port's own screen in the DISPLAY screen's make (OPTIONS.IMG's stone,
/// wells and EXIT, the menu fonts). It lists the mods folder (<see cref="MenuContext.ModList"/>):
/// the mods turned on, the winning one first, then the others by name, then the system mods
/// (locked: always on, below every user mod). A row shows ON / OFF, the name and whether the mod is
/// a ZIP, a folder or both; the box under the list the highlighted mod's <c>mod.json</c> (name,
/// version, author, description) and what it carries. Accept (or a click on the ON / OFF box)
/// turns a mod on or off, UP / DOWN move the selected mod in the load order; every change is saved
/// at once (<see cref="MenuSettings.Mods"/>). The game takes the list in: textures and models at
/// the next map, tables when a game starts (NEW GAME, LOAD, RESTART MISSION), all of it at once on
/// the title. Under <c>--mod</c> the run keeps its own list and the screen says so.
/// GENERATE HI-RES TEXTURES (when the game offers it, <see cref="MenuContext.GenerateHires"/>) asks
/// first (<see cref="GenerateBox"/>), then makes the hi-res texture mod in the background with its
/// progress on the note line; Esc (or EXIT) cancels it; when it is done the mod is turned on and
/// the list read again.
/// </summary>
public sealed class ModsScreen : PanelScreen
{
    /// <summary>OPTIONS' button to here.</summary>
    public const string Caption = "MODS";

    public static readonly Size PanelSize = new(280, 194);
    public const int VisibleRows = 7;
    public const int RowTop = 21;
    public const int RowPitch = 12;
    public static readonly Rectangle ListRect = new(6, RowTop - 2, 206, VisibleRows * RowPitch + 3);
    /// <summary>The scrollbar's grip: the thin bar at its right edge, wider to hit.</summary>
    public static readonly Rectangle TrackRect = new(ListRect.Right - 8, ListRect.Y + 2, 8, ListRect.Height - 4);
    public static readonly Rectangle UpRect = new(216, RowTop - 2, 58, 18);
    public static readonly Rectangle DownRect = new(216, RowTop + 20, 58, 18);
    public static readonly Rectangle InfoRect = new(6, 108, 268, 46);
    public static readonly Rectangle GenerateRect = new(6, 157, 206, 16);
    public static readonly Rectangle NoteRect = new(6, 178, 222, 12);
    public static readonly Rectangle ExitRect = new(PanelSize.Width - OptionsExit.Width, 175, OptionsExit.Width, OptionsExit.Height);
    /// <summary>The ON / OFF box of a row, from the row's left.</summary>
    public const int ToggleWidth = 24;

    public const string OverrideNote = "--MOD SETS THIS RUN'S MODS";
    public const string InGameNote = "FROM THE NEXT MAP; TABLES NEXT GAME";
    public const string GenerateCaption = "GENERATE HI-RES TEXTURES";

    private IReadOnlyList<ModEntry> _found = [];
    private readonly ListScroll _scroll = new(VisibleRows, RowPitch);
    private int _top;
    private int _selected;
    private bool _changed;
    private IMenuJob? _job;
    /// <summary>How the last job ended (its status), until another starts.</summary>
    private string? _jobNote;

    public ModsScreen() : base(OptionsImage, PanelSize, DetailScale)
    {
    }

    public override string Name => "mods";

    /// <summary>The rows in screen order (<see cref="ModOrder.Rows"/>).</summary>
    public IReadOnlyList<ModEntry> Rows { get; private set; } = [];

    /// <summary>The first row shown.</summary>
    public int Top => _top;

    /// <summary>The row UP / DOWN act on (the highlighted one, or the last one highlighted).</summary>
    public ModEntry? Selected => _selected >= 0 && _selected < Rows.Count ? Rows[_selected] : null;

    public static Rectangle RowRect(int i) => new(8, RowTop + i * RowPitch, ListRect.Width - 4, RowPitch - 1);

    public bool IsOn(ModEntry m) => m.System || Menu.Settings.Mods.Contains(m.Name, VfsPath.Comparer);

    public override void Opened()
    {
        if (Items.Count > 0)
        {
            return;
        }

        _found = Menu.Context.ModList?.Invoke() ?? [];
        Rows = ModOrder.Rows(Menu.Settings.Mods, _found);
        for (var i = 0; i < Math.Min(VisibleRows, Rows.Count); i++)
        {
            var slot = i;
            var rect = RowRect(i);
            Add(new MenuItem($"row{i}", At(rect))
            {
                Activate = () => Toggle(_top + slot),
                ClickAt = t =>
                {
                    Select(_top + slot, scroll: true);
                    if (t * rect.Width < ToggleWidth + 2)
                    {
                        Toggle(_selected);
                    }
                },
            });
        }

        Add(new MenuItem("up", At(UpRect)) { Activate = () => Move(up: true) });
        Add(new MenuItem("down", At(DownRect)) { Activate = () => Move(up: false) });
        if (Menu.Context.GenerateHires != null)
        {
            Add(new MenuItem("generate", At(GenerateRect)) { Activate = AskGenerate });
        }

        Add(new MenuItem("exit", At(ExitRect))
        {
            Lit = new LitArt(OptionsLit, 11, new Point(Origin.X + ExitRect.X - OptionsExit.X + OptionsLitAt.X, Origin.Y + ExitRect.Y - OptionsExit.Y + OptionsLitAt.Y)),
            Activate = Back,
        });
        Highlight = Rows.Count > 0 ? 0 : Items.Count - 1;
    }

    /// <summary>The background job making the hi-res textures, while it runs.</summary>
    public IMenuJob? Job => _job;

    /// <summary>Esc or EXIT: a running job is cancelled first (the screen stays).</summary>
    public override void Back()
    {
        if (_job != null)
        {
            _job.Cancel();
            return;
        }

        Menu.Pop();
    }

    private void AskGenerate()
    {
        if (_job == null && Menu.Context.GenerateHires is { } start)
        {
            Menu.Push(new GenerateBox(() =>
            {
                _jobNote = null;
                _job = start();
            }));
        }
    }

    /// <summary>The job looked at: done turns its mod on and reads the list again.</summary>
    public override void Tick(float seconds)
    {
        if (_job is not { } job || job.State == MenuJobState.Running)
        {
            return;
        }

        _job = null;
        _jobNote = job.Status;
        if (job.State != MenuJobState.Done)
        {
            Menu.ShowToast(job.State == MenuJobState.Cancelled ? "CANCELLED" : "HI-RES TEXTURES FAILED");
            return;
        }

        _found = Menu.Context.ModList?.Invoke() ?? [];
        var made = _found.FirstOrDefault(m => VfsPath.Comparer.Equals(m.Name, job.ModName));
        if (made != null && !IsOn(made))
        {
            ModOrder.Set(Menu.Settings.Mods, made.Name, true);
        }

        Rows = ModOrder.Rows(Menu.Settings.Mods, _found);
        RebuildRows();
        if (made != null)
        {
            Reorder(made);
        }

        Menu.ShowToast("HI-RES TEXTURES READY");
    }

    /// <summary>The row items again after the list changed length (a mod made).</summary>
    private void RebuildRows()
    {
        var lit = Highlighted?.Id;
        var shown = Items.Count(i => Slot(i) >= 0);
        var want = Math.Min(VisibleRows, Rows.Count);
        for (var i = shown; i < want; i++)
        {
            var slot = i;
            var rect = RowRect(i);
            Items.Insert(i, new MenuItem($"row{i}", At(rect))
            {
                Activate = () => Toggle(_top + slot),
                ClickAt = t =>
                {
                    Select(_top + slot, scroll: true);
                    if (t * rect.Width < ToggleWidth + 2)
                    {
                        Toggle(_selected);
                    }
                },
            });
        }

        if (lit != null)
        {
            Select(lit);
        }
    }

    /// <summary>The row of item <paramref name="item"/>, -1 for the buttons.</summary>
    private static int Slot(MenuItem? item) =>
        item != null && item.Id.StartsWith("row", StringComparison.Ordinal) ? int.Parse(item.Id.AsSpan(3), provider: null) : -1;

    private void Sync()
    {
        if (Slot(Highlighted) is var slot and >= 0)
        {
            _selected = Math.Min(_top + slot, Rows.Count - 1);
        }
    }

    /// <summary>Selects row <paramref name="row"/>; with <paramref name="scroll"/> the list scrolls so
    /// that a row stays visible on either side of it when there is one (a click on the edge row
    /// scrolls on).</summary>
    public void Select(int row, bool scroll)
    {
        _selected = Math.Clamp(row, 0, Math.Max(0, Rows.Count - 1));
        if (scroll)
        {
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
        }

        if (Slot(Highlighted) >= 0)
        {
            Highlight = Items.FindIndex(i => i.Id == $"row{_selected - _top}");
        }
    }

    /// <summary>Up / down on a row walk the whole list (scrolling it); off its ends the highlight
    /// moves on as on every screen.</summary>
    public override bool Intercept(in MenuInput input)
    {
        Sync();
        if (_scroll.Update(input, PointerAt, ref _top, Rows.Count, At(ListRect), At(TrackRect)))
        {
            Scrolled();
            return true;
        }

        if (input.Pointer != null)
        {
            return false;
        }

        if (input.Left && Highlighted?.Id is "up" or "down" && Rows.Count > 0)
        {
            // Back from the buttons to the mod they move.
            Highlight = 0;
            Select(_selected, scroll: true);
            return true;
        }

        if (Slot(Highlighted) < 0 || !(input.Up || input.Down))
        {
            return false;
        }

        var next = _selected + (input.Down ? 1 : -1);
        if (next < 0 || next >= Rows.Count)
        {
            return false;
        }

        Select(next, scroll: true);
        return true;
    }

    /// <summary>The list scrolled by the pointer: the selection kept in view, a highlighted row on it.</summary>
    private void Scrolled()
    {
        _selected = Math.Clamp(_selected, _top, Math.Min(Rows.Count, _top + VisibleRows) - 1);
        if (Slot(Highlighted) >= 0)
        {
            Highlight = Items.FindIndex(i => i.Id == $"row{_selected - _top}");
        }
    }

    private void Toggle(int row)
    {
        if (row < 0 || row >= Rows.Count)
        {
            return;
        }

        var mod = Rows[row];
        if (mod.System)
        {
            Menu.ShowToast("SYSTEM MODS ARE ALWAYS ON");
            return;
        }

        ModOrder.Set(Menu.Settings.Mods, mod.Name, !IsOn(mod));
        Reorder(mod);
    }

    private void Move(bool up)
    {
        if (Selected is not { } mod)
        {
            return;
        }

        if (mod.System)
        {
            Menu.ShowToast("SYSTEM MODS LOAD FIRST");
            return;
        }

        if (!IsOn(mod))
        {
            Menu.ShowToast("TURN THE MOD ON FIRST");
            return;
        }

        var present = _found.Where(m => !m.System).Select(m => m.Name).ToHashSet(VfsPath.Comparer);
        if (ModOrder.Move(Menu.Settings.Mods, mod.Name, up, present.Contains))
        {
            Reorder(mod);
        }
    }

    /// <summary>The rows made again after a change, the selection following <paramref name="mod"/>; saved and handed to the game.</summary>
    private void Reorder(ModEntry mod)
    {
        Rows = ModOrder.Rows(Menu.Settings.Mods, _found);
        Select(Rows.ToList().FindIndex(r => VfsPath.Comparer.Equals(r.Name, mod.Name)), scroll: true);
        _changed = true;
        Menu.Changed();
    }

    /// <summary>What the note line says: <c>--mod</c> in force, or when a change in game comes in.</summary>
    public string? Note => _job?.Status ?? _jobNote ?? (Menu.Context.ModsOverride != null ? OverrideNote : _changed && Menu.InGame ? InGameNote : null);

    /// <summary>A row's kind: ZIP, DIR (a folder) or ZIP+DIR.</summary>
    public static string Kind(ModEntry m) => m.System ? "SYSTEM" : m.Folder && m.Zip ? "ZIP+DIR" : m.Zip ? "ZIP" : "DIR";

    /// <summary>What a mod carries, as the info box's last line says it.</summary>
    public static string CarriesLine(ModEntry m)
    {
        if (m.System)
        {
            return "THE MAPS BAKED FROM THE GAME DATA";
        }

        List<string> parts = [];
        if (m.Carries.HasFlag(ModCarries.Textures))
        {
            parts.Add("TEXTURES");
        }

        if (m.Carries.HasFlag(ModCarries.Models))
        {
            parts.Add("MODELS");
        }

        if (m.Carries.HasFlag(ModCarries.Tables))
        {
            parts.Add("TABLES");
        }

        if (m.Carries.HasFlag(ModCarries.Weather))
        {
            parts.Add("WEATHER");
        }

        return parts.Count > 0 ? "CARRIES " + string.Join(", ", parts) : "CARRIES NOTHING THE GAME READS";
    }

    public override void Draw(MenuPainter p)
    {
        Sync();
        DosDialog.Panel(p, new Rectangle(Origin, Size));
        p.TextIn(DosDialog.TitleFont, "MODS", At(new Rectangle(0, 4, Size.Width, 14)), MenuPainter.TextPale);
        DosDialog.Box(p, At(ListRect));
        if (Rows.Count == 0)
        {
            p.TextIn(DosDialog.Font, "NO MODS IN THE MODS FOLDER", At(RowRect(2)), Greyed);
        }

        for (var i = 0; i < Math.Min(VisibleRows, Rows.Count - _top); i++)
        {
            var mod = Rows[_top + i];
            var row = At(RowRect(i));
            var on = IsOn(mod);
            if (_top + i == _selected && Slot(Highlighted) < 0)
            {
                p.Mark(row);
            }

            var box = new Rectangle(row.X + 1, row.Y + 1, ToggleWidth - 2, row.Height - 2);
            DosDialog.Bevel(p, box, raised: !on);
            p.TextIn(DosDialog.Font, mod.System ? "SYS" : on ? "ON" : "OFF", box, mod.System ? Greyed : on ? MenuPainter.TextGreen : Greyed);
            var colour = mod.Problem != null && mod.Problem.StartsWith("does not open", StringComparison.Ordinal) ? DosDialog.Pending
                : mod.System ? Greyed : on ? MenuPainter.TextGreen : MenuPainter.TextPale;
            var kind = Kind(mod);
            var kindWidth = p.Font(DosDialog.Font)?.Measure(kind) ?? 0;
            p.Text(DosDialog.Font, Fit(p, mod.Name.ToUpperInvariant(), row.Width - ToggleWidth - kindWidth - 10), row.X + ToggleWidth + 3, row.Y + 2, colour);
            p.Text(DosDialog.Font, kind, row.Right - 2, row.Y + 2, Greyed, TextAlign.Right);
        }

        if (Rows.Count > VisibleRows)
        {
            // Where the list stands: a thin bar down its right edge.
            var thumb = _scroll.Thumb(At(TrackRect), _top, Rows.Count);
            p.Fill(new Rectangle(thumb.Right - 3, thumb.Y, 2, thumb.Height), DosDialog.Light);
        }

        var movable = Selected is { System: false } s && IsOn(s);
        DosDialog.Button(p, At(UpRect), "UP", movable);
        DosDialog.Button(p, At(DownRect), "DOWN", movable);
        DrawInfo(p);
        if (Menu.Context.GenerateHires != null)
        {
            // The dialogs' smaller font: the caption is long.
            var r = At(GenerateRect);
            DosDialog.Panel(p, r);
            p.TextIn(DosDialog.Font, _job != null ? "GENERATING... ESC CANCELS" : GenerateCaption, r, _job != null ? Greyed : MenuPainter.TextPale);
        }

        if (Note is { } note)
        {
            p.TextIn(DosDialog.Font, note, At(NoteRect), DosDialog.Pending);
        }

        if (!p.ImagePart(OptionsImage, MenuPalette, OptionsExit, At(ExitRect)))
        {
            DosDialog.Button(p, At(ExitRect), "EXIT");
        }

        if (Highlighted is { } hl && Menu.Current == this)
        {
            p.Highlight(hl);
        }
    }

    private void DrawInfo(MenuPainter p)
    {
        var box = At(InfoRect);
        DosDialog.Box(p, box);
        if (Selected is not { } mod || p.Font(DosDialog.Font) is not { } font)
        {
            return;
        }

        var x = box.X + 4;
        var width = box.Width - 8;
        var line = font.LineHeight + 2;
        var y = box.Y + 3;
        var last = box.Bottom - 3 - line;
        var title = mod.Title.ToUpperInvariant() + (mod.Info?.Version is { } v ? "  V" + v.ToUpperInvariant().TrimStart('V') : "") + (mod.System ? "  (SYSTEM)" : "");
        p.Text(DosDialog.Font, Fit(p, title, width), x, y, MenuPainter.TextGreen);
        y += line;
        if (mod.Info?.Author is { } author)
        {
            p.Text(DosDialog.Font, Fit(p, "BY " + author.ToUpperInvariant(), width), x, y, MenuPainter.TextPale);
            y += line;
        }

        var (text, colour) = mod.Problem is { } problem ? (problem, DosDialog.Pending) : (mod.Info?.Description ?? "", MenuPainter.TextPale);
        var lines = Wrap(font, text.ToUpperInvariant(), width);
        var room = Math.Max(0, (last - y) / line);
        for (var i = 0; i < Math.Min(room, lines.Count); i++)
        {
            var shown = i == room - 1 && lines.Count > room ? Fit(p, lines[i] + " ...", width) : lines[i];
            p.Text(DosDialog.Font, shown, x, y, colour);
            y += line;
        }

        p.Text(DosDialog.Font, CarriesLine(mod), x, last, mod.System ? Greyed : MenuPainter.TextGreen);
    }

    /// <summary><paramref name="text"/> cut (with "...") to <paramref name="width"/> DOS pixels.</summary>
    private static string Fit(MenuPainter p, string text, int width)
    {
        if (p.Font(DosDialog.Font) is not { } f || f.Measure(text) <= width)
        {
            return text;
        }

        while (text.Length > 1 && f.Measure(text + "...") > width)
        {
            text = text[..^1];
        }

        return text.TrimEnd() + "...";
    }

    /// <summary>Words into lines no wider than <paramref name="width"/> (a word too long for a line is cut).</summary>
    public static List<string> Wrap(BitmapFont font, string text, int width)
    {
        List<string> lines = [];
        var current = "";
        foreach (var word in text.Split(' ', StringSplitOptions.RemoveEmptyEntries))
        {
            var next = current.Length == 0 ? word : current + " " + word;
            if (font.Measure(next) <= width)
            {
                current = next;
                continue;
            }

            if (current.Length > 0)
            {
                lines.Add(current);
            }

            current = word;
            while (current.Length > 1 && font.Measure(current) > width)
            {
                current = current[..^1];
            }
        }

        if (current.Length > 0)
        {
            lines.Add(current);
        }

        return lines;
    }

    internal static readonly Vector4 Greyed = new(0.45f, 0.5f, 0.5f, 1f);
}

/// <summary>
/// HI-RES TEXTURES, a one-time notice: it comes up on the title (or when DISPLAY's LOOK turns to
/// ENHANCED) while the look is ENHANCED, no mod with textures is in use and the notice was never
/// dismissed (<see cref="MenuSettings.HiresNoticeSeen"/>). It says the textures can be made from the
/// player's own data with OPTIONS → MODS → GENERATE HI-RES TEXTURES or with tools/upscale
/// (docs/hires-textures.md); the game never makes them unasked.
/// OK (or Esc) dismisses it for good; MODS dismisses it and opens the MODS screen. Never under the
/// ORIGINAL look.
/// </summary>
public sealed class HiresNotice : PanelScreen
{
    public static readonly Size PanelSize = new(236, 124);
    public static readonly Rectangle TextRect = new(10, 22, 216, 76);
    public static readonly Rectangle ModsRect = new(10, 102, 70, 18);
    public static readonly Rectangle OkRect = new(PanelSize.Width - 80, 102, 70, 18);

    public const string Title = "HI-RES TEXTURES";

    public const string Text =
        "THE ENHANCED LOOK CAN DRAW SHARPER TEXTURES, UPSCALED FROM YOUR OWN GAME DATA. MAKE THEM ONCE " +
        "WITH GENERATE HI-RES TEXTURES UNDER OPTIONS - MODS (A 50 MB DOWNLOAD, A FEW MINUTES ON A VULKAN " +
        "GPU) OR WITH TOOLS/UPSCALE AS DOCS/HIRES-TEXTURES.MD SAYS.";

    public HiresNotice() : base(OptionsImage, PanelSize, DetailScale)
    {
    }

    public override string Name => "hires";

    /// <summary>The notice is due: the enhanced look, no texture mod in use, never dismissed.</summary>
    public static bool Due(MenuSettings s, MenuContext c) =>
        s.Look == Look.Enhanced && !s.HiresNoticeSeen && c.EnhancedAvailable && c.HiresTextures is { } has && !has();

    public override void Opened()
    {
        if (Items.Count > 0)
        {
            return;
        }

        Add(new MenuItem("mods", At(ModsRect))
        {
            Activate = () =>
            {
                Dismiss();
                Menu.Push(new ModsScreen());
            },
        });
        Add(new MenuItem("ok", At(OkRect)) { Activate = Dismiss });
        Highlight = 1;
    }

    public override void Back() => Dismiss();

    private void Dismiss()
    {
        Menu.Settings.HiresNoticeSeen = true;
        Menu.Changed();
        Menu.Pop();
    }

    public override void Draw(MenuPainter p)
    {
        DosDialog.Panel(p, new Rectangle(Origin, Size));
        p.TextIn(DosDialog.TitleFont, Title, At(new Rectangle(0, 4, Size.Width, 14)), MenuPainter.TextPale);
        if (p.Font(DosDialog.Font) is { } font)
        {
            var r = At(TextRect);
            var y = r.Y;
            foreach (var line in ModsScreen.Wrap(font, Text, r.Width))
            {
                if (y + font.LineHeight > r.Bottom)
                {
                    break;
                }

                p.Text(DosDialog.Font, line, r.X + r.Width / 2f, y, MenuPainter.TextGreen, TextAlign.Centre);
                y += font.LineHeight + 2;
            }
        }

        DosDialog.Button(p, At(ModsRect), "MODS");
        DosDialog.Button(p, At(OkRect), "OK");
        if (Highlighted is { } h && Menu.Current == this)
        {
            p.Highlight(h);
        }
    }
}

/// <summary>
/// GENERATE HI-RES TEXTURES? (from MODS): what it takes, then YES starts it (the MODS screen runs
/// it) and NO or Esc goes back.
/// </summary>
public sealed class GenerateBox(Action yes) : PanelScreen(OptionsImage, PanelSize, DetailScale)
{
    public static readonly Size PanelSize = new(236, 100);
    public static readonly Rectangle TextRect = new(10, 22, 216, 52);
    public static readonly Rectangle YesRect = new(10, 78, 70, 18);
    public static readonly Rectangle NoRect = new(PanelSize.Width - 80, 78, 70, 18);

    public const string Text =
        "UPSCALE EVERY TEXTURE 4X WITH REAL-ESRGAN? IT DOWNLOADS THE UPSCALER ONCE (ABOUT 50 MB), NEEDS A " +
        "VULKAN GPU AND TAKES A FEW MINUTES. ESC CANCELS.";

    public override string Name => "generatehires";

    public override void Opened()
    {
        if (Items.Count > 0)
        {
            return;
        }

        Add(new MenuItem("yes", At(YesRect))
        {
            Activate = () =>
            {
                Menu.Pop();
                yes();
            },
        });
        Add(new MenuItem("no", At(NoRect)) { Activate = Menu.Pop });
        Highlight = 1;
    }

    public override void Draw(MenuPainter p)
    {
        DosDialog.Panel(p, new Rectangle(Origin, Size));
        p.TextIn(DosDialog.TitleFont, ModsScreen.GenerateCaption, At(new Rectangle(0, 4, Size.Width, 14)), MenuPainter.TextPale);
        if (p.Font(DosDialog.Font) is { } font)
        {
            var r = At(TextRect);
            var y = r.Y;
            foreach (var line in ModsScreen.Wrap(font, Text, r.Width))
            {
                if (y + font.LineHeight > r.Bottom)
                {
                    break;
                }

                p.Text(DosDialog.Font, line, r.X + r.Width / 2f, y, MenuPainter.TextGreen, TextAlign.Centre);
                y += font.LineHeight + 2;
            }
        }

        DosDialog.Button(p, At(YesRect), "YES");
        DosDialog.Button(p, At(NoRect), "NO");
        if (Highlighted is { } h && Menu.Current == this)
        {
            p.Highlight(h);
        }
    }
}
