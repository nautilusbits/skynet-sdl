using System.Numerics;
using RetroEng.Physics;
using SkyNet.Formats;
using SkyNet.Game.Campaign;
using SkyNet.Game.Player;
using SkyNet.Game.Tests.Behaviour;
using SkyNet.Game.Weapons;

namespace SkyNet.Game.Tests.Weapons;

/// <summary>The impact pieces (ammo +0x2a/+0x2e, FUN_0011f761/FUN_0011f91e) and the drops of
/// destroyed records (FUN_00124619) taken as pickups.</summary>
public class ImpactPieceTests
{
    private const float Dt = 1f / 60f;

    private static IEnumerable<Triangle> Floor(float y, float half = 100000f)
    {
        yield return new Triangle(new(-half, y, -half), new(half, y, -half), new(half, y, half));
        yield return new Triangle(new(-half, y, -half), new(half, y, half), new(-half, y, half));
    }

    [Fact]
    public void PieceCountsPerAmmoTypeComeFromTheExe()
    {
        var table = WeaponTable.Shipped;
        // (ammo type, count, piece type): shotgun, launcher shell, rocket, the thrown items.
        (int Ammo, int Count, int Type)[] expected =
        [
            (2, 3, 1), (3, 15, 1), (4, 15, 1), (5, 16, 3), (12, 5, 1), (13, 11, 3), (14, 9, 1), (19, 6, 1), (23, 16, 3), (24, 16, 3),
        ];
        foreach (var (ammo, count, type) in expected)
        {
            Assert.Equal((count, type), (table!.Ammo[ammo].Pieces, table.Ammo[ammo].PieceType));
        }

        Assert.Equal(expected.Length, table!.Ammo.Count(a => a.Pieces > 0));
        // The thrown items by record: pipe bomb 5, molotov 11 of type 3, grenade 15, canister 9.
        int[] records = [14, 15, 16, 18];
        int[] pieces = [5, 11, 15, 9];
        Assert.Equal(pieces, records.Select(r => table.AmmoOf(table.Weapons[r])!.Pieces));
        Assert.Equal(3, table.AmmoOf(table.Weapons[15])!.PieceType);
    }

    [Fact]
    public void PieceTypeTableHoldsTheExesTypes()
    {
        // SKYNET.EXE v1.01 0x3844b, as shipped (data/tables impact_pieces).
        PieceType[] expected =
        [
            new(1, (192 << 7) | 1, 105, 200, 70000, 0x1CC),
            new(2, (192 << 7) | 0, 35, 125, 50000, 0x14C),
            new(3, (192 << 7) | 2, 60, 175, 90000, 0x14C),
            new(4, (192 << 7) | 0, 105, 200, 50000, 0x14C),
            new(5, (192 << 7) | 2, 35, -400, 30000, 0x148),
        ];
        Assert.Equal(expected, ImpactPieces.Types);
    }

    [Fact]
    public void AGrenadeBurstThrowsItsPiecesWhichSettleAndExpire()
    {
        var world = new CollisionWorld().Add(new TriangleGrid(Floor(0f)));
        var pieces = new ImpactPieces();
        var t = ImpactPieces.TypeOf(1)!.Value;
        // A shell falling on the floor: the pieces leave up off it.
        Assert.Equal(15, pieces.Spawn(15, 1, new Vector3(0f, 2f, 0f), new Vector3(0.3f, -1f, 0f), Vector3.UnitY));
        Assert.Equal(15, pieces.ActiveCount);
        foreach (var p in pieces.Slots)
        {
            Assert.True(p.Velocity.Y > 0f);
            Assert.Equal(MathF.Abs(t.Speed), p.Velocity.Length(), 1f);
        }

        var maxY = 0f;
        for (var time = 0f; time < t.Life * 0.9f; time += Dt)
        {
            pieces.Step(Dt, world);
            foreach (var p in pieces.Slots)
            {
                maxY = MathF.Max(maxY, p.Position.Y);
                Assert.True(p.Position.Y >= -0.01f, "a piece fell through the floor");
            }
        }

        Assert.True(maxY > 20f, $"the pieces rose {maxY}");
        Assert.Equal(15, pieces.ActiveCount);
        foreach (var p in pieces.Slots)
        {
            Assert.True(p.Resting, "settled on the floor");
            Assert.Equal(1f, p.Position.Y, 0.1f);
        }

        for (var time = 0f; time < t.Life * 0.2f; time += Dt)
        {
            pieces.Step(Dt, world);
        }

        Assert.Equal(0, pieces.ActiveCount);
        Assert.Equal(0, pieces.High);
    }

    [Fact]
    public void ThePoolHoldsDossLimitAndReusesSlots()
    {
        var pieces = new ImpactPieces();
        Assert.Equal(ImpactPieces.Capacity, pieces.Spawn(1000, 2, Vector3.Zero, -Vector3.UnitY, Vector3.UnitY));
        Assert.Equal(0, pieces.Spawn(1, 2, Vector3.Zero, -Vector3.UnitY, Vector3.UnitY));
        for (var time = 0f; time < 1f; time += Dt)
        {
            pieces.Step(Dt, null);
        }

        Assert.Equal(0, pieces.ActiveCount);
        Assert.Equal(16, pieces.Spawn(16, 3, Vector3.Zero, -Vector3.UnitY, Vector3.UnitY));
        Assert.Equal(0, pieces.Spawn(1, 99, Vector3.Zero, -Vector3.UnitY, Vector3.UnitY));
    }

    [Fact]
    public void FramesSpanTheLifeOrLoop()
    {
        var once = new ImpactPieces.Piece { Flags = 0x14C, Life = 1f, Age = 0.99f };
        Assert.Equal(3, ImpactPieces.FrameOf(once, 4));
        Assert.Equal(0, ImpactPieces.FrameOf(once with { Age = 0f }, 4));
        var loop = new ImpactPieces.Piece { Flags = 0x1CC, Life = 1.5f, Phase = 2, Age = 1f / 12f };
        Assert.Equal(3, ImpactPieces.FrameOf(loop, 4));
    }

    [Fact]
    public void AThrownGrenadeBurstsIntoPieces()
    {
        var table = WeaponTable.Shipped;
        var weapons = new WeaponSystem(table, new CollisionWorld().Add(new TriangleGrid(Floor(-100f))));
        weapons.Inventory.AddAmmo(table.Weapons[16].Pool, 5);
        Assert.True(weapons.Inventory.SelectThrown(16));
        var shooter = new Shooter(Vector3.Zero, 0f, -0.5f);
        weapons.Tick(new WeaponInput(Fire: false, Throw: true, Run: false), shooter, Dt);
        var reports = new List<ShotReport>();
        weapons.Reported += reports.Add;
        for (var i = 0; i < 600 && !reports.Any(r => r.What == "burst"); i++)
        {
            weapons.Tick(default, shooter, Dt);
        }

        Assert.Contains(reports, r => r.What == "burst");
        Assert.Equal(15, weapons.Pieces.ActiveCount);
    }

    [BehaviourDataFact]
    public void AMap210CrateDropsItsItemAndWalkingOverItTakesIt()
    {
        var session = new GameSession(BehaviourData.Data);
        session.StartMission(session.Table.ByNumber(1)!.Value);
        Assert.Equal(210, session.MapNumber);
        var destruction = DestructionTable.Load();
        Assert.NotEmpty(destruction.Drops);
        var status = new List<string>();
        var log = new List<string>();
        session.Logger = log.Add;
        session.Listener = new StatusRecorder(status);
        var behaviour = session.Behaviour!;
        // Every breakable record whose destruction type drops something; break them one by one
        // until one leaves an item (a list may pick "nothing").
        var crates = session.Map!.Entities
            .Where(e => e.Variant == MapEntityVariant.Mesh && behaviour.IsDamageable(e.FileOffset)
                && e.Link is { } l && destruction.DropListOf(l.DestroyType) >= 0)
            .ToList();
        Assert.NotEmpty(crates);
        DroppedSprite? item = null;
        foreach (var c in crates)
        {
            behaviour.Hit(c.FileOffset, 100000f);
            Tick(session, Dt);
            item = session.Drops.FirstOrDefault(d => PickupTable.Items.ContainsKey(d.Sprite)) is { Id: < 0 } d ? d : null;
            if (item != null)
            {
                break;
            }
        }

        Assert.True(item != null, "no crate dropped an item:\n" + string.Join("\n", log));
        var drop = item!.Value;
        Assert.Contains(session.PickupsLeft, p => p.Offset == drop.Id);
        status.Clear();
        session.Player.Spawn(drop.At + new Vector3(0f, 1f, 0f), 0f);
        Tick(session, 0.2f);
        Assert.DoesNotContain(session.PickupsLeft, p => p.Offset == drop.Id);
        Assert.DoesNotContain(session.Drops, d => d.Id == drop.Id);
        Assert.Contains(log, l => l.StartsWith("pickup:", StringComparison.Ordinal));
        Assert.NotEmpty(status);
    }

    /// <summary>
    /// The cars of MAP.210 as DOS breaks them: a TRANSFRM.PRS car (COPCAR0A) shows a bank-358 blast
    /// at each stage it steps while it has hit points (handler 0x120833), and blows up with its own
    /// blast when they run out (FUN_00124793, type 0: no drop); a plain car (CAR0S, destruction
    /// type 104) has no stages, and its end is type 104's blast (bank 360) and a burning wreck from
    /// drop list 2 (bank 218).
    /// </summary>
    [BehaviourDataFact]
    public void Map210CarsBlastAtEachStageAndBurnOut()
    {
        var session = new GameSession(BehaviourData.Data);
        session.StartMission(session.Table.ByNumber(1)!.Value);
        Assert.Equal(210, session.MapNumber);
        var behaviour = session.Behaviour!;
        int Blasts(int bank) => session.Weapons.Effects.Count(e => e.Bank == bank);

        const int CopCar = 0x0c9fb, Car = 0x0be75;
        behaviour.Hit(CopCar, 12.5f);
        Assert.Equal(1, behaviour.StageOf(CopCar));
        Assert.Equal(1, Blasts(358));
        behaviour.Hit(CopCar, 12.5f);
        Assert.Equal(2, behaviour.StageOf(CopCar));
        Assert.Equal(2, Blasts(358));
        var drops = session.Drops.Count;
        behaviour.Hit(CopCar, 1000f);
        Assert.Equal(3, Blasts(358));
        Assert.True(behaviour.TryGetPose(CopCar, out var cop) && !cop.Visible);
        Assert.Equal(drops, session.Drops.Count);

        behaviour.Hit(Car, 100f);
        Assert.Equal(0, behaviour.StageOf(Car));
        Assert.True(behaviour.TryGetPose(Car, out var car) && car.Visible);
        Assert.Equal(0, Blasts(360));
        behaviour.Hit(Car, 100f);
        Assert.True(behaviour.TryGetPose(Car, out car) && !car.Visible);
        Assert.True(Blasts(360) >= 1);
        Assert.Contains(session.Drops, d => d.Sprite >> 7 == 218);
    }

    private sealed class StatusRecorder(List<string> status) : ISessionListener
    {
        public void Status(string text) => status.Add(text);
    }

    private static void Tick(GameSession s, float seconds)
    {
        var keys = new PlayerActions(0, 1, 2, 3, 4, 5, 6);
        for (var t = 0f; t < seconds; t += GameSession.Dt)
        {
            s.Tick(default, keys, new WeaponInput(false, false, false), false);
        }
    }
}
