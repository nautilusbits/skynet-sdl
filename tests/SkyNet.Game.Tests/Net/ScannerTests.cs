using System.Drawing;
using System.Numerics;
using SkyNet.Formats;
using SkyNet.Game.Net;
using SkyNet.Game.Tests.Behaviour;
using SkyNet.Game.Weapons;

namespace SkyNet.Game.Tests.Net;

/// <summary>DOS's motion detector (SKYNET.EXE v1.01 FUN_0011ff00): a top-down plot on the scanner's
/// own screen in WEAPON13.CFA, and MOTLOOP2.RAW once a sweep.</summary>
public class ScannerTests
{
    // The scanner's screen in the low frame's pixels (table 0x38504): the holder at (39, 32).
    private static readonly Point Origin = new(39, 32);

    private static DmAvatar At(int id, string name, Vector3 feet, int flags = DmProtocol.FlagMoving, DmClass cls = DmClass.Human)
    {
        var a = new DmAvatar(id, name, cls);
        a.SpawnAt(feet, 0f);
        a.ApplyPose(feet, 0f, 0f, flags);
        return a;
    }

    [Fact]
    public void A_mover_is_plotted_by_its_bearing_from_the_holder()
    {
        // Yaw 0 looks down −Z: 1280 ahead is 9 pixels up (0.9 · 1280 / 128), 1280 right 10 right.
        var ahead = At(2, "ahead", new Vector3(0f, 0f, -1280f));
        var right = At(3, "right", new Vector3(1280f, 0f, 0f));

        var plot = MotionDetector.Scan([ahead, right], Vector3.Zero, 0f);

        Assert.Equal([new Point(39, 23), new Point(49, 32)], plot.Blips.Select(b => b.At));
    }

    [Fact]
    public void The_plot_turns_with_the_holder()
    {
        // Yaw π/2 looks down −X: a body there is straight ahead.
        var ahead = At(2, "ahead", new Vector3(-1280f, 0f, 0f));

        var plot = MotionDetector.Scan([ahead], Vector3.Zero, MathF.PI / 2f);

        Assert.Equal(new Point(39, 23), Assert.Single(plot.Blips).At);
    }

    [Fact]
    public void Only_movers_shooters_drivers_and_machines_show()
    {
        // 0x1200f1: flag 0x20 (driving), 0x10 (moving or firing) or 0x400 (a machine).
        var still = At(2, "still", new Vector3(0f, 0f, -640f), flags: 0);
        var firing = At(3, "firing", new Vector3(0f, 0f, -768f), flags: DmProtocol.FlagFiring);
        var driving = At(4, "driving", new Vector3(0f, 0f, -896f), flags: (int)SkyNet.Game.Vehicles.VehicleMode.Jeep << DmProtocol.VehicleShift);
        var machine = At(5, "machine", new Vector3(0f, 0f, -1024f), flags: 0, cls: DmClass.Terminator);
        var dead = At(6, "dead", new Vector3(0f, 0f, -1152f));
        dead.Die();

        var plot = MotionDetector.Scan([still, firing, driving, machine, dead], Vector3.Zero, 0f);

        // 27, 26 and 25 rows: 768, 896 and 1024 ahead; the one standing at 640 (row 28) is not there.
        Assert.Equal([27, 26, 25], plot.Blips.Select(b => b.At.Y));
    }

    [Fact]
    public void A_still_player_with_a_scanner_in_hand_shows()
    {
        var holder = At(2, "holder", new Vector3(0f, 0f, -640f), flags: DmProtocol.FlagScanner);

        Assert.Single(MotionDetector.Scan([holder], Vector3.Zero, 0f).Blips);
    }

    [Fact]
    public void The_others_scanners_beep_where_they_are_once_a_sweep()
    {
        // FUN_00122e41 sends the sweep's MOTLOOP2.RAW to the others: heard at the holder.
        var holder = At(2, "holder", new Vector3(100f, 0f, 0f), flags: DmProtocol.FlagScanner);
        var other = At(3, "other", new Vector3(200f, 0f, 0f));
        var beeps = new ScannerBeeps();
        const float dt = 1f / 60f;
        var heard = new List<int>();
        for (var i = 0; i < 60; i++)
        {
            heard.AddRange(beeps.Tick([holder, other], dt).Select(a => a.Id));
        }

        Assert.Equal([2, 2, 2], heard);

        // Put away and taken again: a new sweep, a beep at once.
        holder.ApplyPose(holder.Position, 0f, 0f, 0);
        Assert.Empty(beeps.Tick([holder], dt));
        holder.ApplyPose(holder.Position, 0f, 0f, DmProtocol.FlagScanner);
        Assert.Equal([2], beeps.Tick([holder], dt).Select(a => a.Id));
    }

    [Fact]
    public void The_screen_clips_the_plot()
    {
        // The screen is x 19..59, y 12..33: 2844 units ahead, 2600 aside and two pixels behind.
        var far = At(2, "far", new Vector3(0f, 0f, -3000f));
        var wide = At(3, "wide", new Vector3(2700f, 0f, -500f));
        var behind = At(4, "behind", new Vector3(0f, 0f, 200f));
        var tooFarBehind = At(5, "back", new Vector3(0f, 0f, 400f));

        var plot = MotionDetector.Scan([far, wide, behind, tooFarBehind], Vector3.Zero, 0f);

        Assert.Equal(new Point(39, 33), Assert.Single(plot.Blips).At);
    }

    [Fact]
    public void The_one_nearest_the_line_ahead_is_named()
    {
        // 0x120134: |x| ≤ ahead, the smallest |x| wins, then the nearest.
        var offLine = At(2, "offline", new Vector3(300f, 0f, -1000f));
        var onLine = At(3, "online", new Vector3(20f, 0f, -2000f));
        var behind = At(4, "behind", new Vector3(0f, 0f, 100f));

        var plot = MotionDetector.Scan([offLine, onLine, behind], Vector3.Zero, 0f);

        Assert.Equal("ONLINE", plot.Target);
        Assert.Equal([false, true, false], plot.Blips.Select(b => b.Target));
    }

    [Fact]
    public void Nobody_ahead_names_nobody()
    {
        var aside = At(2, "aside", new Vector3(1000f, 0f, -500f));

        var plot = MotionDetector.Scan([aside], Vector3.Zero, 0f);

        Assert.Null(plot.Target);
        Assert.False(Assert.Single(plot.Blips).Target);
    }

    [Fact]
    public void The_sweep_beeps_as_it_starts_and_once_a_cycle()
    {
        // Record 13 animates at 18 frames a second over WEAPON13.CFA's 7 frames: a beep each 7/18 s.
        var sweep = new ScannerSweep();
        const float dt = 1f / 60f;
        var beeps = new List<int>();
        for (var i = 0; i < 60; i++)
        {
            if (sweep.Tick(dt))
            {
                beeps.Add(i);
            }
        }

        Assert.Equal(3, beeps.Count);
        Assert.Equal(0, beeps[0]);
        Assert.Equal(MotionDetector.SweepSeconds, (beeps[1] - beeps[0]) * dt, 0.02f);

        sweep.Reset();
        Assert.True(sweep.Tick(dt));
    }

    [Fact]
    public void The_scanner_animates_in_hand_without_a_shot()
    {
        var a = new ViewModelAnimation();
        const float dt = 1f / 60f;
        var seen = new HashSet<int>();
        for (var i = 0; i < 30; i++)
        {
            a.Tick(dt, WeaponTable.MotionDetector, MotionDetector.SweepFrames, 1f);
            seen.Add(a.Frame);
        }

        Assert.Equal(Enumerable.Range(0, MotionDetector.SweepFrames), seen.Order());
    }

    [BehaviourDataFact]
    public void The_screen_and_the_sweep_are_weapon13_cfa()
    {
        var (path, variant) = BehaviourData.Data.FrontArchive("MDMDIMGS.BSA");
        using var bsa = BsaArchive.Open(path, variant);
        var cfa = CfaFile.Parse(bsa.Read("WEAPON13.CFA")!)!;
        Assert.Equal(MotionDetector.SweepFrames, cfa.Frames.Length);

        // The green screen fills the clip rectangle; outside it, the casing.
        var f = cfa.Frames[0];
        var screen = MotionDetector.Screen;
        bool Green(int x, int y) => f[y * cfa.Width + x] is >= 0xb0 and <= 0xbf;
        Assert.True(Green(screen.Left, screen.Top) && Green(screen.Right - 1, screen.Bottom - 1));
        Assert.False(Green(screen.Left - 1, screen.Top) || Green(screen.Right, screen.Bottom - 1));
        Assert.True(screen.Contains(Origin));
    }
}
