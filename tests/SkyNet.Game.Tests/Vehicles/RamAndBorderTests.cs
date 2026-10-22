using System.Numerics;
using RetroEng.Loop;
using RetroEng.Physics;
using SkyNet.Formats;
using SkyNet.Game.Campaign;
using SkyNet.Game.Enemies;
using SkyNet.Game.Player;
using SkyNet.Game.Tests.Behaviour;
using SkyNet.Game.Vehicles;

namespace SkyNet.Game.Tests.Vehicles;

/// <summary>Ramming (DOS 0x135a7e, the Godot port's _ram_check) and the map border fence (DOS
/// FUN_00122711 / FUN_00122789, Godot _border_clamp) on the real maps: a MAP.220 robot, MAP.260's
/// car-wash door and town fence.</summary>
public class RamAndBorderTests
{
    private const float Dt = 1f / 60f;
    private static readonly PlayerActions Keys = new(Forward: 0, Back: 1, Left: 2, Right: 3, Run: 4, Jump: 5, Crouch: 6,
        TurnLeft: 7, TurnRight: 8, LookUp: 9, LookDown: 10, Slide: 11);

    private sealed class Hints : ISessionListener
    {
        public readonly List<string> Seen = [];
        public void Hint(string text) => Seen.Add(text);
    }

    private static (GameSession Session, VehicleRig Rig) Start(int map)
    {
        var session = new GameSession(BehaviourData.Data);
        var rig = new VehicleRig(session);
        session.StartAt(map);
        rig.MapEntered();
        Assert.Equal(VehicleMode.Jeep, rig.Mode);
        return (session, rig);
    }

    /// <summary>Heading (yaw) that looks along x/z <paramref name="d"/>.</summary>
    private static float YawAlong(Vector3 d) => MathF.Atan2(-d.X, -d.Z);

    /// <summary>One ram: the car stands at <paramref name="contact"/> having come from
    /// <paramref name="pre"/> at <paramref name="speed"/>. <paramref name="wait"/>: seconds since the last ram.</summary>
    private static bool Ram(VehicleRig rig, Vector3 pre, Vector3 contact, float yaw, float speed, float wait = 1f)
    {
        var car = rig.Controller;
        car.Body.Position = contact;
        car.CarYaw = yaw;
        car.Speed = speed;
        return car.RamAfterMove(pre, yaw, wait);
    }

    [BehaviourDataFact]
    public void Map220RobotTakesHalfTheSpeedPerRamBouncesTheCarAndBreaksAtLast()
    {
        var (session, rig) = Start(220);
        var robot = session.Robots!.Active.FirstOrDefault(r => !r.Indestructible && !r.OnPath && !r.IsFlying && r.DormantDistance <= 0f && r.MaxHp > 150f);
        Assert.NotNull(robot);
        var hull = rig.Hull.Points;
        var d = new Vector3(0f, 0f, -1f);
        var yaw = YawAlong(d);
        var target = robot.Centre - new Vector3(0f, Ramming.Lift, 0f);
        var contact = target - d * (Ramming.Half.Z + robot.Radius * 0.5f);
        var pre = target - d * 900f;

        // Too slow to count: no hit at all, the move stands.
        var hp = robot.Hp;
        Assert.False(Ram(rig, pre, contact, yaw, 1.5f));
        Assert.Equal(hp, robot.Hp);
        Assert.Equal(contact, rig.Controller.Body.Position);

        // Slow: 20 u/s deals 10, the robot holds, the car bounces back to where the move began.
        Assert.True(Ram(rig, pre, contact, yaw, 20f));
        Assert.Equal(hp - 10f, robot.Hp);
        Assert.True(robot.Alive);
        Assert.Equal(-20f, rig.Controller.Speed);
        Assert.Equal(0f, rig.Controller.Wheel);
        Assert.Equal(pre, rig.Controller.Body.Position);

        // Again at once: only stopped, not dosed (the port's 0.3 s wait).
        Assert.True(Ram(rig, pre, contact, yaw, 300f, wait: Dt));
        Assert.Equal(hp - 10f, robot.Hp);
        Assert.Equal(0f, rig.Controller.Speed);

        // Full speed until it breaks: ⌊683.6 / 2⌋ = 341 a ram, no difficulty factor; the breaking ram
        // lets the car on at 7/8.
        var rams = 0;
        while (robot.Alive && rams < 20)
        {
            var before = robot.Hp;
            var undone = Ram(rig, pre, contact, yaw, VehicleController.JeepMaxSpeed);
            rams++;
            if (robot.Alive)
            {
                Assert.True(undone);
                Assert.Equal(before - 341f, robot.Hp);
                Assert.Equal(-VehicleController.JeepMaxSpeed, rig.Controller.Speed);
            }
            else
            {
                Assert.False(undone);
                Assert.Equal(VehicleController.JeepMaxSpeed * 0.875f, rig.Controller.Speed, 3);
                Assert.Equal(contact, rig.Controller.Body.Position);
            }
        }

        Assert.False(robot.Alive);
        Assert.Equal((int)MathF.Ceiling((hp - 10f) / 341f), rams);
        Assert.Equal(hull, rig.Hull.Points);                                    // the ram itself spares the hull
    }

    [BehaviourDataFact]
    public void Map220RobotIsRammedByDrivingIntoIt()
    {
        var (session, rig) = Start(220);
        var robot = session.Robots!.Active.First(r => !r.Indestructible && !r.OnPath && !r.IsFlying && r.DormantDistance <= 0f);
        var hp = robot.Hp;
        var d = new Vector3(0f, 0f, -1f);
        var body = session.Player;
        // The car on a flat lane of its own, the robot where the map put it.
        var flat = new CollisionWorld().Add(new TriangleGrid(Flat(robot.Centre.Y - Ramming.Lift - 20f)));
        var car = new VehicleController(flat, body) { Ram = new Ramming(flat, session.Weapons.Targets) };
        body.Spawn(robot.Centre - d * 400f - new Vector3(0f, Ramming.Lift, 0f), YawAlong(d));
        car.Enter(VehicleMode.Jeep);
        car.Speed = 300f;
        var hit = false;
        for (var i = 0; i < 120 && !hit; i++)
        {
            body.Step(new TickInput(TickInput.Bit(Keys.Forward), 0, 0, 0, 0, 0, 0, 0), Dt, Keys);
            hit = robot.Hp < hp;
        }

        Assert.True(hit);
        Assert.True(car.Speed < -300f);                                          // it held: bounced at the ram's speed
        Assert.Equal(Ramming.DamageAt(-car.Speed), hp - robot.Hp);               // ⌊v/2⌋ of that speed
    }

    private static IEnumerable<Triangle> Flat(float y, float half = 200000f)
    {
        yield return new Triangle(new(-half, y, -half), new(half, y, -half), new(half, y, half));
        yield return new Triangle(new(-half, y, -half), new(half, y, half), new(-half, y, half));
    }

    [BehaviourDataFact]
    public void Map260CarWashDoorBreaksToOneRamAbove120()
    {
        var (session, rig) = Start(260);
        var map = session.Map!;
        var door = map.Entities.Single(e => e.Variant == MapEntityVariant.Mesh && map.EntityName(e).StartsWith("CWDOOR", StringComparison.OrdinalIgnoreCase));
        var off = door.FileOffset;
        Assert.True(session.Behaviour!.IsDamageable(off));
        var tris = new List<Triangle>();
        session.World.Gather(new Box(new Vector3(-1e6f), new Vector3(1e6f)), tris);
        var mine = tris.Where(t => t.Owner == off).ToList();
        Assert.NotEmpty(mine);
        var lo = mine.Aggregate(new Vector3(float.MaxValue), (m, t) => Vector3.Min(m, t.Min));
        var hi = mine.Aggregate(new Vector3(float.MinValue), (m, t) => Vector3.Max(m, t.Max));
        var size = hi - lo;
        var mid = (lo + hi) / 2f;
        // Drive at the slab's thin side.
        var d = size.X < size.Z ? Vector3.UnitX : Vector3.UnitZ;
        var yaw = YawAlong(d);
        var foot = new Vector3(mid.X, lo.Y, mid.Z);
        var contact = foot - d * (Ramming.Half.Z - 20f);
        var pre = foot - d * 900f;

        // 100 u/s: 50 of its 60 points, the door holds and the car bounces.
        Assert.True(Ram(rig, pre, contact, yaw, 100f));
        Assert.True(session.Behaviour.IsDamageable(off));
        // 250 u/s: 125 — it breaks and the car drives on at 7/8.
        Assert.False(Ram(rig, pre, contact, yaw, 250f));
        Assert.False(session.Behaviour.IsDamageable(off));
        Assert.Equal(250f * 0.875f, rig.Controller.Speed, 3);

        // A fresh door falls to one ram at 130 u/s (65 ≥ 60).
        var (fresh, rig2) = Start(260);
        Assert.False(Ram(rig2, pre, contact, yaw, 130f));
        Assert.False(fresh.Behaviour!.IsDamageable(off));
    }

    [BehaviourDataFact]
    public void Map260FenceIsOneBoxAndHoldsTheJeepAndTheSoldier()
    {
        var (session, rig) = Start(260);
        var hints = new Hints();
        session.Listener = hints;
        Assert.Single(rig.Border.Boxes);
        var (min, max) = rig.Border.Boxes[0];
        // The Godot port's action_smoke_test numbers.
        Assert.Equal(14208f, min.X, 0);
        Assert.Equal(64378f, max.X, 0);
        Assert.Equal(-27880f, min.Y, 0);
        Assert.Equal(-4898f, max.Y, 0);

        // Drive at the east edge at full speed: the car never leaves the box and is stopped.
        var body = session.Player;
        Assert.True(rig.Border.Inside(body.Position));
        var east = new Vector3(max.X - 200f, body.Position.Y, (min.Y + max.Y) / 2f);
        body.Spawn(east, YawAlong(Vector3.UnitX));
        rig.Board(VehicleMode.Jeep);
        rig.Controller.Speed = VehicleController.JeepMaxSpeed;
        var stopped = false;
        for (var i = 0; i < 120; i++)
        {
            session.Tick(new TickInput(TickInput.Bit(Keys.Forward), 0, 0, 0, 0, 0, 0, 0), Keys, default, false);
            Assert.True(rig.Border.Inside(body.Position), $"tick {i}: {body.Position}");
            stopped |= rig.Controller.Speed == 0f;
        }

        Assert.True(stopped);
        Assert.True(body.Position.X > max.X - MapBorder.HintMargin, $"{body.Position}"); // it reached the fence itself
        var hint = session.Progress.HintText(MapBorder.HintSlot);
        Assert.Contains("highway", hint, StringComparison.OrdinalIgnoreCase);
        Assert.Contains(hint, hints.Seen);

        // On foot too.
        rig.Board(VehicleMode.Foot);
        body.Spawn(new Vector3(max.X - 30f, body.Position.Y + 50f, (min.Y + max.Y) / 2f), YawAlong(Vector3.UnitX));
        for (var i = 0; i < 120; i++)
        {
            session.Tick(new TickInput(TickInput.Bit(Keys.Forward) | TickInput.Bit(Keys.Run), 0, 0, 0, 0, 0, 0, 0), Keys, default, false);
            Assert.True(rig.Border.Inside(body.Position));
        }
    }

    [Fact]
    public void OutsideTheFenceAMoveStandsUntilItIsBackIn()
    {
        var border = new MapBorder([(new Vector2(0f, 0f), new Vector2(1000f, 1000f))]);
        var body = new PlayerController(new CollisionWorld());
        body.Spawn(new Vector3(-100f, 0f, 500f), 0f);
        var before = body.Position;
        body.Position += new Vector3(10f, 0f, 0f);
        Assert.False(border.Clamp(body, before, out _));                       // the refuge rule
        Assert.Equal(-90f, body.Position.X);
        body.Position = new Vector3(990f, 0f, 500f);
        Assert.False(border.Clamp(body, new Vector3(980f, 0f, 500f), out var hint));
        Assert.True(hint);
        body.Velocity = new Vector3(400f, 0f, 0f);
        body.Position = new Vector3(1005f, 0f, 500f);
        Assert.True(border.Clamp(body, new Vector3(990f, 0f, 500f), out _));
        Assert.Equal(new Vector3(990f, 0f, 500f), body.Position);
        Assert.Equal(Vector3.Zero, body.Velocity);
        Assert.Empty(MapBorder.None.Boxes);
    }
}
