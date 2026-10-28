using System.Drawing;
using System.Numerics;
using System.Reflection;
using RetroEng.Render;
using SkyNet.Formats;
using SkyNet.Game.Campaign;
using SkyNet.Game.Enemies;
using SkyNet.Game.Hud;
using static SkyNet.Game.Menu.MenuLayout;

namespace SkyNet.Game.Menu;

/// <summary>The bar: MAIN1.IMG over the title picture, or in game MAIN2.IMG over the darkened view
/// (RETURN / LOAD / SAVE / OPTIONS / QUIT, the panel left showing).</summary>
public sealed class MainMenuScreen : MenuScreen
{
    private const string VersionFont = "FONT0004.FNT";
    private static readonly Vector4 VersionColour = new(0.83f, 0.93f, 0.84f, 0.35f);

    public override string Name => Menu.InGame ? "pause" : "title";

    /// <summary>The build's version for the title's corner: "v0.20.0", the commit suffix ("+abc…") cut.</summary>
    public static string VersionLabel { get; } = Label(typeof(MainMenuScreen).Assembly);

    private static string Label(Assembly assembly)
    {
        var version = assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion
            ?? assembly.GetName().Version?.ToString(3) ?? "?";
        var plus = version.IndexOf('+', StringComparison.Ordinal);
        return "v" + (plus < 0 ? version : version[..plus]);
    }

    public override bool Pauses => Menu.InGame;

    public override void Opened()
    {
        if (Items.Count > 0)
        {
            return;
        }

        var inGame = Menu.InGame;
        var lit = inGame ? GameBarLit : TitleBarLit;
        LitArt Lit(int i) => new(lit, i, Point.Empty);
        Add(new MenuItem(inGame ? "return" : "new", BarItems[0]) { Lit = Lit(0), Activate = inGame ? Menu.Resume : () => Menu.Push(new NewGameScreen()) });
        Add(new MenuItem("load", BarItems[1]) { Lit = Lit(1), Activate = () => Menu.Push(new SlotsScreen(saving: false)) });
        Add(new MenuItem("save", BarItems[2]) { Lit = Lit(2), Enabled = inGame, Activate = () => Menu.Push(new SlotsScreen(saving: true)) });
        Add(new MenuItem("options", BarItems[3]) { Lit = Lit(3), Activate = () => Menu.Push(new OptionsScreen()) });
        Add(new MenuItem("quit", BarItems[4]) { Lit = Lit(4), Activate = () => Menu.Push(inGame ? ConfirmBox.QuitToTitle() : ConfirmBox.QuitGame()) });
        Highlight = 0;
    }

    /// <summary>Esc: in game it resumes; on the title it asks QUIT TO DOS?.</summary>
    public override void Back()
    {
        if (Menu.InGame)
        {
            Menu.Resume();
        }
        else
        {
            Menu.Push(ConfirmBox.QuitGame());
        }
    }

    public override void Draw(MenuPainter p)
    {
        if (Menu.InGame)
        {
            p.FillWindow(new Vector4(0f, 0f, 0f, 0.45f));
        }
        else
        {
            p.FillWindow(MenuPainter.Black);
            p.Image(TitleImage, TitlePalette, 0, 0);

            // The build's version, dim in the bottom-right corner (title only).
            var line = p.Font(VersionFont)?.LineHeight ?? 0;
            p.Text(VersionFont, VersionLabel, MenuFrame.ScreenWidth - 2, MenuFrame.ScreenHeight - line - 1, VersionColour, TextAlign.Right);
        }

        if (!p.Image(Menu.InGame ? GameBar : TitleBar, MenuPalette, 0, 0))
        {
            p.Fill(new Rectangle(0, 0, MenuFrame.ScreenWidth, BarHeight), new Vector4(0.16f, 0.22f, 0.22f, 1f));
            string[] captions = [Menu.InGame ? "RETURN" : "NEW GAME", "LOAD", "SAVE", "OPTIONS", "QUIT"];
            for (var i = 0; i < Items.Count; i++)
            {
                p.TextIn("FONT0003.FNT", captions[i], Items[i].Rect, MenuPainter.TextPale);
            }
        }

        if (Highlighted is { } h && Menu.Current == this)
        {
            p.Highlight(h);
        }
    }
}

/// <summary>A dialog panel (an .IMG with baked buttons) centred on the window and scaled with it
/// as the Godot port scales it (<paramref name="scale"/> on its 1280 × 720 canvas), over the screen below it.</summary>
public abstract class PanelScreen(string image, Size size, float scale) : MenuScreen
{
    public string ImageName { get; } = image;
    public Size Size { get; } = size;
    public Point Origin { get; protected init; } = Centred(size);

    /// <summary>The Godot port's scale for the panel on its 1280 × 720 canvas.</summary>
    public float Scale { get; } = scale;

    public override MenuFrame FrameIn(MenuFrame window) => window.Panel(new Rectangle(Origin, Size), Scale);

    protected Rectangle At(Rectangle r) => MenuLayout.At(Origin, r);

    /// <summary>Frame <paramref name="frame"/> of the panel's lit art placed at <paramref name="at"/> of the panel.</summary>
    protected LitArt Lit(string art, Point at, int frame) => new(art, frame, new Point(Origin.X + at.X, Origin.Y + at.Y));

    public override void Draw(MenuPainter p)
    {
        if (!p.Image(ImageName, MenuPalette, Origin.X, Origin.Y))
        {
            p.Fill(new Rectangle(Origin, Size), new Vector4(0.12f, 0.17f, 0.17f, 1f));
            p.Outline(new Rectangle(Origin, Size), MenuPainter.TextPale);
        }

        DrawContents(p);
        if (Highlighted is { } h && Menu.Current == this)
        {
            p.Highlight(h);
        }
    }

    protected virtual void DrawContents(MenuPainter p)
    {
    }
}

/// <summary>NETGAME1.IMG: MULTI-PLAYER / ONE PLAYER / FUTURE SHOCK / EXIT.</summary>
public sealed class NewGameScreen : PanelScreen
{
    public NewGameScreen() : base(NewGameImage, NewGameSize, NewGameScale)
    {
    }

    public override string Name => "newgame";

    public override void Opened()
    {
        if (Items.Count > 0)
        {
            return;
        }

        Add(new MenuItem("multi", At(NewGameMulti)) { Lit = Lit(NewGameLit, NewGameLitAt, 0), Activate = () => Menu.Push(new MultiplayerScreen()) });
        Add(new MenuItem("one", At(NewGameOne)) { Lit = Lit(NewGameLit, NewGameLitAt, 1), Activate = () => Start(GameKind.SkyNet, Menu.Context.SkyNetAvailable, "SKYNET DATA NOT FOUND") });
        Add(new MenuItem("futureshock", At(NewGameFutureShock))
        {
            Lit = Lit(NewGameLit, NewGameLitAt, 3),
            Activate = () => Start(GameKind.FutureShock, Menu.Context.FutureShockAvailable, "FUTURE SHOCK DATA NOT FOUND"),
        });
        Add(new MenuItem("exit", At(NewGameExit)) { Lit = Lit(NewGameLit, NewGameLitAt, 4), Activate = Menu.Pop });
        Highlight = 1;
    }

    private void Start(GameKind game, bool available, string missing)
    {
        if (!available)
        {
            Menu.ShowToast(missing);
            return;
        }

        Menu.Push(new PilotScreen(game));
    }
}

/// <summary>LOAD.IMG / SAVE.IMG: ten slots and EXIT. The slot names come from the caller.</summary>
public sealed class SlotsScreen : PanelScreen
{
    private readonly bool _saving;

    public SlotsScreen(bool saving) : base(saving ? SaveImage : LoadImage, SlotsSize, SlotsScale) => _saving = saving;

    public override string Name => _saving ? "save" : "load";

    public override void Opened()
    {
        Items.Clear();
        for (var i = 0; i < Slots; i++)
        {
            var slot = i;
            Add(new MenuItem($"slot{i}", At(Slot(i)))
            {
                Enabled = _saving || Menu.Context.SlotName(i) != null,
                Activate = () =>
                {
                    if (_saving)
                    {
                        // Only a save that reached the disk says so; a refused one leaves the
                        // screen up for the game's own message.
                        if (Menu.Actions.SaveGame(slot))
                        {
                            Menu.ShowToast("GAME SAVED");
                            Menu.Pop();
                        }
                    }
                    else
                    {
                        Menu.Close();
                        Menu.Actions.LoadGame(slot);
                    }
                },
            });
        }

        Add(new MenuItem("exit", At(SlotsExit)) { Lit = Lit(SlotsExitLit, SlotsExit.Location, 0), Activate = Menu.Pop });
        Highlight = Items.FindIndex(i => i.Enabled);
    }

    protected override void DrawContents(MenuPainter p)
    {
        for (var i = 0; i < Slots; i++)
        {
            if (Menu.Context.SlotName(i) is { } name)
            {
                var r = Items[i].Rect;
                p.Text("FONT0004.FNT", name.ToUpperInvariant(), r.X + 4, r.Y + 3, MenuPainter.TextGreen);
            }
        }
    }
}

/// <summary>OPTIONS.IMG: SOUND and MUSIC bars, REVERSE STEREO, DIFFICULTY LOW / MED / HIGH,
/// CONTROLS / DETAIL / EXIT; under it the port's DISPLAY and MODS bars (<see cref="DisplayScreen"/>,
/// <see cref="ModsScreen"/>), of the same stone, since the art has no room for them.</summary>
public sealed class OptionsScreen : PanelScreen
{
    /// <summary>The DISPLAY bar under OPTIONS.IMG, its left half.</summary>
    public static readonly Rectangle DisplayBar = new(0, OptionsSize.Height, OptionsSize.Width / 2 - 1, 18);

    /// <summary>The MODS bar beside it (<see cref="ModsScreen"/>).</summary>
    public static readonly Rectangle ModsBar = new(OptionsSize.Width / 2 + 1, OptionsSize.Height, OptionsSize.Width - OptionsSize.Width / 2 - 1, 18);

    public OptionsScreen() : base(OptionsImage, new Size(OptionsSize.Width, OptionsSize.Height + DisplayBar.Height), OptionsScale)
    {
    }

    public override string Name => "options";

    public override void Opened()
    {
        if (Items.Count > 0)
        {
            return;
        }

        var s = Menu.Settings;
        Add(new MenuItem("sound", At(OptionsSound))
        {
            Adjust = d => Set(() => s.SoundVolume = Math.Clamp(s.SoundVolume + d, 0, MenuSettings.VolumeSteps)),
            ClickAt = t => Set(() => s.SoundVolume = (int)MathF.Round(t * MenuSettings.VolumeSteps)),
        });
        Add(new MenuItem("music", At(OptionsMusic))
        {
            Adjust = d => Set(() => s.MusicVolume = Math.Clamp(s.MusicVolume + d, 0, MenuSettings.VolumeSteps)),
            ClickAt = t => Set(() => s.MusicVolume = (int)MathF.Round(t * MenuSettings.VolumeSteps)),
        });
        Add(new MenuItem("stereo", At(OptionsStereo)) { Lit = Lit(OptionsLit, OptionsLitAt, 2), Activate = () => Set(() => s.ReverseStereo = !s.ReverseStereo) });
        for (var i = 0; i < OptionsDifficulty.Length; i++)
        {
            var level = (DifficultyLevel)i;
            Add(new MenuItem($"difficulty{i}", At(OptionsDifficulty[i])) { Lit = Lit(OptionsLit, OptionsLitAt, 3 + i), Activate = () => Set(() => s.Difficulty = level) });
        }

        Add(new MenuItem("controls", At(OptionsControls)) { Lit = Lit(OptionsLit, OptionsLitAt, 9), Activate = () => Menu.Push(new ControlsScreen()) });
        Add(new MenuItem("detail", At(OptionsDetail)) { Lit = Lit(OptionsLit, OptionsLitAt, 10), Activate = () => Menu.Push(new DetailScreen()) });
        Add(new MenuItem("exit", At(OptionsExit)) { Lit = Lit(OptionsLit, OptionsLitAt, 11), Activate = Menu.Pop });
        Add(new MenuItem("display", At(DisplayBar)) { Activate = () => Menu.Push(new DisplayScreen()) });
        Add(new MenuItem("mods", At(ModsBar)) { Activate = () => Menu.Push(new ModsScreen()) });
        Highlight = 0;
    }

    private void Set(Action change)
    {
        change();
        Menu.Changed();
    }

    protected override void DrawContents(MenuPainter p)
    {
        DosDialog.Button(p, At(DisplayBar), "DISPLAY");
        DosDialog.Button(p, At(ModsBar), ModsScreen.Caption);
        var s = Menu.Settings;
        Bar(p, Find("sound")!.Rect, s.SoundVolume);
        Bar(p, Find("music")!.Rect, s.MusicVolume);
        if (s.ReverseStereo)
        {
            p.Mark(Find("stereo")!, Lit(OptionsLit, OptionsLitAt, 2));
        }

        var d = (int)s.Difficulty;
        p.Mark(Find($"difficulty{d}")!, Lit(OptionsLit, OptionsLitAt, 6 + d));
    }

    private static void Bar(MenuPainter p, Rectangle track, int value)
    {
        var w = (track.Width - 2) * Math.Clamp(value, 0, MenuSettings.VolumeSteps) / MenuSettings.VolumeSteps;
        p.Fill(new Rectangle(track.X + 1, track.Y + 1, w, track.Height - 2), new Vector4(0.35f, 0.85f, 0.45f, 0.85f));
    }
}

/// <summary>DETAIL.IMG, RENDER DETAIL: LOW / MED / HIGH (the haze distance and the HD textures' size), GAMEPLAY over DOS's
/// RESOLUTION 320 × 200 / 640 × 480 pair (<see cref="GameplayScreen"/>; the HUD art set it chose is
/// the console's <c>hud</c> and the settings' <c>hudArt</c> now), EXIT.</summary>
public sealed class DetailScreen : PanelScreen
{
    public DetailScreen() : base(DetailImage, DetailSize, DetailScale)
    {
    }

    public override string Name => "detail";

    public override void Opened()
    {
        if (Items.Count > 0)
        {
            return;
        }

        var s = Menu.Settings;
        for (var i = 0; i < DetailLevels.Length; i++)
        {
            var d = (Detail)i;
            Add(new MenuItem($"detail{i}", At(DetailLevels[i])) { Lit = Lit(DetailLit, DetailLitAt, i), Activate = () => Set(() => s.Detail = d) });
        }

        Add(new MenuItem("gameplay", At(DetailGameplay)) { Activate = () => Menu.Push(new GameplayScreen()) });
        Add(new MenuItem("exit", At(DetailExit)) { Lit = Lit(DetailLit, DetailLitAt, 10), Activate = Menu.Pop });
        Highlight = (int)s.Detail;
    }

    private void Set(Action change)
    {
        change();
        Menu.Changed();
    }

    protected override void DrawContents(MenuPainter p)
    {
        var s = Menu.Settings;
        var d = (int)s.Detail;
        p.Mark(Find($"detail{d}")!, Lit(DetailLit, DetailLitAt, 3 + d));
        // The stone over DOS's RESOLUTION caption and its two buttons, GAMEPLAY on it.
        DosDialog.Plain(p, At(DetailResolutionArea));
        DosDialog.Button(p, At(DetailGameplay), GameplayScreen.Caption);
        // DETAIL also sizes the HD textures (Mods.HdDetail), taken at the next map's load.
        p.TextIn(DosDialog.Font, TextureHint[d], At(DetailHint), DosDialog.Pending);
    }

    /// <summary>The HD textures' size per DETAIL (LOW, MED, HIGH).</summary>
    private static readonly string[] TextureHint = ["HD TEXTURES 1/4, NEXT MAP", "HD TEXTURES 1/2, NEXT MAP", "HD TEXTURES FULL, NEXT MAP"];

    /// <summary>The hint's line: the stone's top, above GAMEPLAY.</summary>
    private static readonly Rectangle DetailHint = new(DetailResolutionArea.X, DetailResolutionArea.Y + 1, DetailResolutionArea.Width, 11);
}

/// <summary>CONTROLS.IMG, CONTROL CONFIGURATION: the 17 boxes show their key; Accept or a click
/// waits for the next key or mouse button (Esc cancels), GAMEPAD (DOS's JOYSTICK, its caption
/// drawn over the baked one) opens <see cref="GamepadScreen"/>, MOUSE opens MOUSE SENSITIVITY (<see cref="MouseScreen"/>),
/// DEFAULT resets, EXIT returns.</summary>
public sealed class ControlsScreen : PanelScreen
{
    private int _capturing = -1;
    private bool _armed;

    public ControlsScreen() : base(ControlsImage, ControlsSize, ControlsScale)
    {
    }

    public override string Name => "controls";

    /// <summary>The box waiting for a key, -1 when none.</summary>
    public int Capturing => _capturing;

    private IBindings? Bindings => Menu.Context.Bindings;

    /// <summary>The action id of a box, -1 when the game has no such action.</summary>
    public int ActionOf(string slot) =>
        Bindings is { } b && Menu.Context.ControlActions.TryGetValue(slot, out var name) ? b.Find(name) : -1;

    public override void Opened()
    {
        if (Items.Count > 0)
        {
            return;
        }

        for (var i = 0; i < ControlBoxes.Length; i++)
        {
            var (slot, box) = ControlBoxes[i];
            var index = i;
            Add(new MenuItem(slot, At(box))
            {
                Enabled = ActionOf(slot) >= 0,
                Activate = () => (_capturing, _armed) = (index, false),
            });
        }

        Add(new MenuItem("gamepad", At(ControlsJoystick)) { Activate = () => Menu.Push(new GamepadScreen()) });
        Add(new MenuItem("mouse", At(ControlsMouse)) { Lit = Lit(ControlsLit, ControlsLitAt, 1), Activate = () => Menu.Push(new MouseScreen()) });
        Add(new MenuItem("default", At(ControlsDefault)) { Lit = Lit(ControlsLit, ControlsLitAt, 2), Activate = ResetDefaults });
        Add(new MenuItem("exit", At(ControlsExit)) { Lit = Lit(ControlsLit, ControlsLitAt, 3), Activate = Menu.Pop });

        Highlight = Items.FindIndex(i => i.Enabled);

        // A touch screen's TOUCH LAYOUT: the first row, a bar over the art (the art has no room),
        // highlighted first. Added last, so the boxes keep their item indices.
        if (Menu.Context.Touch() != null)
        {
            Add(new MenuItem(TouchLayoutScreen.Id, At(TouchBar)) { Activate = () => Menu.Push(new TouchLayoutScreen()) });
            Highlight = Items.Count - 1;
        }
    }

    /// <summary>The TOUCH LAYOUT bar over CONTROLS.IMG.</summary>
    private static readonly Rectangle TouchBar = new(0, -19, ControlsSize.Width, 18);

    /// <summary>With the TOUCH LAYOUT bar, the panel takes its room too.</summary>
    public override MenuFrame FrameIn(MenuFrame window) => Find(TouchLayoutScreen.Id) == null
        ? base.FrameIn(window)
        : window.Panel(new Rectangle(Origin.X, Origin.Y + TouchBar.Y, Size.Width, Size.Height - TouchBar.Y), Scale);

    private void ResetDefaults()
    {
        Bindings?.ResetBindings();
        Menu.Settings.Bindings = Bindings is { } b ? Rebind.Snapshot(b) : new(StringComparer.Ordinal);
        Menu.Changed();
    }

    /// <summary>While a box waits: the first frame after the press that opened it is skipped (it
    /// carries that press), then Back cancels and any key or mouse button binds.</summary>
    public override bool Intercept(in MenuInput input)
    {
        if (_capturing < 0)
        {
            return false;
        }

        if (!_armed)
        {
            _armed = true;
            return true;
        }

        if (input.Back)
        {
            _capturing = -1;
            return true;
        }

        if (input.Control.Length > 0 && Bindings is { } b)
        {
            var boxes = ControlBoxes.Select(c => ActionOf(c.Slot)).ToArray();
            var action = boxes[_capturing];
            if (Rebind.Apply(b, action, input.Control, boxes))
            {
                Menu.Settings.Bindings = Rebind.Snapshot(b);
                Menu.Changed();
            }

            _capturing = -1;
        }

        return true;
    }

    public override void Back()
    {
        _capturing = -1;
        base.Back();
    }

    /// <summary>A control's name short enough for a 48-pixel box ("Left mouse" → "MOUSE L").</summary>
    public static string ShortName(string control)
    {
        var s = control.ToUpperInvariant();
        foreach (var (side, tag) in new[] { ("LEFT ", "L "), ("RIGHT ", "R ") })
        {
            if (s.StartsWith(side, StringComparison.Ordinal) && s.Length > side.Length)
            {
                s = tag + s[side.Length..];
            }
        }

        return s switch
        {
            "L MOUSE" => "MOUSE L",
            "R MOUSE" => "MOUSE R",
            "MIDDLE MOUSE" => "MOUSE M",
            "PAGEUP" => "PG UP",
            "PAGEDOWN" => "PG DN",
            _ => s,
        };
    }

    protected override void DrawContents(MenuPainter p)
    {
        // DOS's JOYSTICK button (an analogue PC stick) is the port's GAMEPAD.
        DosDialog.Button(p, At(ControlsJoystick), "GAMEPAD");
        if (Find(TouchLayoutScreen.Id) is { } touch)
        {
            DosDialog.Button(p, touch.Rect, TouchLayoutScreen.Caption);
        }

        for (var i = 0; i < ControlBoxes.Length; i++)
        {
            var item = Items[i];
            var action = ActionOf(item.Id);
            string label;
            if (i == _capturing)
            {
                label = "?";
            }
            else if (action < 0 || Bindings is not { } b)
            {
                label = "---";
            }
            else
            {
                label = b.Bindings(action) is [var first, ..] ? ShortName(first) : "---";
            }

            p.TextIn("FONT0004.FNT", label, item.Rect, i == _capturing ? new Vector4(1f, 0.85f, 0.4f, 1f) : MenuPainter.TextGreen);
        }
    }
}

/// <summary>The 96 × 37 YES / NO boxes: QUIT.IMG, QUITMAIN.IMG and RESTART.IMG; the port's own
/// questions (STOP THE SERVER?) use QUIT.IMG with their caption over the baked one.</summary>
public sealed class ConfirmBox : PanelScreen
{
    /// <summary>Where a caption of the port's own covers QUIT.IMG's (inside its bevel, above YES / NO).</summary>
    public static readonly Rectangle CaptionRect = new(2, 2, 92, 17);

    private readonly string _name;
    private readonly Action<MenuSystem> _yes;
    private readonly Action<MenuSystem> _no;
    private readonly string _lit;
    private string? _caption;
    private Action<float>? _tick;

    private ConfirmBox(string name, string image, Action<MenuSystem> yes, Action<MenuSystem> no) : base(image, BoxSize, BoxScale)
    {
        _lit = image == RestartImage ? RestartLit : QuitLit;
        _name = name;
        _yes = yes;
        _no = no;
    }

    public override string Name => _name;

    /// <summary>QUIT TO DOS? on the title: YES quits the program.</summary>
    public static ConfirmBox QuitGame() => new("quit", QuitImage, m => m.Actions.Quit(), m => m.Pop());

    /// <summary>QUIT TO MAIN MENU? in game.</summary>
    public static ConfirmBox QuitToTitle() => new("quitmain", QuitMainImage, m => m.QuitToTitle(), m => m.Pop());

    /// <summary>RESTART MISSION? after a death or a failed mission (FUN_0011d09e): YES replays the
    /// mission from its first map, NO goes to the title.</summary>
    public static ConfirmBox Restart() => new("restart", RestartImage, m => m.RestartMission(), m => m.QuitToTitle());

    /// <summary>STOP THE SERVER? (Back or Esc on SERVER STATUS): YES stops it for everybody, NO goes
    /// back to the status. <paramref name="keepRunning"/> steps the server while the box is up.</summary>
    public static ConfirmBox StopServer(Action<float> keepRunning, Action stop) =>
        new("stopserver", QuitImage, _ => stop(), m => m.Pop()) { _caption = "STOP THE SERVER?", _tick = keepRunning };

    /// <summary>The question when it is the port's own (null: the image's baked one).</summary>
    public string? Caption => _caption;

    public override bool ShowsBelow => _caption != null;

    public override void Tick(float seconds) => _tick?.Invoke(seconds);

    public override void Opened()
    {
        if (Items.Count > 0)
        {
            return;
        }

        Add(new MenuItem("yes", At(BoxYes)) { Lit = Lit(_lit, BoxLitAt, 0), Activate = () => _yes(Menu) });
        Add(new MenuItem("no", At(BoxNo)) { Lit = Lit(_lit, BoxLitAt, 1), Activate = () => _no(Menu) });
        Highlight = 0;
    }

    public override void Back() => _no(Menu);

    public override void Draw(MenuPainter p)
    {
        if (Menu.Stack.Count == 1 && Menu.InGame)
        {
            p.FillWindow(new Vector4(0f, 0f, 0f, 0.45f));
        }

        base.Draw(p);
        if (_caption != null)
        {
            // OPTIONS.IMG's stone over the baked question, then the port's own in the dialogs' font.
            var r = At(CaptionRect);
            if (!p.ImageTiled(OptionsImage, MenuPalette, DosDialog.Stone, r))
            {
                p.Fill(r, new Vector4(0.12f, 0.17f, 0.17f, 1f));
            }

            p.TextIn(DosDialog.Font, _caption, r, MenuPainter.TextPale);
        }
    }
}

/// <summary>
/// NAME.IMG, the new-game dialog (SKYNET.EXE v1.01 0x135345): NAME and CALL SIGN (<see cref="Pilot"/>),
/// asked after NEW GAME → ONE PLAYER or FUTURE SHOCK before the first briefing (0x11cb4a: every
/// new game that is not a load or a network game). The 198 × 57 image is centred on the screen,
/// top-left (61, 72); each field is written at its offset (0x53929: (70, 21) and (70, 41)) in
/// FONT0004, MENU.COL colour 0xB5, with a one-pixel shadow and the '_' cursor after the text of
/// the field being edited. Typing goes to that field (SDL's text input is on while the screen is
/// up, <see cref="MenuScreen.TakesText"/>): up to 17 letters A-Z, shown upper case; Backspace
/// erases. Enter on the name goes on to the call sign, on the call sign starts the game; an empty
/// field takes the STRINGS.PRS default ("Noname", "Ice"), so a gamepad's A (DOS has no on-screen
/// keyboard, and neither does the port) starts with the defaults or the pre-filled pair. Up / down
/// or a click change the field. The port's own: Esc goes back to NEW GAME (DOS's Esc takes the
/// field's default and goes on), and the fields come pre-filled with the last game's pair from the
/// settings (DOS starts them empty).
/// </summary>
public sealed class PilotScreen : PanelScreen
{
    private readonly GameKind _game;
    private readonly string[] _text = ["", ""];
    private bool _accepting;

    public PilotScreen(GameKind game) : base(NameImage, NameSize, NameScale)
    {
        _game = game;
        Origin = NameOrigin;
    }

    public override string Name => "pilot";

    public override bool TakesText => true;

    /// <summary>A field's text as shown: 0 the name, 1 the call sign.</summary>
    public string Text(int field) => _text[field];

    /// <summary>What the dialog would store now (capitalised, empty fields defaulted).</summary>
    public Pilot Pilot => Pilot.FromFields(_text[0], _text[1], Menu.Context.DefaultPilot(_game));

    public override void Opened()
    {
        if (Items.Count > 0)
        {
            return;
        }

        _text[0] = Pilot.Clean(Menu.Settings.PlayerName, Pilot.NameLength);
        _text[1] = Pilot.Clean(Menu.Settings.CallSign, Pilot.CallSignLength);
        // A click only puts the cursor in a field; Enter moves on.
        Add(new MenuItem("name", At(NameField)) { Activate = () => Highlight = _accepting ? 1 : 0 });
        Add(new MenuItem("callsign", At(CallSignField))
        {
            Activate = () =>
            {
                if (_accepting)
                {
                    Accept();
                }
            },
        });
        Highlight = 0;
    }

    private static int Length(int field) => field == 0 ? Pilot.NameLength : Pilot.CallSignLength;

    /// <summary>Typing and Backspace go to the highlighted field. The space bar is also a menu
    /// Accept key: a frame that typed a space is taken here (not a letter, so nothing is typed).</summary>
    public override bool Intercept(in MenuInput input)
    {
        _accepting = input.Accept;
        var field = Math.Clamp(Highlight, 0, 1);
        var used = false;
        if (input.Typed.Length > 0)
        {
            _text[field] = Pilot.Clean(_text[field] + input.Typed, Length(field));
            used = input.Typed.Contains(' ', StringComparison.Ordinal);
        }

        if (input.Erase)
        {
            if (_text[field].Length > 0)
            {
                _text[field] = _text[field][..^1];
            }

            used = true;
        }

        return used;
    }

    /// <summary>Enter on the call sign: the pair is stored (and kept as the next game's default) and the game starts.</summary>
    private void Accept()
    {
        var pilot = Pilot;
        Menu.Settings.PlayerName = pilot.Name;
        Menu.Settings.CallSign = pilot.CallSign;
        Menu.Changed();
        Menu.StartGame(_game, MissionTable.For(_game).Missions[0].Number, pilot);
    }

    public override void Draw(MenuPainter p)
    {
        if (!p.Image(ImageName, MenuPalette, Origin.X, Origin.Y))
        {
            p.Fill(new Rectangle(Origin, Size), new Vector4(0.12f, 0.17f, 0.17f, 1f));
            p.Outline(new Rectangle(Origin, Size), MenuPainter.TextPale);
            p.Text(NameFont, "NAME", Origin.X + 36, Origin.Y + NameTextAt[0].Y, MenuPainter.TextPale);
            p.Text(NameFont, "CALLSIGN", Origin.X + 20, Origin.Y + NameTextAt[1].Y, MenuPainter.TextPale);
        }

        var colour = p.PaletteColour(MenuPalette, NameTextIndex) ?? MenuPainter.TextPale;
        for (var i = 0; i < 2; i++)
        {
            var text = _text[i] + (i == Highlight && Menu.Current == this ? NameCursor : "");
            var (x, y) = (Origin.X + NameTextAt[i].X, Origin.Y + NameTextAt[i].Y);
            p.Text(NameFont, text, x + 1, y + 1, MenuPainter.Black);
            p.Text(NameFont, text, x, y, colour);
        }
    }
}
