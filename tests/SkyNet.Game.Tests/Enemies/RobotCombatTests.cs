using System.Numerics;
using RetroEng.Physics;
using SkyNet.Game.Enemies;
using SkyNet.Game.Tests.Behaviour;
using SkyNet.Game.Weapons;

namespace SkyNet.Game.Tests.Enemies;

/// <summary>ROBOT AI IMPROVED's combat (phase 2 of docs/plans/smarter-ai.md): reaction delay, aim
/// error, attack tokens, cover, and the ruling that robots hit no more often than before.</summary>
public class RobotCombatTests
{
    private const float Dt = 1f / 60f;
    private const int DroneType = 2, HvyTurretType = 25, PlasmaTurretType = 59, RaptorType = 32, T800Type = 37;

    /// <summary>A floor at Y 0 and the boxes (both windings).</summary>
    private static CollisionWorld World(params (Vector3 Min, Vector3 Max)[] boxes)
    {
        const float L = 20000f;
        var tris = new List<Triangle>
        {
            new(new Vector3(-L, 0f, -L), new Vector3(-L, 0f, L), new Vector3(L, 0f, L)),
            new(new Vector3(-L, 0f, -L), new Vector3(L, 0f, L), new Vector3(L, 0f, -L)),
        };
        foreach (var (a, b) in boxes)
        {
            Vector3 P(int i) => new((i & 1) != 0 ? b.X : a.X, (i & 2) != 0 ? b.Y : a.Y, (i & 4) != 0 ? b.Z : a.Z);
            int[][] faces = [[0, 1, 3, 2], [4, 6, 7, 5], [0, 4, 5, 1], [2, 3, 7, 6], [0, 2, 6, 4], [1, 5, 7, 3]];
            foreach (var f in faces)
            {
                tris.Add(new Triangle(P(f[0]), P(f[1]), P(f[2])));
                tris.Add(new Triangle(P(f[0]), P(f[2]), P(f[3])));
                tris.Add(new Triangle(P(f[0]), P(f[2]), P(f[1])));
                tris.Add(new Triangle(P(f[0]), P(f[3]), P(f[2])));
            }
        }

        return new CollisionWorld().Add(new TriangleGrid(tris, 256f));
    }

    private static (Robots Robots, Soldier Soldier, WeaponSystem Weapons) Setup(CollisionWorld world, RobotAi ai, Vector3 feet, int seed = 3)
    {
        var data = BehaviourData.Data;
        var soldier = new Soldier { Feet = feet };
        var weapons = new WeaponSystem(WeaponTable.Load(data), world) { Player = soldier };
        var robots = new Robots(SkyNet.Data.GameTables.Ais, new EnemyModels(data), world, weapons, seed: seed) { Ai = ai };
        return (robots, soldier, weapons);
    }

    private static float Facing(Vector3 from, Vector3 at) => MathF.Atan2(-(at.X - from.X), -(at.Z - from.Z));

    private static void Step(Robots robots, WeaponSystem weapons, Soldier soldier)
    {
        robots.Tick(Dt, soldier.Feet);
        weapons.Tick(default, new Shooter(soldier.Feet + new Vector3(0f, 75f, 0f), 0f, 0f), Dt);
    }

    // ---------------------------------------------------------------- the ruling

    /// <summary>The scripted fight: three raptors, a T-800, two drones and a heavy turret against a
    /// soldier who strafes left and right, then stands, for <paramref name="seconds"/>.
    /// Returns the robots' hits on him and their shots.</summary>
    internal static (int Hits, int Shots) Fight(RobotAi ai, int seed, float seconds = 60f)
    {
        var start = new Vector3(0f, 0f, -1400f);
        var (robots, soldier, weapons) = Setup(World(), ai, start, seed);
        Vector3[] at =
        [
            new(-500f, 0f, 0f), new(0f, 0f, 100f), new(500f, 0f, 0f), new(-900f, 0f, -200f),
            new(-300f, 0f, -300f), new(300f, 0f, -300f), new(900f, 0f, -200f),
        ];
        int[] types = [RaptorType, RaptorType, RaptorType, T800Type, DroneType, DroneType, HvyTurretType];
        for (var i = 0; i < at.Length; i++)
        {
            robots.Add(types[i], at[i], Facing(at[i], start));
        }

        var ticks = (int)(seconds / Dt);
        for (var t = 0; t < ticks; t++)
        {
            // Strafes 600 u side to side for two thirds of the time (turning at the ends), then stands.
            var s = t * Dt;
            var x = s < seconds * 2f / 3f ? 600f * MathF.Sin(s * 0.9f) : 0f;
            soldier.Feet = start with { X = x };
            Step(robots, weapons, soldier);
        }

        return (soldier.Hits.Count, robots.All.Sum(r => r.ShotsFired));
    }

    /// <summary>The robots' hits on the soldier in <see cref="Fight"/> over <see cref="FightSeeds"/>,
    /// IMPROVED at cea8727 (perfect aim at the eye, DOS's fire roll): 134 + 172 + 178 + 165 + 174 +
    /// 144 = 967 of 3109 shots. Phase 2 measured 860 of 2939 (0.89).</summary>
    private const int BaselineHits = 967;
    /// <summary>The band IMPROVED must land in: not deadlier, and not much weaker (crowded maps keep their balance).</summary>
    private const float BandLow = 0.75f, BandHigh = 1f;
    private static readonly int[] FightSeeds = [3, 11, 29, 41, 53, 67];

    /// <summary>The ruling: IMPROVED robots hit him no more often than before phase 2, and not much less.</summary>
    [BehaviourDataFact]
    public void ImprovedHitsNoMoreThanBefore()
    {
        var hits = 0;
        var shots = 0;
        foreach (var seed in FightSeeds)
        {
            var (h, s) = Fight(RobotAi.Improved, seed);
            (hits, shots) = (hits + h, shots + s);
        }

        Assert.True(shots > 0 && hits > 0, $"no fight: {hits} hits of {shots} shots");
        Assert.InRange(hits, (int)(BaselineHits * BandLow), (int)(BaselineHits * BandHigh));
    }

    // ---------------------------------------------------------------- aim

    /// <summary>Seconds from the plasma turret's first look at him to its first shot.</summary>
    private static float FirstShot(RobotAi ai)
    {
        var feet = new Vector3(0f, 0f, -500f);
        var (robots, soldier, weapons) = Setup(World(), ai, feet);
        var turret = robots.Add(PlasmaTurretType, Vector3.Zero);
        float? seen = null;
        for (var t = 0; t < 60 * 10; t++)
        {
            Step(robots, weapons, soldier);
            seen ??= turret.Sees ? t * Dt : null;
            if (turret.ShotsFired > 0)
            {
                return t * Dt - (seen ?? 0f);
            }
        }

        return float.PositiveInfinity;
    }

    /// <summary>The plasma turret, facing him, sees him: CLASSIC fires on its first lucky roll;
    /// IMPROVED only after its reaction (0.5 s ± a quarter).</summary>
    [BehaviourDataFact]
    public void TheReactionDelaysTheFirstShot()
    {
        var classic = FirstShot(RobotAi.Classic);
        var improved = FirstShot(RobotAi.Improved);
        Assert.True(improved >= 0.375f && improved > classic, $"improved {improved:0.00} s, classic {classic:0.00} s");
        Assert.True(float.IsFinite(improved), "never fired");
    }

    /// <summary>A plasma turret standing on the floor (a spawn sprite's, at its own Y): its muzzle is
    /// 6 under the origin, in the floor. The bolt starts outside the model, so CLASSIC fires; IMPROVED's
    /// clear-line test ran from the muzzle, met the floor and never fired.</summary>
    [BehaviourDataTheory]
    [InlineData(RobotAi.Classic)]
    [InlineData(RobotAi.Improved)]
    public void AFloorTurretFires(RobotAi ai)
    {
        var feet = new Vector3(0f, 0f, -500f);
        var (robots, soldier, weapons) = Setup(World(), ai, feet);
        var turret = robots.Add(PlasmaTurretType, Vector3.Zero);
        for (var t = 0; t < 60 * 5 && turret.ShotsFired == 0; t++)
        {
            Step(robots, weapons, soldier);
        }

        Assert.True(turret.ShotsFired > 0, $"{ai}: never fired");
    }

    /// <summary>A raptor tracking a standing soldier: the aim error settles within 2 s; his sharp
    /// turn and a hit throw it off again.</summary>
    [BehaviourDataFact]
    public void TheAimErrorShrinksWhileTracking()
    {
        var feet = new Vector3(0f, 0f, -900f);
        var (robots, soldier, weapons) = Setup(World(), RobotAi.Improved, feet);
        robots.HoldFire = true;
        var raptor = robots.Add(RaptorType, Vector3.Zero, Facing(Vector3.Zero, feet));
        var t = 0;
        for (; t < 60 * 2 && !raptor.Sees; t++)
        {
            Step(robots, weapons, soldier);
        }

        Step(robots, weapons, soldier);
        var acquired = raptor.AimError;
        for (var i = 0; i < 60 * 2; i++)
        {
            Step(robots, weapons, soldier);
        }

        var settled = raptor.AimError;
        Assert.True(settled < acquired * 0.4f, $"acquired {acquired:0.000}, settled {settled:0.000}");

        // He runs left, then cuts back right.
        for (var i = 0; i < 20; i++)
        {
            soldier.Feet += new Vector3(-5f, 0f, 0f);
            Step(robots, weapons, soldier);
        }

        var running = raptor.AimError;
        soldier.Feet += new Vector3(5f, 0f, 0f);
        Step(robots, weapons, soldier);
        Assert.True(raptor.AimError > running + 0.05f, $"running {running:0.000}, cut back {raptor.AimError:0.000}");

        var before = raptor.AimError;
        raptor.TakeDamage(new Damage(1f, DamageKind.Direct, raptor.Centre, true));
        Assert.True(raptor.AimError > before + 0.05f, $"before {before:0.000}, hit {raptor.AimError:0.000}");
    }

    /// <summary>A save keeps the fight: contact, aim error, token; an older save (none) starts afresh.</summary>
    [BehaviourDataFact]
    public void TheFightSurvivesASave()
    {
        var feet = new Vector3(0f, 0f, -900f);
        var (robots, soldier, weapons) = Setup(World(), RobotAi.Improved, feet);
        var raptor = robots.Add(RaptorType, Vector3.Zero, Facing(Vector3.Zero, feet));
        for (var t = 0; t < 90; t++)
        {
            Step(robots, weapons, soldier);
        }

        var saved = robots.Snapshot();
        Assert.True(saved[0].Memory?.Combat is { Contact: true });
        Assert.True(raptor.HoldsToken);

        var (loaded, _, _) = Setup(World(), RobotAi.Improved, feet);
        var back = loaded.Add(RaptorType, Vector3.Zero, Facing(Vector3.Zero, feet));
        Assert.True(loaded.Restore(saved));
        Assert.Equal((raptor.AimError, true), (back.AimError, back.HoldsToken));

        var (older, _, _) = Setup(World(), RobotAi.Improved, feet);
        var fresh = older.Add(RaptorType, Vector3.Zero, Facing(Vector3.Zero, feet));
        Assert.True(older.Restore([saved[0] with { Memory = saved[0].Memory!.Value with { Combat = null } }]));
        Assert.False(fresh.HoldsToken);
    }

    /// <summary>A load mid-fight deals the tokens as the run without one (a demo from a save must
    /// not desync). Raptor 0 saw him, then he went beyond the activity window: it stopped ticking,
    /// in contact but stale; raptors 1 and 2 fight him there. A load once stamped every robot's last
    /// combat tick with the clock before the trail put the clock back: all stale, the tokens dropped.</summary>
    [BehaviourDataFact]
    public void ALoadDealsTheTokensAsBefore()
    {
        var near = new Vector3(0f, 0f, -900f);
        var far = new Vector3(0f, 0f, -5000f);
        Vector3[] at = [new(0f, 0f, 0f), new(-300f, 0f, -4200f), new(300f, 0f, -4200f)];
        (Robots, Soldier, WeaponSystem) Make()
        {
            var made = Setup(World(), RobotAi.Improved, near);
            made.Robots.HoldFire = true;
            foreach (var p in at)
            {
                made.Robots.Add(RaptorType, p, Facing(p, p == at[0] ? near : far));
            }

            return made;
        }

        var (robots, soldier, weapons) = Make();
        for (var t = 0; t < 90; t++)
        {
            Step(robots, weapons, soldier);
        }

        soldier.Feet = far;
        for (var t = 0; t < 120; t++)
        {
            Step(robots, weapons, soldier);
        }

        Assert.True(robots.All[1].HoldsToken || robots.All[2].HoldsToken);
        var saved = robots.Snapshot();
        var trail = robots.SaveTrail();
        var (loaded, ls, lw) = Make();
        ls.Feet = far;
        Assert.True(loaded.Restore(saved));
        loaded.RestoreTrail(trail);
        for (var t = 0; t < 120; t++)
        {
            Step(robots, weapons, soldier);
            Step(loaded, lw, ls);
            for (var i = 0; i < at.Length; i++)
            {
                Assert.True(robots.All[i].HoldsToken == loaded.All[i].HoldsToken, $"tick {t}, raptor {i}: token {robots.All[i].HoldsToken} before, {loaded.All[i].HoldsToken} loaded");
            }
        }
    }

    /// <summary>A crafted save (an hour's reaction, token rest and cover, cover for a T-800 that
    /// never takes any) loads held to the robot's own limits: it fights on within seconds.</summary>
    [BehaviourDataFact]
    public void ACraftedFightLoadsWithinLimits()
    {
        var feet = new Vector3(0f, 0f, -600f);
        var (robots, soldier, weapons) = Setup(World(), RobotAi.Improved, feet);
        var t800 = robots.Add(T800Type, Vector3.Zero, Facing(Vector3.Zero, feet));
        Step(robots, weapons, soldier);
        var s = robots.Snapshot()[0];
        var hour = new RobotCombat(true, 0f, 3600f, 0.1f, -Vector3.UnitZ, feet, new Vector3(1e6f, 0f, 0f), false, 0f, 0, 3600f,
            new Vector3(5000f, 0f, 5000f), 3600f, true, 3600f, 0, 1, 3600f, null, 1, 0f);
        Assert.True(robots.Restore([s with { Memory = s.Memory!.Value with { Combat = hour } }]));
        Assert.False(t800.InCover);
        for (var t = 0; t < 60 * 5 && t800.ShotsFired == 0; t++)
        {
            Step(robots, weapons, soldier);
        }

        Assert.True(t800.ShotsFired > 0, "silenced by the save");
    }

    // ---------------------------------------------------------------- tokens

    /// <summary>Eight raptors in sight of him: at most <see cref="Robots.MaxAttackers"/> hold a token at
    /// once, those without fire less, and the tokens go round.</summary>
    [BehaviourDataFact]
    public void AtMostFiveFireFreely()
    {
        var feet = new Vector3(0f, 0f, -1000f);
        var (robots, soldier, weapons) = Setup(World(), RobotAi.Improved, feet);
        var raptors = new List<Enemy>();
        for (var i = 0; i < 8; i++)
        {
            var at = new Vector3(-1050f + i * 300f, 0f, 0f);
            raptors.Add(robots.Add(RaptorType, at, Facing(at, feet)));
        }

        var shots = new int[raptors.Count];
        var holders = new HashSet<Enemy>();
        var (heldTicks, waitTicks, heldShots, waitShots) = (0, 0, 0, 0);
        for (var t = 0; t < 60 * 30; t++)
        {
            Step(robots, weapons, soldier);
            Assert.True(raptors.Count(r => r.HoldsToken) <= Robots.MaxAttackers);
            for (var i = 0; i < raptors.Count; i++)
            {
                var r = raptors[i];
                var shot = r.ShotsFired - shots[i];
                shots[i] = r.ShotsFired;
                if (r.HoldsToken)
                {
                    (heldTicks, heldShots) = (heldTicks + 1, heldShots + shot);
                    holders.Add(r);
                }
                else if (r.Sees)
                {
                    (waitTicks, waitShots) = (waitTicks + 1, waitShots + shot);
                }
            }
        }

        Assert.True(holders.Count > Robots.MaxAttackers, $"{holders.Count} raptors held a token");
        Assert.True(waitTicks > 0 && heldTicks > 0);
        Assert.True((float)waitShots / waitTicks < (float)heldShots / heldTicks,
            $"without a token {waitShots} shots in {waitTicks} ticks, with {heldShots} in {heldTicks}");
    }

    // ---------------------------------------------------------------- cover

    /// <summary>A wall off to one side between him and the robot's flank (tall: a drone flies over most things).</summary>
    private static readonly (Vector3, Vector3) SideWall = (new Vector3(150f, 0f, -300f), new Vector3(1200f, 3000f, -260f));

    /// <summary>Hurt below 35 %, a drone goes where he cannot see it, holds, then fights on; a hurt
    /// T-800 never does.</summary>
    [BehaviourDataTheory]
    [InlineData(DroneType, true)]
    [InlineData(T800Type, false)]
    public void AHurtDroneBreaksTheLineOfSight(int type, bool hides)
    {
        var feet = new Vector3(0f, 0f, -1000f);
        var (robots, soldier, weapons) = Setup(World(SideWall), RobotAi.Improved, feet);
        robots.HoldFire = true;
        var robot = robots.Add(type, Vector3.Zero, Facing(Vector3.Zero, feet));
        for (var t = 0; t < 60; t++)
        {
            Step(robots, weapons, soldier);   // the drone climbs to its height
        }

        Assert.True(robot.Sees);
        robot.TakeDamage(new Damage(robot.MaxHp * 0.7f, DamageKind.Direct, robot.Centre, true));
        var covered = false;
        var hidden = false;
        var back = false;
        for (var t = 0; t < 60 * 12; t++)
        {
            Step(robots, weapons, soldier);
            covered |= robot.InCover;
            hidden |= robot.InCover && !robot.LineOfSight(feet);
            back |= covered && !robot.InCover;
        }

        Assert.Equal(hides, covered);
        Assert.Equal(hides, hidden);
        Assert.Equal(hides, back);
    }
}
