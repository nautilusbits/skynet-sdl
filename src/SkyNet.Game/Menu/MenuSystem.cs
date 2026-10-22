using System.Drawing;
using System.Numerics;
using SkyNet.Formats;

namespace SkyNet.Game.Menu;

/// <summary>A frame of DOS's lit-button art (a .CFA such as MAIN1BTN.CFA) and its top-left in DOS
/// pixels: what DOS draws over a button when it is the chosen one.</summary>
public readonly record struct LitArt(string Art, int Frame, Point At);

/// <summary>A baked button of a menu image: its DOS rectangle on the 320 × 200 screen and what it does.</summary>
public sealed class MenuItem(string id, Rectangle rect)
{
    public string Id { get; } = id;
    public Rectangle Rect { get; set; } = rect;
    public bool Enabled { get; set; } = true;

    /// <summary>DOS's lit frame of the button; null: a soft highlight over <see cref="Rect"/>.</summary>
    public LitArt? Lit { get; init; }

    /// <summary>Accept or a click.</summary>
    public Action? Activate { get; init; }

    /// <summary>Left / right on the item (sliders, LOW / MED / HIGH rows); null: left / right move the highlight.</summary>
    public Action<int>? Adjust { get; init; }

    /// <summary>A click at a fraction 0..1 across the item (sliders).</summary>
    public Action<float>? ClickAt { get; init; }

    public bool Contains(Vector2 dos) => dos.X >= Rect.Left && dos.X < Rect.Right && dos.Y >= Rect.Top && dos.Y < Rect.Bottom;

    public Vector2 Centre => new(Rect.X + Rect.Width * 0.5f, Rect.Y + Rect.Height * 0.5f);
}

/// <summary>
/// One menu screen: its items, the highlighted one, and how it draws. Navigation is the same on
/// every screen: the arrows (or the d-pad) move the highlight to the nearest item that way, an item
/// with <see cref="MenuItem.Adjust"/> takes left / right itself, Accept or a click activates, the
/// pointer highlights what it is over, Back steps out.
/// </summary>
public abstract class MenuScreen
{
    private MenuSystem? _menu;

    public MenuSystem Menu => _menu ?? throw new InvalidOperationException("The screen is not open.");

    public List<MenuItem> Items { get; } = [];

    /// <summary>The highlighted item, -1 for none.</summary>
    public int Highlight { get; set; }

    public MenuItem? Highlighted => Highlight >= 0 && Highlight < Items.Count ? Items[Highlight] : null;

    /// <summary>The screen's name in logs and tests.</summary>
    public abstract string Name { get; }

    /// <summary>Is the game frozen under it (every in-game screen)?</summary>
    public virtual bool Pauses => true;

    /// <summary>A screen over the game view (the game draws first, the menu darkens it).</summary>
    public virtual bool OverGame => Menu.InGame;

    internal void Attach(MenuSystem menu) => _menu = menu;

    /// <summary>The screen came up (or back to the top).</summary>
    public virtual void Opened()
    {
    }

    /// <summary>The screen left the stack (popped, or the menus closed or replaced).</summary>
    public virtual void Closed()
    {
    }

    /// <summary>The screen takes typed text (SDL's text input stays on while it is up).</summary>
    public virtual bool TakesText => false;

    /// <summary>Time passed with the screen on top (a countdown); called before its input.</summary>
    public virtual void Tick(float seconds)
    {
    }

    /// <summary>A box that keeps the screen below it in view (KEEP THESE SETTINGS? over DISPLAY).</summary>
    public virtual bool ShowsBelow => false;

    /// <summary>Takes the frame's input first (rebinding); true when it used it.</summary>
    public virtual bool Intercept(in MenuInput input) => false;

    /// <summary>Back / Esc: by default one screen back.</summary>
    public virtual void Back() => Menu.Pop();

    public abstract void Draw(MenuPainter painter);

    /// <summary>Where this screen's DOS pixels go in the window (<paramref name="window"/>): the
    /// stretched screen, or a dialog's own scaled frame.</summary>
    public virtual MenuFrame FrameIn(MenuFrame window) => window;

    public MenuItem Add(MenuItem item)
    {
        Items.Add(item);
        return item;
    }

    public MenuItem? Find(string id) => Items.FirstOrDefault(i => i.Id == id);

    public void Select(string id) => Highlight = Items.FindIndex(i => i.Id == id);

    /// <summary>This frame's pointer in DOS pixels (as <see cref="MenuItem.Rect"/>), null when it did not move.</summary>
    protected Vector2? PointerAt { get; private set; }

    internal void Update(in MenuInput input, MenuFrame frame)
    {
        PointerAt = input.Pointer is { } at ? FrameIn(frame).ToDos(at) : null;
        if (Intercept(input))
        {
            return;
        }

        if (input.Pointer is { } p)
        {
            var dos = FrameIn(frame).ToDos(p);
            var hit = Items.FindIndex(i => i.Enabled && i.Contains(dos));
            if (hit >= 0)
            {
                Highlight = hit;
                if (input.Click)
                {
                    var item = Items[hit];
                    if (item.ClickAt != null)
                    {
                        item.ClickAt(Math.Clamp((dos.X - item.Rect.X) / Math.Max(1, item.Rect.Width), 0f, 1f));
                        Menu.Clicked();
                    }
                    else
                    {
                        Fire(item);
                    }

                    return;
                }
            }
        }

        if (input.Back)
        {
            Back();
            return;
        }

        if (input.Accept && Highlighted is { Enabled: true } current)
        {
            Fire(current);
            return;
        }

        var dir = input.Left ? new Vector2(-1, 0) : input.Right ? new Vector2(1, 0) : input.Up ? new Vector2(0, -1) : input.Down ? new Vector2(0, 1) : Vector2.Zero;
        if (dir == Vector2.Zero)
        {
            return;
        }

        if (dir.Y == 0 && Highlighted is { Enabled: true, Adjust: { } adjust })
        {
            adjust((int)dir.X);
            return;
        }

        Move(dir);
    }

    private void Fire(MenuItem item)
    {
        Menu.Clicked();
        item.Activate?.Invoke();
    }

    /// <summary>Moves the highlight to the nearest enabled item in <paramref name="dir"/>; past the
    /// edge it wraps to the farthest one the other way.</summary>
    public void Move(Vector2 dir)
    {
        if (Highlighted is not { } from)
        {
            Highlight = Items.FindIndex(i => i.Enabled);
            return;
        }

        var best = -1;
        var bestScore = float.MaxValue;
        var far = -1;
        var farScore = float.MinValue;
        for (var i = 0; i < Items.Count; i++)
        {
            if (i == Highlight || !Items[i].Enabled)
            {
                continue;
            }

            var d = Items[i].Centre - from.Centre;
            var along = Vector2.Dot(d, dir);
            var across = MathF.Abs(dir.X != 0 ? d.Y : d.X);
            if (along > 0.5f)
            {
                var score = along + across * 2f;
                if (score < bestScore)
                {
                    (best, bestScore) = (i, score);
                }
            }
            else if (along < -0.5f)
            {
                var score = -along - across * 2f;
                if (score > farScore)
                {
                    (far, farScore) = (i, score);
                }
            }
        }

        if (best >= 0)
        {
            Highlight = best;
        }
        else if (far >= 0)
        {
            Highlight = far;
        }
    }
}

/// <summary>
/// The menus: a stack of <see cref="MenuScreen"/>s fed one <see cref="MenuInput"/> a frame and
/// drawn on the canvas over the game (or over the title). Everything the menus decide goes out
/// through <see cref="IMenuActions"/>; settings are saved through <see cref="SettingsStore"/>.
/// No GPU until <see cref="Draw"/>.
/// </summary>
public sealed class MenuSystem
{
    private readonly List<MenuScreen> _stack = [];
    private float _toastLeft;
    private Look _look;

    public MenuSystem(IMenuActions actions, MenuSettings settings, MenuContext? context = null, string? settingsPath = null)
    {
        Actions = actions;
        Settings = settings;
        Context = context ?? new MenuContext();
        SettingsPath = settingsPath;
        _look = settings.Look;
    }

    public IMenuActions Actions { get; }
    public MenuSettings Settings { get; }
    public MenuContext Context { get; }

    /// <summary>Where settings are saved (null: not saved, tests).</summary>
    public string? SettingsPath { get; }

    /// <summary>The last save's result.</summary>
    public SaveResult? LastSave { get; private set; }

    /// <summary>The menus belong to a running game (the Esc menu, the RESTART box) rather than the title.</summary>
    public bool InGame { get; private set; }

    public MenuScreen? Current => _stack.Count > 0 ? _stack[^1] : null;

    public bool IsOpen => _stack.Count > 0;

    /// <summary>The game should not tick (a screen that pauses is up).</summary>
    public bool PausesGame => Current is { Pauses: true };

    /// <summary>The top screen takes typed text (<see cref="MenuScreen.TakesText"/>).</summary>
    public bool TakesText => Current is { TakesText: true };

    /// <summary>A short line under the menu ("NOT AVAILABLE"), for a few seconds.</summary>
    public string? Toast { get; private set; }

    public IReadOnlyList<MenuScreen> Stack => _stack;

    /// <summary>The title menu (MAIN1.IMG over the title picture).</summary>
    public void OpenTitle()
    {
        InGame = false;
        Replace(new MainMenuScreen());
        OfferHiresNotice();
    }

    /// <summary>HI-RES TEXTURES over the top screen when it is due (<see cref="HiresNotice.Due"/>).</summary>
    public void OfferHiresNotice()
    {
        if (Current is not HiresNotice && HiresNotice.Due(Settings, Context))
        {
            Push(new HiresNotice());
        }
    }

    /// <summary>The in-game Esc menu (MAIN2.IMG over the darkened game); pauses.</summary>
    public void OpenPause()
    {
        InGame = true;
        Replace(new MainMenuScreen());
    }

    /// <summary>The mission screen: BEGIN / BRIEFING / TACTICAL / STATISTICS.</summary>
    public MissionScreen OpenMission(MissionBriefing briefing)
    {
        InGame = true;
        var screen = new MissionScreen(briefing);
        Replace(screen);
        return screen;
    }

    /// <summary>The network screens over the title, as NEW GAME → MULTI-PLAYER left them: the host
    /// screen, or the join screen (<paramref name="join"/>). Where a network game returns to.</summary>
    public void OpenNetwork(bool join)
    {
        InGame = false;
        Replace(new MainMenuScreen());
        Push(new NewGameScreen());
        Push(new MultiplayerScreen());
        Push(join ? new NetJoinScreen() : new NetHostScreen());
    }

    /// <summary>The RESTART MISSION? YES / NO box over the game.</summary>
    public void OpenRestart()
    {
        InGame = true;
        Replace(ConfirmBox.Restart());
    }

    public void Close()
    {
        CloseAll();
        Toast = null;
    }

    public void Push(MenuScreen screen)
    {
        screen.Attach(this);
        _stack.Add(screen);
        screen.Opened();
        if (screen.Highlight >= screen.Items.Count || screen.Highlight >= 0 && !screen.Items[screen.Highlight].Enabled)
        {
            screen.Highlight = screen.Items.FindIndex(i => i.Enabled);
        }
    }

    /// <summary>One screen back; off the in-game menu's last screen the game resumes.</summary>
    public void Pop()
    {
        if (_stack.Count == 0)
        {
            return;
        }

        var top = _stack[^1];
        _stack.RemoveAt(_stack.Count - 1);
        top.Closed();
        if (_stack.Count > 0)
        {
            _stack[^1].Opened();
        }
        else if (InGame)
        {
            Actions.Resume();
        }
    }

    private void Replace(MenuScreen screen)
    {
        CloseAll();
        Push(screen);
    }

    private void CloseAll()
    {
        var gone = _stack.ToArray();
        _stack.Clear();
        for (var i = gone.Length - 1; i >= 0; i--)
        {
            gone[i].Closed();
        }
    }

    /// <summary>Feeds one frame of input to the top screen.</summary>
    public void Update(in MenuInput input, MenuFrame frame, float seconds = 0f)
    {
        if (Toast != null && (_toastLeft -= seconds) <= 0f)
        {
            Toast = null;
        }

        Current?.Tick(seconds);
        Current?.Update(input, frame);
    }

    /// <summary>Queues the menus on the canvas (call after the game's own canvas, before Present).</summary>
    public void Draw(MenuPainter painter)
    {
        if (Current == null)
        {
            return;
        }

        // A dialog draws over the full screen it came from; a dialog it opened from another dialog
        // (DETAIL from OPTIONS) hides that one, as the Godot port shows one panel at a time.
        var from = Math.Max(0, Math.Min(_stack.FindLastIndex(s => !s.IsDialog()), _stack.Count - 1));
        var top = _stack.Count - 1;
        List<int> shown = [from];
        if (top - 1 > from && _stack[top].ShowsBelow)
        {
            shown.Add(top - 1);
        }

        if (top > from)
        {
            shown.Add(top);
        }

        foreach (var i in shown)
        {
            painter.Use(_stack[i].FrameIn(painter.Screen));
            _stack[i].Draw(painter);
        }

        painter.Use(null);

        if (Toast is { } t)
        {
            painter.Toast(t);
        }
    }

    public void ShowToast(string text, float seconds = 2.5f)
    {
        Toast = text;
        _toastLeft = seconds;
    }

    /// <summary>A setting changed for a trial (a display mode before KEEP THESE SETTINGS?): handed
    /// to the game, not saved.</summary>
    public void Try() => Actions.SettingsChanged(Settings);

    internal void Clicked() => Actions.Sound("BUTTON1.RAW");

    /// <summary>A setting changed: saved (when there is a path) and handed to the game.</summary>
    public void Changed()
    {
        if (SettingsPath != null)
        {
            LastSave = SettingsStore.Save(Settings, SettingsPath);
        }

        Actions.SettingsChanged(Settings);
        // DISPLAY's LOOK turned to ENHANCED: the hi-res notice, when due.
        var turnedEnhanced = Settings.Look == Look.Enhanced && _look != Look.Enhanced;
        _look = Settings.Look;
        if (turnedEnhanced)
        {
            OfferHiresNotice();
        }
    }

    // The menus' own actions, closing first where the game takes over.
    internal void StartGame(GameKind game, int mission, Campaign.Pilot pilot)
    {
        Close();
        Actions.StartGame(game, mission, pilot);
    }

    internal void Resume()
    {
        Close();
        Actions.Resume();
    }

    internal void BeginMission()
    {
        Close();
        Actions.BeginMission();
    }

    internal void RestartMission()
    {
        Close();
        Actions.RestartMission();
    }

    internal void QuitToTitle()
    {
        Close();
        Actions.QuitToTitle();
    }
}

internal static class MenuScreenExtensions
{
    /// <summary>Dialogs (panels and boxes) draw over the screen below them; full screens hide it.</summary>
    public static bool IsDialog(this MenuScreen s) => s is PanelScreen;
}
