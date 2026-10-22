using System.Numerics;
using SkyNet.Formats;
using SkyNet.Game.Behaviour;
using SkyNet.Game.Campaign;
using SkyNet.Game.Enemies;
using SkyNet.Game.Tests.Behaviour;
using SkyNet.Game.Weapons;

namespace SkyNet.Game.Tests.Campaign;

/// <summary>The STATISTICS counters (DOS FUN_00134b38 / FUN_00134be5): counted in play, folded into
/// the career between missions, put back by a restart, kept by a save; and dropped items kept by a
/// save (format 3) while format-2 files still load.</summary>
public class MissionStatsTests
{
    private static readonly DateTime When = new(2026, 10, 10, 12, 34, 0);

    [Fact]
    public void TheCountersFoldIntoTheCareerAndARestartPutsItBack()
    {
        var s = new MissionStats(GameKind.SkyNet);
        Assert.Null(s.Sheet.Mission);
        Assert.Empty(MissionStats.Rows(s.Sheet, null));
        Assert.Equal("STATS ARE NOT YET AVAILABLE.", MissionStats.Header(s.Sheet, null));

        s.Begin(0, restart: false);
        Assert.Equal(new StatCounters(0, 0, 130, 0), s.Mission);
        s.Shot();
        s.Shot();
        s.Shot();
        s.Hit();
        s.Kill();
        Assert.Equal(new StatCounters(3, 1, 130, 1), s.Mission);
        Assert.Equal(new StatCounters(3, 1, 130, 1), s.Sheet.Career); // the page adds the row
        Assert.Equal(33, s.Mission!.Value.HitRatio);
        Assert.Equal(1, s.Mission!.Value.Destroyed);                  // at least 1% once one is killed

        s.Begin(1, restart: false);
        Assert.Equal(new StatCounters(3, 1, 130, 1), s.Career);
        Assert.Equal(new StatCounters(0, 0, 82, 0), s.Mission);
        s.Shot();
        s.Begin(1, restart: true);
        Assert.Equal(new StatCounters(3, 1, 130, 1), s.Career);       // the failed attempt is gone
        Assert.Equal(0, s.Mission!.Value.Shots);

        var rows = MissionStats.Rows(s.Sheet, null);
        Assert.Equal(
            [("SHOTS HIT-RATIO:", "000%"), ("ENEMIES DESTROYED:", "000%"), ("HIT-RATIO TOTAL:", "033%"), ("ENEMIES TOTAL:", "001%")],
            rows);
        Assert.Equal("100%", MissionStats.Percent(new StatCounters(0, 0, 0, 0).Destroyed));

        var back = new MissionStats(GameKind.SkyNet);
        back.Restore(s.Save());
        Assert.Equal(s.Sheet, back.Sheet);
        Assert.Equal(s.CareerAtStart, back.CareerAtStart);
    }

    [BehaviourDataFact]
    public void ShotsHitsAndKillsAreCountedAndThePageReadsStringsPrs()
    {
        var a = new GameSession(BehaviourData.Data);
        a.StartMission(a.Table.ByNumber(1)!.Value);
        Assert.Null(a.BriefingStats.Mission);
        Assert.Equal("STATS ARE NOT YET AVAILABLE.", a.StatsHeader(a.BriefingStats));
        Assert.Equal(new StatCounters(0, 0, 130, 0), a.Stats.Mission);

        // A burst point-blank into a robot: shots and a hit.
        var robot = a.Robots!.All.First(r => r.Alive && !r.Indestructible && !r.Hidden);
        a.Weapons.Inventory.Grant(1);
        a.Weapons.Inventory.Select(1);
        a.Weapons.Inventory.AddAmmo(a.Weapons.Table.Weapons[1].Pool, 50);
        var eye = robot.Centre + new Vector3(0f, 0f, 60f);
        Assert.Equal(FireResult.Fired, a.Weapons.Fire(new Shooter(eye, 0f, 0f)));
        Assert.Equal(1, a.Stats.Mission!.Value.Shots);
        Assert.Equal(1, a.Stats.Mission!.Value.Hits);

        // Killed: one kill.
        if (robot.Alive)
        {
            robot.TakeDamage(new Damage(1e6f, DamageKind.Direct, robot.Position, true));
        }

        Assert.Equal(1, a.Stats.Mission!.Value.Kills);

        // A throw is a shot too.
        var before = a.Stats.Mission!.Value.Shots;
        a.Weapons.Inventory.Grant(16);
        a.Weapons.Inventory.SelectThrown(16);
        a.Weapons.Inventory.AddAmmo(a.Weapons.Table.Weapons[16].Pool, 3);
        if (a.Weapons.Throw(new Shooter(a.Player.EyePosition, 0f, 0f)) == FireResult.Fired)
        {
            Assert.Equal(before + 1, a.Stats.Mission!.Value.Shots);
        }

        // The next mission's briefing shows mission 1's page, its labels from STRINGS.PRS.
        var done = a.Stats.Mission!.Value;
        a.StartMission(a.Table.ByNumber(2)!.Value);
        Assert.Equal(done, a.BriefingStats.Mission);
        Assert.Equal("HERE ARE YOUR STATS:", a.StatsHeader(a.BriefingStats));
        var rows = a.StatsRows(a.BriefingStats);
        Assert.Equal(["SHOTS HIT-RATIO:", "ENEMIES DESTROYED:", "HIT-RATIO TOTAL:", "ENEMIES TOTAL:"], rows.Select(r => r.Label));
        Assert.Equal(MissionStats.Percent(done.HitRatio), rows[0].Value);
        Assert.Equal(done, a.Stats.Career);
        Assert.Equal(new StatCounters(0, 0, 82, 0), a.Stats.Mission);

        // A restart's briefing has no page (DOS clears the row), and the career is kept.
        a.RestartMission();
        Assert.Null(a.BriefingStats.Mission);
        Assert.Equal(done, a.Stats.Career);
    }

    [BehaviourDataFact]
    public void TheStatisticsAndADropSurviveSaveAndLoad()
    {
        var a = new GameSession(BehaviourData.Data);
        a.StartMission(a.Table.ByNumber(1)!.Value);
        a.Stats.Shot();
        a.Stats.Shot();
        a.Stats.Hit();
        a.Stats.Kill();

        // A drop lying on MAP.210 (as a destroyed record leaves one), next to where the soldier stands.
        var (sprite, _) = PickupTable.Items.First();
        var at = a.Player.Position + new Vector3(300f, 0f, 0f);
        var captured = a.Capture(When);
        var maps = captured.Maps.Select(m => m.Map == 210 ? m with { Drops = [new DroppedSprite(-1, sprite, at)], NextDrop = -2 } : m).ToArray();
        var bytes = SaveFile.Write(captured with { Maps = maps });

        var b = new GameSession(BehaviourData.Data);
        b.Load(SaveFile.Read(bytes));
        Assert.Equal(a.Stats.Sheet, b.Stats.Sheet);
        Assert.Equal(a.Stats.Save(), b.Stats.Save());
        Assert.Equal([new DroppedSprite(-1, sprite, at)], b.Drops);
        Assert.Contains(b.PickupsLeft, p => p.Offset == -1 && p.At == at);
        Assert.Equal(bytes, SaveFile.Write(b.Capture(When)));

        // Walked onto, it is taken and gone from the next save.
        b.Player.Spawn(at, 0f);
        b.Tick(default, new SkyNet.Game.Player.PlayerActions(0, 1, 2, 3, 4, 5, 6), new WeaponInput(false, false, false), false);
        Assert.Empty(b.Drops);
        Assert.Empty(b.Capture(When).Maps.Single(m => m.Map == 210).Drops);
    }

    [BehaviourDataFact]
    public void AFormatTwoSaveStillLoads()
    {
        var a = new GameSession(BehaviourData.Data);
        a.StartMission(a.Table.ByNumber(1)!.Value);
        a.Stats.Shot();
        var v2 = SaveFile.Write(a.Capture(When), 2);
        Assert.Equal(2, BitConverter.ToUInt16(v2, 8));
        var read = SaveFile.Read(v2);
        Assert.Null(read.Stats);
        Assert.All(read.Maps, m => Assert.Empty(m.Drops));

        var b = new GameSession(BehaviourData.Data);
        b.Load(read);
        Assert.Equal(210, b.MapNumber);
        Assert.Equal(new StatCounters(0, 0, 130, 0), b.Stats.Mission); // a fresh row for its mission
        Assert.Equal(SaveFile.Version, BitConverter.ToUInt16(SaveFile.Write(b.Capture(When)), 8));
    }

    [Fact]
    public void FormatThreeRoundTripsDropsAndStatisticsAndOlderFormatsLeaveThemOut()
    {
        var map = new SavedMap(214, Empty(), [], [], []) { Drops = [new DroppedSprite(-3, 200 << 7 | 4, new Vector3(1, 2, 3))], NextDrop = -4 };
        var stats = new SavedStats(new StatCounters(10, 4, 82, 2), 1, new StatCounters(50, 20, 130, 60), new StatCounters(50, 20, 130, 60));
        var kit = new InventoryState([true], [10], 0, 0);
        var save = new SaveGame(GameKind.SkyNet, 2, 214, 0, Vector3.Zero, 0f, 0f, false, PlayerMode.Foot, 1000f, 0f, kit, kit, 1000f, 0f,
            new ProgressSnapshot(1, false, false, [0]), null, [map], [], When) { Stats = stats };

        var back = SaveFile.Read(SaveFile.Write(save));
        Assert.Equal(stats, back.Stats);
        Assert.Equal(map.Drops, back.Maps[0].Drops);
        Assert.Equal(-4, back.Maps[0].NextDrop);

        // No row yet (a new game's first briefing) round-trips as none.
        var none = SaveFile.Read(SaveFile.Write(save with { Stats = new SavedStats(null, -1, default, default) }));
        Assert.Null(none.Stats!.Value.Mission);

        foreach (ushort v in new ushort[] { 1, 2 })
        {
            var old = SaveFile.Read(SaveFile.Write(save, v));
            Assert.Null(old.Stats);
            Assert.Empty(old.Maps[0].Drops);
            Assert.Equal(save.Label, old.Label);
        }

        // A drop carrying a record's (positive) id is refused.
        var bad = save with { Maps = [map with { Drops = [new DroppedSprite(5, 0, Vector3.Zero)] }] };
        Assert.Throws<SaveFormatException>(() => SaveFile.Read(SaveFile.Write(bad)));
    }

    private static BehaviourSnapshot Empty() => new([], [], [], [], [], [], [], [], [], [], [], 0f, []);
}
