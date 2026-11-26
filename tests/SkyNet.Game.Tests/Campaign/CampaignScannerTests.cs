using System.Drawing;
using System.Numerics;
using SkyNet.Formats;
using SkyNet.Game.Campaign;
using SkyNet.Game.Net;
using SkyNet.Game.Tests.Behaviour;
using SkyNet.Game.Weapons;

namespace SkyNet.Game.Tests.Campaign;

/// <summary>The motion detector (weapon record 13) in the campaign: owned from the start, key 1
/// toggles it with the pipe, and in hand it plots the robots DOS's actor sweep lists
/// (FUN_0011ff00's single-player branch).</summary>
public class CampaignScannerTests
{
    private static readonly DateTime When = new(2026, 11, 26, 12, 0, 0);

    [Fact]
    public void The_skynet_soldier_starts_with_the_scanner_on_key_1()
    {
        var inv = new Inventory(WeaponTable.For(GameKind.SkyNet));
        Assert.True(inv.Owns(WeaponTable.MotionDetector));

        // Key 1's group is [0, 13]: the pipe, then the scanner, then the pipe again.
        Assert.True(inv.SelectGroup(0));
        Assert.Equal(WeaponTable.Pipe, inv.Primary);
        Assert.True(inv.SelectGroup(0));
        Assert.Equal(WeaponTable.MotionDetector, inv.Primary);
        Assert.True(inv.SelectGroup(0));
        Assert.Equal(WeaponTable.Pipe, inv.Primary);
    }

    [Fact]
    public void The_future_shock_soldier_has_no_scanner()
    {
        var inv = new Inventory(WeaponTable.For(GameKind.FutureShock));
        Assert.False(inv.Owns(WeaponTable.MotionDetector));
    }

    [Fact]
    public void The_plot_rotates_the_robots_into_the_view_and_names_nobody()
    {
        // Facing −X (yaw π/2): ahead is −X, right is −Z.
        var eye = Vector3.Zero;
        var yaw = MathF.PI / 2f;
        var ahead = new Vector3(-1280f, 0f, 0f);  // 1280 × 0.9 = 1152 u ahead → 9 pixels up
        var right = new Vector3(0f, 0f, -640f);   // 640 u right → 5 pixels right
        var behind = new Vector3(500f, 0f, 0f);   // 4 pixels under the holder: off the screen
        var far = new Vector3(0f, 0f, -3000f);    // 23 pixels right: off the screen

        var plot = MotionDetector.Scan([ahead, right, behind, far], eye, yaw);

        Assert.Equal([new Point(39, 23), new Point(39 + 5, 32)], plot.Blips.Select(b => b.At));
        Assert.All(plot.Blips, b => Assert.False(b.Target));
        Assert.Null(plot.Target);
    }

    [BehaviourDataFact]
    public void In_hand_the_scanner_plots_the_robots_round_the_soldier()
    {
        var session = new GameSession(BehaviourData.Data);
        session.StartMission(session.Table.ByNumber(1)!.Value);
        var inv = session.Weapons.Inventory;
        Assert.True(inv.Owns(WeaponTable.MotionDetector));
        Assert.False(session.DetectorActive);
        Assert.True(inv.Select(WeaponTable.MotionDetector));
        Assert.True(session.DetectorActive);

        // 640 u south of a robot, facing it (yaw 0 looks down −Z): a blip (−576 + 64) >> 7 = 4 pixels up.
        var robot = session.Robots!.All.First(r => r.Alive && !r.OnPath);
        session.Player.Spawn(robot.Position + new Vector3(0f, 0f, 640f), 0f);
        var spot = new Point(MotionDetector.Holder.X, MotionDetector.Holder.Y - 4);
        Assert.Contains(spot, session.Scanner().Blips.Select(b => b.At));
        Assert.Null(session.Scanner().Target);

        // Dead (0x2c bit 4), it is off the list.
        robot.TakeDamage(new Damage(1e6f, DamageKind.Direct, robot.Position, true));
        Assert.DoesNotContain(session.Robots.All, r => r.Alive && Vector3.Distance(r.Position, robot.Position) < 1f);
        Assert.DoesNotContain(spot, session.Scanner().Blips.Select(b => b.At));

        // The pipe in hand: no plot.
        Assert.True(inv.Select(WeaponTable.Pipe));
        Assert.False(session.DetectorActive);
    }

    [BehaviourDataFact]
    public void A_saved_game_keeps_the_scanner_in_hand()
    {
        var a = new GameSession(BehaviourData.Data);
        a.StartMission(a.Table.ByNumber(1)!.Value);
        Assert.True(a.Weapons.Inventory.Select(WeaponTable.MotionDetector));
        var save = SaveFile.Read(SaveFile.Write(a.Capture(When)));

        var b = new GameSession(BehaviourData.Data);
        b.Load(save);
        Assert.True(b.Weapons.Inventory.Owns(WeaponTable.MotionDetector));
        Assert.Equal(WeaponTable.MotionDetector, b.Weapons.Inventory.Primary);
        Assert.True(b.DetectorActive);
    }

    [Fact]
    public void The_view_puts_a_blip_one_art_pixel_wide_on_the_scanner()
    {
        // 1280 × 960: an art pixel is ViewModelLayout.Scale screen pixels.
        var s = ViewModelLayout.Scale(1280, 960);
        var gun = new RectangleF(100f, 200f, 72f * s, 82f * s);
        var rect = ScannerView.Blip(new ScannerBlip(new Point(39, 23), false), gun, s);
        Assert.Equal(new RectangleF(100f + 39f * s, 200f + 23f * s, s, s), rect);
    }
}
