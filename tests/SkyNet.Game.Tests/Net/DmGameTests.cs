using System.Net;
using System.Numerics;
using RetroEng.Physics;
using SkyNet.Game.Campaign;
using SkyNet.Game.Net;
using SkyNet.Game.Player;
using SkyNet.Game.Tests.Behaviour;
using SkyNet.Game.Weapons;

namespace SkyNet.Game.Tests.Net;

/// <summary>The real-UDP game tests load two arenas and pump the wire: they run alone, so a loaded
/// machine running every other test at once cannot starve them.</summary>
[CollectionDefinition(Name, DisableParallelization = true)]
public sealed class DmUdpCollection
{
    public const string Name = "Deathmatch over UDP";
}

/// <summary>The deathmatch in the game (steps 2-5): the command line split, the soldier's hurts
/// sent to the server, somebody else's shots drawn harmless, the session's arena mode, and a host
/// and a client over UDP on 127.0.0.1 with the real sessions: hits, items, chat, the round's end
/// and the host's new round, the class for the next life.</summary>
[Collection(DmUdpCollection.Name)]
public class DmGameTests
{
    /// <summary>How long a step may wait for the wire: generous, as it only bounds a broken run
    /// (a passing one takes well under a second a step).</summary>
    private static readonly TimeSpan StepBudget = TimeSpan.FromSeconds(60);

    private const float Dt = 1f / 60f;

    private sealed class Target(Vector3 centre, float radius) : IDamageable
    {
        public float Taken;
        public Vector3 Centre => centre;
        public float Radius => radius;
        public bool Alive => true;
        public void TakeDamage(in Damage damage) => Taken += damage.Points;
    }

    [Fact]
    public void The_command_line_splits_into_the_network_and_the_game()
    {
        var (net, game) = DmHeadless.Split(["--host=MAP.601", "--port", "0", "--offscreen", "--frames", "60", "--data", "D", "--no-discovery", "--name=A"]);
        Assert.Equal(["--host=MAP.601", "--port", "0", "--offscreen", "--data", "D", "--no-discovery", "--name=A"], net);
        Assert.Equal(["--offscreen", "--frames", "60", "--data", "D"], game);
        Assert.NotNull(DmHeadless.Parse(net, out var error));
        Assert.Null(error);
        Assert.False(DmHeadless.Headless(["--host=MAP.601", "--offscreen"]));
        Assert.True(DmHeadless.Headless(["--host=MAP.601", "--smoke"]));
        Assert.True(DmHeadless.Headless(["--join", "--headless"]));
    }

    [Theory]
    [InlineData("--port", "http")]
    [InlineData("--port", "70000")]
    [InlineData("--bots", "many")]
    [InlineData("--bot-skill", "")]
    [InlineData("--time", "1.5")]
    [InlineData("--frags", "x")]
    [InlineData("--quit-after", "soon")]
    [InlineData("--bind", "not-an-address")]
    public void A_bad_number_is_a_usage_error_not_a_crash(string option, string value)
    {
        Assert.Null(DmHeadless.Parse(["--host=MAP.601", option, value], out var error));
        Assert.Contains(option, error);
        Assert.NotNull(DmHeadless.Parse(["--host=MAP.601", "--port", "0", "--bots", "3", "--quit-after", "2.5", "--bind", "127.0.0.1"], out error));
        Assert.Null(error);
    }

    [Fact]
    public void The_soldier_sends_his_hurts_to_the_server()
    {
        var soldier = new Soldier(new PlayerController(new CollisionWorld()));
        var sent = new List<float>();
        soldier.Remote = sent.Add;
        soldier.Hurt(120f);
        Assert.Equal([120f], sent);
        Assert.Equal(Soldier.MaxHealth, soldier.Health);
        soldier.Remote = null;
        soldier.Hurt(120f);
        Assert.Equal(Soldier.MaxHealth - 120f, soldier.Health);
    }

    [Fact]
    public void A_shot_tells_where_it_left_from_and_along_what()
    {
        var weapons = new WeaponSystem(WeaponTable.Shipped, new CollisionWorld());
        var shots = new List<(int, Vector3, Vector3)>();
        weapons.Shot += (r, from, dir) => shots.Add((r, from, dir));
        weapons.Inventory.Select(1);
        weapons.Tick(new WeaponInput(true, false, false), new Shooter(new Vector3(0, 75, 0), 0f, 0f), Dt);
        var (record, from, dir) = Assert.Single(shots);
        Assert.Equal(1, record);
        Assert.Equal(new Vector3(0, 75, 0), from);
        Assert.Equal(-1f, dir.Z, 0.001f);
    }

    [Fact]
    public void Somebody_elses_rocket_flies_and_bangs_but_hurts_nobody()
    {
        var weapons = new WeaponSystem(WeaponTable.Shipped, new CollisionWorld());
        var me = new Target(new Vector3(0, 40, -600), 32f);
        var other = new Target(new Vector3(0, 40, -300), 40f);
        weapons.Player = me;
        weapons.Targets.Add(other);
        var shooter = new Target(new Vector3(0, 40, 0), 40f);
        weapons.Targets.Add(shooter);
        Assert.True(weapons.RemoteFire(6, new Vector3(0, 40, -10), -Vector3.UnitZ, shooter));
        Assert.Single(weapons.Projectiles);
        Assert.True(weapons.Projectiles[0].Harmless);
        for (var i = 0; i < 120 && weapons.Projectiles.Count > 0; i++)
        {
            weapons.Tick(default, new Shooter(new Vector3(0, 75, -2000), 0f, 0f), Dt);
        }

        Assert.Empty(weapons.Projectiles);
        Assert.Contains(weapons.Effects, e => e.Bank == WeaponTable.Shipped.AmmoOf(WeaponTable.Shipped.Weapons[6])!.ImpactBank);
        Assert.Equal(0f, me.Taken + other.Taken + shooter.Taken);

        // A bullet: its ray only, no damage; a record that is not in the table is refused.
        Assert.True(weapons.RemoteFire(1, new Vector3(0, 40, -10), -Vector3.UnitZ, shooter));
        Assert.Equal(0f, me.Taken + other.Taken + shooter.Taken);
        Assert.False(weapons.RemoteFire(99, Vector3.Zero, -Vector3.UnitZ));
        Assert.False(weapons.RemoteFire(1, Vector3.Zero, Vector3.Zero));
    }

    [BehaviourDataFact]
    public void The_arena_has_no_robots_no_map_pickups_and_no_mission_failed()
    {
        var session = new GameSession(BehaviourData.Data);
        Assert.True(session.StartDeathmatch(605));
        Assert.True(session.Deathmatch);
        Assert.Null(session.Robots);
        Assert.Empty(session.PickupsLeft);
        Assert.NotEmpty(session.TakenPickups); // the map's own items are hidden (MAP.605 has two)
        Assert.False(session.CanSave);
        Assert.Equal(1f, session.Weapons.DamageToEnemies);
        session.Soldier.Health = 0f;
        for (var i = 0; i < 60; i++)
        {
            session.Tick(default, default, default, false);
        }

        Assert.Equal(SessionPhase.Playing, session.Phase);

        // The campaign afterwards is the campaign again.
        session.StartAt(210);
        Assert.False(session.Deathmatch);
        Assert.NotNull(session.Robots);
    }

    /// <summary>Climbing out of a parked HK puts the soldier clear of its hull: DOS's 20 across lies
    /// inside the parked model's collider, which the clear-spawn nudge cannot see from inside.</summary>
    [BehaviourDataFact]
    public void Climbing_out_of_an_hk_lands_clear_of_its_hull()
    {
        var session = new GameSession(BehaviourData.Data);
        _ = new SkyNet.Game.Vehicles.VehicleRig(session); // the ride the app gives the session
        var options = new DmRunOptions { HostMap = 605, Port = 0, Bind = IPAddress.Loopback, Discovery = false, Name = "HOST" };
        using var game = DmGame.StartHost(session, options, out var error);
        Assert.True(game != null, error);
        var hk = game.Vehicles.Values.First(v => v.Kind == SkyNet.Game.Vehicles.VehicleMode.Hk);
        session.Player.Position = hk.Position;

        Assert.True(game.UseVehicle(), "board");
        Assert.Equal(game.Host!.LocalId, hk.Driver);
        Assert.True(game.UseVehicle(), "leave");
        Assert.Equal(0, hk.Driver);

        // In the hull's frame: across (its right) and along; the parked HK is ±50 × ±115 (0x51FFE).
        var d = session.Player.Position - hk.Position;
        var across = MathF.Abs(Vector3.Dot(d, new Vector3(MathF.Cos(hk.Yaw), 0f, -MathF.Sin(hk.Yaw))));
        var along = MathF.Abs(Vector3.Dot(d, new Vector3(-MathF.Sin(hk.Yaw), 0f, -MathF.Cos(hk.Yaw))));
        Assert.True(across > 50f + PlayerController.BodyRadius || along > 115f + PlayerController.BodyRadius,
            $"stepped out {across:0} across, {along:0} along: inside the hull");
    }

    [BehaviourDataFact]
    public void A_host_and_a_client_play_the_arena_over_udp()
    {
        var hostSession = new GameSession(BehaviourData.Data);
        var clientSession = new GameSession(BehaviourData.Data);
        var options = new DmRunOptions { HostMap = 605, Port = 0, Bind = IPAddress.Loopback, Discovery = false, Name = "HOST" };
        using var host = DmGame.StartHost(hostSession, options, out var error);
        Assert.True(host != null, error);
        Assert.True(host.Spawned);
        var port = host.Host!.Port;
        // The network events are DOS's status lines in colour 0x0C (STRINGS.PRS NewPlayer ...).
        var hostLines = new List<(string Text, byte Colour)>();
        host.Status += (text, colour) => hostLines.Add((text, colour));
        using var client = DmGame.StartClient(clientSession, options with { HostMap = null, JoinHost = "127.0.0.1", Port = port, Name = "CLIENT" }, out error);
        Assert.True(client != null, error);

        // Until the condition holds or the budget runs out (wall clock, not a frame count: a frame
        // waits on the OS timer, which a loaded machine stretches).
        void Pump(Func<bool> until)
        {
            var clock = System.Diagnostics.Stopwatch.StartNew();
            while (!until() && clock.Elapsed < StepBudget)
            {
                host.Frame(Dt);
                client.Frame(Dt);
                Thread.Sleep(1);
            }
        }

        Pump(() => client.Spawned && host.Avatars.ContainsKey(2) && client.Avatars.TryGetValue(1, out var h) && h.Visible);
        Assert.True(client.Spawned);
        Assert.Contains(("CLIENT HAS ENTERED THE GAME.", SkyNet.Game.Hud.StatusQueue.NetworkColour), hostLines);
        Assert.Equal(605, clientSession.MapNumber);
        Assert.True(clientSession.Deathmatch);
        Assert.True(host.Avatars[2].Alive);
        // A HUMAN spawns with the motion detector (record 13), and it reads while in hand.
        Assert.True(clientSession.Weapons.Inventory.Owns(WeaponTable.MotionDetector));
        Assert.True(hostSession.Weapons.Inventory.Owns(WeaponTable.MotionDetector));
        Assert.False(client.DetectorActive);
        Assert.False(client.MachineVision); // a HUMAN's view is DOS's own
        Assert.Equal(DmProtocol.DosHealth, clientSession.Soldier.Health);
        Assert.Equal(1f, clientSession.Player.ClassSpeed);
        clientSession.Weapons.Inventory.Select(WeaponTable.MotionDetector);
        Assert.True(client.DetectorActive);

        // In hand, DOS's scanner plots on its own screen; the marks over the view are the machine's.
        var hudFrame = new DmHudFrame();
        var look = Matrix4x4.CreateLookAt(clientSession.Player.Position, host.Avatars[2].Position, Vector3.UnitY)
            * Matrix4x4.CreatePerspectiveFieldOfView(1f, 4f / 3f, 4f, 60000f);
        DmHud.Capture(client, hudFrame, 1280, 960, look, clientSession.Player.Position, new System.Drawing.RectangleF(0f, 0f, 360f, 410f));
        Assert.Empty(hudFrame.Marks);
        Assert.NotNull(hudFrame.Scanner);

        // The client's soldier stands at the host's body and shoots it: the hit reaches the server.
        var target = client.Avatars[1];
        Pump(() => Vector3.Distance(target.Position, hostSession.Player.Position) < 1f);
        // The server picks the host's spawn at random, so a fixed side of it can be in a wall or
        // behind a slope: stand 200 away on the first side with a clear line to the body.
        var p = clientSession.Player;
        var stood = false;
        for (var i = 0; i < 16 && !stood; i++)
        {
            var side = PlayerController.Direction(i * MathF.Tau / 16f, 0f);
            p.Spawn(hostSession.Player.Position + side * 200f, i * MathF.Tau / 16f + MathF.PI);
            var line = target.Centre - p.EyePosition;
            stood = !clientSession.World.Overlaps(p.Body, p.Position)
                && !clientSession.World.Raycast(p.EyePosition, Vector3.Normalize(line), line.Length() - target.Radius, out _);
        }

        Assert.True(stood, "no clear side of the host's spawn");
        var to = target.Centre - p.EyePosition;
        p.Yaw = MathF.Atan2(-to.X, -to.Z);
        p.Pitch = MathF.Asin(to.Y / to.Length());
        clientSession.Weapons.Inventory.Select(1);
        for (var i = 0; i < 10; i++)
        {
            clientSession.Tick(default, default, new WeaponInput(true, false, false), false);
        }

        Pump(() => hostSession.Soldier.Health < DmProtocol.DosHealth);
        Assert.True(hostSession.Soldier.Health < DmProtocol.DosHealth, "the host was not hit");
        Assert.Equal(hostSession.Soldier.Health, host.Host.Server.Players[1].Health);

        // Step 4: both see the server's items; the client walks onto a weapon and the server's word
        // gives it the weapon and its ammo; the item is gone on both and comes back after 20 s.
        Assert.NotEmpty(host.Items);
        Pump(() => client.Items.Count == host.Items.Count);
        Assert.Equal(host.Items.Select(i => i.Id), client.Items.Select(i => i.Id));
        var inventory = clientSession.Weapons.Inventory;
        inventory.Reset();
        var weapon = client.Pickups.Values.First(i => PickupTable.Items[i.Sprite] is { Weapon: >= 0, Pool: >= 0, Amount: > 0 });
        var fx = PickupTable.Items[weapon.Sprite];
        var ammoBefore = inventory.Pool(fx.Pool);
        var hostAmmo = hostSession.Weapons.Inventory.Pool(fx.Pool);
        p.Spawn(weapon.Position, 0f);
        Pump(() => client.Pickups[weapon.Key].Taken && host.Pickups[weapon.Key].Taken);
        Assert.DoesNotContain(client.Items, i => i.Id == weapon.Key);
        Assert.DoesNotContain(host.Items, i => i.Id == weapon.Key);
        Assert.True(inventory.Owns(fx.Weapon), "the taker got no weapon");
        Assert.True(inventory.Pool(fx.Pool) > ammoBefore, "the taker got no ammo");
        Assert.Equal(hostAmmo, hostSession.Weapons.Inventory.Pool(fx.Pool)); // nobody else's
        p.Spawn(weapon.Position + new Vector3(5000f, 0f, 0f), 0f);
        host.Frame((float)DmProtocol.PickupRespawn + 1f);
        Pump(() => !client.Pickups[weapon.Key].Taken);
        Assert.Contains(client.Items, i => i.Id == weapon.Key);

        // Step 5: a chat line reaches both logs; the round ends on both; the host's new round
        // starts it again with the scores wiped.
        client.SendChat("hello there");
        Pump(() => host.Chat.Count > 0 && client.Chat.Count > 0);
        Assert.Equal("CLIENT: hello there", host.Chat[^1].Text);
        Assert.Equal("CLIENT: hello there", client.Chat[^1].Text);
        // A crate broken on the server is broken on the client; NEW ROUND puts it back on both, a
        // weapon target again.
        var hb = hostSession.Behaviour!;
        var crate = hb.Records.First(o => hb.IsDamageable(o) && hb.HasHp(o) && hb.TryPositionOf(o, out var c)
            && Vector3.Distance(c, hostSession.Player.Position) > 3000f && Vector3.Distance(c, p.Position) > 3000f);
        hb.Hit(crate, 1e6f);
        Pump(() => clientSession.Behaviour!.Spent(crate));
        Assert.True(clientSession.Behaviour!.Spent(crate));
        Assert.Null(hostSession.Weapons.Targets.ByOwner(crate));
        host.Host.Server.EndMatch("TIME LIMIT REACHED");
        Pump(() => client.RoundOver != null);
        Assert.Equal("TIME LIMIT REACHED", host.RoundOver);
        Assert.Equal("TIME LIMIT REACHED", client.RoundOver);
        Assert.True(client.InputBlocked);
        Assert.False(client.Restart()); // only the host
        Assert.True(host.Restart());
        Pump(() => client.RoundOver == null && client.Centre == "NEW ROUND");
        Assert.Null(host.RoundOver);
        Assert.Null(client.RoundOver);
        Assert.All(client.Players.Values, pl => Assert.Equal(0, pl.Frags + pl.Deaths));
        Pump(() => client.LevelClient!.Resets > 0);
        Assert.False(hb.Spent(crate));
        Assert.False(clientSession.Behaviour!.Spent(crate));
        Assert.NotNull(hostSession.Weapons.Targets.ByOwner(crate));
        Assert.NotNull(clientSession.Weapons.Targets.ByOwner(crate));

        // The class for the next life: worn from the respawn, with DOS's bar and BALANCED's pace (the
        // host's CLASSES, from the welcome).
        Assert.Equal(DmClassRules.Balanced, client.Settings.Classes);
        // TERMINATOR VISION chosen: nothing while a HUMAN.
        client.PointCloudVision = true;
        Assert.False(client.PointCloudView);
        Assert.False(SkyNet.Game.Menu.ViewStyles.MachinePoints(client));
        // Picking a HUMAN turns it off; it is chosen again for the machine.
        client.SetClass(DmClass.Human);
        Assert.False(client.PointCloudVision);
        client.PointCloudVision = true;
        client.SetClass(DmClass.Terminator);
        Assert.Equal("NEXT LIFE: TERMINATOR", client.Centre);
        client.SendChat("ready"); // after the class on the same ordered channel: its echo means the server has both
        Pump(() => host.Chat[^1].Text == "CLIENT: ready");
        Assert.Equal(DmClass.Human, client.LocalClass);
        host.Host.Server.Restart();
        Pump(() => client.LocalClass == DmClass.Terminator && clientSession.Player.ClassSpeed < 1f);
        Assert.Equal(DmProtocol.DosHealth, client.MaxHealth);
        Assert.Equal(DmProtocol.DosHealth, clientSession.Soldier.Health);
        Assert.True(client.MachineVision);
        // The machine's view is the point cloud while chosen (this machine's own: the host keeps its view).
        Assert.True(client.PointCloudView);
        Assert.True(SkyNet.Game.Menu.ViewStyles.MachinePoints(client));
        Assert.False(host.PointCloudView);
        client.PointCloudVision = false;
        Assert.False(client.PointCloudView);
        client.PointCloudVision = true;
        Assert.Equal(DmProtocol.BalancedMachineSpeed, clientSession.Player.ClassSpeed);
        Assert.False(clientSession.Weapons.Inventory.Owns(WeaponTable.MotionDetector)); // the machine vision instead
        Assert.False(client.DetectorActive);
        Pump(() => host.Avatars[2].Class == DmClass.Terminator);
        Assert.Equal(DmClass.Terminator, host.Avatars[2].Class);

        // Back to a HUMAN for the next life: DISPLAY's view again.
        client.SetClass(DmClass.Human);
        client.SendChat("human");
        Pump(() => host.Chat[^1].Text == "CLIENT: human");
        host.Host.Server.Restart();
        Pump(() => client.LocalClass == DmClass.Human);
        Assert.False(client.PointCloudView);
    }

    [BehaviourDataFact]
    public void A_menu_host_keeps_its_password_and_sends_its_classes()
    {
        // The host screen's PASSWORD and CLASSES: the in-process server checks the proof, and a joiner
        // plays the host's rules (DOS: the machine at the soldier's pace) from the welcome.
        var hostSession = new GameSession(BehaviourData.Data);
        var options = new DmRunOptions
        {
            HostMap = 605, Port = 0, Bind = IPAddress.Loopback, Discovery = false, Name = "HOST", Password = "sesame", Classes = DmClassRules.Dos,
        };
        using var host = DmGame.StartHost(hostSession, options, out var error);
        Assert.True(host != null, error);
        Assert.NotNull(host.Host!.Policy.PasswordKey);
        var join = options with { HostMap = null, JoinHost = "127.0.0.1", Port = host.Host.Port, Name = "CLIENT", Class = DmClass.Terminator };

        void Pump(DmGame client, Func<bool> until)
        {
            var clock = System.Diagnostics.Stopwatch.StartNew();
            while (!until() && clock.Elapsed < StepBudget)
            {
                host.Frame(Dt);
                client.Frame(Dt);
                Thread.Sleep(1);
            }
        }

        var wrongSession = new GameSession(BehaviourData.Data);
        using (var wrong = DmGame.StartClient(wrongSession, join with { Password = "open sesame" }, out error))
        {
            Assert.True(wrong != null, error);
            string? ended = null;
            wrong.Ended += r => ended = r;
            Pump(wrong, () => ended != null);
            Assert.Contains("Wrong password.", ended, StringComparison.OrdinalIgnoreCase);
            Assert.False(wrong.Spawned);
        }

        var clientSession = new GameSession(BehaviourData.Data);
        using var client = DmGame.StartClient(clientSession, join, out error);
        Assert.True(client != null, error);
        Pump(client, () => client.Spawned);
        Assert.True(client.Spawned);
        Assert.Equal(DmClassRules.Dos, client.Settings.Classes);
        Assert.Equal(DmClass.Terminator, client.LocalClass);
        Assert.Equal(1f, clientSession.Player.ClassSpeed); // DOS: no slower machine
        Assert.Equal(DmProtocol.DosHealth, clientSession.Soldier.Health);
        Assert.Equal(1f, host.Host.Server.SpeedOf(client.LocalId));
    }

    /// <summary>face without an id turns to the nearest living body, one in plain sight before a nearer one behind a wall.</summary>
    [BehaviourDataFact]
    public void Face_alone_turns_to_the_nearest_body_in_sight()
    {
        var session = new GameSession(BehaviourData.Data);
        var options = new DmRunOptions { HostMap = 605, Port = 0, Bind = IPAddress.Loopback, Discovery = false, Name = "HOST", Bots = 3 };
        using var host = DmGame.StartHost(session, options, out var error);
        Assert.True(host != null, error);
        var clock = System.Diagnostics.Stopwatch.StartNew();
        while (host.Avatars.Values.Count(a => a.Alive) < 3 && clock.Elapsed < StepBudget)
        {
            host.Frame(Dt);
        }

        var commands = new RetroEng.Commands.CommandRegistry();
        host.Register(commands);
        var eye = session.Player.EyePosition;
        bool Clear(DmAvatar a) => DmGame.LineClear(session.World, eye, a.Centre, int.MinValue);
        var expected = host.Avatars.Values.Where(a => a.Alive).OrderBy(a => Clear(a) ? 0 : 1).ThenBy(a => Vector3.DistanceSquared(a.Centre, eye)).First();

        Assert.StartsWith($"facing {expected.Name} at", commands.Run("face").Single(), StringComparison.Ordinal);
        var to = expected.Centre - eye;
        Assert.Equal(MathF.Atan2(-to.X, -to.Z), session.Player.Yaw, 1e-4f);
    }

    [Fact]
    public void The_console_refuses_to_leave_the_arena()
    {
        var commands = new RetroEng.Commands.CommandRegistry();
        commands.Add("map", "map <n>", _ => "went");
        commands.Add("tp", "tp", _ => "moved");
        commands.Add("arnold", "arnold", _ => "armed", "", "cskydere");
        var playing = true;
        var cheats = false;
        DmGame.Guard(commands, () => playing, () => cheats);
        Assert.Equal(["'map' is not allowed in a network game"], commands.Run("map 210"));
        // The cheats too, under every name (DOS's CHEAT.PRS code included), unless the host allowed them.
        Assert.StartsWith("'tp': the host has not allowed cheats", commands.Run("tp").Single(), StringComparison.Ordinal);
        Assert.StartsWith("'arnold': the host has not allowed cheats", commands.Run("cskydere").Single(), StringComparison.Ordinal);
        Assert.Equal(3, commands.Commands.Count); // replaced in place, not listed twice
        cheats = true;
        Assert.Equal(["moved"], commands.Run("tp"));
        Assert.Equal(["armed"], commands.Run("cskydere"));
        Assert.Equal(["'map' is not allowed in a network game"], commands.Run("map 210")); // never allowed
        playing = false;
        cheats = false;
        Assert.Equal(["went"], commands.Run("map 210"));
        Assert.Equal(["moved"], commands.Run("tp")); // the campaign's console is not touched
    }
}
