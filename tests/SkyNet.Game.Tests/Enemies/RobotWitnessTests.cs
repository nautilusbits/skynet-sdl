using System.Numerics;
using RetroEng.Physics;
using SkyNet.Game.Enemies;
using SkyNet.Game.Tests.Behaviour;
using SkyNet.Game.Weapons;

namespace SkyNet.Game.Tests.Enemies;

/// <summary>IMPROVED robots that witness a hit and search with purpose (owner's mission 3 playtest:
/// "a robot does not react when the colleague next to it is blown up", "they don't search for me").</summary>
public class RobotWitnessTests
{
    private const float Dt = 1f / 60f;
    private const int RaptorType = 32, Uzi = 1;
    private const float EyeUp = 75f;

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

    private sealed record Rig(Robots Robots, Soldier Soldier, WeaponSystem Weapons);

    private static Rig Setup(CollisionWorld world, Vector3 feet)
    {
        var data = BehaviourData.Data;
        var soldier = new Soldier { Feet = feet };
        var weapons = new WeaponSystem(WeaponTable.Load(data), world) { Player = soldier };
        var robots = new Robots(SkyNet.Data.GameTables.Ais, new EnemyModels(data), world, weapons, seed: 3) { Ai = RobotAi.Improved, HoldFire = true };
        return new Rig(robots, soldier, weapons);
    }

    /// <summary>The yaw facing <paramref name="at"/> from <paramref name="from"/>.</summary>
    private static float Facing(Vector3 from, Vector3 at) => MathF.Atan2(-(at.X - from.X), -(at.Z - from.Z));

    private static float Flat(Vector3 a, Vector3 b) => Vector2.Distance(new(a.X, a.Z), new(b.X, b.Z));

    /// <summary>The soldier's aim from his eye at <paramref name="at"/>.</summary>
    private static Shooter Aim(Rig rig, Vector3 at)
    {
        var eye = rig.Soldier.Feet + new Vector3(0f, EyeUp, 0f);
        var to = Vector3.Normalize(at - eye);
        return new Shooter(eye, MathF.Atan2(-to.X, -to.Z), MathF.Asin(to.Y));
    }

    private static void Arm(Rig rig, int weapon)
    {
        rig.Weapons.Inventory.Grant(weapon);
        rig.Weapons.Inventory.AddAmmo(rig.Weapons.Inventory.Table.Weapons[weapon].Pool, 200);
        rig.Weapons.Tick(new WeaponInput(false, false, false, Select: weapon), Aim(rig, rig.Soldier.Feet + new Vector3(0f, EyeUp, -100f)), Dt);
    }

    /// <summary>One tick: the robots, then his weapon (firing at <paramref name="at"/> when given).</summary>
    private static void Step(Rig rig, Vector3? at = null)
    {
        rig.Robots.Tick(Dt, rig.Soldier.Feet);
        rig.Weapons.Tick(new WeaponInput(at != null, false, false), Aim(rig, at ?? rig.Soldier.Feet + new Vector3(0f, EyeUp, -100f)), Dt);
    }

    private static Vector3 Body(Enemy r) => Vector3.Transform(new Vector3(0f, 0f, 30f), r.Root.World);

    // ---------------------------------------------------------------- witnesses

    /// <summary>A tall wall across his hiding place.</summary>
    private static readonly (Vector3, Vector3) HideWall = (new Vector3(-3000f, 0f, -1600f), new Vector3(3000f, 2000f, -1500f));
    private static readonly Vector3 Hidden = new(0f, 0f, -2600f);

    /// <summary>A pillar between him and the neighbour (it never sees him there).</summary>
    private static readonly (Vector3, Vector3) Pillar = (new Vector3(120f, 0f, -420f), new Vector3(260f, 400f, -300f));

    /// <summary>He shoots the raptor next to another (both facing away), then hides: the neighbour
    /// goes at once (no suspicious hold) for where he shot from, not where he went.</summary>
    [BehaviourDataTheory]
    [InlineData(Uzi)]
    public void AWitnessGoesForTheShotsOrigin(int weapon)
    {
        var shotFrom = new Vector3(0f, 0f, -700f);
        var rig = Setup(World(HideWall, Pillar), shotFrom);
        var victim = rig.Robots.Add(RaptorType, Vector3.Zero, MathF.PI);
        var witness = rig.Robots.Add(RaptorType, new Vector3(300f, 0f, 0f), MathF.PI);
        Arm(rig, weapon);
        for (var t = 0; t < 30; t++)
        {
            Step(rig);
        }

        for (var t = 0; t < 60 * 3 && victim.Hp >= victim.MaxHp; t++)
        {
            Step(rig, Body(victim));
        }

        Assert.True(victim.Hp < victim.MaxHp, "never hit");
        rig.Soldier.Feet = Hidden;
        var after = witness.Alertness;
        var states = new HashSet<RobotAlert>();
        var closest = float.MaxValue;
        for (var t = 0; t < 60 * 15; t++)
        {
            Step(rig);
            states.Add(witness.Alertness);
            closest = MathF.Min(closest, Flat(witness.Position, shotFrom));
        }

        Assert.Equal(RobotAlert.Investigate, after);
        Assert.DoesNotContain(RobotAlert.Combat, states);
        Assert.True(closest < 100f, $"closest {closest:0} to where he shot from");
    }

    /// <summary>A pillar hides him from a raptor 1200 u off that watches the victim: it sees the hit
    /// and goes for him at once; facing away (it hears nothing) it stays idle.</summary>
    [BehaviourDataTheory]
    [InlineData(true)]
    [InlineData(false)]
    public void ARobotThatSeesTheHitReacts(bool watches)
    {
        var shotFrom = new Vector3(0f, 0f, -700f);
        var pillar = (new Vector3(500f, 0f, -450f), new Vector3(700f, 400f, -250f));
        var rig = Setup(World(pillar), shotFrom);
        var victim = rig.Robots.Add(RaptorType, Vector3.Zero, MathF.PI);
        var at = new Vector3(1200f, 0f, 0f);
        var witness = rig.Robots.Add(RaptorType, at, watches ? Facing(at, Vector3.Zero) : Facing(at, new Vector3(2400f, 0f, 0f)));
        Arm(rig, Uzi);
        for (var t = 0; t < 30; t++)
        {
            Step(rig);
        }

        Assert.False(witness.LineOfSight(shotFrom));
        for (var t = 0; t < 60 * 3 && victim.Hp >= victim.MaxHp; t++)
        {
            Step(rig, Body(victim));
        }

        Assert.True(victim.Hp < victim.MaxHp, "never hit");
        Assert.Equal(watches ? RobotAlert.Investigate : RobotAlert.Idle, witness.Alertness);
    }

    /// <summary>He fires from beyond its hearing; the bullets strike the floor next to the raptor:
    /// it goes for him at once.</summary>
    [BehaviourDataFact]
    public void BulletsLandingNearItAlertIt()
    {
        var shotFrom = new Vector3(0f, 0f, -1800f);
        var rig = Setup(World(), shotFrom);
        var raptor = rig.Robots.Add(RaptorType, Vector3.Zero, MathF.PI);
        Arm(rig, Uzi);
        for (var t = 0; t < 30; t++)
        {
            Step(rig);
        }

        var floor = new Vector3(0f, 0f, -120f);
        for (var t = 0; t < 6; t++)
        {
            Step(rig, floor);
        }

        Assert.Equal(RobotAlert.Investigate, raptor.Alertness);
        var start = Flat(raptor.Position, shotFrom);
        for (var t = 0; t < 60 * 5; t++)
        {
            Step(rig);
        }

        Assert.True(start - Flat(raptor.Position, shotFrom) > 200f, $"moved {start - Flat(raptor.Position, shotFrom):0} toward him");
    }

    // ---------------------------------------------------------------- the search

    /// <summary>A crafted save (a lead and an order past its own, an hour to reach the lead, a
    /// sighting at a huge speed) loads held to the robot's limits.</summary>
    [BehaviourDataFact]
    public void ACraftedSearchLoadsWithinLimits()
    {
        var rig = Setup(World(), Hidden);
        rig.Robots.Add(RaptorType, Vector3.Zero);
        Step(rig);
        var s = rig.Robots.Snapshot()[0];
        var crafted = new RobotSearch(Vector3.Zero, new Vector3(1e6f, 0f, 0f), new Vector3(3f, 0f, 4f), 1000, -5, 3600f);
        Assert.True(rig.Robots.Restore([s with { Memory = s.Memory!.Value with { Search = crafted } }]));

        var back = rig.Robots.Snapshot()[0].Memory!.Value.Search!.Value;
        Assert.True(back.Lead <= 4 && back.Slot >= 0 && back.LeadT <= 20f && back.SeenVel.Length() <= 4001f, $"{back}");
        Assert.Equal(1f, back.Way.Length(), 3);
    }

    /// <summary>A wall he runs along out of sight (+X), past its end at x 150.</summary>
    private static readonly (Vector3, Vector3) RunWall = (new Vector3(150f, 0f, -450f), new Vector3(3000f, 2000f, -430f));

    /// <summary>He is seen at the wall's end, runs +X behind it and vanishes (beyond the hide
    /// wall). The searchers go to where they lost him, then on along his way, and keep at it for
    /// 25 s; two pick different spots.</summary>
    [BehaviourDataFact]
    public void TheSearchFollowsHisWay()
    {
        var start = new Vector3(0f, 0f, -650f);
        var rig = Setup(World(RunWall, HideWall), start);
        Vector3[] at = [new(-80f, 0f, 0f), new(80f, 0f, 0f)];
        var raptors = at.Select(p => rig.Robots.Add(RaptorType, p, Facing(p, start))).ToArray();
        for (var t = 0; t < 60; t++)
        {
            Step(rig);
        }

        Assert.All(raptors, r => Assert.Equal(RobotAlert.Combat, r.Alertness));

        // He runs +X at 250 u/s; once both lost him, he is gone.
        var lostAt = new Vector3?[raptors.Length];
        for (var t = 0; t < 60 * 3 && lostAt.Any(l => l == null); t++)
        {
            rig.Soldier.Feet += new Vector3(250f * Dt, 0f, 0f);
            Step(rig);
            for (var i = 0; i < raptors.Length; i++)
            {
                lostAt[i] ??= raptors[i].Alertness == RobotAlert.Search ? rig.Soldier.Feet : null;
            }
        }

        Assert.All(lostAt, l => Assert.NotNull(l));
        rig.Soldier.Feet = Hidden;
        var along = new float[raptors.Length];
        var searchT = new float[raptors.Length];
        var apart = 0f;
        for (var t = 0; t < 60 * 60; t++)
        {
            Step(rig);
            for (var i = 0; i < raptors.Length; i++)
            {
                if (raptors[i].Alertness != RobotAlert.Search)
                {
                    continue;
                }

                searchT[i] += Dt;
                along[i] = MathF.Max(along[i], raptors[i].Position.X - lostAt[i]!.Value.X);
            }

            if (raptors.All(r => r.Alertness == RobotAlert.Search))
            {
                apart = MathF.Max(apart, Flat(raptors[0].Position, raptors[1].Position));
            }
        }

        Assert.True(along.Max() > 450f, $"followed {along.Max():0} u along his way");
        Assert.All(searchT, s => Assert.True(s >= 25f, $"searched {s:0.0} s"));
        Assert.True(apart > 200f, $"searchers at most {apart:0} apart");
        Assert.All(raptors, r => Assert.Equal(RobotAlert.Idle, r.Alertness));
    }
}
