using System.Numerics;
using RetroEng.Physics;
using SkyNet.Formats;
using SkyNet.Game.Collision;
using SkyNet.Game.Enemies;
using SkyNet.Game.Tests.Behaviour;
using SkyNet.Game.Weapons;

namespace SkyNet.Game.Tests.Enemies;

/// <summary>DOS's robot death breakup (EnemyKill 0x129f30 → 0x12a059, state 1 0x1377fe): one part
/// per death-list entry of the robot and its segments, a steep lob at 120-247 u/s under gravity
/// 392, gone off on touching the world; a pooled, capped set.</summary>
public class WreckageTests
{
    private const float Dt = 1f / 35f;

    private static (Robots Robots, WeaponSystem Weapons) Map210()
    {
        var data = BehaviourData.Data;
        var map = data.LoadMap(210)!;
        var w = LevelCollision.Build(data, map, data.LoadWld(210)).World;
        var weapons = new WeaponSystem(WeaponTable.Load(data), w) { Player = new Soldier { Feet = new Vector3(1e6f, 0f, 1e6f) } };
        var robots = new Robots(SkyNet.Data.GameTables.Ais, new EnemyModels(data), w, weapons, seed: 3);
        robots.Populate(map);
        robots.Tick(Dt, null);
        return (robots, weapons);
    }

    private static int DeathParts(Enemy r) => r.Root.Walk().Sum(p => p.Type?.Death.Length ?? 0);

    [BehaviourDataFact]
    public void A_kill_throws_one_part_per_death_list_entry()
    {
        var (robots, _) = Map210();
        var robot = robots.All.FirstOrDefault(r => r.Alive && DeathParts(r) > 0);
        Assert.NotNull(robot);
        var expected = DeathParts(robot!);

        robot!.Die();

        Assert.Equal(expected, robots.Wreckage.Count);
        foreach (var p in robots.Wreckage.Parts)
        {
            if (!p.Live)
            {
                continue;
            }

            Assert.NotNull(p.Model);                  // drawn with its own .3D (scengine, hffin …)
            var speed = p.Velocity.Length();
            Assert.InRange(speed, Wreckage.MinSpeed - 0.01f, Wreckage.MinSpeed + Wreckage.SpeedSpread + 0.01f);
            // normalize(flat unit + 2..4 up): at least 63° above the horizon.
            Assert.True(p.Velocity.Y / speed >= 2f / MathF.Sqrt(5f) - 1e-3f);
        }
    }

    [BehaviourDataFact]
    public void Parts_fly_up_then_fall_and_go_off_on_the_ground_within_the_DOS_flight()
    {
        var (robots, _) = Map210();
        var robot = robots.All.First(r => r.Alive && DeathParts(r) > 0);
        var n = DeathParts(robot);
        robot.Die();
        var startY = robots.Wreckage.Parts.ToArray().Where(p => p.Live).Max(p => p.Position.Y);
        var peak = float.MinValue;
        var t = 0f;
        while (robots.Wreckage.Count > 0 && t < 4f)
        {
            robots.Tick(Dt, null);
            t += Dt;
            foreach (var p in robots.Wreckage.Parts)
            {
                if (p.Live)
                {
                    peak = MathF.Max(peak, p.Position.Y);
                }
            }
        }

        // A lob of at most 247 u/s against 392 u/s²: up and down again in about 1.3 s, never the 8 s guard.
        Assert.Equal(0, robots.Wreckage.Count);
        Assert.Equal(n, robots.Wreckage.Exploded);
        Assert.True(peak > startY, "the parts rise first");
        Assert.True(t < 3f, $"all gone after {t:F2} s");
    }

    /// <summary>A robot of <paramref name="name"/> on MAP.210 carrying act 0x26 (as a marked start
    /// would), with the soldier 40 u off its centre and a log of the acts it raises.</summary>
    private static (Robots Robots, Enemy Robot, Soldier Soldier, List<RobotActEvent> Acts) Marked(string name)
    {
        var (robots, weapons) = Map210();
        var soldier = (Soldier)weapons.Player!;
        var robot = robots.All.First(r => r.Alive && r.Name == name);
        robot.DeathAct = 0x26;
        robot.DeathState = 4;
        var acts = new List<RobotActEvent>();
        robots.DeathAct += acts.Add;
        soldier.Feet = robot.Position + new Vector3(0f, robot.BodyHeight * 0.5f - 38f, 0f) + new Vector3(40f, 0f, 0f);
        return (robots, robot, soldier, acts);
    }

    [BehaviourDataFact]
    public void A_kind_0_kill_throws_its_parts_and_does_not_explode_itself()
    {
        // The heavy turret: root and segments all kind 0 (one part each, their own model).
        var (robots, turret, soldier, acts) = Marked("hvytrrt");
        Assert.All(turret.Root.Walk(), s => Assert.Equal(0, s.Type!.DeathKind));
        Assert.All(turret.Root.Walk(), s => Assert.False(Robots.ExplodesOnDeath(s.Type)));
        var n = DeathParts(turret);
        Assert.Equal(turret.Root.Walk().Count(), n);

        turret.Die();

        Assert.Equal(0x26, Assert.Single(acts).Act);
        Assert.Equal(0, robots.PendingBlasts);       // no blast of its own (jmp 0x129fff)
        Assert.Equal(n, robots.Wreckage.Count);
        robots.Tick(0.4f, null);
        Assert.Equal(0f, soldier.Taken);              // nothing went off 0.375 s after the kill

        // The parts land and each goes off with its blast.
        var blasts = 0;
        var pending = 0;
        for (var t = 0f; t < 4f && robots.Wreckage.Count > 0; t += Dt)
        {
            var before = robots.Wreckage.Exploded;
            robots.Tick(Dt, null);
            blasts += robots.Wreckage.Exploded - before;
            pending = Math.Max(pending, robots.PendingBlasts);
        }

        Assert.Equal(0, robots.Wreckage.Count);
        Assert.Equal(n, robots.Wreckage.Exploded);
        Assert.Equal(n, blasts);
        Assert.True(pending > 0, "a landed part throws its own blast");
        Assert.Single(acts);
    }

    [BehaviourDataFact]
    public void A_kind_1_kill_explodes_at_once_and_throws_its_parts()
    {
        var (robots, raptor, soldier, acts) = Marked("raptor");
        Assert.Equal(1, raptor.Root.Type!.DeathKind);
        Assert.True(Robots.ExplodesOnDeath(raptor.Root.Type));
        var n = DeathParts(raptor);
        Assert.True(n > 0);

        raptor.Die();

        Assert.Equal(0x26, Assert.Single(acts).Act);
        Assert.Equal(raptor.Root.Walk().Count(s => Robots.ExplodesOnDeath(s.Type)), robots.PendingBlasts);
        Assert.True(robots.PendingBlasts >= 1);
        Assert.Equal(n, robots.Wreckage.Count);
        robots.Tick(0.4f, null);
        Assert.True(soldier.Taken > 0f, "the robot's own blast reaches the soldier 40 u off");
    }

    [Fact]
    public void The_death_spec_decides_the_explosion_as_EnemyKill_does()
    {
        static EnemyType T(uint spec, int? kind, params EnemyDeathPart[] death) =>
            new() { Record = [0, 0, 0, 0, spec, 0, 0], DeathKind = kind, Death = [.. death] };

        Assert.False(Robots.ExplodesOnDeath(T(0, null)));                                  // nothing
        Assert.True(Robots.ExplodesOnDeath(T(1, null)));                                   // explosion alone
        Assert.False(Robots.ExplodesOnDeath(T(0x1000, 0, new EnemyDeathPart(95, 0, 0, 0)))); // one part only
        Assert.True(Robots.ExplodesOnDeath(T(0x1000, 1, new EnemyDeathPart(145, 0, 0, 0)))); // a list of one (type 77)
        Assert.False(Robots.ExplodesOnDeath(T(0x1000, 2)));                                // unknown kind: nothing
        // An older table without the kind: a lone part at the origin reads as kind 0.
        Assert.False(Robots.ExplodesOnDeath(T(0x1000, null, new EnemyDeathPart(95, 0, 0, 0))));
        Assert.True(Robots.ExplodesOnDeath(T(0x1000, null, new EnemyDeathPart(88, 35, 0, -61), new EnemyDeathPart(89, 1, 2, 3))));
    }

    [Fact]
    public void A_part_on_flat_ground_lands_where_the_ballistics_put_it()
    {
        var world = new CollisionWorld().Add(new HeightField(new float[4 * 4], 4, 4, 4096f, new Vector2(-8192f, -8192f)));
        var w = new Wreckage();
        Assert.True(w.Add(0, null, new Vector3(0f, 100f, 0f), new Vector3(-10f, 100f, 0f), new RetroEng.Loop.SimRandom(1)));
        var p0 = w.Parts[0];
        Vector3? landed = null;
        var t = 0f;
        while (landed == null && t < 5f)
        {
            w.Tick(Dt, world, (at, _) => landed = at);
            t += Dt;
        }

        Assert.NotNull(landed);
        Assert.InRange(landed!.Value.Y, -0.5f, 0.5f);
        // y(t) = 100 + vy t − 196 t² = 0.
        var vy = p0.Velocity.Y;
        var tFlight = (vy + MathF.Sqrt(vy * vy + 4f * 196f * 100f)) / (2f * 196f);
        Assert.InRange(t, tFlight - 2 * Dt, tFlight + 2 * Dt);
        Assert.True(landed.Value.X > 0f, "thrown away from the robot");
    }

    [Fact]
    public void The_pool_holds_its_cap_and_frees_slots_as_parts_go_off()
    {
        var w = new Wreckage();
        var rng = new RetroEng.Loop.SimRandom(5);
        var accepted = 0;
        for (var i = 0; i < Wreckage.Capacity + 10; i++)
        {
            if (w.Add(0, null, new Vector3(i, 0f, 0f), Vector3.Zero, rng))
            {
                accepted++;
            }
        }

        Assert.Equal(Wreckage.Capacity, accepted);
        Assert.Equal(Wreckage.Capacity, w.Count);
        Assert.Equal(Wreckage.Capacity, w.Parts.Length);

        // Nothing to hit: each goes off at 2000 u of travel (or the guard) and frees its slot.
        var empty = new CollisionWorld();
        for (var t = 0f; t < Wreckage.MaxLife + 1f && w.Count > 0; t += Dt)
        {
            w.Tick(Dt, empty, (_, _) => { });
        }

        Assert.Equal(0, w.Count);
        Assert.True(w.Add(0, null, Vector3.Zero, Vector3.Zero, rng));
        w.Clear();
        Assert.Equal(0, w.Count);
    }
}
