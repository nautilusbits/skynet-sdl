using System.Globalization;
using System.Numerics;
using RetroEng.Loop;
using RetroEng.Net;
using SkyNet.Game.Behaviour;
using SkyNet.Game.Campaign;
using SkyNet.Game.Demos;
using SkyNet.Game.Net;
using SkyNet.Game.Tests.Behaviour;
using SkyNet.Game.Weapons;

namespace SkyNet.Game.Tests.Demos;

/// <summary>Network demos (docs/demos.md "Network demos"): the file round trip, the protocol refusal,
/// and a host with bots and two clients over loopback, recorded on a client and on the host and
/// played back with no server: the replayed client's players, scores, bodies, items, vehicles and
/// level rows are the live client's at sampled times, and after a seek to a keyframe.</summary>
public class NetDemoTests
{
    private const float Dt = 1f / 60f;

    [Fact]
    public void A_network_demo_reads_back_as_written_and_a_keyframe_unpacks()
    {
        var d = new NetDemoData();
        d.SetField(NetDemo.KeyProtocol, DmProtocol.Version.ToString(CultureInfo.InvariantCulture));
        d.SetField("arena", "TEST");
        var key = NetDemoFile.Pack([(NetDemoKind.Message, [DmMsg.Time, 1, 0, 0, 0]), (NetDemoKind.Local, [NetDemoLocal.State, 1])]);
        d.Entries.Add(new(0, NetDemoKind.Keyframe, key));
        d.Entries.Add(new(17, NetDemoKind.Message, [DmMsg.ChatOf, 9, 9]));
        d.Entries.Add(new(17, NetDemoKind.Local, [NetDemoLocal.Fire]));
        d.Entries.Add(new(10017, NetDemoKind.Keyframe, key));
        var bytes = NetDemoFile.Write(d);
        Assert.True(NetDemoFile.IsNetDemo(bytes));
        Assert.False(NetDemoFile.IsNetDemo(DemoFile.Magic));
        var back = NetDemoFile.Read(bytes);
        Assert.Equal(d.Fields, back.Fields);
        Assert.Equal(d.Entries.Select(e => (e.Time, e.Kind, Convert.ToHexString(e.Data))), back.Entries.Select(e => (e.Time, e.Kind, Convert.ToHexString(e.Data))));
        Assert.Equal([0, 3], back.Keyframes());
        Assert.Equal(0, back.KeyframeAt(5000));
        Assert.Equal(3, back.KeyframeAt(20000));
        var entries = NetDemoFile.Unpack(back.Entries[3].Data);
        Assert.Equal(2, entries.Count);
        Assert.Equal(NetDemoKind.Local, entries[1].Kind);

        // A damaged file, another format version, a campaign demo: refused with a reason.
        var damaged = (byte[])bytes.Clone();
        damaged[^3] ^= 0x55;
        Assert.Contains("damaged", Assert.Throws<DemoFormatException>(() => NetDemoFile.Read(damaged)).Message);
        var later = (byte[])bytes.Clone();
        later[8] = 2;
        Assert.Contains("format 2", Assert.Throws<DemoFormatException>(() => NetDemoFile.Read(later)).Message);
        Assert.Throws<DemoFormatException>(() => NetDemoFile.Read(DemoFile.Write(new DemoData())));
    }

    [Fact]
    public void A_demo_of_another_network_protocol_is_refused_with_both_versions()
    {
        var d = new NetDemoData();
        d.Entries.Add(new(0, NetDemoKind.Keyframe, NetDemoFile.Pack([])));
        d.SetField(NetDemo.KeyProtocol, (DmProtocol.Version - 1).ToString(CultureInfo.InvariantCulture));
        var why = NetDemo.Refusal(d, null, []);
        Assert.NotNull(why);
        Assert.Contains($"network protocol {DmProtocol.Version - 1}", why);
        Assert.Contains($"network protocol {DmProtocol.Version} only", why);
        d.SetField(NetDemo.KeyProtocol, DmProtocol.Version.ToString(CultureInfo.InvariantCulture));
        Assert.Null(NetDemo.Refusal(d, null, []));
        Assert.Equal("the demo has no keyframe to start from", NetDemo.Refusal(new NetDemoData { Fields = { new(NetDemo.KeyProtocol, DmProtocol.Version.ToString(CultureInfo.InvariantCulture)) } }, null, []));
    }

    /// <summary>What a client shows of the game, for comparing a live one with its replay.</summary>
    private sealed record View(
        string Players, string Pickups, string Vehicles, string Level, string? RoundOver, float Health, bool Spawned,
        Dictionary<int, (bool Alive, Vector3 At, bool Visible)> Bodies);

    private static View Look(DmGame g, GameSession s) => new(
        string.Join(';', g.Players.Values.OrderBy(p => p.Id).Select(p => $"{p.Id}:{p.Name}:{p.Class}:{p.Frags}/{p.Deaths}/{p.Points}:{p.Alive}")),
        string.Join(';', g.Pickups.Values.OrderBy(p => p.Key).Select(p => $"{p.Key}:{p.Sprite}:{p.Taken}")),
        string.Join(';', g.Vehicles.Values.OrderBy(v => v.Key).Select(v => $"{v.Key}:{v.Driver}:{v.Wrecked}:{v.Hull:0}")),
        string.Join(';', s.Behaviour!.Records.Select(off => s.Behaviour.Capture(off)).Select(r => $"{r.Offset:x}:{r.State}:{r.Act}:{r.Link}:{r.Flags}:{r.Hp:0.##}:{r.Stage}")),
        g.RoundOver, s.Soldier.Health, g.Spawned,
        g.Avatars.ToDictionary(kv => kv.Key, kv => (kv.Value.Alive, kv.Value.Position, kv.Value.Visible)));

    private static void Same(View live, View played, string when, bool bodiesSettled)
    {
        Assert.True(live.Players == played.Players, $"{when}: players\n live   {live.Players}\n played {played.Players}");
        Assert.True(live.Pickups == played.Pickups, $"{when}: items differ");
        Assert.True(live.Vehicles == played.Vehicles, $"{when}: vehicles\n live   {live.Vehicles}\n played {played.Vehicles}");
        Assert.True(live.Level == played.Level, $"{when}: level rows differ");
        Assert.Equal(live.RoundOver, played.RoundOver);
        Assert.True(live.Health == played.Health, $"{when}: health {live.Health} vs {played.Health}");
        Assert.Equal(live.Spawned, played.Spawned);
        Assert.Equal(live.Bodies.Keys.Order(), played.Bodies.Keys.Order());
        foreach (var (id, b) in live.Bodies)
        {
            var p = played.Bodies[id];
            Assert.True(b.Alive == p.Alive, $"{when}: body {id} alive {b.Alive} vs {p.Alive}");
            if (bodiesSettled && b.Alive && b.Visible)
            {
                Assert.True(Vector3.Distance(b.At, p.At) < 1f, $"{when}: body {id} at {b.At} vs {p.At}");
            }
        }
    }

    [BehaviourDataFact]
    public void A_deathmatch_recorded_on_a_client_and_the_host_plays_back_without_a_server()
    {
        var net = new LoopbackNetwork();
        var hostSession = new GameSession(BehaviourData.Data);
        var options = new DmRunOptions { HostMap = 605, Port = 0, Discovery = false, Name = "HOST", Bots = 3, BotSkill = 2 };
        using var host = DmGame.StartHost(hostSession, options, out var error, net.Listen(0, DmProtocol.MaxPeers));
        Assert.True(host != null, error);
        var port = host.Host!.Port;
        var aSession = new GameSession(BehaviourData.Data);
        var bSession = new GameSession(BehaviourData.Data);
        var join = options with { HostMap = null, Bots = 0 };
        using var a = DmGame.StartClient(aSession, join with { Name = "ALPHA" }, out error, net.Connect(port));
        using var b = DmGame.StartClient(bSession, join with { Name = "BRAVO" }, out error, net.Connect(port));
        Assert.NotNull(a);
        Assert.NotNull(b);
        var controls = CampaignControls.Create();
        var frame = 0;

        // One frame of everybody: the wire, then each session's tick. ALPHA walks in circles and fires
        // in bursts; BRAVO stands and turns; the host's player stands; the bots hunt them all.
        void Step()
        {
            host.Frame(Dt);
            hostSession.Tick(default, controls.Player, default, false);
            a.Frame(Dt);
            var down = frame % 300 < 200 ? TickInput.Bit(controls.Forward) : 0UL;
            aSession.Tick(new TickInput(down, 0, 0, frame % 300 < 200 ? 2f : -1f, 0, 0, 0, 0), controls.Player,
                new WeaponInput(frame % 90 < 20, false, false), false);
            b.Frame(Dt);
            bSession.Player.Yaw += 0.01f;
            bSession.Tick(default, controls.Player, default, false);
            frame++;
        }

        for (var i = 0; i < 600 && !(a.Spawned && b.Spawned && a.Avatars.Count >= 4); i++)
        {
            Step();
        }

        Assert.True(a.Spawned && b.Spawned, "the clients did not spawn");
        var folder = Directory.CreateTempSubdirectory("netdemo");
        try
        {
            var aPath = Path.Combine(folder.FullName, "alpha.sndemo");
            var hostPath = Path.Combine(folder.FullName, "host.sndemo");
            Assert.Null(a.StartRecording(aPath, BehaviourData.Data));
            Assert.Null(host.StartRecording(hostPath, BehaviourData.Data));
            Assert.NotNull(a.StartRecording(aPath, BehaviourData.Data)); // one at a time

            // 25 s of play: a crate broken on the server half way, a chat line, the samples taken.
            var samples = new List<(int Frame, View Live)>();
            var keyframes = new List<(int Frame, int Entry, View Live, Dictionary<int, Vector3> Poses)>();
            var hostScores = "";
            const int Frames = 25 * 60;
            var hb = hostSession.Behaviour!;
            var crate = hb.Records.First(o => hb.IsDamageable(o) && hb.HasHp(o));
            for (var f = 0; f < Frames; f++)
            {
                var keysBefore = a.Recorder!.Demo.Keyframes().Count;
                Step();
                if (f == 600)
                {
                    hb.Hit(crate, 1e6f);
                }

                if (f == 700)
                {
                    b.SendChat("gg");
                }

                if (f == 800 && host.Host!.Server.Players.TryGetValue(b.LocalId, out var bravo) && bravo.Alive)
                {
                    // A death for sure (the bots' aim is their own business): a bot's shot ends BRAVO.
                    host.Host.Server.Hit(b.LocalId, 2000f, host.Host.Server.Players.Keys.Min(), 1);
                }

                if (a.Recorder.Demo.Keyframes().Count > keysBefore && f > 0)
                {
                    keyframes.Add((f, a.Recorder.Demo.Keyframes()[^1], Look(a, aSession),
                        a.Players.Values.Where(p => p.Alive && p.Id != a.LocalId).ToDictionary(p => p.Id, p => p.Position)));
                }

                if (f % 150 == 149)
                {
                    samples.Add((f, Look(a, aSession)));
                }
            }

            // The scores as every client has them (the server's points between deaths are its own).
            hostScores = Look(a, aSession).Players;
            Assert.True(aSession.Behaviour!.Spent(crate), "the crate did not break on the client");
            Assert.Contains(a.Players.Values, p => p.Deaths > 0); // the bots have killed somebody
            var recorded = a.StopRecording()!;
            recorded.Save();
            host.StopRecording()!.Save();
            Assert.NotEmpty(keyframes);

            // The client's demo, played on a session of its own with no server: the same state at every sample.
            var demo = NetDemoFile.Read(aPath);
            Assert.Null(NetDemo.Refusal(demo, BehaviourData.Data, []));
            Assert.Equal("client", demo.Field(NetDemo.KeyRole));
            Assert.Equal(a.LocalId, NetDemo.LocalId(demo));
            var replaySession = new GameSession(BehaviourData.Data);
            using (var replay = DmGame.StartReplay(replaySession, new NetDemoPlayer(demo)))
            {
                // Live frame 0 wrote the first keyframe at the demo's time 0: replay frame f is live frame f.
                var next = 0;
                for (var f = 0; f < Frames && next < samples.Count; f++)
                {
                    replay.Frame(f == 0 ? 0f : Dt);
                    replaySession.Tick(default, controls.Player, default, false);
                    if (f == 720)
                    {
                        Assert.Contains(replay.Chat, l => l.Text == "BRAVO: gg");
                    }

                    if (f == samples[next].Frame)
                    {
                        Same(samples[next].Live, Look(replay, replaySession), $"frame {f}", bodiesSettled: f > 90);
                        next++;
                    }
                }

                Assert.Equal(samples.Count, next);

                // A seek to a keyframe: the state as the live client had it when the keyframe was written,
                // the bodies where their last poses put them.
                var (kfFrame, kfEntry, kfLive, kfPoses) = keyframes[^1];
                var at = replay.Replay!.Seek(demo.Entries[kfEntry].Time / 1000.0 + 0.001);
                Assert.Equal(demo.Entries[kfEntry].Time / 1000.0, at, 6);
                replay.Frame(0f);
                var after = Look(replay, replaySession);
                Same(kfLive, after, $"after the seek to {at:0.###} s (frame {kfFrame})", bodiesSettled: false);
                foreach (var (id, feet) in kfPoses)
                {
                    Assert.True(Vector3.Distance(after.Bodies[id].At, feet) < 0.01f, $"body {id} after the seek");
                }

                // The camera: its own first-person view, then behind another player, then free.
                Assert.True(replay.OwnView);
                var other = replay.FollowNext(1);
                Assert.NotEqual(a.LocalId, replay.Follow);
                replay.Camera = DmGame.DemoCamera.Chase;
                replay.Frame(Dt);
                Assert.False(replay.OwnView);
                Assert.True(replay.InputBlocked); // the chase camera takes no keys; the free one flies on them
                replay.Camera = DmGame.DemoCamera.Free;
                Assert.False(replay.InputBlocked);
                Assert.NotEmpty(other);
            }

            // The host's demo: what a client as player 1 would have seen; the roster and scores at the end
            // are the ones every client was sent.
            var hostDemo = NetDemoFile.Read(hostPath);
            Assert.Equal("host", hostDemo.Field(NetDemo.KeyRole));
            Assert.Equal(1, NetDemo.LocalId(hostDemo));
            var hostReplaySession = new GameSession(BehaviourData.Data);
            using var hostReplay = DmGame.StartReplay(hostReplaySession, new NetDemoPlayer(hostDemo));
            for (var f = 0; f < Frames + 30; f++)
            {
                hostReplay.Frame(Dt);
            }

            Assert.Equal(hostScores, Look(hostReplay, hostReplaySession).Players);
            Assert.True(hostReplaySession.Behaviour!.Spent(crate));
        }
        finally
        {
            folder.Delete(recursive: true);
        }
    }
}
