using System.Numerics;
using RetroEng.Physics;
using SkyNet.Formats;
using SkyNet.Game.Behaviour;
using SkyNet.Game.Collision;
using SkyNet.Game.Enemies;
using SkyNet.Game.Tests.Behaviour;
using SkyNet.Game.Weapons;

namespace SkyNet.Game.Tests.Enemies;

/// <summary>The soldier as the robots' target: counts what he takes.</summary>
sealed class Soldier : IDamageable
{
    public Vector3 Feet { get; set; }
    public float Taken { get; private set; }
    public List<Damage> Hits { get; } = [];
    public Vector3 Centre => Feet + new Vector3(0f, 38f, 0f);
    public float Radius => 0f;
    public bool Alive => true;

    public void TakeDamage(in Damage damage)
    {
        Taken += damage.Points;
        Hits.Add(damage);
    }
}

/// <summary>The robots on real data: MAP.210's placements, a walker that turns, fires and hits, a
/// death that raises its act, a turret at rest until alerted.</summary>
public class RobotTests
{
    private const float Dt = 1f / 35f;

    private static (Robots Robots, WeaponSystem Weapons, Soldier Soldier, MapFile Map) Map210(CollisionWorld? world = null)
    {
        var data = BehaviourData.Data;
        var map = data.LoadMap(210)!;
        var w = world ?? LevelCollision.Build(data, map, data.LoadWld(210)).World;
        var soldier = new Soldier();
        var weapons = new WeaponSystem(WeaponTable.Load(data), w) { Player = soldier };
        var robots = new Robots(SkyNet.Data.GameTables.Ais, new EnemyModels(data), w, weapons, seed: 7);
        robots.Populate(map);
        return (robots, weapons, soldier, map);
    }

    /// <summary>A point <paramref name="distance"/> from the robot, sideways first, on a floor near
    /// its feet' level and in its line of sight.</summary>
    private static (Vector3 Feet, Vector3 Dir) InSight(Robots robots, Enemy robot, float distance)
    {
        var feetY = robot.Position.Y + robot.FootOffset;
        for (var i = 0; i < 16; i++)
        {
            var a = i * MathF.Tau / 16f;
            var dir = new Vector3(MathF.Cos(a), 0f, MathF.Sin(a));
            var p = robot.Position + dir * distance;
            if (!robots.World.Raycast(new Vector3(p.X, feetY + 60f, p.Z), -Vector3.UnitY, 300f, out var hit))
            {
                continue;
            }

            var feet = hit.Point;
            if (MathF.Abs(feet.Y - feetY) < 40f && robot.LineOfSight(feet))
            {
                return (feet, dir);
            }
        }

        return (Vector3.Zero, Vector3.Zero);
    }

    /// <summary>The first armed walker (endoskeletons first) with open floor around it.</summary>
    private static (Enemy Robot, Vector3 Feet, Vector3 Dir) WalkerInTheOpen(Robots robots, float distance)
    {
        robots.Tick(Dt, null);                    // settles them on the ground
        foreach (var r in robots.All.Where(r => r.State == 7 && r.Root.Fire != null).OrderBy(r => r.Name != "endorfl"))
        {
            var (feet, dir) = InSight(robots, r, distance);
            if (dir != Vector3.Zero)
            {
                return (r, feet, dir);
            }
        }

        throw new InvalidOperationException("no walker in the open");
    }

    [BehaviourDataFact]
    public void Map210PlacesNineteenRobots()
    {
        var (robots, weapons, _, _) = Map210();
        Assert.Equal(19, robots.All.Count);
        Assert.All(robots.All, r => Assert.NotNull(r.Root.Model));
        Assert.Equal(19, weapons.Targets.Count);
        Assert.Equal(10, robots.All.Count(r => r.State == 7));          // 8 raptors, 2 endoskeletons with rifles
        var truck = Assert.Single(robots.All, r => r.State == 11);
        Assert.True(truck.OnPath);
        // The heavy turret: two plasma guns that pitch on a head that yaws.
        var turret = robots.All.First(r => r.Name == "hvytrrt");
        Assert.Equal(3, turret.Turrets.Count);
        Assert.Equal(2, turret.Turrets.Count(t => t.Fire != null));
    }

    [BehaviourDataFact]
    public void AWalkerInSightTurnsFiresAndHits()
    {
        var (robots, weapons, soldier, _) = Map210();
        // The soldier 300 units off to the side, on the robot's floor, in plain sight.
        var (robot, feet, dir) = WalkerInTheOpen(robots, 300f);
        soldier.Feet = feet;
        Assert.True(MathF.Abs(Vector3.Dot(dir, robot.Forward)) < 0.75f);   // it has to turn

        var wantYaw = MathF.Atan2(-dir.X, -dir.Z);
        var turned = false;
        for (var t = 0f; t < 20f && soldier.Taken <= 0f; t += Dt)
        {
            robots.Tick(Dt, feet);
            weapons.Tick(default, new Shooter(feet + new Vector3(0f, 75f, 0f), 0f, 0f), Dt);
            turned |= MathF.Abs(robot.Yaw - wantYaw) < 0.1f;
        }

        Assert.True(robot.Sees);
        Assert.True(turned, $"yaw {robot.Yaw}");
        Assert.True(robot.ShotsFired > 0);
        Assert.True(soldier.Taken > 0f, $"{robot.ShotsFired} shots, none hit");
        Assert.Contains(soldier.Hits, h => h.Kind == DamageKind.Direct);
    }

    [BehaviourDataFact]
    public void AnAnimatedWalkerChangesFramesAndMovesTowardTheSoldier()
    {
        var (robots, weapons, soldier, _) = Map210();
        robots.HoldFire = true;
        var (robot, feet, _) = WalkerInTheOpen(robots, 900f);
        soldier.Feet = feet;
        var frames = new HashSet<int>();
        var start = robot.Position;
        for (var t = 0f; t < 6f; t += Dt)
        {
            robots.Tick(Dt, feet);
            frames.Add(robot.Root.Frame);
        }

        Assert.True(frames.Count > 3, $"frames {string.Join(",", frames)}");
        Assert.True(Vector3.Distance(robot.Position, feet) < Vector3.Distance(start, feet) - 50f,
            $"from {start} to {robot.Position}");
        Assert.Equal(0, robot.ShotsFired);
        _ = weapons;
    }

    [BehaviourDataFact]
    public void KillingARobotRaisesItsDeathActAndThrowsTheBlast()
    {
        var (robots, weapons, soldier, map) = Map210();
        var log = new EffectLog();
        var behaviour = new LevelBehaviour(map, BehaviourData.Transfrm, new MissionProgress(3), log);
        robots.DeathAct += e => behaviour.RobotAct(e.Act, e.MarkerOffset);
        // No SkyNET marker carries one (Future Shock's MAP.060 does): hand a raptor objective 1, as
        // EnemiesStartMarked would from sub+4 = 0x26, sub+5 = 4.
        var robot = robots.All.First(r => r.Name == "raptor");
        robot.DeathAct = 0x26;
        robot.DeathState = 4;
        var acts = new List<RobotActEvent>();
        robots.DeathAct += acts.Add;
        soldier.Feet = robot.Position + new Vector3(40f, -38f, 0f);   // his centre 40 units off

        robot.TakeDamage(new Damage(robot.Hp - 1f, DamageKind.Direct, robot.Centre, true));
        Assert.False(robot.Dead);
        Assert.Empty(acts);
        robot.TakeDamage(new Damage(5f, DamageKind.Direct, robot.Centre, true));
        Assert.True(robot.Dead);
        var act = Assert.Single(acts);
        Assert.Equal(0x26, act.Act);
        Assert.Equal(robot.MarkerOffset, act.MarkerOffset);
        Assert.Contains("obj0", log.Effects);
        Assert.Equal(2, behaviour.Mission.Left);
        Assert.DoesNotContain(robot, weapons.Targets.All);

        // The death blast strikes 0.375 s later: the soldier 40 units away takes 115 − 40 = 75.
        robots.Tick(0.3f, null);
        Assert.Equal(0f, soldier.Taken);
        robots.Tick(0.1f, null);
        Assert.Equal(75f, soldier.Taken, 0.5f);

        // Dead stays dead: no second act.
        robot.TakeDamage(new Damage(500f, DamageKind.Direct, robot.Centre, true));
        Assert.Single(acts);
    }

    [BehaviourDataFact]
    public void ATurretKeepsItsRestPoseUntilAlerted()
    {
        // The heavy turret alone in an empty world, a wall between it and the soldier.
        var wall = new List<Triangle>();
        var a = new Vector3(-2000f, -500f, -300f);
        var b = new Vector3(2000f, -500f, -300f);
        var c = new Vector3(2000f, 3000f, -300f);
        var d = new Vector3(-2000f, 3000f, -300f);
        wall.Add(new Triangle(a, b, c));
        wall.Add(new Triangle(a, c, d));
        var world = new CollisionWorld().Add(new TriangleGrid(wall, 256f));
        var data = BehaviourData.Data;
        var soldier = new Soldier { Feet = new Vector3(0f, 0f, -600f) };
        var weapons = new WeaponSystem(WeaponTable.Load(data), world) { Player = soldier };
        var robots = new Robots(SkyNet.Data.GameTables.Ais, new EnemyModels(data), world, weapons);
        var type = SkyNet.Data.GameTables.Ais.Types.First(t => t.Name == "hvytrrt").Index;
        var turret = robots.Add(type, Vector3.Zero);
        var head = turret.Turrets.First(t => (t.Type!.Axis ?? 1) == 1);

        float HeadYaw() => head == turret.Root ? turret.Yaw : head.Yaw;
        for (var t = 0f; t < 10f; t += Dt)
        {
            robots.Tick(Dt, soldier.Feet);
        }

        // Rest: turned toward (x, y + 200, z + 300) — world +Z, yaw ±π — and holding there.
        var rest = HeadYaw();
        Assert.False(turret.Sees);
        for (var t = 0f; t < 3f; t += Dt)
        {
            robots.Tick(Dt, soldier.Feet);
            Assert.Equal(rest, HeadYaw(), 3);
        }

        Assert.True(MathF.Abs(MathF.Abs(rest) - MathF.PI) < 0.2f || head != turret.Root, $"rest yaw {rest}");
        Assert.Equal(0, turret.ShotsFired);

        // A hit: the alert bit makes the next look (one tick in four) a sighting, held until the look
        // after it; the head turns toward the soldier (yaw 0)…
        turret.TakeDamage(new Damage(1f, DamageKind.Direct, turret.Centre, true));
        for (var i = 0; i < 4; i++)
        {
            robots.Tick(Dt, soldier.Feet);
        }

        var alerted = HeadYaw();
        Assert.True(MathF.Abs(Wrap(alerted)) < MathF.Abs(Wrap(rest)), $"rest {rest}, alerted {alerted}");
        // …and then swings back to rest.
        for (var t = 0f; t < 10f; t += Dt)
        {
            robots.Tick(Dt, soldier.Feet);
        }

        Assert.Equal(rest, HeadYaw(), 3);
    }

    [BehaviourDataFact]
    public void ATurretFiresWithoutLineOfSight()
    {
        // DOS turret fire (0x137d10 from FUN_00137965) asks range, aim cone and rate but never sight:
        // kept alerted by hits, the heavy turret fires at the soldier behind a wall.
        var wall = new List<Triangle>();
        var a = new Vector3(-2000f, -500f, -300f);
        var b = new Vector3(2000f, -500f, -300f);
        var c = new Vector3(2000f, 3000f, -300f);
        var d = new Vector3(-2000f, 3000f, -300f);
        wall.Add(new Triangle(a, b, c));
        wall.Add(new Triangle(a, c, d));
        var world = new CollisionWorld().Add(new TriangleGrid(wall, 256f));
        var data = BehaviourData.Data;
        var soldier = new Soldier { Feet = new Vector3(0f, 0f, -600f) };
        var weapons = new WeaponSystem(WeaponTable.Load(data), world) { Player = soldier };
        var robots = new Robots(SkyNet.Data.GameTables.Ais, new EnemyModels(data), world, weapons);
        var type = SkyNet.Data.GameTables.Ais.Types.First(t => t.Name == "hvytrrt").Index;
        var turret = robots.Add(type, Vector3.Zero);
        Assert.False(turret.LineOfSight(soldier.Feet));
        for (var i = 0; i < 35 * 8 && turret.ShotsFired == 0; i++)
        {
            if (i % 4 == 0)
            {
                turret.TakeDamage(new Damage(1f, DamageKind.Direct, turret.Centre, true));
            }

            robots.Tick(Dt, soldier.Feet);
        }

        Assert.True(turret.ShotsFired > 0);
    }

    private static float Wrap(float a) => MathF.IEEERemainder(a, MathF.Tau);

    /// <summary>The heavy turret alone in an empty world (engage range 2200, beyond the 2000 sight).</summary>
    private static (Robots Robots, Enemy Turret, Func<float> HeadYaw) LoneHeavyTurret(Soldier soldier)
    {
        var world = new CollisionWorld();
        var data = BehaviourData.Data;
        var weapons = new WeaponSystem(WeaponTable.Load(data), world) { Player = soldier };
        var robots = new Robots(SkyNet.Data.GameTables.Ais, new EnemyModels(data), world, weapons);
        var type = SkyNet.Data.GameTables.Ais.Types.First(t => t.Name == "hvytrrt").Index;
        var turret = robots.Add(type, Vector3.Zero);
        var head = turret.Turrets.First(t => (t.Type!.Axis ?? 1) == 1);
        return (robots, turret, () => head == turret.Root ? turret.Yaw : head.Yaw);
    }

    [Fact]
    public void TheSightTestUsesDosDistanceNotTheTrueOne()
    {
        // FUN_0014d794: the largest axis plus a quarter of the other two.
        Assert.Equal(2100f, Enemy.DosDistance(new Vector3(0f, 0f, -2100f)));
        Assert.Equal(1520f + 380f, Enemy.DosDistance(new Vector3(1520f, 0f, 1520f)));
        Assert.Equal(1000f + 50f + 100f, Enemy.DosDistance(new Vector3(-200f, 400f, 1000f)), 3);
    }

    [BehaviourDataFact]
    public void AFarTurretOffTheAxesSeesAndTracksTheSoldier()
    {
        // 2150 flat units out on the diagonal, off to the turret's side (its rest faces +Z): DOS
        // measures 1520 + 1520/4 = 1900 ≤ 2000, and the head is within its 2200 engage range, so it
        // turns and keeps tracking him with no hit at all (a true-distance cap left it at rest). It
        // holds fire: the heavy turret's guns reach 1900.
        var soldier = new Soldier { Feet = new Vector3(1520f, 0f, -1520f) };
        var (robots, turret, headYaw) = LoneHeavyTurret(soldier);
        var want = MathF.Atan2(-soldier.Feet.X, -soldier.Feet.Z);
        for (var t = 0f; t < 4f; t += Dt)
        {
            robots.Tick(Dt, soldier.Feet);
        }

        for (var t = 0f; t < 4f; t += Dt)
        {
            robots.Tick(Dt, soldier.Feet);
            Assert.True(turret.Sees);
            Assert.True(MathF.Abs(Wrap(headYaw() - want)) < 0.1f, $"head {headYaw()}, soldier {want}");
        }

        Assert.Equal(0, turret.ShotsFired);
    }

    [BehaviourDataFact]
    public void AHitBeyondSightBuysOneLookAndAHitBeyondTheEngageRangeWaits()
    {
        // 2100 straight out to the side: past the 2000 sight, inside the 2200 engage range.
        var soldier = new Soldier { Feet = new Vector3(2100f, 0f, 0f) };
        var (robots, turret, headYaw) = LoneHeavyTurret(soldier);
        for (var t = 0f; t < 6f; t += Dt)
        {
            robots.Tick(Dt, soldier.Feet);
        }

        var rest = headYaw();
        Assert.False(turret.Perceived);
        turret.TakeDamage(new Damage(1f, DamageKind.Direct, turret.Centre, true));
        var seenTicks = 0;
        var turned = false;
        for (var i = 0; i < 12; i++)
        {
            robots.Tick(Dt, soldier.Feet);
            seenTicks += turret.Sees ? 1 : 0;
            turned |= turret.Sees && MathF.Abs(Wrap(headYaw() - rest)) > 0.05f;
        }

        Assert.Equal(4, seenTicks);                    // one look, held until the next
        Assert.True(turned);
        Assert.False(turret.Alerted);

        // Beyond every head's engage range the turret does not look, so the alert waits for him.
        soldier.Feet = new Vector3(3000f, 0f, 0f);
        turret.TakeDamage(new Damage(1f, DamageKind.Direct, turret.Centre, true));
        for (var i = 0; i < 12; i++)
        {
            robots.Tick(Dt, soldier.Feet);
            Assert.False(turret.Sees);
        }

        Assert.True(turret.Alerted);
        soldier.Feet = new Vector3(2100f, 0f, 0f);
        var seen = false;
        for (var i = 0; i < 4; i++)
        {
            robots.Tick(Dt, soldier.Feet);
            seen |= turret.Sees;
        }

        Assert.True(seen);
        Assert.False(turret.Alerted);
    }

    [Fact]
    public void TheTickWindowIsTheFiveByFiveCellsRoundThePlayer()
    {
        // DOS cell = coordinate >> 10; port Z is DOS −Z. The player in cell (3, 4).
        var player = new Vector3(3 * 1024f + 10f, 0f, -(4 * 1024f + 1000f));
        Assert.True(TickWindow.Contains(new Vector3(1 * 1024f, 500f, -(2 * 1024f)), player));
        Assert.True(TickWindow.Contains(new Vector3(6 * 1024f - 1f, 0f, -(7 * 1024f - 1f)), player));
        Assert.False(TickWindow.Contains(new Vector3(6 * 1024f, 0f, -(4 * 1024f)), player));
        Assert.False(TickWindow.Contains(new Vector3(3 * 1024f, 0f, -(2 * 1024f - 1f)), player));
        Assert.False(TickWindow.Contains(new Vector3(3 * 1024f, 0f, -(7 * 1024f)), player));
    }

    [BehaviourDataFact]
    public void ARobotOutsideTheWindowStandsFrozenAndWakesWhenTheSoldierComes()
    {
        var (robots, weapons, soldier, _) = Map210();
        var (robot, feet, _) = WalkerInTheOpen(robots, 300f);
        // The soldier three cells off on X: out of the 5×5 window. Even a hit (the alert bit) does
        // not run it: DOS leaves an actor outside the window untouched.
        var far = new Vector3((TickWindow.CellX(robot.Position) + 3) * TickWindow.Cell + 512f, feet.Y, feet.Z);
        Assert.False(TickWindow.Contains(robot.Position, far));
        soldier.Feet = far;
        robot.TakeDamage(new Damage(1f, DamageKind.Direct, robot.Centre, true));
        var (pos, yaw, frame) = (robot.Position, robot.Yaw, robot.Root.Frame);
        for (var t = 0f; t < 5f; t += Dt)
        {
            robots.Tick(Dt, far);
            weapons.Tick(default, new Shooter(far + new Vector3(0f, 75f, 0f), 0f, 0f), Dt);
        }

        Assert.Equal(pos, robot.Position);
        Assert.Equal(yaw, robot.Yaw);
        Assert.Equal(frame, robot.Root.Frame);
        Assert.Equal(0, robot.ShotsFired);
        Assert.Equal(0f, soldier.Taken);

        // In sight and in the window: it runs again from where it stood, turns and fires.
        soldier.Feet = feet;
        for (var t = 0f; t < 20f && robot.ShotsFired == 0; t += Dt)
        {
            robots.Tick(Dt, feet);
            weapons.Tick(default, new Shooter(feet + new Vector3(0f, 75f, 0f), 0f, 0f), Dt);
        }

        Assert.True(robot.Sees);
        Assert.True(robot.ShotsFired > 0);
        Assert.True(robots.Ticked > 0);
    }

    [BehaviourDataFact]
    public void ASpawnSpriteRobotHidesUntilItsChainFires()
    {
        var (robots, weapons, soldier, _) = Map210();
        var type = robots.Ais.Types.First(t => t.Name == "raptor").Index;
        var r = robots.Add(type, new Vector3(50000f, 900f, -53000f), spawnSource: 0x12345);
        r.HideUntilSpawned();
        Assert.False(r.Alive);
        robots.Tick(Dt, soldier.Feet);
        Assert.False(robots.Spawn(0x11111));
        Assert.True(robots.Spawn(0x12345));
        Assert.True(r.Alive);
        Assert.Contains(r, weapons.Targets.All);
    }

    [Fact]
    public void DifficultySetsTheWeaponsMultipliers()
    {
        var world = new CollisionWorld();
        var weapons = new WeaponSystem(WeaponTable.Shipped, world);
        Assert.Equal(Difficulty.Medium.DamageToEnemy, weapons.DamageToEnemies);
        Assert.Equal(WeaponSystem.DamageToEnemiesByDifficulty, new[] { Difficulty.Low.DamageToEnemy, Difficulty.Medium.DamageToEnemy, Difficulty.High.DamageToEnemy });
        Assert.Equal(0.25f, Difficulty.Of(DifficultyLevel.Low).EnemyFire);
        Assert.Equal(0.75f, Difficulty.Of(DifficultyLevel.Low).DamageToPlayer);
        Assert.Equal(1f, Difficulty.Of(DifficultyLevel.High).EnemyFire);
    }

    [Fact]
    public void DeathBlastDealsOneHundredFifteenLessTheDistance()
    {
        Assert.Equal(75, Enemy.DeathBlastDamage(40f));
        Assert.Equal(0, Enemy.DeathBlastDamage(80f));
        Assert.True(Enemy.DeathBlastDamage(0f) >= 114);
    }
}
