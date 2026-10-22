using SkyNet.Game.Automap;
using RetroEng.Platform;
using SkyNet.Game.Campaign;
using SkyNet.Game.Menu;

namespace SkyNet.Game.Tests.Menu;

/// <summary>The gamepad's buttons: the game's defaults, the GAMEPAD BUTTONS screen's rebinding, the
/// settings file, and an action fired by a gamepad event.</summary>
public class GamepadBindingTests : IDisposable
{
    private static readonly MenuFrame Frame = new(1280, 800);
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "skynet-pad-tests-" + Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        if (Directory.Exists(_dir))
        {
            Directory.Delete(_dir, true);
        }
    }

    private static (CampaignControls Controls, Input Input) Game()
    {
        var controls = CampaignControls.Create();
        return (controls, new Input(controls.Actions));
    }

    [Fact]
    public void DefaultsAreAnFpsScheme()
    {
        var (c, input) = Game();
        Assert.Equal(["RT"], input.PadBindings(c.Fire));
        Assert.Equal(["LT"], input.PadBindings(c.Throw));
        Assert.Equal(["A"], input.PadBindings(c.Jump));
        Assert.Equal(["B"], input.PadBindings(c.Crouch));
        Assert.Equal(["X"], input.PadBindings(c.Use));
        Assert.Equal(["L3"], input.PadBindings(c.Run));
        Assert.Equal(["Y", "RB"], input.PadBindings(c.NextWeapon));
        Assert.Equal(["LB"], input.PadBindings(c.PreviousWeapon));
        Assert.Equal(["Back"], input.PadBindings(c.QuickSave));
        Assert.Equal(["D-pad up"], input.PadBindings(c.ThrownKeys[0]));
        Assert.Equal(["D-pad left"], input.PadBindings(c.ThrownKeys[3]));
        Assert.Equal(["Start"], input.PadBindings(c.Pause));
        Assert.Equal(PadSticks.LeftMoves, new MenuSettings().PadSticks);
    }

    [Fact]
    public void AutomapIsTabAndTheRightStick()
    {
        var (c, input) = Game();
        Assert.Equal("Automap", c.Actions.Name(c.Automap));
        Assert.Equal(["Tab"], input.Bindings(c.Automap));
        Assert.Equal(["R3"], input.PadBindings(c.Automap));

        // CONTROLS' AUTOMAP box names it, and it comes last (the demos' action ids stay as they were).
        Assert.Equal("Automap", MenuLayout.DefaultControlActions["automap"]);
        Assert.Equal(c.Actions.Count - 7, c.Automap);
    }

    [Fact]
    public void TheAutomapsKeysAreDossArrowsAndPlusMinus()
    {
        var (c, input) = Game();
        var (turnLeft, turnRight, up, down, zoomIn, zoomOut) = c.AutomapMoves;
        Assert.Equal(["Left"], input.Bindings(turnLeft));
        Assert.Equal(["Right"], input.Bindings(turnRight));
        Assert.Equal(["Up"], input.Bindings(up));
        Assert.Equal(["Down"], input.Bindings(down));
        Assert.Equal(["Keypad +", "="], input.Bindings(zoomIn));
        Assert.Equal(["Keypad -", "-"], input.Bindings(zoomOut));
        Assert.Equal(["D-pad left"], input.PadBindings(turnLeft));
        Assert.Equal(["RB"], input.PadBindings(zoomIn));

        // The held actions → the automap's keys.
        Assert.Equal(AutomapKeys.TurnLeft | AutomapKeys.ZoomOut, c.AutomapHeld(a => a == turnLeft || a == zoomOut));
    }

    [Fact]
    public void APadEventFiresItsAction()
    {
        var (c, input) = Game();
        input.HoldPad(PadButton.RightTrigger, Input.State.Down);
        input.HoldPad(PadButton.South, Input.State.Down);
        var tick = input.Latch();
        Assert.True(c.WeaponInput(tick).Fire);
        Assert.True(tick.IsDown(c.Jump));

        input.HoldPad(PadButton.RightTrigger, Input.State.Up);
        input.HoldPad(PadButton.LeftShoulder, Input.State.Down);
        tick = input.Latch();
        Assert.False(c.WeaponInput(tick).Fire);
        Assert.Equal(-1, c.WeaponInput(tick).Cycle);
    }

    /// <summary>The title → OPTIONS → CONTROLS → GAMEPAD → BUTTONS, over a real <see cref="Input"/>.</summary>
    private static (MenuSystem Menu, PadBindScreen Screen) OpenButtons(Input input)
    {
        var menu = new MenuSystem(new SettingsRecorder(), new MenuSettings(), new MenuContext { Bindings = new InputBindings(input) }, null);
        menu.OpenTitle();
        foreach (var id in new[] { "options", "controls", "gamepad", "buttons" })
        {
            menu.Current!.Select(id);
            menu.Update(new MenuInput { Accept = true }, Frame);
        }

        return (menu, Assert.IsType<PadBindScreen>(menu.Current));
    }

    /// <summary>Highlights the row labelled <paramref name="label"/> as the d-pad would.</summary>
    private static void Walk(MenuSystem menu, PadBindScreen screen, string label)
    {
        var row = screen.Rows.ToList().FindIndex(r => r.Label == label);
        Assert.True(row >= 0, label);
        for (var steps = 0; screen.Selected != row && steps < screen.Rows.Count; steps++)
        {
            menu.Update(new MenuInput { Down = row > screen.Selected, Up = row < screen.Selected }, Frame);
        }

        Assert.Equal(row, screen.Selected);
    }

    [Fact]
    public void TheWheelScrollsTheList()
    {
        var (_, input) = Game();
        var (menu, screen) = OpenButtons(input);
        Assert.True(screen.Rows.Count > PadBindScreen.VisibleRows);

        menu.Update(new MenuInput { Wheel = -1 }, Frame);
        Assert.True(screen.Top > 0);
        menu.Update(new MenuInput { Wheel = 1 }, Frame);
        Assert.Equal(0, screen.Top);
    }

    [Fact]
    public void TheScreenListsEveryActionAndRebindsByPad()
    {
        var (c, input) = Game();
        var (menu, screen) = OpenButtons(input);
        Assert.Equal(["MOVE", "LOOK", "FIRE", "THROW", "JUMP"], screen.Rows.Take(5).Select(r => r.Label));
        Assert.Equal(c.Actions.Count + 2, screen.Rows.Count);
        Assert.Equal(PadBindScreen.RowKind.Fixed, screen.Rows[^1].Kind);                 // MENU: Start
        Assert.Equal("Y, RB", screen.Value(screen.Rows.ToList().FindIndex(r => r.Label == "NEXT WEAPON")));

        // JUMP: A, then Y. Y leaves NEXT WEAPON (RB stays); the settings keep it.
        Walk(menu, screen, "JUMP");
        menu.Update(new MenuInput { Accept = true, PadControl = "A" }, Frame);
        Assert.Equal(PadBindScreen.Waiting, screen.Value(screen.Selected));
        menu.Update(new MenuInput(), Frame);                                             // the opening press's frame
        menu.Update(new MenuInput { PadControl = "Y" }, Frame);
        Assert.Equal(-1, screen.Capturing);
        Assert.Equal(["Y"], input.PadBindings(c.Jump));
        Assert.Equal(["RB"], input.PadBindings(c.NextWeapon));
        Assert.Equal(["Y"], menu.Settings.PadBindings["Jump"]);

        // B binds (not cancels); Start cancels; Left clears.
        menu.Update(new MenuInput { Accept = true }, Frame);
        menu.Update(new MenuInput(), Frame);
        menu.Update(new MenuInput { PadControl = "B", Back = true }, Frame);
        Assert.Equal(["B"], input.PadBindings(c.Jump));
        Assert.Empty(input.PadBindings(c.Crouch));
        Assert.Equal("padbind", menu.Current!.Name);
        menu.Update(new MenuInput { Accept = true }, Frame);
        menu.Update(new MenuInput(), Frame);
        menu.Update(new MenuInput { PadControl = "Start", Back = true }, Frame);
        Assert.Equal(["B"], input.PadBindings(c.Jump));
        menu.Update(new MenuInput { Left = true }, Frame);
        Assert.Empty(input.PadBindings(c.Jump));
        Assert.Empty(menu.Settings.PadBindings["Jump"]);

        // MOVE swaps the sticks; DEFAULT puts everything back.
        Walk(menu, screen, "MOVE");
        menu.Update(new MenuInput { Accept = true }, Frame);
        Assert.Equal(PadSticks.RightMoves, menu.Settings.PadSticks);
        Assert.Equal("RIGHT STICK", screen.Value(0));
        menu.Current!.Select("default");
        menu.Update(new MenuInput { Accept = true }, Frame);
        Assert.Equal(["A"], input.PadBindings(c.Jump));
        Assert.Equal(["B"], input.PadBindings(c.Crouch));
        Assert.Equal(PadRebind.Snapshot(new InputBindings(input)), menu.Settings.PadBindings);
        Assert.Equal(PadSticks.LeftMoves, menu.Settings.PadSticks);
    }

    [Fact]
    public void BindingsRoundTripThroughTheSettingsFile()
    {
        var (c, input) = Game();
        var b = new InputBindings(input);
        Assert.True(PadRebind.Apply(b, c.Fire, "A"));
        Assert.False(PadRebind.Apply(b, c.Use, "Start"));                                // the menu's
        Assert.Equal(["X"], input.PadBindings(c.Use));
        var s = new MenuSettings { PadBindings = PadRebind.Snapshot(b), PadSticks = PadSticks.RightMoves };

        var path = Path.Combine(_dir, "settings.json");
        Assert.Equal(SaveResult.Saved, SettingsStore.Save(s, path));
        var back = SettingsStore.Load(path);
        Assert.Equal(SettingsStore.Text(s), SettingsStore.Text(back));
        Assert.Equal(PadSticks.RightMoves, back.PadSticks);

        // A new run: the saved buttons over the defaults, the sticks on Input.
        var (c2, fresh) = Game();
        PadRebind.Restore(new InputBindings(fresh), back.PadBindings);
        back.ApplyLook(fresh);
        for (var a = 0; a < c2.Actions.Count; a++)
        {
            Assert.Equal(input.PadBindings(a), fresh.PadBindings(a));
        }

        Assert.Equal(["A"], fresh.PadBindings(c2.Fire));
        Assert.Empty(fresh.PadBindings(c2.Jump));
        Assert.Equal(PadSticks.RightMoves, fresh.PadSticks);
    }
}
