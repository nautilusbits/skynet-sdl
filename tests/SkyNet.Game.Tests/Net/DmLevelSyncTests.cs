using System.Collections.Immutable;
using System.Numerics;
using RetroEng.Net;
using SkyNet.Formats;
using SkyNet.Game.Behaviour;
using SkyNet.Game.Campaign;
using SkyNet.Game.Net;
using SkyNet.Game.Tests.Behaviour;

namespace SkyNet.Game.Tests.Net;

/// <summary>
/// Step 8: the map's own state over the loopback wire. The server's level owns the doors, the
/// switches and the destructibles; a client's use key and shots are intents the server runs through
/// its level; what changed comes back as rows every client lays over its own copy of the map; a
/// late joiner gets the whole overlay after its welcome. The doors are MAP.604's (gates driving
/// movers: the Godot port's net_smoke_test DOOR_ARENA, the only arena with doors), the destructibles
/// MAP.605's.
/// </summary>
public class DmLevelSyncTests
{
    private const int DoorArena = 604, CrateArena = 605;
    private const float Dt = 1f / 60f;
    private static readonly Vector3 Away = new(-1e7f, -1e7f, -1e7f);

    private static LevelBehaviour Level(int arena)
    {
        var map = BehaviourData.Data.LoadMap(arena) ?? throw new InvalidOperationException($"MAP.{arena} missing");
        var b = new LevelBehaviour(map, BehaviourData.Transfrm, new MissionProgress(0));
        b.Start(Away, Away);
        return b;
    }

    /// <summary>A server on an arena and its clients, each with its own level, over loopback.</summary>
    private sealed class Rig
    {
        public readonly LoopbackNetwork Net = new();
        public readonly DmHost Host;
        public readonly int Arena;
        public readonly LevelBehaviour Server;
        public readonly List<(DmClient Client, DmLevelClient Level)> Clients = [];

        public Rig(int arena)
        {
            Arena = arena;
            Server = Level(arena);
            var settings = new DmSettings { Map = Arena, Items = ImmutableDictionary<string, int>.Empty };
            Host = new DmHost(new NetEndpoint(Net.Listen(0, DmProtocol.MaxPeers)!), new DmServer(settings, new Random(5)) { LimitTravel = false }, "HOST", DmClass.Human);
            Host.Server.SetArena(new DmArena { Map = Arena, Spawns = [new(Vector3.Zero, 0f)], ItemSpots = [] });
            Host.ReportLevelReady();
            Host.Level = new DmLevelServer(Server);
        }

        public (DmClient Client, DmLevelClient Level) Join(string name)
        {
            var c = new DmClient(new NetEndpoint(Net.Connect(Host.Port)), name, DmClass.Human);
            var level = new DmLevelClient(Level(Arena), i => c.SendLevelIntent(i));
            c.Welcomed += c.ReportLevelReady;
            c.LevelState += u => Assert.True(level.Apply(u));
            Clients.Add((c, level));
            Pump();
            Assert.True(c.Active);
            return (c, level);
        }

        /// <summary>The wire both ways, then one tick of every level (the server's player away).</summary>
        public void Pump(int frames = 4)
        {
            for (var i = 0; i < frames; i++)
            {
                Host.Update(Dt);
                foreach (var (c, _) in Clients)
                {
                    c.Update(Dt);
                }

                Server.Tick(Dt, new PlayerProbe(Away, Away));
                foreach (var (_, l) in Clients)
                {
                    l.Behaviour.Tick(Dt, new PlayerProbe(Away, Away));
                }
            }
        }

        /// <summary>Puts client <paramref name="c"/>'s body (its pose on the server) with its eye at <paramref name="eye"/>.</summary>
        public void Stand(DmClient c, Vector3 eye)
        {
            c.SendPose(eye - new Vector3(0f, Player.PlayerController.EyeHeight, 0f), 0f, 0f, 0, 1.0);
            Pump(1);
        }
    }

    private static void SameLevel(LevelBehaviour expected, LevelBehaviour actual)
    {
        foreach (var off in expected.Records)
        {
            Assert.Equal(expected.Capture(off), actual.Capture(off));
        }
    }

    [Fact]
    public void A_delta_is_the_same_bytes_every_time()
    {
        LevelNetRecord[] rows =
        [
            new(0x0123, 0x05, 0xEF, 0x0456, NetRecordFlags.Turned),
            new(0x0456, 0x01, 0x5F, 0, NetRecordFlags.Mover, Progress: 37.5f, Dir: -1, Running: true),
            new(0x0789, 0x06, 0x18, 0, NetRecordFlags.HasHp | NetRecordFlags.Wreck | NetRecordFlags.Spent | NetRecordFlags.NoCollide,
                Hp: 120.25f, Stage: 3, Accum: 48f),
            new(0x0abc, 0x00, 0x01, 0, NetRecordFlags.Light | NetRecordFlags.CueSpent | NetRecordFlags.Hidden, LightEnable: -1, LightIntensity: 200),
        ];
        var update = new LevelUpdate(false, unchecked((int)0xDEADBEEF), rows, [0x0123, 0x0789]);
        var w = new NetWriter();
        update.Write(w);
        var first = w.ToArray();
        update.Write(w);
        Assert.Equal(first, w.ToArray());

        var r = new NetReader(first);
        Assert.Equal(DmMsg.LevelState, r.U8());
        var back = LevelUpdate.Read(ref r);
        Assert.True(r.End);
        Assert.Equal(update.Whole, back.Whole);
        Assert.Equal(update.Signature, back.Signature);
        Assert.Equal(rows, back.Records);
        Assert.Equal(update.Fired, back.Fired);
        back.Write(w);
        Assert.Equal(first, w.ToArray());

        // A mover's travel is carried but is no news by itself; its direction is.
        Assert.True(rows[1].SameAs(rows[1] with { Progress = 99f, Running = false }));
        Assert.False(rows[1].SameAs(rows[1] with { Dir = 1 }));

        // A short or poisoned message is refused, as every handler's read is.
        Assert.Throws<NetReadException>(() =>
        {
            var cut = new NetReader(first.AsSpan(1, first.Length - 3));
            LevelUpdate.Read(ref cut);
        });
        var nan = (byte[])first.Clone();
        // header 8, a plain row 12, a mover's 18, then the third row's 12 and its hit points.
        BitConverter.TryWriteBytes(nan.AsSpan(8 + 12 + 18 + 12), float.NaN);
        Assert.Throws<NetReadException>(() =>
        {
            var bad = new NetReader(nan.AsSpan(1));
            LevelUpdate.Read(ref bad);
        });
    }

    [BehaviourDataFact]
    public void A_door_a_client_uses_opens_everywhere_and_a_late_joiner_walks_into_it_open()
    {
        var rig = new Rig(DoorArena);
        var server = rig.Server;
        var gate = server.Triggers.FirstOrDefault(t => server.Act(t) == ActRules.ProxGate && server.KindOf(server.Link(t)) == NodeKind.Mover, -1);
        Assert.True(gate >= 0, $"MAP.{DoorArena} has a gate that drives a mover");
        var door = server.Link(gate);
        Assert.True(server.TryPositionOf(gate, out var at));

        var (alice, aliceLevel) = rig.Join("ALICE");
        var mirror = aliceLevel.Behaviour;
        Assert.Equal(LevelRole.Client, mirror.Role);
        Assert.False(server.Enabled(door));

        // The client's use key at the gate: nothing moves on the client by itself; the intent
        // goes to the server.
        rig.Stand(alice, at);
        var sent = new List<LevelIntent>();
        var send = mirror.Intent!;
        mirror.Intent = i =>
        {
            sent.Add(i);
            send(i);
        };
        var before = mirror.Capture(gate);
        mirror.Tick(Dt, new PlayerProbe(at, at, -1, UsePressed: true, AimedOwner: gate));
        Assert.Equal([new LevelIntent(LevelIntentKind.Use, gate, at, 0f)], sent);
        Assert.Equal(before, mirror.Capture(gate));
        Assert.False(mirror.Enabled(door));

        rig.Pump();
        Assert.True(rig.Host.Level!.IntentsRun > 0);
        Assert.True(server.MoverRunning(door) || server.MoverProgress(door).Progress > 0f, "the server's door is set going");
        Assert.True(mirror.MoverRunning(door) || mirror.MoverProgress(door).Progress > 0f, "the client's door is set going");

        // Both animate it from that one flip, and stop when the server's handler clears the bit.
        rig.Pump(60 * 30);
        Assert.False(server.Enabled(door));
        Assert.True(server.MoverProgress(door).Progress > 0f, "the door travelled");
        SameLevel(server, mirror);

        // A late joiner: its welcome brings the whole overlay, so the door stands open before it
        // plays a single tick.
        var (_, bobLevel) = rig.Join("BOB");
        Assert.True(bobLevel.Applied > 0);
        Assert.True(bobLevel.Applied < server.Records.Count(), "the overlay is sparse");
        Assert.Equal(server.MoverProgress(door), bobLevel.Behaviour.MoverProgress(door));
        SameLevel(server, bobLevel.Behaviour);

        // A quiet arena sends nothing.
        rig.Pump();
        Assert.Null(rig.Host.Level!.Delta());
    }

    [BehaviourDataFact]
    public void A_crate_a_client_shoots_breaks_everywhere_and_a_late_joiner_finds_it_gone()
    {
        var rig = new Rig(CrateArena);
        var server = rig.Server;
        var (alice, aliceLevel) = rig.Join("ALICE");
        var mirror = aliceLevel.Behaviour;

        // The hits are intents, the server's level breaks it, every client sees it gone.
        var crate = server.Records.FirstOrDefault(o => server.IsDamageable(o) && server.HasHp(o) && server.KindOf(o) != NodeKind.Mover, -1);
        Assert.True(crate >= 0, $"MAP.{CrateArena} has a record with hit points");
        Assert.True(server.TryPositionOf(crate, out var crateAt));
        rig.Stand(alice, crateAt + new Vector3(0f, 0f, 300f));
        var hp = server.Hp(crate);
        Assert.False(mirror.Hit(crate, 50f)); // costs it nothing here
        Assert.Equal(hp, mirror.Hp(crate));
        for (var i = 0; i < 400 && !server.Spent(crate); i++)
        {
            mirror.Hit(crate, DmProtocol.MaxHitDamage);
            rig.Pump(2);
        }

        rig.Pump();
        Assert.True(server.Spent(crate), "the server's crate broke");
        Assert.True(mirror.Spent(crate), "the client saw it break");
        Assert.False(mirror.IsDamageable(crate));
        SameLevel(server, mirror);

        // A late joiner finds it gone.
        var (_, bobLevel) = rig.Join("BOB");
        Assert.True(bobLevel.Behaviour.Spent(crate));
        Assert.True(bobLevel.Applied > 0);
        Assert.True(bobLevel.Applied < server.Records.Count(), "the overlay is sparse");
        SameLevel(server, bobLevel.Behaviour);

        // A quiet arena sends nothing.
        rig.Pump();
        Assert.Null(rig.Host.Level!.Delta());
    }

    [BehaviourDataFact]
    public void Forged_level_intents_are_refused()
    {
        var rig = new Rig(CrateArena);
        var server = rig.Server;
        var (alice, _) = rig.Join("ALICE");
        var level = rig.Host.Level!;
        var crate = server.Records.First(o => server.IsDamageable(o) && server.HasHp(o));
        Assert.True(server.TryPositionOf(crate, out var at));
        var hp = server.Hp(crate);
        var srv = rig.Host.Server;

        // From across the map, an unknown sender, an unknown record, a bad amount.
        rig.Stand(alice, at + new Vector3(DmProtocol.MaxHitRange * 2f, 0f, 0f));
        Assert.False(level.ClientHit(srv, 2, crate, 100f));
        rig.Stand(alice, at);
        Assert.False(level.ClientHit(srv, 987654, crate, 100f));
        Assert.False(level.ClientHit(srv, 2, 0x7FFFFF, 100f));
        Assert.False(level.ClientHit(srv, 2, crate, float.NaN));
        Assert.False(level.ClientHit(srv, 2, crate, -100f));
        Assert.Equal(hp, server.Hp(crate));
        // A use from far away is refused before the level sees it.
        Assert.False(level.ClientUse(srv, 2, at + new Vector3(5000f, 0f, 0f), crate));
        Assert.Equal(0, level.IntentsRun);

        // A real one lands, clamped like a hit on a player.
        Assert.True(level.ClientHit(srv, 2, crate, 1e9f));
        Assert.Equal(1, level.IntentsRun);
        Assert.True(server.Spent(crate) || hp - server.Hp(crate) == DmProtocol.MaxHitDamage);
    }

    [BehaviourDataFact]
    public void A_server_of_other_map_data_is_refused_whole()
    {
        var mirror = new DmLevelClient(Level(DoorArena), _ => { });
        var server = new DmLevelServer(Level(DoorArena));
        var door = server.Behaviour.Movers.First();
        var row = server.Behaviour.Capture(door) with { State = 1, Progress = 10f };
        Assert.False(mirror.Apply(new LevelUpdate(true, server.Signature ^ 1, [row], [])));
        Assert.False(mirror.InStep);
        Assert.Equal(0f, mirror.Behaviour.MoverProgress(door).Progress);
        Assert.False(mirror.Apply(new LevelUpdate(false, server.Signature, [row], [])));
    }

    [BehaviourDataFact]
    public void A_hosts_broken_crate_drops_a_server_item()
    {
        var session = new GameSession(BehaviourData.Data);
        var drops = new List<(int Sprite, Vector3 At)>();
        // The drop is the generator's pick from the crate's list (some entries drop nothing): a few
        // seeds over the arenas until one drops an item.
        foreach (var (seed, map) in Enumerable.Range(1, 8).SelectMany(seed => new[] { 604, 605, 600, 601, 602, 603, 606, 607, 608, 609 }.Select(m => (seed, m))))
        {
            if (!session.StartDeathmatch(map))
            {
                continue;
            }

            session.Random.Reseed((ulong)seed);

            session.DeathmatchDrop = (sprite, at) => drops.Add((sprite, at));
            var b = session.Behaviour!;
            foreach (var off in b.Records.Where(o => b.IsDamageable(o) && b.HasHp(o)).ToList())
            {
                b.Hit(off, b.Hp(off) + 1f);
            }

            if (drops.Count > 0)
            {
                break;
            }
        }

        Assert.NotEmpty(drops);
        Assert.All(drops, d => Assert.True(PickupTable.Items.ContainsKey(d.Sprite)));
        Assert.Empty(session.PickupsLeft); // none of the session's own
        var server = new DmServer(new DmSettings());
        var item = server.Drop(drops[0].Sprite, drops[0].At);
        Assert.NotNull(item);
        Assert.True(item.Drop);
    }
}
