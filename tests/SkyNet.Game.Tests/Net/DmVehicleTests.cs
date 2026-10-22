using System.Collections.Immutable;
using System.Numerics;
using RetroEng.Net;
using SkyNet.Game.Net;
using SkyNet.Game.Vehicles;

namespace SkyNet.Game.Tests.Net;

/// <summary>Step 9: the arena's jeeps and HKs on the server (SKYNET.EXE v1.01's rules) and over the
/// loopback wire.</summary>
public class DmVehicleTests
{
    private static readonly DmArena Arena = new()
    {
        Map = 605,
        Spawns = [new(new Vector3(0, 0, 0), 0f), new(new Vector3(10000, 0, 0), 1f)],
        ItemSpots = [new(100, 0, 100)],
        JeepSpots = [new(1000, 0, 0), new(2000, 0, 0)],
        HkSpots = [new(3000, 0, 0)],
    };

    private static DmSettings Settings(int jeeps = 3, int hks = 3) => new()
    {
        Items = ImmutableDictionary<string, int>.Empty.Add("jeeps", jeeps).Add("hks", hks).Add("slugthrowers", 2),
    };

    private static DmServer NewServer(DmSettings? settings = null)
    {
        var s = new DmServer(settings ?? Settings(), new Random(1));
        s.SetArena(Arena);
        for (var id = 1; id <= 3; id++)
        {
            s.AddHuman(id, $"P{id}", DmClass.Human);
            s.LevelReady(id);
        }

        return s;
    }

    [Fact]
    public void Vehicles_park_on_the_first_markers_in_order_with_DOS_headings()
    {
        var s = NewServer();
        // jeeps = 3 but only two jeep markers; hks = 3 on one HK marker: min(count, markers) each.
        Assert.Equal(3, s.Vehicles.Count);
        Assert.Equal([VehicleMode.Jeep, VehicleMode.Jeep, VehicleMode.Hk], s.Vehicles.Values.OrderBy(v => v.Key).Select(v => v.Kind));
        Assert.Equal(new Vector3(1000, 0, 0), s.Vehicles[1].Position);
        Assert.Equal(new Vector3(3000, 0, 0), s.Vehicles[3].Position);
        Assert.Equal(0f, s.Vehicles[1].Yaw);
        Assert.Equal(DmServer.VehicleYawStep * 2, s.Vehicles[3].Yaw, 4);
        Assert.Equal(Hull.JeepPoints, s.Vehicles[1].Hull);
        Assert.Equal(Hull.HkPoints, s.Vehicles[3].Hull);
        // Items lie on the marker-100 spots only, one a spot: the second slugthrower does not fit.
        Assert.Equal((new Vector3(100, 0, 100), 25612), s.Pickups.Values.Select(p => (p.Position, p.Sprite)).Single());
        Assert.Empty(NewServer(Settings(0, 0)).Vehicles);
    }

    [Fact]
    public void One_driver_a_vehicle_and_only_within_reach()
    {
        var s = NewServer();
        s.Pose(2, new Vector3(1010, 0, 0), 0f, 0f, 0);
        s.Pose(3, new Vector3(1020, 0, 0), 0f, 0f, 0);
        Assert.True(s.ClientEnterVehicle(2, 1));
        Assert.False(s.ClientEnterVehicle(3, 1)); // taken
        Assert.False(s.ClientEnterVehicle(2, 2)); // already driving
        Assert.Equal(2, s.Vehicles[1].Driver);
        Assert.Equal((int)VehicleMode.Jeep << DmProtocol.VehicleShift, s.ServerFlags(2, 0) & DmProtocol.VehicleMask);

        s.Pose(3, new Vector3(9000, 0, 0), 0f, 0f, 0);
        Assert.False(s.ClientEnterVehicle(3, 3)); // the HK is 6000 away

        // The driver's poses move the vehicle; its heading rides along.
        s.Pose(2, new Vector3(1500, 0, 50), 0.3f, 0f, 0, carYaw: 1.2f);
        Assert.Equal(new Vector3(1500, 0, 50), s.Vehicles[1].Position);
        Assert.Equal(1.2f, s.Vehicles[1].Yaw);

        // Out: parked where the driver says (near its last pose), for good.
        Assert.True(s.ClientLeaveVehicle(2, new Vector3(1510, 0, 50), 1.1f));
        Assert.Equal(0, s.Vehicles[1].Driver);
        Assert.Equal(new Vector3(1510, 0, 50), s.Vehicles[1].Position);
        s.Update(120.0);
        Assert.Equal(new Vector3(1510, 0, 50), s.Vehicles[1].Position); // no return to the marker
        s.Pose(3, new Vector3(1500, 0, 0), 0f, 0f, 0);
        Assert.True(s.ClientEnterVehicle(3, 1));
        // A far-off exit is replaced by the server's own last pose.
        Assert.True(s.ClientLeaveVehicle(3, new Vector3(90000, 0, 0), 0f));
        Assert.Equal(new Vector3(1510, 0, 50), s.Vehicles[1].Position); // boarding put the body at the vehicle
    }

    [Fact]
    public void The_hull_takes_every_hit_and_a_wreck_puts_the_driver_out_alive()
    {
        var s = NewServer();
        var events = new List<(int Key, DmVehicleEvent Why)>();
        s.VehicleChanged += (v, why) => events.Add((v.Key, why));
        s.Pose(2, new Vector3(3000, 0, 0), 0f, 0f, 0);
        Assert.True(s.EnterVehicle(2, 3));
        var health = s.Players[2].Health;

        Assert.True(s.Hit(2, 600f, 1, 24));
        Assert.Equal(Hull.HkPoints - 600f, s.Vehicles[3].Hull);
        Assert.Equal(health, s.Players[2].Health); // the driver takes nothing
        Assert.Equal(100, s.Players[1].Points); // a hit is a hit

        // The driver's own blast is a self-hit: the hull again.
        Assert.True(s.ClientHit(2, 2, 300f, 25));
        Assert.Equal(Hull.HkPoints - 900f, s.Vehicles[3].Hull);

        Assert.True(s.Hit(2, 2000f, 1, 24));
        var v = s.Vehicles[3];
        Assert.True(v.Wrecked);
        Assert.Equal(0, v.Driver);
        Assert.True(s.IsAlive(2));
        Assert.Equal(health, s.Players[2].Health);
        Assert.Equal(0, s.Players[1].Frags);
        Assert.Equal((3, DmVehicleEvent.Wrecked), events[^1]);

        // A wreck is not boarded and never comes back in the round; a new round parks it again.
        Assert.False(s.EnterVehicle(1, 3));
        s.Update(600.0);
        Assert.True(s.Vehicles[3].Wrecked);
        s.Restart();
        Assert.False(s.Vehicles[3].Wrecked);
        Assert.Equal(Hull.HkPoints, s.Vehicles[3].Hull);
        Assert.Equal(new Vector3(3000, 0, 0), s.Vehicles[3].Position);
        Assert.Equal((3, DmVehicleEvent.Returned), events[^1]);
    }

    [Fact]
    public void A_driver_who_dies_or_leaves_frees_the_seat()
    {
        var s = NewServer();
        s.Pose(2, new Vector3(1000, 0, 0), 0f, 0f, 0);
        Assert.True(s.EnterVehicle(2, 1));
        s.Pose(2, new Vector3(1200, 0, 0), 0f, 0f, 0);
        s.RemovePlayer(2);
        Assert.Equal(0, s.Vehicles[1].Driver);
        Assert.Equal(new Vector3(1200, 0, 0), s.Vehicles[1].Position);
        Assert.False(s.EnterVehicle(5, 1)); // nobody
    }

    private sealed class Wire
    {
        public readonly LoopbackNetwork Net = new();
        public readonly DmHost Host;

        public Wire()
        {
            Host = new DmHost(new NetEndpoint(Net.Listen(0, DmProtocol.MaxPeers)!), new DmServer(Settings(), new Random(3)) { LimitTravel = false }, "HOST", DmClass.Human);
            Host.Server.SetArena(Arena);
            Host.ReportLevelReady();
        }

        public DmClient Join(string name)
        {
            var c = new DmClient(new NetEndpoint(Net.Connect(Host.Port)), name, DmClass.Human);
            c.Welcomed += c.ReportLevelReady;
            return c;
        }

        public void Pump(params DmClient[] clients)
        {
            for (var i = 0; i < 4; i++)
            {
                Host.Update(0.01);
                foreach (var c in clients)
                {
                    c.Update(0.01);
                }
            }
        }
    }

    [Fact]
    public void Over_the_wire_the_server_arbitrates_the_seat()
    {
        var w = new Wire();
        var a = w.Join("ALICE");
        var b = w.Join("BOB");
        w.Pump(a, b);
        a.SendPose(new Vector3(1000, 0, 0), 0f, 0f, 0, 1.0);
        b.SendPose(new Vector3(1050, 0, 0), 0f, 0f, 0, 1.0);
        w.Pump(a, b);

        // Both ask for jeep 1 in the same frame: the first message the server reads wins.
        a.RequestVehicle(1);
        b.RequestVehicle(1);
        w.Pump(a, b);
        Assert.Equal(a.LocalId, w.Host.Server.Vehicles[1].Driver);
        Assert.Equal(a.LocalId, a.Vehicles[1].Driver);
        Assert.Equal(a.LocalId, b.Vehicles[1].Driver);

        // The driver's pose carries the vehicle flag and heading to the others.
        var seen = new List<(int Flags, float CarYaw)>();
        b.PoseReceived += (id, _, _, _, flags, carYaw) =>
        {
            if (id == a.LocalId)
            {
                seen.Add((flags, carYaw));
            }
        };
        a.SendPose(new Vector3(1300, 0, 0), 2.0f, 0f, DmProtocol.FlagMoving, 1.0, carYaw: 0.5f);
        w.Pump(a, b);
        Assert.Contains(seen, s => DmProtocol.VehicleOf(s.Flags) == VehicleMode.Jeep && s.CarYaw == 0.5f);
        Assert.Equal(new Vector3(1300, 0, 0), b.Vehicles.Count > 0 ? w.Host.Server.Vehicles[1].Position : default);

        // The host shoots the jeep: the hull, not the driver, to everybody.
        var hull = new List<float>();
        a.VehicleChanged += (v, _) => hull.Add(v.Hull);
        var health = new List<float>();
        a.LocalHealthChanged += (hp, _, _) => health.Add(hp);
        w.Host.Hit(a.LocalId, 400f, 3);
        w.Pump(a, b);
        Assert.Equal([Hull.JeepPoints - 400f], hull);
        Assert.Empty(health);
        Assert.Equal(Hull.JeepPoints - 400f, b.Vehicles[1].Hull);

        // Blown up: the wreck and the driver out, alive.
        var wrecks = new List<int>();
        b.VehicleChanged += (v, why) =>
        {
            if (why == DmVehicleEvent.Wrecked)
            {
                wrecks.Add(v.Key);
            }
        };
        w.Host.Hit(a.LocalId, 1000f, 3);
        w.Pump(a, b);
        Assert.Equal([1], wrecks);
        Assert.True(a.Vehicles[1].Wrecked);
        Assert.Equal(0, a.Vehicles[1].Driver);
        Assert.True(a.Players[a.LocalId].Alive);

        // Bob takes jeep 2 and climbs out somewhere else.
        b.SendPose(new Vector3(2000, 0, 0), 0f, 0f, 0, 1.0);
        w.Pump(a, b);
        b.RequestVehicle(2);
        w.Pump(a, b);
        Assert.Equal(b.LocalId, a.Vehicles[2].Driver);
        b.LeaveVehicle(new Vector3(2100, 0, 30), 0.7f);
        w.Pump(a, b);
        Assert.Equal(0, a.Vehicles[2].Driver);
        Assert.Equal(new Vector3(2100, 0, 30), a.Vehicles[2].Position);
        Assert.Equal(0.7f, a.Vehicles[2].Yaw);
    }

    [Fact]
    public void A_late_joiner_sees_parked_driven_and_wrecked_vehicles()
    {
        var w = new Wire();
        var a = w.Join("ALICE");
        w.Pump(a);
        a.SendPose(new Vector3(3000, 0, 0), 0f, 0f, 0, 1.0);
        w.Pump(a);
        a.RequestVehicle(3);
        w.Pump(a);
        a.SendPose(new Vector3(3400, 200, 0), 1f, 0f, 0, 1.0, carYaw: 1f);
        w.Pump(a);
        w.Host.Server.Pose(1, new Vector3(2000, 0, 0), 0f, 0f, 0);
        Assert.True(w.Host.RequestVehicle(2));
        w.Host.Hit(1, 5000f, 3, attacker: a.LocalId); // jeep 2 blown up under the host

        var late = w.Join("LATE");
        var poses = new Dictionary<int, int>();
        late.PoseReceived += (id, _, _, _, flags, _) => poses[id] = flags;
        w.Pump(a, late);
        Assert.Equal(3, late.Vehicles.Count);
        Assert.Equal(0, late.Vehicles[1].Driver);
        Assert.False(late.Vehicles[1].Wrecked);
        Assert.True(late.Vehicles[2].Wrecked);
        Assert.Equal(a.LocalId, late.Vehicles[3].Driver);
        Assert.Equal(new Vector3(3400, 200, 0), late.Vehicles[3].Position);
        Assert.Equal(VehicleMode.Hk, DmProtocol.VehicleOf(poses[a.LocalId]));
        Assert.Equal(VehicleMode.Foot, DmProtocol.VehicleOf(poses[1]));
    }

    [Fact]
    public void A_driving_avatar_is_the_vehicle_s_target()
    {
        var av = new DmAvatar(2, "A", DmClass.Human);
        av.SpawnAt(Vector3.Zero, 0f);
        Assert.Equal(DmAvatar.HitRadius, av.Radius);
        av.ApplyPose(Vector3.Zero, 1f, 0f, (int)VehicleMode.Hk << DmProtocol.VehicleShift, carYaw: 0.25f);
        Assert.Equal(VehicleMode.Hk, av.Vehicle);
        Assert.Equal(VehicleController.HkRadius, av.Radius);
        Assert.Equal(0.25f, av.CarYaw);
        Assert.Equal(VehicleController.HkRadius, av.Centre.Y);
    }
}

/// <summary>DOS parks each vehicle as a map record of its own (0x1310cc): it stops bodies and shots.</summary>
public class ParkedVehicleColliderTests
{
    [Tests.Behaviour.BehaviourDataFact]
    public void A_parked_vehicle_stops_rays_and_bodies_a_driven_or_wrecked_one_does_not()
    {
        var jeep = new DmVehicle { Key = 1, Kind = VehicleMode.Jeep, Position = new Vector3(1000, 0, 0), Yaw = 0.3f };
        var hk = new DmVehicle { Key = 2, Kind = VehicleMode.Hk, Position = new Vector3(-1000, 0, 0) };
        var world = new RetroEng.Physics.CollisionWorld().Add(
            new ParkedVehicleColliders(() => [jeep, hk], new SkyNet.Game.Enemies.EnemyModels(Tests.Behaviour.BehaviourData.Data)));

        // Through the jeep's body (30 up) and the HK's cabin (110 up: under it are only its skids).
        bool Ray(float x) => world.Raycast(new Vector3(x, x < 0 ? 110 : 30, 500), new Vector3(0, 0, -1), 1000f, out _);
        Assert.True(Ray(1000));
        Assert.True(Ray(-1000));
        Assert.False(Ray(0));
        var body = new RetroEng.Physics.SphereBody(22f, new Vector3(0, 22, 0));
        Assert.True(world.Overlaps(body, new Vector3(1000, 0, 0)));

        jeep.Driver = 5;
        hk.Wrecked = true;
        Assert.False(Ray(1000));
        Assert.False(Ray(-1000));
        jeep.Driver = 0;
        jeep.Position = new Vector3(3000, 0, 0);
        Assert.False(Ray(1000));
        Assert.True(Ray(3000));
    }
}
