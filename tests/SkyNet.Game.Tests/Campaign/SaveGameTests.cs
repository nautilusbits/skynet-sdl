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
        Assert.Equal(rig.Guns!.Overheated, rigB.Guns!.Overheated);
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

    [Fact]
    public void ThePilotRoundTripsInFormatFourAndAFormatThreeSaveStillLoads()
    {
        var save = Sample() with { Pilot = new Pilot("Testpilot", "Ace") };
        var back = SaveFile.Read(SaveFile.Write(save));
        Assert.Equal(new Pilot("Testpilot", "Ace"), back.Pilot);
        Assert.Equal(9, SaveFile.Version);

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
            When, new SavedVehicle(PlayerMode.Jeep, 640f, 300f, 1.5f, -0.4f, 0.2f, 700, true, 512f));
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
