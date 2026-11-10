using System.Numerics;
using RetroEng.Physics;
using SkyNet.Game.Enemies;
using SkyNet.Game.Tests.Behaviour;
using SkyNet.Game.Weapons;

namespace SkyNet.Game.Tests.Enemies;

/// <summary>Issue #58: every segment is its own map record with its own hit points (v1.01
/// 0x129a9d); a shot strikes the segment whose triangles it meets (0x123bc0 → 0x144600), ObjHit
/// (0x139819) drains that one alone, and at zero EnemyKill (0x129f30) takes it and its children
/// off a robot that fights on. A blast (0x124886) strikes each segment by its own distance.</summary>
public class SegmentDamageTests
{
    private const int HeavyTurret = 22;     // hvytrrt 200 → two hvyplsm 125
    private const int GunTower = 13;        // guntwr1 400 → hvytrrt2 200 → hmslpodr 125
    private const int Drone = 2;            // drone 50, one segment
    private static readonly Vector3 Air = new(0f, 500f, 0f);

    private static (Robots Robots, WeaponSystem Weapons) Empty()
    {
        var world = new CollisionWorld();
        var weapons = new WeaponSystem(WeaponTable.Shipped, world);
        var robots = new Robots(SkyNet.Data.GameTables.Ais, new EnemyModels(BehaviourData.Data), world, weapons, seed: 7) { HoldFire = true };
        return (robots, weapons);
    }

    /// <summary>A ray from far out, sideways from the root, at the segment's own origin.</summary>
    private static (Vector3 From, Vector3 Dir) At(Enemy robot, EnemyPart part)
    {
        var to = part.World.Translation;
        var out_ = to - robot.Root.World.Translation;
        out_.Y = 0f;
        var dir = -Vector3.Normalize(out_);
        return (to - dir * 600f, dir);
    }

    [BehaviourDataFact]
    public void AShotOnAGunHurtsTheGunAlone()
    {
        var (robots, weapons) = Empty();
        var robot = robots.Add(HeavyTurret, Air);
        var gun = robot.Root.Children[0];
        var (from, dir) = At(robot, gun);

        var hit = weapons.Targets.RaySpheres(from, dir, 2000f, out _);
        Assert.Same(gun, hit);
        hit!.TakeDamage(new Damage(50f, DamageKind.Direct, from, true));
        Assert.Equal((125f - 50f, 200f, 125f), (gun.Hp, robot.Root.Hp, robot.Root.Children[1].Hp));
        Assert.Equal(200f, robot.Hp);
    }

    [BehaviourDataFact]
    public void AGunShotOffLeavesALivingBase()
    {
        var (robots, weapons) = Empty();
        var robot = robots.Add(HeavyTurret, Air);
        var gun = robot.Root.Children[0];

        gun.TakeDamage(new Damage(125f, DamageKind.Direct, gun.World.Translation, true));
        Assert.True(gun.Gone);
        Assert.False(gun.Alive);
        Assert.False(robot.Dead);
        Assert.DoesNotContain(gun, weapons.Targets.All);
        Assert.Contains(robot.Root, weapons.Targets.All);
        Assert.Equal(1, robots.Wreckage.Count);              // its one wreck part (hvyplsm kind 0)

        // A second shot along the same line passes where the gun was.
        var (from, dir) = At(robot, gun);
        Assert.NotSame(gun, weapons.Targets.RaySpheres(from, dir, 2000f, out _));
    }

    [BehaviourDataFact]
    public void AHeadShotOffTakesItsPodAndSparesTheTower()
    {
        var (robots, weapons) = Empty();
        var robot = robots.Add(GunTower, Air);
        var head = robot.Root.Children[0];
        var pod = head.Children[0];

        head.TakeDamage(new Damage(200f, DamageKind.Direct, head.World.Translation, true));
        Assert.Equal((true, true, false), (head.Gone, pod.Gone, robot.Dead));
        Assert.Equal(400f, robot.Hp);
        Assert.Equal(new IDamageable[] { robot.Root }, weapons.Targets.All.ToArray());

        // Only the root kills the robot.
        robot.Root.TakeDamage(new Damage(400f, DamageKind.Direct, robot.Position, true));
        Assert.True(robot.Dead);
        Assert.Empty(weapons.Targets.All);
    }

    [BehaviourDataFact]
    public void ABlastStrikesEachSegmentByItsOwnDistance()
    {
        var (robots, weapons) = Empty();
        var robot = robots.Add(HeavyTurret, Air);
        var parts = robot.Root.Walk().ToArray();
        var at = parts[1].World.Translation + new Vector3(0f, 0f, 30f);
        const float Strength = 80f;                  // none dies: each takes its own share
        var expected = parts.Select(p => p.Hp - Blast.Damage(Strength, Vector3.Distance(p.BlastOrigin, at), p.BlastPad)).ToArray();

        weapons.BlastAt(at, Strength);
        Assert.Equal(expected, parts.Select(p => p.Hp).ToArray());
        Assert.True(parts[1].Hp < parts[2].Hp);              // the near gun took more than the far one
    }

    [BehaviourDataFact]
    public void AHitAlertsTheStruckSegmentAndItsChildrenAlone()
    {
        // 0x12a280 sets bit 0x8000 on the struck actor and recurses into its children only.
        var (robots, _) = Empty();
        var tower = robots.Add(GunTower, Air);
        var head = tower.Root.Children[0];
        var pod = head.Children[0];
        head.TakeDamage(new Damage(1f, DamageKind.Direct, head.Centre, true));
        Assert.Equal((false, true, true), (tower.Alerted, head.Alerted, pod.Alerted));

        var turret = robots.Add(HeavyTurret, Air + new Vector3(3000f, 0f, 0f));
        turret.Root.Children[1].TakeDamage(new Damage(1f, DamageKind.Direct, Air, true));
        Assert.Equal((false, false, true), (turret.Alerted, turret.Root.Children[0].Alerted, turret.Root.Children[1].Alerted));

        // The root's hit alerts it and every segment under it.
        turret.Root.TakeDamage(new Damage(1f, DamageKind.Direct, Air, true));
        Assert.True(turret.Alerted && turret.Root.Children.All(c => c.Alerted));
    }

    [BehaviourDataFact]
    public void EachSegmentLooksOnItsOwnCounter()
    {
        // 0x129a9d deals every actor its own counter (0x12a57e), the root first, then its segments.
        var (robots, _) = Empty();
        var tower = robots.Add(GunTower, Air);
        var parts = tower.Root.Walk().ToArray();
        Assert.Equal([1, 2, 3], parts.Select(p => p == tower.Root ? tower.SenseCount : p.SenseCount).ToArray());
    }

    [BehaviourDataFact]
    public void ASnapshotKeepsEachSegmentsLook()
    {
        var (robots, _) = Empty();
        var robot = robots.Add(HeavyTurret, Air);
        robots.Tick(1f / 35f, null);                          // the counters move on from their deal
        robot.Root.Children[1].TakeDamage(new Damage(1f, DamageKind.Direct, Air, true));
        var saved = robots.Snapshot();

        var (again, _) = Empty();
        var back = again.Add(HeavyTurret, Air);
        Assert.True(again.Restore(saved));
        Assert.Equal(Looks(robot), Looks(back));
        Assert.True(back.Root.Children[1].Alerted);

        // An older snapshot: no look kept, no alert pending.
        Assert.True(again.Restore([saved[0] with { Senses = null }]));
        Assert.False(back.Root.Children[1].Alerted);
    }

    private static (int, bool, bool)[] Looks(Enemy r) => [.. r.Root.Walk().Skip(1).Select(p => (p.SenseCount, p.Perceived, p.Alerted))];

    [BehaviourDataFact]
    public void AShotBesideTheDroneMissesIt()
    {
        // Issue #47: the 45-minimum sphere caught shots well clear of the small drone.
        var (robots, weapons) = Empty();
        var drone = robots.Add(Drone, Air);
        var m = drone.Root.Model!;
        var y = drone.Root.World.Translation.Y + m.Bounds.Max.Y + 8f;
        var start = new Vector3(-600f, y, drone.Position.Z);
        Assert.True(DamageTargets.RaySphere(start, Vector3.UnitX, drone.Centre, drone.Radius, out _));

        Assert.Null(weapons.Targets.RaySpheres(start, Vector3.UnitX, 2000f, out _));
        Assert.Same(drone.Root, weapons.Targets.RaySpheres(start with { Y = drone.Root.World.Translation.Y }, Vector3.UnitX, 2000f, out _));
    }

    [BehaviourDataFact]
    public void AShotStrikesTheDrawnFrameBeyondTheRestPose()
    {
        // A segment whose frame reaches past its frame-0 box (a machine arm stretched out): a shot
        // grazing that reach strikes it.
        var (robots, weapons) = Empty();
        var ais = SkyNet.Data.GameTables.Ais;
        var found = 0;
        for (var type = 0; type < ais.Types.Length; type++)
        {
            var robot = robots.Add(type, Air + new Vector3(type * 2000f, 0f, 0f));
            foreach (var part in robot.Root.Walk())
            {
                if (part.Model is not { } m)
                {
                    continue;
                }

                var centre = (m.Bounds.Min + m.Bounds.Max) * 0.5f;
                var rest = (m.Bounds.Max - m.Bounds.Min).Length() * 0.5f;
                for (var f = 1; f < m.Frames; f++)
                {
                    var tip = m.Tips[f];
                    if (Vector3.Distance(tip, centre) < rest + 20f)
                    {
                        continue;
                    }

                    // Across the arm just inside its tip, square to the origin's line.
                    part.Frame = f;
                    var radial = Vector3.Normalize(tip);
                    var across = Vector3.Normalize(Vector3.Cross(radial, MathF.Abs(radial.Y) < 0.9f ? Vector3.UnitY : Vector3.UnitX));
                    var hits = 0;
                    for (var k = 0; k < 8; k++)
                    {
                        var at = tip * (0.9f + 0.01f * k);
                        var dir = Vector3.TransformNormal(across, part.World);
                        var from = Vector3.Transform(at, part.World) - dir * 300f;
                        hits += Brute(part, m, from, dir) ? 1 : 0;
                        Assert.Equal(Brute(part, m, from, dir), part.Ray(from, dir, 1000f, out _));
                    }

                    found += hits > 0 ? 1 : 0;
                }

                part.Frame = 0;
            }
        }

        Assert.True(found > 0, "no frame reaches past its rest box");
    }

    /// <summary>Every triangle of the drawn frame, no quick reject.</summary>
    private static bool Brute(EnemyPart part, EnemyModelInfo m, Vector3 from, Vector3 dir)
    {
        Matrix4x4.Invert(part.World, out var inverse);
        var o = Vector3.Transform(from, inverse);
        var d = Vector3.TransformNormal(dir, inverse);
        var verts = m.Model.Frames[part.Frame];
        foreach (var face in m.Model.Faces)
        {
            foreach (var (a, b, c) in SkyNet.Formats.Model3D.TriangleFan(face))
            {
                var tri = new Triangle(verts[face.Indices[a]].ToYUp(), verts[face.Indices[b]].ToYUp(), verts[face.Indices[c]].ToYUp());
                if (Intersect.Ray(o, d, in tri, out var t) && t <= 1000f)
                {
                    return true;
                }
            }
        }

        return false;
    }

    [BehaviourDataFact]
    public void ASnapshotKeepsThePartsAndAnOldOneRestoresThemWhole()
    {
        var (robots, _) = Empty();
        var robot = robots.Add(HeavyTurret, Air);
        robot.Root.Children[0].TakeDamage(new Damage(125f, DamageKind.Direct, Air, true));
        robot.Root.Children[1].TakeDamage(new Damage(25f, DamageKind.Direct, Air, true));
        var saved = robots.Snapshot();

        var (again, weapons) = Empty();
        var copy = again.Add(HeavyTurret, Air);
        Assert.True(again.Restore(saved));
        Assert.Equal((true, 100f), (copy.Root.Children[0].Gone, copy.Root.Children[1].Hp));
        Assert.DoesNotContain(copy.Root.Children[0], weapons.Targets.All);

        // A save from before the parts were kept: every part whole.
        Assert.True(again.Restore([saved[0] with { Parts = null, Gone = 0 }]));
        Assert.Equal((false, 125f, 125f), (copy.Root.Children[0].Gone, copy.Root.Children[0].Hp, copy.Root.Children[1].Hp));
        Assert.Contains(copy.Root.Children[0], weapons.Targets.All);
    }
}
