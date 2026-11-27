using System.Numerics;
using System.Runtime.CompilerServices;
using RetroEng.Loop;
using SkyNet.Formats;
using SkyNet.Game.Behaviour;
using SkyNet.Game.Campaign;
using SkyNet.Game.Enemies;
using SkyNet.Game.Tests.Behaviour;
using SkyNet.Game.Vehicles;
using SkyNet.Game.Weapons;

namespace SkyNet.Game.Tests.Campaign;

/// <summary>Save and load: a mission-1 game saved part way and loaded into a fresh session holds
/// the same state and plays on to MISSION COMPLETE; a damaged, foreign or old file is refused.</summary>
public class SaveGameTests
{
    private static readonly DateTime When = new(2026, 10, 10, 12, 34, 0);

    private static string Here([CallerFilePath] string path = "") => Path.GetDirectoryName(path)!;

    [BehaviourDataFact]
    public void AGameSavedPartWayLoadsTheSameAndPlaysOn()
    {
        var log = new List<string>();
        var a = new GameSession(BehaviourData.Data) { Logger = log.Add };
        a.StartMission(a.Table.ByNumber(1)!.Value);

        // A robot of MAP.210 killed.
        var robot = a.Robots!.All.First(r => r.Alive && r.MarkerOffset >= 0 && !r.Indestructible && r.DormantDistance == 0f);
        robot.TakeDamage(new Damage(1e6f, DamageKind.Direct, robot.Position, true));
        Assert.Contains(robot.MarkerOffset, a.Dead);
        Tick(a, 1f);

        // An item taken: MAP.210's first, else MAP.214's once there.
        var taken = TakeAPickup(a);

        // The bunker gate opened: through it into MAP.214.
        var failures = PlayScript.Parse(["god on", "use 0x07fcb", "expect map 214"]).Run(a, log.Add);
        Assert.True(failures.Count == 0, string.Join("\n", failures.Concat(log)));
        taken ??= TakeAPickup(a);
        Assert.NotNull(taken);
        a.Soldier.God = false;
        a.Soldier.Hurt(120f);
        a.Weapons.Inventory.AddAmmo(0, -7);
        Tick(a, 0.5f);

        Assert.True(a.CanSave);
        var saved = a.Capture(When);
        var bytes = SaveFile.Write(saved);

        var b = new GameSession(BehaviourData.Data) { Logger = log.Add };
        b.Load(SaveFile.Read(bytes));
        Assert.Equal(1, b.Mission?.Number);
        Assert.Equal(214, b.MapNumber);
        Assert.Equal(210, b.PreviousMap);
        Assert.Equal(a.Player.Position, b.Player.Position);
        Assert.Equal(a.Player.Yaw, b.Player.Yaw);
        Assert.Equal(a.Soldier.Health, b.Soldier.Health);
        Assert.Equal(a.Soldier.Armour, b.Soldier.Armour);
        Assert.Equal(a.Weapons.Inventory.Pool(0), b.Weapons.Inventory.Pool(0));
        Assert.Equal(a.Progress.Left, b.Progress.Left);
        Assert.Equal(a.TakenPickups.Order(), b.TakenPickups.Order());
        // Everything else: the same game saves to the same bytes.
        Assert.Equal(bytes, SaveFile.Write(b.Capture(When)));

        // MAP.210 kept its dead robot and its open gate.
        Assert.Contains(saved.Maps, m => m.Map == 210 && m.Dead.Contains(robot.MarkerOffset));

        // The rest of mission 1 from the loaded game.
        var lines = File.ReadAllLines(Path.Combine(Here(), "mission1.play"));
        var from = Array.FindIndex(lines, l => l.StartsWith("use 0x02f4e", StringComparison.Ordinal));
        Assert.True(from > 0);
        failures = PlayScript.Parse(lines[from..]).Run(b, log.Add);
        Assert.True(failures.Count == 0, string.Join("\n", failures.Concat(log)));
        Assert.Equal(2, b.Mission?.Number);
    }

    [BehaviourDataFact]
    public void ALoadedGameRestartsTheMissionWithTheKitItBeganWith()
    {
        var a = new GameSession(BehaviourData.Data);
        a.StartMission(a.Table.ByNumber(1)!.Value);
        var startAmmo = a.Weapons.Inventory.Pool(0);
        a.Weapons.Inventory.AddAmmo(0, -startAmmo);
        var save = SaveFile.Read(SaveFile.Write(a.Capture(When)));

        var b = new GameSession(BehaviourData.Data);
        b.Load(save);
        Assert.Equal(0, b.Weapons.Inventory.Pool(0));
        b.RestartMission();
        Assert.Equal(startAmmo, b.Weapons.Inventory.Pool(0));
    }

    [BehaviourDataFact]
    public void ASaveFromTheOtherGameIsRefusedAndTheSessionKept()
    {
        var a = new GameSession(BehaviourData.Data);
        a.StartMission(a.Table.ByNumber(1)!.Value);
        var save = a.Capture(When) with { Game = GameKind.FutureShock };
        Assert.Throws<SaveFormatException>(() => a.Load(save));
        Assert.Equal(210, a.MapNumber);
        Assert.NotNull(a.Behaviour);
    }

    [BehaviourDataTheory]
    [InlineData(220, VehicleMode.Jeep)]
    [InlineData(270, VehicleMode.Hk)]
    public void ARideSavedPartWayLoadsTheSame(int map, VehicleMode mode)
    {
        var (a, rig) = Riding(map);
        Assert.Equal(mode, rig.Mode);

        // Driven (or flown) ahead with the turret swung, the gun fired, the hull hit.
        rig.Controller.AimYaw = MathF.PI;                                        // turret turned to the rear
        var start = a.Player.Position;
        var ahead = new TickInput(TickInput.Bit(0), 0, 0, 0, 0, 0, 0, 0);
        var keys = new SkyNet.Game.Player.PlayerActions(0, 1, 2, 3, 4, 5, 6);
        for (var t = 0; t < 45; t++)
        {
            a.Tick(ahead, keys, new WeaponInput(t < 6, false, false), false);
        }

        rig.Hull.Hit(123f);
        Assert.True(Vector3.Distance(a.Player.Position, start) > 50f);
        Assert.True(a.CanSave);
        var saved = a.Capture(When);
        Assert.NotNull(saved.Ride);
        var bytes = SaveFile.Write(saved);

        var (b, rigB) = Riding(map);
        b.Load(SaveFile.Read(bytes));
        Assert.Equal(mode, rigB.Mode);
        Assert.Same(rigB.Guns, b.Weapons.Mount);
        Assert.Same(rigB.Hull, b.Weapons.Player);
        Assert.Equal(rig.Hull.Points, rigB.Hull.Points);
        Assert.Equal(rig.Controller.Speed, rigB.Controller.Speed);
        Assert.Equal(rig.Controller.CarYaw, rigB.Controller.CarYaw);
        Assert.Equal(rig.Controller.AimYaw, rigB.Controller.AimYaw);
        Assert.Equal(rig.Controller.AimPitch, rigB.Controller.AimPitch);
        Assert.Equal(a.Weapons.Inventory.Pool(VehicleGuns.EnergyPool), b.Weapons.Inventory.Pool(VehicleGuns.EnergyPool));
        // The HK is not raised a second time by the map's entry.
        Assert.Equal(a.Player.Position, b.Player.Position);
        Assert.Equal(a.Player.Pitch, b.Player.Pitch);
        Assert.Equal(a.Player.Yaw, b.Player.Yaw);                                // the view: car + turret
        Assert.Equal(bytes, SaveFile.Write(b.Capture(When)));
    }

    [BehaviourDataFact]
    public void AFormatOneSaveLoadsWithTheMissionTablesRide()
    {
        var (a, rig) = Riding(220);
        rig.Hull.Hit(300f);
        var v1 = SaveFile.Write(a.Capture(When) with { Ride = null }, 1);
        Assert.Equal(1, BitConverter.ToUInt16(v1, 8));
        var read = SaveFile.Read(v1);
        Assert.Null(read.Ride);
        Assert.Equal(a.Capture(When).Label, SaveFile.ReadLabel(new MemoryStream(v1)));

        var (b, rigB) = Riding(210);
        Assert.False(rigB.Riding);
        b.Load(read);
        Assert.Equal(220, b.MapNumber);
        Assert.Equal(VehicleMode.Jeep, rigB.Mode);
        Assert.Equal(Hull.JeepPoints, rigB.Hull.Points);
    }

    /// <summary>A session started on MAP.<paramref name="map"/> with the vehicle layer hooked as the app hooks it.</summary>
    private static (GameSession Session, VehicleRig Rig) Riding(int map)
    {
        var s = new GameSession(BehaviourData.Data);
        var rig = new VehicleRig(s);
        s.Listener = new RigListener(rig);
        s.StartAt(map);
        return (s, rig);
    }

    private sealed class RigListener(VehicleRig rig) : ISessionListener
    {
        public void MapEntered(GameSession session) => rig.MapEntered();
    }

    [Fact]
    public void TheFormatRoundTrips()
    {
        var save = Sample();
        var back = SaveFile.Read(SaveFile.Write(save));
        Assert.Equal(SaveFile.Write(save), SaveFile.Write(back));
        Assert.Equal(save.Feet, back.Feet);
        Assert.Equal(save.Maps[0].Behaviour.Records, back.Maps[0].Behaviour.Records);
        Assert.Equal(save.Robots, back.Robots);
        Assert.Equal(save.Ride, back.Ride);
        Assert.Equal(0.75f, back.CompleteIn);
        Assert.Equal("MISSION 1 MAP 214  2026-10-10 12:34", back.Label);
    }

    /// <summary>The label counts the campaign's missions, not the table's numbers: Future Shock
    /// skips 4 and 8, so MAP.070's mission is the 6th (issue #39).</summary>
    [Theory]
    [InlineData(5, 70, "MISSION 4 MAP 070")]
    [InlineData(7, 70, "MISSION 6 MAP 070")]
    [InlineData(9, 90, "MISSION 7 MAP 090")]
    public void AFutureShockLabelCountsTheMissions(int mission, int map, string label)
    {
        var save = Sample() with { Game = GameKind.FutureShock, Mission = mission, Map = map };
        Assert.StartsWith(label, save.Label, StringComparison.Ordinal);
    }

    [Fact]
    public void ThePilotRoundTripsInFormatFourAndAFormatThreeSaveStillLoads()
    {
        var save = Sample() with { Pilot = new Pilot("Testpilot", "Ace") };
        var back = SaveFile.Read(SaveFile.Write(save));
        Assert.Equal(new Pilot("Testpilot", "Ace"), back.Pilot);
        Assert.Equal(15, SaveFile.Version);

        // No pilot (a game started without the dialog) round-trips as none.
        Assert.Null(SaveFile.Read(SaveFile.Write(Sample())).Pilot);

        // A format-3 file (the previous build's) reads, without a pilot; its version is kept in the header.
        var old = SaveFile.Write(save, 3);
        Assert.Equal(3, BitConverter.ToUInt16(old, 8));
        var three = SaveFile.Read(old);
        Assert.Null(three.Pilot);
        Assert.Equal(save.Map, three.Map);
        Assert.Equal(save.Ride, three.Ride);
    }

    [Fact]
    public void TheAutomapsSeenSetRoundTripsInFormatNine()
    {
        var save = Sample() with { Seen = [0x2540, 0x2600, 0x2abc] };
        Assert.Equal([0x2540, 0x2600, 0x2abc], SaveFile.Read(SaveFile.Write(save)).Seen);

        // A format-8 file (the previous build's) reads with nothing seen.
        Assert.Empty(SaveFile.Read(SaveFile.Write(save, 8)).Seen);
    }

    [Fact]
    public void TheTrailAndTheHoversPlaceOnItRoundTripInFormatEleven()
    {
        var sample = Sample();
        var trail = new SavedTrail(12.5, 7, [.. Enumerable.Range(0, 150)], [.. Enumerable.Range(0, 150).Select(i => new Vector3(i, 2f, 3f))]);
        var save = sample with { Robots = [sample.Robots[0] with { Node = 7, NodeStamp = 6 }, .. sample.Robots[1..]] };
        save = save with { Trail = trail };
        var back = SaveFile.Read(SaveFile.Write(save));
        Assert.Equal(15, SaveFile.Version);
        Assert.Equal((12.5, 7), (back.Trail!.Clock, back.Trail.Head));
        Assert.Equal(trail.Stamps, back.Trail.Stamps);
        Assert.Equal(trail.Points, back.Trail.Points);
        Assert.Equal((7, 6), (back.Robots[0].Node, back.Robots[0].NodeStamp));

        // A format-10 file (the previous build's): no trail, no hover on one.
        var old = SaveFile.Read(SaveFile.Write(save, 10));
        Assert.Null(old.Trail);
        Assert.Equal((0, 0), (old.Robots[0].Node, old.Robots[0].NodeStamp));
    }

    [Fact]
    public void ImprovedMemoryRoundTripsInFormatTwelve()
    {
        var sample = Sample();
        var memory = new RobotMemory(new Vector3(1f, 2f, 3f), 12.5f, true, new Vector3(4f, 5f, 6f), 3.25f,
            new Vector3(7f, 8f, 9f), 1.5f, 0.75f, -2f, 0x0123456789ABCDEFul);
        var save = sample with { Robots = [sample.Robots[0] with { Memory = memory }, .. sample.Robots[1..]] };
        Assert.Equal(memory, SaveFile.Read(SaveFile.Write(save)).Robots[0].Memory);

        // Nothing seen yet: the sighting stays none.
        var unseen = memory with { LastSeen = null };
        save = sample with { Robots = [sample.Robots[0] with { Memory = unseen }, .. sample.Robots[1..]] };
        Assert.Equal(unseen, SaveFile.Read(SaveFile.Write(save)).Robots[0].Memory);

        // A format-11 file (the previous build's): no memory kept.
        Assert.Null(SaveFile.Read(SaveFile.Write(save, 11)).Robots[0].Memory);
    }

    [Fact]
    public void ImprovedAlertRoundTripsInFormatThirteen()
    {
        var sample = Sample();
        var memory = new RobotMemory(null, 0f, false, default, 0f, default, 0f, 0f, 0f, 7ul)
        {
            Alert = RobotAlert.Search, AlertT = 4.5f, Post = new Vector3(10f, 0f, -20f), Spot = new Vector3(1f, 2f, 3f),
        };
        var save = sample with { Robots = [sample.Robots[0] with { Memory = memory }, .. sample.Robots[1..]] };
        Assert.Equal(memory, SaveFile.Read(SaveFile.Write(save)).Robots[0].Memory);

        // A format-12 file (the previous build's): idle, placed where it stands.
        var old = SaveFile.Read(SaveFile.Write(save, 12)).Robots[0].Memory!.Value;
        Assert.Equal((RobotAlert.Idle, 0f, null, default(Vector3)), (old.Alert, old.AlertT, old.Post, old.Spot));
        Assert.Equal(7ul, old.Rng);

        // A format-12 robot that had seen him: fighting.
        var seen = memory with { LastSeen = new Vector3(5f, 0f, 5f) };
        save = sample with { Robots = [sample.Robots[0] with { Memory = seen }, .. sample.Robots[1..]] };
        Assert.Equal(RobotAlert.Combat, SaveFile.Read(SaveFile.Write(save, 12)).Robots[0].Memory!.Value.Alert);
    }

    private static readonly RobotCombat Fight = new(true, 0.25f, 0.4f, 0.12f, new Vector3(0f, 0f, -1f), new Vector3(5f, 0f, 6f),
        new Vector3(100f, 0f, 0f), true, 1.5f, 3, 0.5f, new Vector3(300f, 0f, -50f), 2f, true, 4f, 5, -1, 0.75f,
        new Vector3(-200f, 0f, 400f), -1, 0.25f);

    [Fact]
    public void ImprovedFightRoundTripsInFormatFourteen()
    {
        var sample = Sample();
        var memory = new RobotMemory(null, 0f, false, default, 0f, default, 0f, 0f, 0f, 7ul) { Alert = RobotAlert.Combat, Combat = Fight };
        var save = sample with { Robots = [sample.Robots[0] with { Memory = memory }, .. sample.Robots[1..]] };
        Assert.Equal(memory, SaveFile.Read(SaveFile.Write(save)).Robots[0].Memory);

        var open = memory with { Combat = Fight with { Cover = null } };
        save = sample with { Robots = [sample.Robots[0] with { Memory = open }, .. sample.Robots[1..]] };
        Assert.Equal(open, SaveFile.Read(SaveFile.Write(save)).Robots[0].Memory);

        // A format-13 file (the previous build's): no fight kept, the alert still there.
        var old = SaveFile.Read(SaveFile.Write(save, 13)).Robots[0].Memory!.Value;
        Assert.Null(old.Combat);
        Assert.Equal(RobotAlert.Combat, old.Alert);
    }

    private static readonly RobotSearch Leads = new(new Vector3(5f, 0f, 6f), new Vector3(250f, 0f, 0f), new Vector3(1f, 0f, 0f), 2, 1, 3.5f);

    [Fact]
    public void ImprovedSearchRoundTripsInFormatFifteen()
    {
        var sample = Sample();
        var memory = new RobotMemory(null, 0f, false, default, 0f, default, 0f, 0f, 0f, 7ul) { Alert = RobotAlert.Search, Combat = Fight, Search = Leads };
        var save = sample with { Robots = [sample.Robots[0] with { Memory = memory }, .. sample.Robots[1..]] };
        Assert.Equal(memory, SaveFile.Read(SaveFile.Write(save)).Robots[0].Memory);

        // A format-14 file (the previous build's): no search kept, the fight still there.
        var old = SaveFile.Read(SaveFile.Write(save, 14)).Robots[0].Memory!.Value;
        Assert.Null(old.Search);
        Assert.Equal(Fight, old.Combat);
    }

    /// <summary>A search timer beyond an hour loads clamped (the robot holds it tighter, RobotWitnessTests).</summary>
    [Fact]
    public void ASearchTimerLoadsClamped()
    {
        var sample = Sample();
        var memory = new RobotMemory(null, 0f, false, default, 0f, default, 0f, 0f, 0f, 7ul) { Search = Leads with { LeadT = 1e6f } };
        var save = sample with { Robots = [sample.Robots[0] with { Memory = memory }, .. sample.Robots[1..]] };
        Assert.Equal(3600f, SaveFile.Read(SaveFile.Write(save)).Robots[0].Memory!.Value.Search!.Value.LeadT);
    }

    /// <summary>A crafted save with a NaN, infinite or huge number in a robot's fields is refused (a
    /// NaN alert timer held a robot suspicious for ever).</summary>
    [Theory]
    [InlineData(BadField.AlertT)]
    [InlineData(BadField.Post)]
    [InlineData(BadField.MemoryT)]
    [InlineData(BadField.Position)]
    [InlineData(BadField.Yaw)]
    [InlineData(BadField.HugePosition)]
    [InlineData(BadField.AimError)]
    [InlineData(BadField.Cover)]
    [InlineData(BadField.SearchWay)]
    public void ANonFiniteRobotFieldIsRefused(BadField field)
    {
        var sample = Sample();
        var robot = sample.Robots[0];
        var memory = new RobotMemory(null, 1f, false, default, 0f, default, 0f, 0f, 0f, 7ul) { Alert = RobotAlert.Suspicious, AlertT = 1f };
        (robot, memory) = field switch
        {
            BadField.AlertT => (robot, memory with { AlertT = float.NaN }),
            BadField.Post => (robot, memory with { Post = new Vector3(float.PositiveInfinity, 0f, 0f) }),
            BadField.MemoryT => (robot, memory with { MemoryT = float.NaN }),
            BadField.Position => (robot with { Position = new Vector3(0f, float.NaN, 0f) }, memory),
            BadField.Yaw => (robot with { Yaw = float.NegativeInfinity }, memory),
            BadField.AimError => (robot, memory with { Combat = Fight with { AimErr = float.NaN } }),
            BadField.Cover => (robot, memory with { Combat = Fight with { Cover = new Vector3(0f, float.PositiveInfinity, 0f) } }),
            BadField.SearchWay => (robot, memory with { Search = Leads with { Way = new Vector3(float.NaN, 0f, 0f) } }),
            _ => (robot with { Position = new Vector3(3e38f, 0f, 0f) }, memory),
        };
        var save = sample with { Robots = [robot with { Memory = memory }, .. sample.Robots[1..]] };
        Assert.Throws<SaveFormatException>(() => SaveFile.Read(SaveFile.Write(save)));
    }

    public enum BadField
    {
        AlertT,
        Post,
        MemoryT,
        Position,
        Yaw,
        HugePosition,
        AimError,
        Cover,
        SearchWay,
    }

    /// <summary>Timers beyond an hour or below zero load clamped.</summary>
    [Fact]
    public void RobotTimersLoadClamped()
    {
        var sample = Sample();
        var memory = new RobotMemory(null, 1e6f, false, default, -5f, default, 0f, 0f, 0f, 7ul) { AlertT = 1e6f };
        var save = sample with { Robots = [sample.Robots[0] with { Memory = memory }, .. sample.Robots[1..]] };
        var back = SaveFile.Read(SaveFile.Write(save)).Robots[0].Memory!.Value;
        Assert.Equal((3600f, 0f, 3600f), (back.MemoryT, back.SearchT, back.AlertT));
    }

    [Fact]
    public void TheRobotsPartsRoundTripInFormatTenAndAFormatNineSaveLoadsThemWhole()
    {
        var sample = Sample();
        var hurt = sample.Robots[0] with { Parts = [200f, 0f, 75f], Gone = 0b010, Senses = [0, 9, 2] };
        var save = sample with
        {
            Robots = [hurt, .. sample.Robots[1..]],
            Maps = [sample.Maps[0] with { ShotOff = [new SavedShotOff(0x2000, 0b110)] }, .. sample.Maps[1..]],
        };
        Assert.Equal([new SavedShotOff(0x2000, 0b110)], SaveFile.Read(SaveFile.Write(save)).Maps[0].ShotOff);
        Assert.Empty(SaveFile.Read(SaveFile.Write(save, 9)).Maps[0].ShotOff);
        var back = SaveFile.Read(SaveFile.Write(save)).Robots[0];
        Assert.Equal(15, SaveFile.Version);
        Assert.Equal([200f, 0f, 75f], back.Parts!);
        Assert.Equal(0b010UL, back.Gone);
        Assert.Equal([0, 9, 2], back.Senses!);

        // A format-9 file (the previous build's): no parts kept, every one whole.
        var old = SaveFile.Read(SaveFile.Write(save, 9)).Robots[0];
        Assert.Equal((null, 0UL), (old.Parts, old.Gone));
        Assert.Null(old.Senses);
        Assert.Equal(hurt.Hp, old.Hp);
    }

    [Fact]
    public void ADamagedOrForeignFileIsRefused()
    {
        var good = SaveFile.Write(Sample());

        // A byte of the body changed: the hash no longer holds.
        var flipped = (byte[])good.Clone();
        flipped[^5] ^= 0x40;
        Assert.Throws<SaveFormatException>(() => SaveFile.Read(flipped));

        // Cut short anywhere.
        foreach (var n in new[] { 0, 4, 9, 20, good.Length / 2, good.Length - 1 })
        {
            Assert.Throws<SaveFormatException>(() => SaveFile.Read(good[..n]));
        }

        // Not a save at all, and another version.
        Assert.Throws<SaveFormatException>(() => SaveFile.Read("{\"format\":\"skynet\"}"u8.ToArray()));
        var newer = (byte[])good.Clone();
        newer[8] = SaveFile.Version + 1;
        var e = Assert.Throws<SaveFormatException>(() => SaveFile.Read(newer));
        Assert.Contains("format", e.Message, StringComparison.Ordinal);
        Assert.Null(SaveFile.ReadLabel(new MemoryStream(newer)));
    }

    [Fact]
    public void SlotsWriteThroughATemporaryFileAndNameThemselves()
    {
        var dir = Path.Combine(Path.GetTempPath(), "skynet-save-test-" + Guid.NewGuid().ToString("N"));
        try
        {
            var slots = new SaveSlots(dir);
            Assert.Null(slots.Label(3));
            Assert.Throws<SaveFormatException>(() => slots.Read(3));
            Assert.Null(slots.Write(3, Sample()));
            Assert.Equal(Sample().Label, slots.Label(3));
            Assert.Equal(214, slots.Read(3).Map);
            Assert.False(File.Exists(slots.PathOf(3) + ".tmp"));
            Assert.EndsWith("slot3.sav", slots.PathOf(3), StringComparison.Ordinal);

            File.WriteAllBytes(slots.PathOf(4), [1, 2, 3]);
            Assert.Null(slots.Label(4));
            Assert.Throws<SaveFormatException>(() => slots.Read(4));
        }
        finally
        {
            if (Directory.Exists(dir))
            {
                Directory.Delete(dir, recursive: true);
            }
        }
    }

    private static SaveGame Sample()
    {
        var behaviour = new BehaviourSnapshot(
            [new RecordBytes(0x100, 1, 0xF0, 0x200), new RecordBytes(0x200, 0, 0x26, 0)],
            [(0x100, 12.5f)], [0x300], [0x200], [0x300], [0x300],
            [new MoverSnapshot(0x400, 64f, -1f, true)],
            [new TriggerSnapshot(0x500, true, false, true)],
            [(0x100, true)],
            [new WreckSnapshot(0x600, 2, 32f)],
            [new LightSnapshot(0x700, -1, 200, true, false, true)],
            0.125f,
            [new PathVehicleSnapshot(0x800, true, new Vector3(1, 2, 3), 0.5f, 0x900, 40f, 50f, false, false)]);
        var kit = new InventoryState([true, false, true], [10, 20, 30], 1, 15);
        return new SaveGame(GameKind.SkyNet, 1, 214, 210, new Vector3(100.5f, -20f, 3000f), 1.25f, -0.1f, false, PlayerMode.Foot,
            880f, 0.25f, kit, kit with { Primary = 0 }, 1000f, 0f, new ProgressSnapshot(2, false, false, [1, 0, 0, 0, 0]), 0.75f,
            [new SavedMap(214, behaviour, [0x1000], [0x2000], [new SavedLoop(0x3000, 77, new Vector3(4, 5, 6))])],
            [new RobotSnapshot(0x2000, -1, new Vector3(7, 8, 9), 0.3f, 0f, true, false, 0f, 0xFF, true, false, 2, true)],
            When, new SavedVehicle(PlayerMode.Jeep, 640f, 300f, 1.5f, -0.4f, 0.2f, 700, 512f));
    }

    /// <summary>Walks onto the current map's first item; its offset, null when the map has none.</summary>
    private static int? TakeAPickup(GameSession s)
    {
        if (!s.PickupsLeft.Any())
        {
            return null;
        }

        var (offset, at) = s.PickupsLeft.First();
        var back = (s.Player.Position, s.Player.Yaw);
        s.Player.Spawn(at, 0f);
        Tick(s, 1f / 60f);
        Assert.Contains(offset, s.TakenPickups);
        s.Player.Spawn(back.Position, back.Yaw);
        Tick(s, 0.25f);
        return offset;
    }

    private static void Tick(GameSession s, float seconds)
    {
        var keys = new SkyNet.Game.Player.PlayerActions(0, 1, 2, 3, 4, 5, 6);
        for (var t = 0f; t < seconds; t += GameSession.Dt)
        {
            s.Tick(default, keys, new WeaponInput(false, false, false), false);
        }
    }
}
