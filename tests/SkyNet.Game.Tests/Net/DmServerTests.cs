using System.Collections.Immutable;
using System.Numerics;
using RetroEng.Net;
using SkyNet.Formats;
using SkyNet.Game.Campaign;
using SkyNet.Game.Net;

namespace SkyNet.Game.Tests.Net;

/// <summary>The deathmatch rules on a hand-driven clock (DmServer.Update).</summary>
public class DmServerTests
{
    private static readonly DmArena Arena = new()
    {
        Map = 605,
        Spawns = [new(new Vector3(0, 0, 0), 0f), new(new Vector3(10000, 0, 0), 1f), new(new Vector3(0, 0, 10000), 2f)],
        ItemSpots = [new(100, 0, 100), new(200, 0, 200), new(300, 0, 300)],
    };

    private static DmServer NewServer(DmSettings? s = null, int humans = 2)
    {
        var server = new DmServer(s ?? new DmSettings { Items = ImmutableDictionary<string, int>.Empty }, new Random(1));
        server.SetArena(Arena);
        for (var id = 1; id <= humans; id++)
        {
            server.AddHuman(id, $"P{id}", DmClass.Human);
            server.LevelReady(id);
        }

        return server;
    }

    [Fact]
    public void A_kill_scores_frags_points_and_a_death()
    {
        var s = NewServer();
        var died = new List<(int, int, int)>();
        s.Died += (v, k, w) => died.Add((v, k, w));
        Assert.True(s.Hit(2, 90f, 1, 3));
        Assert.True(s.IsAlive(2));
        Assert.True(s.Hit(2, 90f, 1, 3));
        Assert.False(s.IsAlive(2));
        Assert.Equal([(2, 1, 3)], died);
        var (killer, victim) = (s.Players[1], s.Players[2]);
        Assert.Equal((1, 0), (killer.Frags, killer.Deaths));
        Assert.Equal((0, 1), (victim.Frags, victim.Deaths));
        // DOS scoring (FUN_00121313): the first hit 100, the killing blow a kill's 500 and no hit;
        // the victim loses 400.
        Assert.Equal(600, killer.Points);
        Assert.Equal(-400, victim.Points);
        Assert.False(s.Hit(2, 100f, 1, 3)); // the dead take no more
    }

    [Fact]
    public void Suicide_and_world_deaths_cost_only_the_death_and_bot_kills_count()
    {
        var s = NewServer();
        s.Hit(1, 5000f, 1, 0);
        // DOS FUN_00122b52: killer 0, so no kill for anybody, one death and scoring_death lost.
        Assert.Equal((0, 1, -400), (s.Players[1].Frags, s.Players[1].Deaths, s.Players[1].Points));
        s.Hit(2, 5000f, 0, 0);
        Assert.Equal((0, 1, -400), (s.Players[2].Frags, s.Players[2].Deaths, s.Players[2].Points));
        var bot = s.AddBot(DmClass.Human);
        Assert.True(bot.Id < 0);
        Assert.True(bot.Alive);
        s.Update(DmProtocol.RespawnDelay);
        s.Hit(1, 5000f, bot.Id, 2);
        Assert.Equal(1, bot.Frags);       // a negative id is a killer, not the world
        Assert.Equal(0, s.Players[1].Frags);
    }

    [Fact]
    public void Respawn_comes_three_seconds_after_death_at_full_health()
    {
        var s = NewServer();
        var respawns = new List<int>();
        s.Respawned += (id, _) => respawns.Add(id);
        s.Hit(2, 5000f, 1, 1);
        s.Update(DmProtocol.RespawnDelay - 0.1);
        Assert.False(s.IsAlive(2));
        Assert.Empty(respawns);
        s.Update(0.2);
        Assert.True(s.IsAlive(2));
        Assert.Equal([2], respawns);
        Assert.Equal(DmProtocol.DosHealth, s.Players[2].Health);
        Assert.Equal(0f, s.Players[2].Armour);
    }

    [Fact]
    public void Armour_is_dos_armour()
    {
        var s = NewServer();
        var p = s.Players[2];
        p.Armour = 0.5f;
        s.Hit(2, 100f, 1, 1);
        var armour = 0.5f - Soldier.ArmourWear * 100f;
        Assert.Equal(armour, p.Armour, 5);
        Assert.Equal(DmProtocol.DosHealth - (1f - armour) * 100f, p.Health, 3);
    }

    [Theory]
    [InlineData(DmClassRules.Dos)]
    [InlineData(DmClassRules.Balanced)]
    public void A_deathmatch_bar_is_dos_150_points_for_every_class_under_both_rules(DmClassRules rules)
    {
        // SKYNET.EXE v1.01 0x122348: 0x9600 (150 in 8.8) in a network game, 0x3e800 in the campaign.
        Assert.Equal(0x9600 / 256f, DmProtocol.DosHealth);
        var s = NewServer(new DmSettings { Classes = rules }, humans: 0);
        s.AddHuman(1, "H", DmClass.Human);
        s.AddHuman(2, "T", DmClass.Terminator);
        var bot = s.AddBot(DmClass.Terminator);
        s.LevelReady(1);
        s.LevelReady(2);
        Assert.All(s.Players.Values, p => Assert.Equal(DmProtocol.DosHealth, p.Health));
        Assert.Equal(DmProtocol.DosHealth, s.MaxHealthOf(2));
        Assert.Equal(DmProtocol.DosHealth, s.MaxHealthOf(bot.Id));
    }

    [Fact]
    public void Classes_dos_runs_every_class_at_the_soldiers_pace_and_balanced_slows_the_machine()
    {
        Assert.Equal(1f, DmProtocol.SpeedFactor(DmClass.Human, DmClassRules.Dos));
        Assert.Equal(1f, DmProtocol.SpeedFactor(DmClass.Terminator, DmClassRules.Dos));
        Assert.Equal(1f, DmProtocol.SpeedFactor(DmClass.Human, DmClassRules.Balanced));
        Assert.Equal(0.9f, DmProtocol.SpeedFactor(DmClass.Terminator, DmClassRules.Balanced));
        Assert.Equal(DmClassRules.Balanced, new DmSettings().Classes); // the default

        // The travel budget follows: DOS's run with the run sidestep, times the class's pace.
        var run = MathF.Sqrt(400f * 400f + 310f * 310f);
        foreach (var (rules, machine) in new[] { (DmClassRules.Dos, 1f), (DmClassRules.Balanced, 0.9f) })
        {
            var s = new DmServer(new DmSettings { Classes = rules });
            s.AddHuman(1, "H", DmClass.Human);
            s.AddHuman(2, "T", DmClass.Terminator);
            Assert.Equal(run, s.TopSpeed(1), 1);
            Assert.Equal(run * machine, s.TopSpeed(2), 1);
            Assert.Equal(machine, s.SpeedOf(2));
        }
    }

    [Fact]
    public void Classes_travel_in_the_welcome()
    {
        foreach (var rules in new[] { DmClassRules.Dos, DmClassRules.Balanced })
        {
            var w = new NetWriter().Clear();
            DmProtocol.WriteSettings(w, new DmSettings { ServerName = "X", Classes = rules, Items = NetLevelList.Default(605).Items });
            var r = new NetReader(w.ToArray());
            var back = DmProtocol.ReadSettings(ref r);
            Assert.Equal(rules, back.Classes);
            Assert.True(r.End);
        }

        Assert.Equal((DmClassRules?)DmClassRules.Dos, DmProtocol.ParseRules(" DOS "));
        Assert.Equal((DmClassRules?)DmClassRules.Balanced, DmProtocol.ParseRules("balanced"));
        Assert.Null(DmProtocol.ParseRules("godot"));
    }

    [Fact]
    public void Spawn_is_the_one_farthest_from_the_living()
    {
        var s = NewServer(humans: 0);
        s.AddHuman(1, "A", DmClass.Human);
        s.LevelReady(1);
        s.Players[1].Position = new Vector3(0, 0, 0);
        s.AddHuman(2, "B", DmClass.Human);
        s.LevelReady(2);
        Assert.NotEqual(Vector3.Zero, s.Players[2].Position);
    }

    [Fact]
    public void Items_follow_netlevel_counts_in_DOS_order_and_come_back_after_20_s()
    {
        var items = ImmutableDictionary<string, int>.Empty.Add("health", 2).Add("lasers", 1).Add("jeeps", 3);
        var s = NewServer(new DmSettings { Items = items, Replenish = true });
        Assert.Equal(3, s.Pickups.Count); // the jeeps are no items
        // 0x11c407: the lasers come before the health, each item on the next spot.
        Assert.Equal([(new Vector3(100, 0, 100), 25611), (new Vector3(200, 0, 200), 27402), (new Vector3(300, 0, 300), 27401)],
            s.Pickups.Values.OrderBy(p => p.Key).Select(p => (p.Position, p.Sprite)));
        var laser = s.Pickups.Values.Single(p => p.Sprite == 25611);
        Assert.Equal(20.0, DmProtocol.PickupRespawn);

        var health = s.Pickups.Values.First(p => p.Position != laser.Position);
        s.Players[2].Health = 100f;
        var spawned = new List<int>();
        s.PickupSpawned += spawned.Add;
        Assert.True(s.Pickup(health.Key, 2));
        Assert.True(s.Players[2].Health > 100f);
        Assert.False(s.Pickup(health.Key, 1)); // taken
        s.Update(DmProtocol.PickupRespawn - 0.1);
        Assert.True(health.Taken);
        s.Update(0.2);
        Assert.False(health.Taken);
        Assert.Equal([health.Key], spawned);
    }

    [Fact]
    public void Without_replenish_items_and_drops_stay_taken()
    {
        var s = NewServer(new DmSettings { Items = ImmutableDictionary<string, int>.Empty.Add("armor", 1), Replenish = false });
        var armour = s.Pickups.Values.Single();
        Assert.True(s.Pickup(armour.Key, 1));
        Assert.True(s.Players[1].Armour > 0f);
        var drop = s.Drop(27401, new Vector3(1, 2, 3))!;
        Assert.True(drop.Drop);
        Assert.Null(s.Drop(12345, Vector3.Zero)); // no such item
        Assert.True(s.Pickup(drop.Key, 2));
        s.Update(1000);
        Assert.True(armour.Taken);
        Assert.True(drop.Taken);
    }

    [Fact]
    public void Client_pickups_need_the_taker_near()
    {
        var s = NewServer(new DmSettings { Items = ImmutableDictionary<string, int>.Empty.Add("bullets", 1) });
        var item = s.Pickups.Values.Single();
        s.Players[2].Position = item.Position + new Vector3(DmProtocol.PickupReach + 1, 0, 0);
        Assert.False(s.ClientPickup(2, item.Key));
        s.Players[2].Position = item.Position;
        Assert.True(s.ClientPickup(2, item.Key));
    }

    [Fact]
    public void Time_limit_ends_the_round()
    {
        var s = NewServer(new DmSettings { TimeLimitMinutes = 1 });
        string? over = null;
        var secs = new List<int>();
        s.MatchOver += r => over = r;
        s.TimeChanged += secs.Add;
        s.Update(59.5);
        Assert.Null(over);
        s.Update(1);
        Assert.Equal("TIME LIMIT REACHED", over);
        Assert.False(s.MatchRunning);
        Assert.Equal(0, secs[^1]);
        Assert.False(s.Hit(1, 10f, 2, 1)); // no damage after the round
    }

    [Fact]
    public void Frag_limit_ends_the_round_and_restart_wipes_it()
    {
        var s = NewServer(new DmSettings { Goal = DmGoal.Kills, GoalLimit = 2, Items = ImmutableDictionary<string, int>.Empty.Add("health", 1) });
        string? over = null;
        s.MatchOver += r => over = r;
        s.Hit(2, 5000f, 1, 1);
        s.Update(DmProtocol.RespawnDelay);
        s.Pickup(s.Pickups.Keys.First(), 1);
        s.Drop(27401, Vector3.One);
        s.Hit(2, 5000f, 1, 1);
        Assert.Equal("P1 REACHED 2 KILLS", over);

        s.Restart();
        Assert.True(s.MatchRunning);
        Assert.All(s.Players.Values, p => Assert.Equal((0, 0, 0, true), (p.Frags, p.Deaths, p.Points, p.Alive)));
        Assert.Single(s.Pickups);                       // the drop is gone
        Assert.False(s.Pickups.Values.Single().Taken);  // the item is back
    }

    [Fact]
    public void Reported_hits_are_checked()
    {
        var s = NewServer();
        s.Players[1].Position = Vector3.Zero;
        s.Players[2].Position = new Vector3(DmProtocol.MaxHitRange + 10, 0, 0);
        Assert.False(s.ClientHit(1, 2, 100f, 1));       // out of range
        s.Players[2].Position = new Vector3(100, 0, 0);
        Assert.False(s.ClientHit(1, 2, float.NaN, 1));
        Assert.False(s.ClientHit(1, 2, -50f, 1));       // no healing
        Assert.False(s.ClientHit(1, 99, 50f, 1));       // nobody
        Assert.False(s.ClientHit(7, 2, 50f, 1));        // not admitted
        Assert.True(s.ClientHit(1, 2, 1e9f, 1));        // capped ...
        Assert.Equal(0f, s.Players[2].Health);
        Assert.False(s.IsAlive(2));
    }

    [Fact]
    public void A_dead_shooter_lands_shots_only_for_a_moment()
    {
        var s = NewServer();
        s.Players[1].Position = s.Players[2].Position = Vector3.Zero;
        s.Hit(1, 5000f, 2, 1);
        s.Update(1.0);
        Assert.True(s.ClientHit(1, 2, 10f, 6));          // a rocket still in the air
        s.Update(1.0);
        Assert.False(s.ClientHit(1, 2, 10f, 6));
    }

    [Fact]
    public void Inputs_are_rate_limited()
    {
        var s = NewServer();
        var at = s.Players[1].Position;
        var ok = 0;
        for (var i = 0; i < 100; i++)
        {
            ok += s.ClientPose(1, at, 0, 0, 0) ? 1 : 0;
        }

        Assert.Equal(15, ok); // the pose burst
        s.Update(1.0);
        Assert.True(s.ClientPose(1, at, 0, 0, 0));
        Assert.False(s.ClientPose(1, new Vector3(1e9f, 0, 0), 0, 0, 0));
        Assert.Equal("hi", s.ClientChat(1, "  hi\u0007 "));
    }

    [Fact]
    public void Names_are_cleaned_and_unique_and_the_server_fills_up()
    {
        var s = new DmServer(new DmSettings { MaxPlayers = 2 });
        Assert.Equal("BOB", s.AddHuman(1, "BOB", DmClass.Human)!.Name);
        Assert.Equal("bob2", s.AddHuman(2, " bob\u202E", DmClass.Human)!.Name);
        Assert.Null(s.AddHuman(3, "X", DmClass.Human));  // full (bots do not count)
        Assert.Null(s.AddHuman(2, "Y", DmClass.Human));  // id taken
        Assert.Equal("PLAYER", new DmServer(new DmSettings()).AddHuman(1, "\u0001", DmClass.Human)!.Name);
    }

    [Fact]
    public void A_class_change_is_worn_from_the_next_spawn()
    {
        var s = NewServer();
        Assert.True(s.ClientSetClass(2, DmClass.Terminator));
        Assert.Equal(DmClass.Human, s.Players[2].Class);
        s.Hit(2, 5000f, 1, 1);
        s.Update(DmProtocol.RespawnDelay);
        Assert.Equal(DmClass.Terminator, s.Players[2].Class);
        Assert.Equal(DmProtocol.DosHealth, s.Players[2].Health);
        Assert.Equal(DmProtocol.BalancedMachineSpeed, s.SpeedOf(2));
    }

    [Fact]
    public void Only_a_human_on_foot_carries_the_scanner_on_the_wire()
    {
        // FUN_00118d4c: record 13 in hand sets the network flag 0x10; a machine never owns it.
        var s = NewServer();
        Assert.Equal(DmProtocol.FlagScanner, s.ServerFlags(1, DmProtocol.FlagScanner));
        s.ClientSetClass(2, DmClass.Terminator);
        s.Hit(2, 5000f, 1, 1);
        s.Update(DmProtocol.RespawnDelay);
        Assert.Equal(0, s.ServerFlags(2, DmProtocol.FlagScanner));
    }

    [Fact]
    public void Scoreboard_order_of_a_kills_game_is_kills_then_deaths_then_points_then_name()
    {
        var s = NewServer(humans: 3);
        s.Hit(3, 5000f, 2, 1);
        s.Hit(1, 5000f, 1, 1); // suicide: a death, no kill taken away
        Assert.Equal([2, 1, 3], s.Scoreboard().Select(p => p.Id)); // P1 and P3 tie on everything but the name
    }
}

/// <summary>SKYNET.EXE v1.01 0x11c3b0 / 0x11c407: the item spots' order and the item kinds.</summary>
public class DmItemLayoutTests
{
    [Fact]
    public void The_spots_at_index_7_and_then_4_move_to_index_1()
    {
        Assert.Equal([0, 3, 7, 1, 2, 4, 5, 6, 8], DmItemLayout.SpotOrder(9));
        Assert.Equal([0, 4, 1, 2, 3, 5], DmItemLayout.SpotOrder(6));
        Assert.Equal([0, 1, 2, 3], DmItemLayout.SpotOrder(4));
    }

    [Fact]
    public void One_of_each_kind_then_the_rest_by_weight_and_what_does_not_fit_is_left_out()
    {
        // Bullets 5: the three kinds once, then rest 2 at 0x4000 and 0xC000 of the weights.
        var bullets = DmItemLayout.Lay(10, c => c == "bullets" ? 5 : 0);
        Assert.Equal([25602, 25601, 25735, 25602, 25735], bullets.Select(p => p.Sprite));
        Assert.Equal(DmItemLayout.SpotOrder(10)[..5], bullets.Select(p => p.Spot));

        // Rockets come before health in DOS's table; with 3 spots the health does not fit.
        var full = DmItemLayout.Lay(3, c => c switch { "health" => 4, "rockets" => 2, "slugthrowers" => 1, _ => 0 });
        Assert.Equal([25612, 25734, 25738], full.Select(p => p.Sprite));
    }
}
