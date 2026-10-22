using System.Globalization;
using RetroEng.Loop;
using RetroEng.Platform;
using SkyNet.Game.Automap;
using SkyNet.Game.Player;
using SkyNet.Game.Weapons;

namespace SkyNet.Game.Campaign;

/// <summary>
/// The game's actions (their ids are the <see cref="TickInput"/> bits) and what one tick's input
/// does to a <see cref="GameSession"/>: the end screen skipped, noclip toggled, the weapon keys read,
/// the session ticked. The app's loop, the demo player and the tests all go through
/// <see cref="Tick"/>, so a recorded input replays the very tick the player had.
/// </summary>
public sealed class CampaignControls
{
    private CampaignControls(ActionSet a)
    {
        Actions = a;
        Forward = a.Add("Forward", "W", "Up");
        Back = a.Add("Back", "S", "Down");
        Left = a.Add("Strafe left", "A");
        Right = a.Add("Strafe right", "D");
        Run = a.Add("Run", "Left Shift", "Right Shift");
        Jump = a.Add("Jump", "Space");
        Crouch = a.Add("Crouch", "C");
        TurnLeft = a.Add("Turn left", "Left");
        TurnRight = a.Add("Turn right", "Right");
        LookUp = a.Add("Look up", "PageUp");
        LookDown = a.Add("Look down", "PageDown");
        Slide = a.Add("Slide", "Left Alt");
        Use = a.Add("Use", "E");
        Fire = a.Add("Fire", "Left mouse", "Left Ctrl");
        Throw = a.Add("Throw", "Right mouse");
        WeaponKeys = [.. Enumerable.Range(1, 9).Select(k => a.Add($"Weapon {k}", k.ToString(CultureInfo.InvariantCulture)))];
        PreviousWeapon = a.Add("Previous weapon", ";", "Wheel up");
        NextWeapon = a.Add("Next weapon", "'", "Wheel down");
        ThrownKeys = [.. Enumerable.Range(1, 5).Select(k => a.Add($"Thrown item {k}", $"F{k}"))];
        // No default key: the console's noclip, or a key given by bind (bind f +noclip, bind f noclip).
        Noclip = a.Add("Noclip");
        QuickSave = a.Add("Quick save", "F6");
        QuickLoad = a.Add("Quick load", "F7");
        Confirm = a.Add("Confirm", "Return");
        Pause = a.Add("Menu", "Escape");
        // Last, so the actions before it keep the ids old demos recorded.
        Automap = a.Add("Automap", "Tab");
        // The automap's own keys (DOS reads the arrows and +/- scancodes in it, 0x13dd70).
        AutomapMoves = (a.Add("Automap turn left", "Left"), a.Add("Automap turn right", "Right"),
            a.Add("Automap tilt up", "Up"), a.Add("Automap tilt down", "Down"),
            a.Add("Automap zoom in", "Keypad +", "="), a.Add("Automap zoom out", "Keypad -", "-"));
        a.Walk = new WalkActions(Forward, Back, Left, Right, Run);
        a.Pause = Pause;
        a.Tap = Fire;
        // The gamepad's defaults (Xbox labels; the sticks walk and look, Start is Menu): an FPS
        // scheme. A also skips the end screen; Back quick-saves; R3 opens the automap; the d-pad picks thrown items 1-4 (F1-F4).
        a.Pad(Fire, PadButton.RightTrigger).Pad(Throw, PadButton.LeftTrigger)
            .Pad(Jump, PadButton.South).Pad(Confirm, PadButton.South).Pad(Crouch, PadButton.East).Pad(Use, PadButton.West)
            .Pad(NextWeapon, PadButton.North, PadButton.RightShoulder).Pad(PreviousWeapon, PadButton.LeftShoulder)
            .Pad(Run, PadButton.LeftStick).Pad(QuickSave, PadButton.Back).Pad(Automap, PadButton.RightStick)
            .Pad(ThrownKeys[0], PadButton.DpadUp).Pad(ThrownKeys[1], PadButton.DpadRight)
            .Pad(ThrownKeys[2], PadButton.DpadDown).Pad(ThrownKeys[3], PadButton.DpadLeft)
            .Pad(AutomapMoves.TurnLeft, PadButton.DpadLeft).Pad(AutomapMoves.TurnRight, PadButton.DpadRight)
            .Pad(AutomapMoves.TiltUp, PadButton.DpadUp).Pad(AutomapMoves.TiltDown, PadButton.DpadDown)
            .Pad(AutomapMoves.ZoomIn, PadButton.RightShoulder).Pad(AutomapMoves.ZoomOut, PadButton.LeftShoulder);
        // The on-screen buttons (touch screens): columns from the right edge, rows from the bottom.
        // No automap button: the screen's space goes to the fight.
        a.AddTouch(Fire, "FIRE", 0, 0).AddTouch(Jump, "JUMP", 1, 0)
            .AddTouch(Use, "USE", 0, 1).AddTouch(Throw, "ALT", 1, 1)
            .AddTouch(NextWeapon, "WPN+", 0, 2).AddTouch(PreviousWeapon, "WPN-", 1, 2);
        Player = new PlayerActions(Forward, Back, Left, Right, Run, Jump, Crouch, TurnLeft, TurnRight, LookUp, LookDown, Slide);
    }

    /// <summary>The game's actions, registered in their fixed order.</summary>
    public static CampaignControls Create() => new(new ActionSet());

    public ActionSet Actions { get; }
    public int Forward { get; }
    public int Back { get; }
    public int Left { get; }
    public int Right { get; }
    public int Run { get; }
    public int Jump { get; }
    public int Crouch { get; }
    public int TurnLeft { get; }
    public int TurnRight { get; }
    public int LookUp { get; }
    public int LookDown { get; }
    public int Slide { get; }
    public int Use { get; }
    public int Fire { get; }
    public int Throw { get; }
    public int[] WeaponKeys { get; }
    public int PreviousWeapon { get; }
    public int NextWeapon { get; }
    public int[] ThrownKeys { get; }
    public int Noclip { get; }
    public int QuickSave { get; }
    public int QuickLoad { get; }
    public int Confirm { get; }
    public int Pause { get; }
    /// <summary>The automap (CONTROLS slot 17, DOS's Tab).</summary>
    public int Automap { get; }
    /// <summary>The automap's turn, tilt and zoom actions (the d-pad and the shoulders on a pad).</summary>
    internal (int TurnLeft, int TurnRight, int TiltUp, int TiltDown, int ZoomIn, int ZoomOut) AutomapMoves { get; }
    public PlayerActions Player { get; }

    /// <summary>The actions' names by id (a demo's header).</summary>
    public string[] Names => [.. Enumerable.Range(0, Actions.Count).Select(Actions.Name)];

    /// <summary>The automap's keys held, by <paramref name="isDown"/> of an action.</summary>
    public AutomapKeys AutomapHeld(Func<int, bool> isDown)
    {
        var (turnLeft, turnRight, up, down, zoomIn, zoomOut) = AutomapMoves;
        (int Action, AutomapKeys Key)[] map =
        [
            (turnLeft, AutomapKeys.TurnLeft), (turnRight, AutomapKeys.TurnRight), (up, AutomapKeys.TiltUp),
            (down, AutomapKeys.TiltDown), (zoomIn, AutomapKeys.ZoomIn), (zoomOut, AutomapKeys.ZoomOut),
        ];
        return map.Where(m => isDown(m.Action)).Aggregate(AutomapKeys.None, (keys, m) => keys | m.Key);
    }

    /// <summary>The weapons' input of a tick. A press counts even when it was let go before the
    /// tick (a quick click in a slow frame, a script's key step), as DOS's key handler latches it.</summary>
    public WeaponInput WeaponInput(in TickInput tick)
    {
        var held = tick;
        var select = Array.FindIndex(WeaponKeys, k => held.WasPressed(k));
        var thrown = Array.FindIndex(ThrownKeys, k => held.WasPressed(k));
        var cycle = (tick.WasPressed(NextWeapon) ? 1 : 0) - (tick.WasPressed(PreviousWeapon) ? 1 : 0);
        return new WeaponInput(tick.IsDown(Fire) || tick.WasPressed(Fire), tick.IsDown(Throw) || tick.WasPressed(Throw), tick.IsDown(Run),
            select, thrown >= 0 ? Inventory.ThrowKeys[thrown] : -1, cycle);
    }

    /// <summary>One tick of <paramref name="session"/> from <paramref name="tick"/> (with
    /// <paramref name="weapons"/> as <see cref="WeaponInput(in TickInput)"/> made it, or changed by a
    /// scripted run).</summary>
    public void Tick(GameSession session, in TickInput tick, in WeaponInput weapons)
    {
        if (session.Phase != SessionPhase.Playing && (tick.WasPressed(Confirm) || tick.WasPressed(Use)))
        {
            session.SkipEndScreen();
        }

        if (tick.WasPressed(Noclip))
        {
            var player = session.Player;
            player.Noclip = !player.Noclip;
            player.Velocity = System.Numerics.Vector3.Zero;
            session.Logger?.Invoke(player.Noclip ? "Noclip on" : "Noclip off");
        }

        session.Tick(tick, Player, weapons, tick.WasPressed(Use));
    }

    /// <summary>One tick from the input alone.</summary>
    public void Tick(GameSession session, in TickInput tick) => Tick(session, tick, WeaponInput(tick));
}
