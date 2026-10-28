using System.Collections.Immutable;
using System.Numerics;
using RetroEng.Net;
using SkyNet.Formats;
using SkyNet.Game.Net;
using SkyNet.Game.Tests.Behaviour;

namespace SkyNet.Game.Tests.Net;

/// <summary>A host and clients over the loopback transport: the protocol end to end.</summary>
public class DmSessionTests
{
    private static readonly DmArena Arena = new()
    {
        Map = 605,
        Spawns = [new(new Vector3(0, 0, 0), 0f), new(new Vector3(10000, 0, 0), 1f)],
        ItemSpots = [new(100, 0, 100)],
    };

    private sealed class Rig
    {
        public readonly LoopbackNetwork Net = new();
        public readonly DmHost Host;

        public Rig(DmSettings? settings = null)
        {
            settings ??= new DmSettings { Items = ImmutableDictionary<string, int>.Empty.Add("health", 1) };
            Host = new DmHost(new NetEndpoint(Net.Listen(0, DmProtocol.MaxPeers)!), new DmServer(settings, new Random(3)) { LimitTravel = false }, "HOST", DmClass.Human);
            Host.Server.SetArena(Arena);
            Host.ReportLevelReady();
        }

        public DmClient Join(string name, NetEndpoint? ep = null) =>
            new(ep ?? new NetEndpoint(Net.Connect(Host.Port)), name, DmClass.Human);

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
    public void Join_welcome_spawn_and_see_each_other()
    {
        var rig = new Rig();
        var c = rig.Join("ALICE");
        var welcomed = false;
        c.Welcomed += () =>
        {
            welcomed = true;
            c.ReportLevelReady();
        };
        rig.Pump(c);
        Assert.True(welcomed);
        Assert.Equal(2, c.LocalId);
        Assert.Equal(605, c.Settings!.Map);
        Assert.Equal(["HOST", "ALICE"], c.Players.Values.OrderBy(p => p.Id).Select(p => p.Name));
        Assert.Single(c.Pickups);
        Assert.True(c.Players[2].Alive);

        // The client's pose reaches the host; the host's reaches the client.
        var seenByHost = new List<int>();
        rig.Host.PoseReceived += (id, _, _, _, _, _) => seenByHost.Add(id);
        var seenByClient = new List<int>();
        c.PoseReceived += (id, _, _, _, _, _) => seenByClient.Add(id);
        c.SendPose(new Vector3(50, 0, 50), 1f, 0f, DmProtocol.FlagMoving, 1.0);
        rig.Host.SendPose(new Vector3(9, 0, 9), 0f, 0f, 0, 1.0);
        rig.Pump(c);
        Assert.Contains(2, seenByHost);
        Assert.Contains(1, seenByClient);
        Assert.Equal(new Vector3(50, 0, 50), rig.Host.Server.Players[2].Position);
    }

    [Fact]
    public void The_weapon_in_hand_travels_with_the_pose()
    {
        // DOS's player byte +0x20: the others draw the gun the player holds, switched without a shot.
        var rig = new Rig();
        var c = rig.Join("ALICE");
        c.Welcomed += c.ReportLevelReady;
        rig.Pump(c);
        var byHost = -1;
        var byClient = -1;
        rig.Host.PoseReceived += (_, _, _, _, flags, _) => byHost = DmProtocol.WeaponOf(flags);
        c.PoseReceived += (id, _, _, _, flags, _) => byClient = id == 1 ? DmProtocol.WeaponOf(flags) : byClient;
        c.SendPose(Vector3.Zero, 0f, 0f, DmProtocol.WeaponBits(4), 1.0);
        rig.Host.SendPose(new Vector3(100, 0, 0), 0f, 0f, DmProtocol.WeaponBits(0), 1.0);
        rig.Pump(c);
        Assert.Equal(4, byHost);
        Assert.Equal(4, rig.Host.Server.Players[2].Weapon);
        Assert.Equal(0, byClient);
        Assert.Equal(-1, DmProtocol.WeaponOf(0));
    }

    [Fact]
    public void A_hit_kill_respawn_round_trip()
    {
        var rig = new Rig();
        var c = rig.Join("BOB");
        c.Welcomed += c.ReportLevelReady;
        rig.Pump(c);
        c.SendPose(Vector3.Zero, 0, 0, 0, 1.0);
        rig.Host.SendPose(new Vector3(100, 0, 0), 0, 0, 0, 1.0);
        rig.Pump(c);

        // The client hits the host: the host's own health event.
        var hostHealth = new List<(float Hp, int Attacker)>();
        rig.Host.LocalHealthChanged += (hp, _, a) => hostHealth.Add((hp, a));
        c.SendHit(1, 30f, 2);
        rig.Pump(c);
        Assert.Equal([(DmProtocol.DosHealth - 30f, 2)], hostHealth);

        // The host kills the client: health to the victim, the death and scores to everyone.
        var clientHealth = new List<float>();
        c.LocalHealthChanged += (hp, _, _) => clientHealth.Add(hp);
        var deaths = new List<(int, int)>();
        c.Died += (v, k, _) => deaths.Add((v, k));
        var respawns = new List<int>();
        c.Respawned += (id, _) => respawns.Add(id);
        rig.Host.Hit(2, 5000f, 3);
        rig.Pump(c);
        Assert.Equal([0f], clientHealth);
        Assert.Equal([(2, 1)], deaths);
        Assert.Equal(1, c.Players[1].Frags);
        Assert.Equal(1, c.Players[2].Deaths);
        Assert.Equal(c.Players[1].Points, rig.Host.Server.Players[1].Points);
        Assert.False(c.Players[2].Alive);

        rig.Host.Update(DmProtocol.RespawnDelay);
        rig.Pump(c);
        Assert.Equal([2], respawns);
        Assert.True(c.Players[2].Alive);
    }

    [Fact]
    public void Pickups_chat_and_leaving()
    {
        var rig = new Rig();
        var a = rig.Join("A");
        var b = rig.Join("B");
        a.Welcomed += a.ReportLevelReady;
        b.Welcomed += b.ReportLevelReady;
        rig.Pump(a, b);
        Assert.Equal(3, b.Players.Count);

        var key = a.Pickups.Keys.Single();
        a.SendPose(new Vector3(100, 0, 100), 0, 0, 0, 1.0);
        rig.Pump(a, b);
        var taken = new List<(int, int)>();
        b.PickupTaken += (k, by) => taken.Add((k, by));
        a.RequestPickup(key);
        rig.Pump(a, b);
        Assert.Equal([(key, a.LocalId)], taken);
        Assert.True(b.Pickups[key].Taken);

        var lines = new List<(int, string)>();
        b.ChatReceived += (from, t) => lines.Add((from, t));
        a.SendChat("  gg ");
        rig.Pump(a, b);
        Assert.Equal([(a.LocalId, "gg")], lines);

        var left = new List<int>();
        b.PlayerLeft += left.Add;
        a.Leave();
        rig.Pump(a, b);
        Assert.Equal([a.LocalId], left);
        Assert.False(rig.Host.Server.Players.ContainsKey(a.LocalId));
    }

    [Fact]
    public void A_clients_avatar_reaches_the_other_clients()
    {
        var rig = new Rig();
        var a = new DmClient(new NetEndpoint(rig.Net.Connect(rig.Host.Port)), "ALICE", DmClass.Human, avatar: 10);
        var b = new DmClient(new NetEndpoint(rig.Net.Connect(rig.Host.Port)), "BORG", DmClass.Terminator, avatar: 13);
        rig.Pump(a, b);
        // A late joiner gets it in the welcome's roster, an earlier one in the player's arrival.
        Assert.Equal((10, 13), (b.Players[a.LocalId].Avatar, a.Players[b.LocalId].Avatar));
        Assert.Equal((10, 13), (rig.Host.Server.Players[a.LocalId].Avatar, rig.Host.Server.Players[b.LocalId].Avatar));
        Assert.Equal(1, a.Players[DmServer.HostId].Avatar);

        // A new avatar for the next life reaches everyone at the respawn.
        a.Welcomed += a.ReportLevelReady;
        b.SetClass(DmClass.Human, 5);
        rig.Pump(a, b);
        rig.Host.Server.Respawn(b.LocalId);
        rig.Pump(a, b);
        Assert.Equal((DmClass.Human, 5), (a.Players[b.LocalId].Class, a.Players[b.LocalId].Avatar));
    }

    [Fact]
    public void The_server_keeps_only_an_avatar_of_the_players_class()
    {
        var rig = new Rig();
        var ep = new NetEndpoint(rig.Net.Connect(rig.Host.Port));
        // A machine asking for the soldier's look, by hand on the wire: the machine's default instead.
        ep.Connected += _ => ep.Send(INetTransport.ServerPeer,
            new NetWriter().Begin(DmMsg.Hello).I32(DmProtocol.Version).U8((byte)DmClass.Terminator).Str("ODD").U8(0), Channel.Reliable);
        var ep2 = new NetEndpoint(rig.Net.Connect(rig.Host.Port));
        ep2.Connected += _ => ep2.Send(INetTransport.ServerPeer,
            new NetWriter().Begin(DmMsg.Hello).I32(DmProtocol.Version).U8((byte)DmClass.Human).Str("FAR").U8(200), Channel.Reliable);
        for (var i = 0; i < 4; i++)
        {
            rig.Host.Update(0.01);
            ep.Poll();
            ep2.Poll();
        }

        var players = rig.Host.Server.Players.Values.ToDictionary(p => p.Name, p => p.Avatar);
        Assert.Equal((DmAvatarTable.MachineSoldier, 1), (players["ODD"], players["FAR"]));

        // Bots wear one of their class's avatars.
        for (var i = 0; i < 12; i++)
        {
            var bot = rig.Host.Server.AddBot(i % 2 == 0 ? DmClass.Human : DmClass.Terminator);
            Assert.Equal(bot.Class, DmAvatarTable.Get(bot.Avatar).Class);
            Assert.Contains(bot.Avatar, DmAvatarTable.OfClass(bot.Class));
        }
    }

    [Fact]
    public void Wrong_version_is_kicked_with_the_reason()
    {
        var rig = new Rig();
        var ep = new NetEndpoint(rig.Net.Connect(rig.Host.Port));
        string? reason = null;
        ep.Disconnected += (_, _, r) => reason = r;
        ep.Connected += _ => ep.Send(INetTransport.ServerPeer, new NetWriter().Begin(DmMsg.Hello).I32(DmProtocol.Version + 1).U8(0).Str("OLD"), Channel.Reliable);
        for (var i = 0; i < 4; i++)
        {
            rig.Host.Update(0.01);
            ep.Poll();
        }

        Assert.Contains("Version mismatch", reason);
        Assert.Single(rig.Host.Server.Players);
    }

    [Fact]
    public void Silent_peers_are_dropped_and_full_servers_say_so()
    {
        var rig = new Rig(new DmSettings { MaxPlayers = 2 });
        var silent = new NetEndpoint(rig.Net.Connect(rig.Host.Port));
        string? silentReason = null;
        silent.Disconnected += (_, _, r) => silentReason = r;
        rig.Host.Update(0.01);
        rig.Host.Update(DmProtocol.HelloTimeout + 0.1);
        silent.Poll();
        silent.Poll();
        Assert.Equal("No hello from this client.", silentReason);

        var a = rig.Join("A");
        rig.Pump(a);
        Assert.True(a.Active);
        var b = rig.Join("B");
        string? why = null;
        b.Disconnected += r => why = r;
        rig.Pump(a, b);
        Assert.Equal("Server is full.", why);
    }

    [BehaviourDataFact]
    public void Every_dos_arena_has_spawns_and_item_spots()
    {
        foreach (var level in NetLevelList.Load(BehaviourData.Data))
        {
            var arena = DmArena.Load(BehaviourData.Data, level.Map);
            Assert.NotNull(arena);
            Assert.True(arena.Spawns.Count >= 2, $"{level.MapName}: {arena.Spawns.Count} spawns");
            Assert.True(arena.ItemSpots.Count > 0, $"{level.MapName}: no item spots");
            Assert.All(arena.Spawns, s => Assert.True(DmProtocol.SanePos(s.Feet)));
        }
    }
}
