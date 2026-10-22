using System.Numerics;
using RetroEng.Net;
using RetroEng.Physics;
using SkyNet.Game.Campaign;
using SkyNet.Game.Navigation;
using SkyNet.Game.Net;
using SkyNet.Game.Tests.Behaviour;
using SkyNet.Game.Weapons;
using Xunit.Abstractions;

namespace SkyNet.Game.Tests.Navigation;

/// <summary>Bots on the nav mesh: out of a cup that traps the straight walk, and an arena's
/// minute with and without the mesh side by side.</summary>
public class BotNavTests(ITestOutputHelper output)
{
    private const float Dt = 1f / 60f;

    private sealed class Quiet : IBotOutput
    {
        public readonly List<int> Pickups = [];

        public void Pose(int id, Vector3 feet, float yaw, float pitch, int flags) { }
        public void Fire(int id, int weapon, Vector3 from, Vector3 dir) { }
        public void Hit(int id, int victim, float points, int weapon) { }
        public void SelfHit(int id, float points, int weapon) { }
        public void Pickup(int id, int key) => Pickups.Add(key);
    }

    /// <summary>A cup open towards the bot, between it and the item:
    /// <code>
    ///        item (0, −1500)
    ///   ┌───────────────┐  z −600
    ///   │               │
    ///   │      bot      │  z −100 .. 0
    /// </code></summary>
    private static CollisionWorld Cup() => NavMeshTests.World(NavMeshTests.Floor()
        .Concat(NavMeshTests.Block(new(-500, 0, -620), new(500, 300, -600)))
        .Concat(NavMeshTests.Block(new(-520, 0, -620), new(-500, 300, 100)))
        .Concat(NavMeshTests.Block(new(500, 0, -620), new(520, 300, 100))));

    private static readonly Vector3 Item = new(0, 0, -1500);

    /// <summary>Seconds until the bot reaches the item behind the cup (its goal); null: not within the limit.</summary>
    private static (float? Took, int Stuck) Fetch(CollisionWorld world, NavMesh? nav, float limit)
    {
        var server = new DmServer(new DmSettings(), new Random(1));
        var b = server.AddBot(DmClass.Human);
        b.Alive = true;
        var bot = new BotBrain(b.Id, 1, world, WeaponTable.Shipped, new Random(7), nav);
        bot.Spawn(new Vector3(0, 1, 0), 0f);
        server.SetArena(new DmArena { Map = 605, Spawns = [new DmSpawn(new Vector3(0, 1, 0), 0f)], ItemSpots = [Item] });
        server.Drop(25601, Item);
        var rec = new Quiet();
        for (var t = 0f; t < limit; t += Dt)
        {
            bot.Step(Dt, server, rec);
            var d = bot.Body.Position - Item;
            if (new Vector2(d.X, d.Z).Length() < BotBrain.GoalReached)
            {
                return (t, bot.Stuck);
            }
        }

        return (null, bot.Stuck);
    }

    [Fact]
    public void A_bot_on_the_mesh_walks_out_of_the_cup_to_the_item()
    {
        var world = Cup();
        var nav = NavMesh.Build(world, NavAgents.Soldier, NavAgents.SoldierCells);
        var (straight, straightStuck) = Fetch(world, null, 30f);
        var (pathed, pathedStuck) = Fetch(world, nav, 30f);
        output.WriteLine($"straight: {straight?.ToString("0.0") ?? "never"} s, stuck {straightStuck}; nav: {pathed?.ToString("0.0") ?? "never"} s, stuck {pathedStuck}");

        Assert.NotNull(pathed);
        Assert.True(pathed < 10f, $"took {pathed:0.0} s");
        Assert.Equal(0, pathedStuck);
    }

    /// <summary>Three bots, two minutes on MAP.605, straight then on the mesh: how often they stuck,
    /// how far they walked, what they took.</summary>
    [BehaviourDataFact]
    public void Three_bots_on_map_605_stick_less_on_the_mesh()
    {
        var straight = Match(nav: false);
        var pathed = Match(nav: true);
        output.WriteLine($"straight: stuck {straight.Stuck}, walked {straight.Walked:0}, pickups {straight.Pickups}, frags {straight.Frags}");
        output.WriteLine($"nav:      stuck {pathed.Stuck}, walked {pathed.Walked:0}, pickups {pathed.Pickups}, frags {pathed.Frags}");
        Assert.True(pathed.Stuck < straight.Stuck, $"stuck {pathed.Stuck} on the mesh, {straight.Stuck} without");
    }

    private (int Stuck, float Walked, int Pickups, int Frags) Match(bool nav)
    {
        var session = new GameSession(BehaviourData.Data);
        Assert.True(session.StartDeathmatch(605));
        var arena = DmBots.Cleared(DmArena.Load(BehaviourData.Data, 605)!, session);
        var net = new LoopbackNetwork();
        var items = SkyNet.Formats.NetLevelList.For(SkyNet.Formats.NetLevelList.Load(BehaviourData.Data), 605).Items;
        var settings = new DmSettings { Map = 605, Bots = 3, BotSkill = 1, Items = items };
        using var host = new DmHost(new NetEndpoint(net.Listen(0, DmProtocol.MaxPeers)!), new DmServer(settings, new Random(11)), "HOST", DmClass.Human);
        host.Server.SetArena(arena);
        host.ReportLevelReady();
        var bots = DmBots.Start(host, session.World, session.Weapons.Table, seed: 42, nav: nav ? DmNav.For(session) : null);
        var pickups = 0;
        host.Server.PickupTaken += (_, by) => pickups += by < 0 ? 1 : 0;
        for (var i = 0; i < 120 * 60; i++)
        {
            host.Update(Dt);
        }

        var frags = host.Server.Players.Values.Where(p => p.Bot).Sum(p => p.Frags);
        return (bots.Brains.Values.Sum(b => b.Stuck), bots.Brains.Values.Sum(b => b.Walked), pickups, frags);
    }
}
