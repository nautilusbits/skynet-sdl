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
    private const int HkType = 4, RaptorType = 32;

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
        var raptor = robots.Add(RaptorType, Vector3.Zero);
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
    [BehaviourDataFact]
    public void ClassicIsDosAndImprovedIsNot()
    {
        Assert.Equal(DosParityTests.RobotGolden, DosParityTests.RobotRun(RobotAi.Classic).Hash);
        Assert.NotEqual(DosParityTests.RobotGolden, DosParityTests.RobotRun(RobotAi.Improved).Hash);
    }
}
