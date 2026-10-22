using System.Numerics;
using RetroEng.Loop;
using RetroEng.Physics;
using RetroEng.Platform;
using SkyNet.Game.Collision;
using SkyNet.Game.Menu;
using SkyNet.Game.Player;

namespace SkyNet.Game.Tests.Menu;

/// <summary>MOUSE (MOUSE.IMG, DOS's divisor bars and REVERSE VERTICAL) and GAMEPAD: the settings
/// file, the look they give through <see cref="Input"/>, and the screens under CONTROLS.</summary>
public class MouseSettingsTests : IDisposable
{
    private static readonly MenuFrame Frame = new(1280, 800);
    private static readonly PlayerActions Keys = new(Forward: 0, Back: 1, Left: 2, Right: 3, Run: 4, Jump: 5, Crouch: 6);
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "skynet-mouse-tests-" + Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        if (Directory.Exists(_dir))
        {
            Directory.Delete(_dir, true);
        }
    }

    [Fact]
    public void DefaultsAreTodaysFeel()
    {
        var s = new MenuSettings();
        Assert.Equal((3, 3, false), (s.MouseH, s.MouseV, s.MouseInvertY));
        Assert.Equal((1f, 1f), (s.MouseScaleX, s.MouseScaleY));
        Assert.Equal(9, MenuSettings.MouseLit(s.MouseH));                       // DOS: 12 - 3 segments lit
        Assert.Equal((100, 100, false, 15, false), (s.PadH, s.PadV, s.PadInvertY, s.PadDeadZone, s.PadVibration));
        var input = new Input();
        s.ApplyLook(input);
        Assert.Equal((1f, 1f, false, 1f, 1f, false), (input.MouseScaleX, input.MouseScaleY, input.InvertMouse, input.PadScaleX, input.PadScaleY, input.InvertPad));
        Assert.Equal(GamepadMap.DefaultDeadZone, input.PadDeadZone, 5);
    }

    [Fact]
    public void RoundTripsAndRejectsOddValues()
    {
        var s = new MenuSettings { MouseH = 1, MouseV = 11, MouseInvertY = true, PadH = 250, PadV = 50, PadInvertY = true, PadDeadZone = 30, PadVibration = true };
        var path = Path.Combine(_dir, "settings.json");
        Assert.Equal(SaveResult.Saved, SettingsStore.Save(s, path));
        var back = SettingsStore.Load(path);
        Assert.Equal(SettingsStore.Text(s), SettingsStore.Text(back));
        Assert.Equal((1, 11, true, 250, 50, true, 30, true),
            (back.MouseH, back.MouseV, back.MouseInvertY, back.PadH, back.PadV, back.PadInvertY, back.PadDeadZone, back.PadVibration));

        File.WriteAllText(path, $"{{\"format\":\"{SettingsStore.Format}\",\"mouseH\":0,\"mouseV\":12,\"padH\":5,\"padDeadZone\":90}}");
        var odd = SettingsStore.Load(path);
        Assert.Equal((3, 3, 100, 15), (odd.MouseH, odd.MouseV, odd.PadH, odd.PadDeadZone));
        // A file from before the MOUSE screen keeps today's feel.
        File.WriteAllText(path, $"{{\"format\":\"{SettingsStore.Format}\",\"detail\":\"Low\"}}");
        var old = SettingsStore.Load(path);
        Assert.Equal((3, 3, false), (old.MouseH, old.MouseV, old.MouseInvertY));
    }

    /// <summary>The yaw a mouse move of <paramref name="pixels"/> across turns the soldier on foot.</summary>
    private static (float Yaw, float Pitch) Look(MenuSettings s, float x, float y)
    {
        var input = new Input();
        s.ApplyLook(input);
        input.MoveMouse(x, y);
        var player = new PlayerController(new CollisionWorld());
        player.Spawn(Vector3.Zero, 0f);
        player.Noclip = true;
        player.Step(input.Latch(), 1f / 60f, Keys);
        return (player.Yaw, player.Pitch);
    }

    [Fact]
    public void SensitivityScalesTheYawAndPitchPerPixel()
    {
        var (yaw, pitch) = Look(new MenuSettings(), 100f, 10f);
        Assert.Equal(-100f * PlayerController.MouseRadians, yaw, 6);               // 0.176° a pixel, as before
        Assert.Equal(-10f * PlayerController.MouseRadians, pitch, 6);
        // Divisor 1 (all 11 segments lit) is three times the default; 11 (one lit) is 3/11 of it.
        var fast = Look(new MenuSettings { MouseH = 1, MouseV = 11 }, 100f, 10f);
        Assert.Equal(3f * yaw, fast.Yaw, 6);
        Assert.Equal(pitch * 3f / 11f, fast.Pitch, 6);
    }

    [Fact]
    public void ReverseVerticalFlipsThePitchOnly()
    {
        var plain = Look(new MenuSettings(), 50f, 20f);
        var reversed = Look(new MenuSettings { MouseInvertY = true }, 50f, 20f);
        Assert.Equal(plain.Yaw, reversed.Yaw, 6);
        Assert.Equal(-plain.Pitch, reversed.Pitch, 6);
    }

    [Fact]
    public void GamepadLookScalesInvertsAndKeepsItsDeadZone()
    {
        var s = new MenuSettings { PadH = 200, PadV = 50, PadInvertY = true, PadDeadZone = 40 };
        var plain = new Input();
        new MenuSettings().ApplyLook(plain);
        plain.LookPad(new Vector2(3f, 2f));
        var a = plain.Latch();
        var input = new Input();
        s.ApplyLook(input);
        input.LookPad(new Vector2(3f, 2f));
        var b = input.Latch();
        Assert.Equal(2f * a.LookX, b.LookX, 4);
        Assert.Equal(-0.5f * a.LookY, b.LookY, 4);
        // A 30 % tilt looks with the default dead zone (15 %), not inside a 40 % one.
        var tilt = new Vector2(0.3f, 0f);
        Assert.NotEqual(Vector2.Zero, GamepadMap.Look(tilt, 1f));
        Assert.Equal(Vector2.Zero, GamepadMap.Look(tilt, 1f, input.PadDeadZone));
        Assert.Equal(GamepadMap.LookRate.X, GamepadMap.Look(Vector2.UnitX, 1f, input.PadDeadZone).X, 3);
    }

    private static MenuSystem OpenControls(SettingsRecorder log, MenuContext? context = null)
    {
        var menu = new MenuSystem(log, new MenuSettings(), context ?? new MenuContext(), null);
        menu.OpenTitle();
        foreach (var id in new[] { "options", "controls" })
        {
            menu.Current!.Select(id);
            menu.Update(new MenuInput { Accept = true }, Frame);
        }

        Assert.Equal("controls", menu.Current!.Name);
        return menu;
    }

    private static void Press(MenuSystem menu, string id, MenuInput input)
    {
        menu.Current!.Select(id);
        menu.Update(input, Frame);
    }

    [Fact]
    public void MouseOpensFromControlsAndItsBarsStepAndClick()
    {
        var log = new SettingsRecorder();
        var menu = OpenControls(log);
        Press(menu, "mouse", new MenuInput { Accept = true });
        var screen = Assert.IsType<MouseScreen>(menu.Current);
        Assert.Equal(["horizontal", "vertical", "reverse", "exit"], screen.Items.Select(i => i.Id));
        Assert.Equal("horizontal", screen.Highlighted!.Id);

        Press(menu, "horizontal", new MenuInput { Right = true });                 // 9 → 10 lit: divisor 2
        Assert.Equal(2, menu.Settings.MouseH);
        Press(menu, "vertical", new MenuInput { Left = true });                    // 9 → 8 lit: divisor 4
        Assert.Equal(4, menu.Settings.MouseV);
        Press(menu, "horizontal", new MenuInput { Right = true });
        Press(menu, "horizontal", new MenuInput { Right = true });                 // stops at 11 lit
        Assert.Equal(1, menu.Settings.MouseH);
        Assert.Equal(1, MouseScreen.LitAt(0f));
        Assert.Equal(MenuSettings.MouseSteps, MouseScreen.LitAt(1f));
        Press(menu, "reverse", new MenuInput { Accept = true });
        Assert.True(menu.Settings.MouseInvertY);
        Assert.True(log.Applied[^1].MouseInvertY);                                 // applied live
        Press(menu, "exit", new MenuInput { Accept = true });
        Assert.Equal("controls", menu.Current!.Name);
    }

    [Fact]
    public void GamepadOpensFromControlsWithItsRowsAndThePadsName()
    {
        var log = new SettingsRecorder();
        var shakes = new List<int>();
        var menu = OpenControls(log, new MenuContext { GamepadName = () => "Xbox Controller", Rumble = (_, _, ms) => shakes.Add(ms) });
        Press(menu, "gamepad", new MenuInput { Accept = true });
        var screen = Assert.IsType<GamepadScreen>(menu.Current);
        Assert.Equal(["padh", "padv", "invert", "deadzone", "vibration", "buttons", "exit"], screen.Items.Select(i => i.Id));
        Assert.Equal("XBOX CONTROLLER", screen.PadLine);

        Press(menu, "padh", new MenuInput { Right = true });
        Assert.Equal(125, menu.Settings.PadH);
        Press(menu, "padv", new MenuInput { Left = true });
        Assert.Equal(75, menu.Settings.PadV);
        Press(menu, "invert", new MenuInput { Accept = true });
        Assert.True(menu.Settings.PadInvertY);
        Press(menu, "deadzone", new MenuInput { Right = true });
        Assert.Equal(20, menu.Settings.PadDeadZone);
        Assert.Equal("20%", screen.Value("deadzone"));
        Press(menu, "vibration", new MenuInput { Accept = true });
        Assert.True(menu.Settings.PadVibration);
        Assert.Single(shakes);                                                     // turning it on tries it
        Assert.True(log.Applied[^1].PadVibration);
        Press(menu, "exit", new MenuInput { Accept = true });
        Assert.Equal("controls", menu.Current!.Name);

        var none = OpenControls(new SettingsRecorder());
        Press(none, "gamepad", new MenuInput { Accept = true });
        Assert.Equal(GamepadScreen.NoPad, ((GamepadScreen)none.Current!).PadLine);
    }
}
