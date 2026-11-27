using System.Net;
using System.Numerics;
using RetroEng.Loop;
using SkyNet.Formats;
using SkyNet.Game.Campaign;
using SkyNet.Game.Net;
using SkyNet.Game.Player;
using SkyNet.Game.Tests.Behaviour;
using SkyNet.Game.Vehicles;
using SkyNet.Game.Weapons;

namespace SkyNet.Game.Tests.Net;

/// <summary>Owner report (MAP.601 Freight Yard, demo): the soldier walked into a parked HK on a
/// deck. Every parked jeep and HK of every arena stops him, from every side.</summary>
[Collection(DmUdpCollection.Name)]
public class ParkedBlockTests
{
    /// <summary>Where the walk starts, from the vehicle's marker.</summary>
    private const float StartOut = 260f;

    /// <summary>Feet this far above the vehicle's floor still stand on that floor (not on its roof).</summary>
    private const float OnFloor = 10f;

    /// <summary>How deep into the hull's box the feet must get to count as inside.</summary>
    private const float Margin = 10f;

    public static TheoryData<int> Maps => [.. NetLevelList.BuiltInMaps];

    /// <summary>The parked hulls' half extents across × along (0x51F54 / 0x51FFE).</summary>
    private static (float Across, float Along) Half(VehicleMode kind) =>
        kind == VehicleMode.Hk ? (50f, 115f) : (57f, 72f);

    [BehaviourDataTheory]
    [MemberData(nameof(Maps))]
    public void The_soldier_cannot_walk_into_a_parked_vehicle(int map)
    {
        var session = new GameSession(BehaviourData.Data);
        _ = new VehicleRig(session);
        var options = new DmRunOptions { HostMap = map, Port = 0, Bind = IPAddress.Loopback, Discovery = false, Name = "HOST" };
        using var game = DmGame.StartHost(session, options, out var error);
        Assert.True(game != null, error);

        var ahead = new TickInput(TickInput.Bit(0), 0, 0, 0, 0, 0, 0, 0);
        var keys = new PlayerActions(0, 1, 2, 3, 4, 5, 6);
        var fails = new List<string>();
        foreach (var v in game.Vehicles.Values.ToList())
        {
            for (var side = 0; side < 8; side++)
            {
                // Start on the vehicle's floor, face its marker, walk 3 s.
                var a = side * MathF.PI / 4f;
                var start = v.Position + new Vector3(MathF.Cos(a), 0f, MathF.Sin(a)) * StartOut;
                var to = v.Position - start;
                session.Player.Spawn(start + new Vector3(0f, 2f, 0f), MathF.Atan2(-to.X, -to.Z));
                for (var t = 0; t < 3 * GameSession.TicksPerSecond; t++)
                {
                    session.Tick(ahead, keys, new WeaponInput(false, false, false), false);
                    game.Frame(GameSession.Dt);
                    if (Inside(v, session.Player.Position))
                    {
                        fails.Add($"{v.Kind} {v.Key} at {v.Position} from side {side}: in at {session.Player.Position}");
                        break;
                    }
                }
            }
        }

        Assert.True(fails.Count == 0, string.Join("\n", fails));
    }

    /// <summary>The feet on the vehicle's floor within its hull's box (in its frame).</summary>
    private static bool Inside(DmVehicle v, Vector3 feet)
    {
        var d = feet - v.Position;
        if (MathF.Abs(d.Y) > OnFloor)
        {
            return false; // below the deck or on the roof: not in the hull
        }

        var (across, along) = Half(v.Kind);
        var x = MathF.Abs(Vector3.Dot(d, new Vector3(MathF.Cos(v.Yaw), 0f, -MathF.Sin(v.Yaw))));
        var z = MathF.Abs(Vector3.Dot(d, new Vector3(-MathF.Sin(v.Yaw), 0f, -MathF.Cos(v.Yaw))));
        return x < across - Margin && z < along - Margin;
    }
}
