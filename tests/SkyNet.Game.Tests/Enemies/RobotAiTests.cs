using System.Numerics;
using RetroEng.Physics;
using SkyNet.Game.Demos;
using SkyNet.Game.Enemies;
using SkyNet.Game.Menu;
using SkyNet.Game.Tests.Behaviour;
using SkyNet.Game.Weapons;

namespace SkyNet.Game.Tests.Enemies;

/// <summary>ROBOT AI: IMPROVED's movement and fire on small built worlds (a floor and boxes), and
/// CLASSIC left as DOS's.</summary>
public class RobotAiTests
{
    private const float Dt = 1f / 60f;
    private const int GlobeType = 1, DroneType = 2, HkType = 4, RaptorType = 32;

    // ---------------------------------------------------------------- a world of boxes

    /// <summary>A floor at Y 0 (facing up) and the boxes (both windings: solid from either side).</summary>
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

    private static (Robots Robots, Soldier Soldier, WeaponSystem Weapons) Setup(CollisionWorld world, RobotAi ai, Vector3 soldierFeet)
    {
        var data = BehaviourData.Data;
        var soldier = new Soldier { Feet = soldierFeet };
        var weapons = new WeaponSystem(WeaponTable.Load(data), world) { Player = soldier };
        var robots = new Robots(SkyNet.Data.GameTables.Ais, new EnemyModels(data), world, weapons, seed: 3) { Ai = ai };
        return (robots, soldier, weapons);
    }

    /// <summary>The yaw facing <paramref name="at"/> from the origin (IMPROVED sees in a cone).</summary>
    private static float Facing(Vector3 at) => MathF.Atan2(-at.X, -at.Z);

    /// <summary>The body (the ellipsoid inside the model's bounds, scaled by <paramref name="shrink"/>,
    /// sampled on a grid) has a point inside <paramref name="box"/>.</summary>
    private static bool Overlaps(Enemy hk, float shrink, (Vector3 Min, Vector3 Max) box)
    {
        const int N = 6;
        var b = hk.Root.Model!.Bounds;
        var c = (b.Min + b.Max) * 0.5f;
        var h = (b.Max - b.Min) * 0.5f * shrink;
        var world = Matrix4x4.CreateRotationY(hk.Yaw) * Matrix4x4.CreateTranslation(hk.Position);
        for (var i = -N; i <= N; i++)
        {
            for (var j = -N; j <= N; j++)
            {
                for (var k = -N; k <= N; k++)
                {
                    var u = new Vector3(i, j, k) / N;
                    if (u.LengthSquared() > 1f)
                    {
                        continue;
                    }

                    var p = Vector3.Transform(c + u * h, world);
                    if (p.X > box.Min.X && p.X < box.Max.X && p.Y > box.Min.Y && p.Y < box.Max.Y && p.Z > box.Min.Z && p.Z < box.Max.Z)
                    {
                        return true;
                    }
                }
            }
        }

        return false;
    }

    // ---------------------------------------------------------------- flyers

    /// <summary>The HK chasing the soldier between two scaffold posts: the gap lets its middle through
    /// but not its wings. CLASSIC tests one ray from the middle and flies its wings through; IMPROVED
    /// never puts its body into them.</summary>
    [BehaviourDataTheory]
    [InlineData(RobotAi.Classic, true)]
    [InlineData(RobotAi.Improved, false)]
    public void TheHkKeepsOutOfTheScaffolding(RobotAi ai, bool enters)
    {
        (Vector3, Vector3)[] posts =
        [
            (new Vector3(-140f, 0f, -1420f), new Vector3(-80f, 3000f, -1380f)),
            (new Vector3(80f, 0f, -1420f), new Vector3(140f, 3000f, -1380f)),
        ];
        var (robots, _, _) = Setup(World(posts), ai, new Vector3(0f, 0f, -2400f));
        robots.HoldFire = true;
        var hk = robots.Add(HkType, new Vector3(0f, 320f, -500f));
        var inside = 0;
        var seen = false;
        for (var t = 0; t < 60 * 15; t++)
        {
            robots.Tick(Dt, new Vector3(0f, 0f, -2400f));
            seen |= hk.Sees;
            inside += posts.Any(p => Overlaps(hk, 0.8f, p)) ? 1 : 0;
        }

        Assert.True(seen);
        Assert.Equal(enters, inside > 0);
    }

    /// <summary>A beam across its way at its height, too long to go round: IMPROVED climbs over it.</summary>
    [BehaviourDataFact]
    public void TheHkClimbsOverAGirder()
    {
        var beam = (new Vector3(-6000f, 200f, -1200f), new Vector3(6000f, 420f, -1100f));
        var (robots, _, _) = Setup(World(beam), RobotAi.Improved, new Vector3(0f, 0f, -1800f));
        robots.HoldFire = true;
        var hk = robots.Add(HkType, new Vector3(0f, 320f, 0f));
        var crossed = false;
        for (var t = 0; t < 60 * 20; t++)
        {
            robots.Tick(Dt, new Vector3(0f, 0f, -1800f));
            Assert.False(Overlaps(hk, 0.8f, beam), $"in the beam at {hk.Position}");
            crossed |= hk.Position.Z < -1300f;
        }

        Assert.True(crossed, $"stayed at {hk.Position}");
    }

    /// <summary>A flying mine indoors (a room under a ceiling slab), the soldier out of its sight at
    /// first, then in the room (#23). It hovers below the ceiling, not on or over it, and once he is
    /// in sight it comes for him and goes off.</summary>
    [BehaviourDataTheory]
    [InlineData(RobotAi.Classic)]
    [InlineData(RobotAi.Improved)]
    public void TheMineChasesUnderACeiling(RobotAi ai)
    {
        const float ceiling = 300f;
        var slab = (new Vector3(-8000f, ceiling, -8000f), new Vector3(8000f, ceiling + 40f, 8000f));
        var far = new Vector3(0f, 0f, -3000f);
        var (robots, _, _) = Setup(World(slab), ai, far);
        var mine = robots.Add(GlobeType, new Vector3(0f, 100f, 0f));
        for (var t = 0; t < 60 * 10; t++)
        {
            robots.Tick(Dt, far);
            Assert.True(mine.Position.Y < ceiling - 20f, $"at {mine.Position} under a ceiling at {ceiling}");
        }

        var near = new Vector3(mine.Position.X, 0f, mine.Position.Z - 800f);
        for (var t = 0; t < 60 * 15 && mine.Alive; t++)
        {
            robots.Tick(Dt, near);
        }

        Assert.False(mine.Alive, $"still at {mine.Position}, the soldier at {near}");
    }

    // ---------------------------------------------------------------- walkers

    /// <summary>A raptor in a pocket of knee-high walls, open at the back, the soldier in sight over
    /// the front wall. CLASSIC presses into the front wall for good; IMPROVED walks round it and on.</summary>
    [BehaviourDataTheory]
    [InlineData(RobotAi.Classic, false)]
    [InlineData(RobotAi.Improved, true)]
    public void AWalkerInAPocketWalksRound(RobotAi ai, bool gets)
    {
        var feet = new Vector3(0f, 0f, -900f);
        var world = World(
            (new Vector3(-160f, 0f, -110f), new Vector3(160f, 45f, -100f)),
            (new Vector3(-160f, 0f, -110f), new Vector3(-150f, 45f, 150f)),
            (new Vector3(150f, 0f, -110f), new Vector3(160f, 45f, 150f)));
        var (robots, _, _) = Setup(world, ai, feet);
        robots.HoldFire = true;
        var raptor = robots.Add(RaptorType, Vector3.Zero);
        var out1 = false;
        for (var t = 0; t < 60 * 30; t++)
        {
            robots.Tick(Dt, feet);
            out1 |= raptor.Position.Z < -130f;
        }

        Assert.True(raptor.Sees);
        Assert.Equal(gets, out1);
    }

    /// <summary>Wedged so it cannot move at all while it tries to: IMPROVED notices within seconds
    /// and backs off.</summary>
    [BehaviourDataFact]
    public void AStuckWalkerBacksOff()
    {
        // A closed box of knee-high walls round the raptor, too tight to turn round in: no heading is
        // clear, so following finds nothing and only the stuck check gets it out of its pose.
        var feet = new Vector3(0f, 0f, -900f);
        var world = World(
            (new Vector3(-60f, 0f, -45f), new Vector3(60f, 45f, -40f)),
            (new Vector3(-60f, 0f, 40f), new Vector3(60f, 45f, 45f)),
            (new Vector3(-45f, 0f, -60f), new Vector3(-40f, 45f, 60f)),
            (new Vector3(40f, 0f, -60f), new Vector3(45f, 45f, 60f)));
        var (robots, _, _) = Setup(world, RobotAi.Improved, feet);
        robots.HoldFire = true;
        var raptor = robots.Add(RaptorType, Vector3.Zero);
        for (var t = 0; t < 60 * 8; t++)
        {
            robots.Tick(Dt, feet);
        }

        Assert.True(raptor.Unstuck > 0);
        Assert.InRange(raptor.Position.X, -40f, 40f);
        Assert.InRange(raptor.Position.Z, -40f, 40f);
    }

    /// <summary>IMPROVED's search round the soldier draws from the robot's own stream: draws taken
    /// from the session generator meanwhile do not move the search.</summary>
    [BehaviourDataTheory]
    [InlineData(GlobeType)]
    [InlineData(HkType)]
    [InlineData(RaptorType)]
    public void ImprovedDrawsLeaveTheSharedRng(int type)
    {
        Assert.Equal(Searched(type, 0), Searched(type, 1000));
    }

    /// <summary>Where a robot that saw the soldier stands after hunting him out of sight for 20 s,
    /// <paramref name="drawn"/> numbers taken from the session generator as he hid.</summary>
    private static Vector3 Searched(int type, int drawn)
    {
        var seenAt = new Vector3(700f, 0f, -300f);
        var hidden = new Vector3(0f, 0f, -2600f);
        var wall = (new Vector3(-3000f, 0f, -1600f), new Vector3(3000f, 2000f, -1500f));
        var (robots, soldier, _) = Setup(World(wall), RobotAi.Improved, seenAt);
        robots.HoldFire = true;
        var robot = robots.Add(type, Vector3.Zero, Facing(seenAt));
        for (var t = 0; t < 60; t++)
        {
            robots.Tick(Dt, seenAt);
        }

        soldier.Feet = hidden;
        for (var i = 0; i < drawn; i++)
        {
            robots.Rng.NextUInt();
        }

        for (var t = 0; t < 60 * 20; t++)
        {
            robots.Tick(Dt, hidden);
        }

        return robot.Position;
    }

    /// <summary>A save taken while the raptor hunts the hidden soldier: loaded, it still goes where it
    /// saw him; an older save (no memory) forgets him.</summary>
    [BehaviourDataTheory]
    [InlineData(MemoryKept.Saved, true)]
    [InlineData(MemoryKept.None, false)]
    public void ImprovedMemorySurvivesASave(MemoryKept kept, bool goes)
    {
        var seenAt = new Vector3(700f, 0f, -300f);
        var hidden = new Vector3(0f, 0f, -2600f);
        var wall = (new Vector3(-3000f, 0f, -1600f), new Vector3(3000f, 2000f, -1500f));
        var (robots, soldier, _) = Setup(World(wall), RobotAi.Improved, seenAt);
        robots.HoldFire = true;
        robots.Add(RaptorType, Vector3.Zero, Facing(seenAt));
        for (var t = 0; t < 60; t++)
        {
            robots.Tick(Dt, seenAt);
        }

        soldier.Feet = hidden;
        for (var t = 0; t < 60; t++)
        {
            robots.Tick(Dt, hidden);
        }

        var saved = robots.Snapshot();
        Assert.NotNull(saved[0].Memory?.LastSeen);
        if (kept == MemoryKept.None)
        {
            saved = [saved[0] with { Memory = null }];
        }

        var (loaded, _, _) = Setup(World(wall), RobotAi.Improved, hidden);
        loaded.HoldFire = true;
        var raptor = loaded.Add(RaptorType, Vector3.Zero, Facing(seenAt));
        Assert.True(loaded.Restore(saved));
        var closest = float.MaxValue;
        for (var t = 0; t < 60 * 20; t++)
        {
            loaded.Tick(Dt, hidden);
            closest = MathF.Min(closest, Vector3.Distance(raptor.Position with { Y = 0f }, seenAt));
        }

        Assert.Equal(goes, closest < 100f);
    }

    /// <summary>What a test save keeps of IMPROVED's memory.</summary>
    public enum MemoryKept
    {
        Saved,
        None,
    }

    /// <summary>Two raptors placed on one spot step apart.</summary>
    [BehaviourDataFact]
    public void WalkersKeepApart()
    {
        var feet = new Vector3(0f, 0f, -1500f);
        var (robots, _, _) = Setup(World(), RobotAi.Improved, feet);
        robots.HoldFire = true;
        var a = robots.Add(RaptorType, Vector3.Zero);
        var b = robots.Add(RaptorType, new Vector3(5f, 0f, 0f));
        for (var t = 0; t < 60 * 3; t++)
        {
            robots.Tick(Dt, feet);
        }

        Assert.True(Vector3.Distance(a.Position with { Y = 0f }, b.Position with { Y = 0f }) > 40f);
    }

    // ---------------------------------------------------------------- not idle

    /// <summary>The soldier seen, then gone behind a tall wall: IMPROVED goes to where he was seen;
    /// CLASSIC wanders off at random.</summary>
    [BehaviourDataTheory]
    [InlineData(RobotAi.Classic, false)]
    [InlineData(RobotAi.Improved, true)]
    public void AnActiveRobotGoesWhereItLastSawTheSoldier(RobotAi ai, bool goes)
    {
        var seenAt = new Vector3(700f, 0f, -300f);
        var hidden = new Vector3(0f, 0f, -2600f);
        var wall = (new Vector3(-3000f, 0f, -1600f), new Vector3(3000f, 2000f, -1500f));
        var (robots, soldier, _) = Setup(World(wall), ai, seenAt);
        robots.HoldFire = true;
        var raptor = robots.Add(RaptorType, Vector3.Zero, Facing(seenAt));
        for (var t = 0; t < 60; t++)
        {
            robots.Tick(Dt, seenAt);
        }

        Assert.True(raptor.Sees);
        soldier.Feet = hidden;
        var closest = float.MaxValue;
        for (var t = 0; t < 60 * 20; t++)
        {
            robots.Tick(Dt, hidden);
            closest = MathF.Min(closest, Vector3.Distance(raptor.Position with { Y = 0f }, seenAt));
        }

        Assert.False(raptor.Sees);
        Assert.Equal(goes, closest < 100f);
    }

    /// <summary>The soldier right next to a raptor: DOS walks it in place; IMPROVED steps round him.</summary>
    [BehaviourDataTheory]
    [InlineData(RobotAi.Classic, false)]
    [InlineData(RobotAi.Improved, true)]
    public void AWalkerNextToTheSoldierSideSteps(RobotAi ai, bool moves)
    {
        var feet = new Vector3(0f, 0f, -90f);
        var (robots, _, _) = Setup(World(), ai, feet);
        robots.HoldFire = true;
        var raptor = robots.Add(RaptorType, Vector3.Zero);
        var path = 0f;
        var last = raptor.Position;
        for (var t = 0; t < 60 * 6; t++)
        {
            robots.Tick(Dt, feet);
            path += Vector3.Distance(raptor.Position with { Y = 0f }, last with { Y = 0f });
            last = raptor.Position;
        }

        Assert.Equal(moves, path > 100f);
    }

    // ---------------------------------------------------------------- perception and alert

    /// <summary>A tall wall across the soldier's hiding place: robots on the near side never see him.</summary>
    private static readonly (Vector3 Min, Vector3 Max) HideWall = (new Vector3(-3000f, 0f, -1600f), new Vector3(3000f, 2000f, -1500f));
    private static readonly Vector3 Hidden = new(0f, 0f, -2600f);

    private static void Run(Robots robots, Vector3 feet, float seconds, Action? each = null)
    {
        for (var t = 0; t < seconds / Dt; t++)
        {
            robots.Tick(Dt, feet);
            each?.Invoke();
        }
    }

    /// <summary>The raptor faces −Z: the soldier in front is seen, behind it (outside its cone) not.</summary>
    [BehaviourDataTheory]
    [InlineData(-500f, true)]
    [InlineData(500f, false)]
    public void TheConeBlocksASightingBehind(float z, bool sees)
    {
        var feet = new Vector3(0f, 0f, z);
        var (robots, _, _) = Setup(World(), RobotAi.Improved, feet);
        robots.HoldFire = true;
        var raptor = robots.Add(RaptorType, Vector3.Zero);
        var seen = false;
        Run(robots, feet, 2f, () => seen |= raptor.Sees);

        Assert.Equal(sees, seen);
        Assert.Equal(sees, raptor.Alertness == RobotAlert.Combat);
    }

    /// <summary>A shot heard behind it: the raptor turns, goes there and searches; the soldier stays unseen.</summary>
    [BehaviourDataFact]
    public void AHeardShotIsInvestigated()
    {
        var (robots, _, _) = Setup(World(HideWall), RobotAi.Improved, Hidden);
        robots.HoldFire = true;
        var raptor = robots.Add(RaptorType, Vector3.Zero);
        var noise = new Vector3(400f, 0f, 400f);
        Run(robots, Hidden, 0.5f);
        robots.Noise(noise, NoiseKind.Shot);
        var states = new HashSet<RobotAlert>();
        var closest = float.MaxValue;
        Run(robots, Hidden, 15f, () =>
        {
            states.Add(raptor.Alertness);
            closest = MathF.Min(closest, Vector3.Distance(raptor.Position with { Y = 0f }, noise));
        });

        Assert.Contains(RobotAlert.Suspicious, states);
        Assert.Contains(RobotAlert.Investigate, states);
        Assert.DoesNotContain(RobotAlert.Combat, states);
        Assert.True(closest < 100f, $"closest {closest}");
    }

    /// <summary>Saved while suspicious (or searching), loaded: it does not walk off to the world's
    /// origin (the hold spot is not saved).</summary>
    [BehaviourDataTheory]
    [InlineData(RobotAlert.Suspicious)]
    [InlineData(RobotAlert.Search)]
    public void ALoadedAlertHoldsWhereItStands(RobotAlert state)
    {
        var post = new Vector3(400f, 0f, 400f);
        var (robots, _, _) = Setup(World(HideWall), RobotAi.Improved, Hidden);
        robots.HoldFire = true;
        var raptor = robots.Add(RaptorType, post);
        robots.Noise(post + new Vector3(state == RobotAlert.Search ? 30f : 500f, 0f, 0f), NoiseKind.Shot);
        for (var t = 0; t < 60 * 8 && raptor.Alertness != state; t++)
        {
            robots.Tick(Dt, Hidden);
        }

        Assert.Equal(state, raptor.Alertness);
        var saved = robots.Snapshot();
        var (loaded, _, _) = Setup(World(HideWall), RobotAi.Improved, Hidden);
        loaded.HoldFire = true;
        var back = loaded.Add(RaptorType, post);
        Assert.True(loaded.Restore(saved));
        var start = (back.Position with { Y = 0f }).Length();
        var nearest = start;
        Run(loaded, Hidden, 2f, () => nearest = MathF.Min(nearest, (back.Position with { Y = 0f }).Length()));

        Assert.True(start - nearest < 30f, $"walked {start - nearest} toward the origin");
    }

    /// <summary>A hit raptor fights; its neighbour witnesses it and investigates at once, with no
    /// suspicious hold (no shot heard: it goes for the hit one, it does not know where the soldier
    /// is); one beyond the witness's and the alarm's reach stays idle.</summary>
    [BehaviourDataFact]
    public void AHitAlertsANeighbour()
    {
        var (robots, _, _) = Setup(World(HideWall), RobotAi.Improved, Hidden);
        robots.HoldFire = true;
        var hit = robots.Add(RaptorType, Vector3.Zero);
        var near = robots.Add(RaptorType, new Vector3(400f, 0f, 0f));
        var far = robots.Add(RaptorType, new Vector3(MathF.Max(Enemy.AlarmRadius, Enemy.WitnessSight) + 300f, 0f, 0f));
        Run(robots, Hidden, 0.5f);
        hit.TakeDamage(new Damage(0f, DamageKind.Direct, hit.Centre, true));
        var nearStates = new HashSet<RobotAlert>();
        var farStates = new HashSet<RobotAlert>();
        Run(robots, Hidden, 1f, () =>
        {
            nearStates.Add(near.Alertness);
            farStates.Add(far.Alertness);
        });

        Assert.Equal([RobotAlert.Investigate], nearStates);
        Assert.Equal([RobotAlert.Idle], farStates);
    }

    /// <summary>Nothing found where it heard the noise: it searches, gives up and goes back to its post.</summary>
    [BehaviourDataFact]
    public void TheSearchGivesUpAndReturns()
    {
        var (robots, _, _) = Setup(World(HideWall), RobotAi.Improved, Hidden);
        robots.HoldFire = true;
        var raptor = robots.Add(RaptorType, Vector3.Zero);
        robots.Noise(new Vector3(600f, 0f, 0f), NoiseKind.Blast);
        var order = new List<RobotAlert>();
        Run(robots, Hidden, 60f, () =>
        {
            if (order.Count == 0 || order[^1] != raptor.Alertness)
            {
                order.Add(raptor.Alertness);
            }
        });

        var search = order.IndexOf(RobotAlert.Search);
        Assert.True(search > 0, string.Join(" ", order));
        var back = order.IndexOf(RobotAlert.Return, search);
        Assert.True(back > search, string.Join(" ", order));
        Assert.Equal(RobotAlert.Idle, order[^1]);
        Assert.True(Vector3.Distance(raptor.Position with { Y = 0f }, Vector3.Zero) < 400f, $"at {raptor.Position}");
    }

    /// <summary>Idle, it patrols round its post: it moves, and it stands (a hover holds in place) for
    /// seconds at a time (#57).</summary>
    [BehaviourDataTheory]
    [InlineData(RaptorType)]
    [InlineData(DroneType)]
    public void ThePatrolPauses(int type)
    {
        var (robots, _, _) = Setup(World(HideWall), RobotAi.Improved, Hidden);
        robots.HoldFire = true;
        var raptor = robots.Add(type, type == DroneType ? new Vector3(0f, 300f, 0f) : Vector3.Zero);
        var last = raptor.Position;
        var path = 0f;
        var still = 0f;
        var longest = 0f;
        var farthest = 0f;
        Run(robots, Hidden, 30f, () =>
        {
            var step = Vector3.Distance(raptor.Position with { Y = 0f }, last with { Y = 0f });
            path += step;
            still = step < 0.05f ? still + Dt : 0f;
            longest = MathF.Max(longest, still);
            farthest = MathF.Max(farthest, (raptor.Position with { Y = 0f }).Length());
            last = raptor.Position;
        });

        Assert.Equal(RobotAlert.Idle, raptor.Alertness);
        Assert.True(path > 100f, $"walked {path}");
        Assert.True(longest >= 1.9f, $"longest stand {longest} s");
        Assert.True(farthest < 450f, $"strayed {farthest}");
    }

    /// <summary>Lost sight of him: it searches from where it saw him on, the same wherever he went.</summary>
    [BehaviourDataFact]
    public void NoSearchRoundHisLivePlace()
    {
        var seenAt = new Vector3(700f, 0f, -300f);
        List<Vector3> Searched(Vector3 hidden)
        {
            var (robots, soldier, _) = Setup(World(HideWall), RobotAi.Improved, seenAt);
            robots.HoldFire = true;
            var raptor = robots.Add(RaptorType, Vector3.Zero, Facing(seenAt));
            Run(robots, seenAt, 1f);
            Assert.Equal(RobotAlert.Combat, raptor.Alertness);

            soldier.Feet = hidden;
            var path = new List<Vector3>();
            Run(robots, hidden, 25f, () => path.Add(raptor.Position));
            return path;
        }

        var there = Searched(Hidden);
        var elsewhere = Searched(new Vector3(2500f, 0f, -2600f));
        Assert.True(there.Any(p => Vector3.Distance(p with { Y = 0f }, seenAt) < 100f), "never got where it saw him");
        Assert.Equal(there, elsewhere);
    }

    // ---------------------------------------------------------------- fire

    /// <summary>The heavy turret behind a wall, alerted by hits: DOS fires blind (RobotTests); IMPROVED
    /// holds fire without a line.</summary>
    [BehaviourDataTheory]
    [InlineData(RobotAi.Classic, true)]
    [InlineData(RobotAi.Improved, false)]
    public void NoShotWithoutALine(RobotAi ai, bool fires)
    {
        var wall = (new Vector3(-2000f, -500f, -310f), new Vector3(2000f, 3000f, -300f));
        var feet = new Vector3(0f, 0f, -600f);
        var (robots, _, _) = Setup(World(wall), ai, feet);
        var type = SkyNet.Data.GameTables.Ais.Types.First(t => t.Name == "hvytrrt").Index;
        var turret = robots.Add(type, new Vector3(0f, 0f, 0f));
        for (var i = 0; i < 60 * 8; i++)
        {
            if (i % 4 == 0)
            {
                turret.TakeDamage(new Damage(1f, DamageKind.Direct, turret.Centre, true));
            }

            robots.Tick(Dt, feet);
        }

        Assert.Equal(fires, turret.ShotsFired > 0);
    }

    // ---------------------------------------------------------------- the setting

    [Fact]
    public void ImprovedWakesRobotsAQuarterFurther()
    {
        var player = new Vector3(3 * 1024f + 512f, 0f, -(4 * 1024f + 512f));
        var out3 = player + new Vector3(3100f, 0f, 0f);
        Assert.False(TickWindow.Contains(out3, player));
        Assert.True(TickWindow.ContainsImproved(out3, player));
        Assert.False(TickWindow.ContainsImproved(player + new Vector3(3300f, 0f, 0f), player));
        Assert.Equal(3200f, TickWindow.ImprovedReach);
    }

    [Fact]
    public void TheSettingDefaultsToImprovedAndIsSaved()
    {
        Assert.Equal(RobotAi.Improved, new MenuSettings().RobotAi);
        var path = Path.Combine(Path.GetTempPath(), $"robotai-{Environment.ProcessId}-{Guid.NewGuid():n}.json");
        try
        {
            Assert.Equal(SaveResult.Saved, SettingsStore.Save(new MenuSettings { RobotAi = RobotAi.Classic }, path));
            Assert.Equal(RobotAi.Classic, SettingsStore.Load(path).RobotAi);
        }
        finally
        {
            File.Delete(path);
        }

        Assert.Equal(RobotAi.Improved, RobotAis.Parse("IMPROVED"));
        Assert.Null(RobotAis.Parse("smart"));
    }

    [Fact]
    public void ADemoWithoutTheFieldPlaysClassic()
    {
        var demo = new RetroEng.Loop.DemoData();
        Assert.Equal(RobotAi.Classic, CampaignDemo.RobotAiOf(demo));
        demo.SetField(CampaignDemo.KeyRobotAi, nameof(RobotAi.Improved));
        Assert.Equal(RobotAi.Improved, CampaignDemo.RobotAiOf(demo));
    }

    /// <summary>CLASSIC set by hand runs as the DOS golden; IMPROVED changes the run.</summary>
    [FloatGoldenFact]
    public void ClassicIsDosAndImprovedIsNot()
    {
        Assert.Equal(DosParityTests.RobotGolden, DosParityTests.RobotRun(RobotAi.Classic).Hash);
        Assert.NotEqual(DosParityTests.RobotGolden, DosParityTests.RobotRun(RobotAi.Improved).Hash);
    }
}
