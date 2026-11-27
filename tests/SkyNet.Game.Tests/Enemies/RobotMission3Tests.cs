using System.Numerics;
using RetroEng.Physics;
using SkyNet.Game.Collision;
using SkyNet.Game.Enemies;
using SkyNet.Game.Navigation;
using SkyNet.Game.Player;
using SkyNet.Game.Tests.Behaviour;
using SkyNet.Game.Weapons;
using Xunit.Abstractions;

namespace SkyNet.Game.Tests.Enemies;

/// <summary>
/// The owner's mission 3 playtest of IMPROVED, on the real maps (MAP.230 and its base MAP.233): a
/// pair of robots (the rest retired), the soldier with his own weapons. The logs show tick by tick
/// what the other robot does. Causes found before the fix:
/// <list type="bullet">
/// <item>A hit or a death alerted no one directly. Its neighbour only heard the noises: it held
/// suspicious 1 s, turned slowly, then went for the nearest noise (the impact or the wreck), not
/// for him. Alone within 800 u of a robot entering combat it went for that robot's place. A
/// hitscan shot striking a wall made no noise at all.</item>
/// <item>The search ran 12 s from losing him, walking included (a raptor 300 u off had ~4 s left
/// on arrival), then random spots within 250 u of his last known place: never on along his way.</item>
/// </list>
/// </summary>
public class RobotMission3Tests(ITestOutputHelper output)
{
    private const float Dt = 1f / 60f;
    private const int Uzi = 1, Shotgun = 4;

    private sealed record Rig(Robots Robots, WeaponSystem Weapons, Soldier Soldier, CollisionWorld World);

    /// <summary>The map's robots nearest <paramref name="keep"/> alone, IMPROVED, on its nav mesh.</summary>
    private static Rig Make(int map, params (float X, float Z)[] keep)
    {
        var data = BehaviourData.Data;
        var level = LevelCollision.Load(data, map)!.Value;
        var soldier = new Soldier();
        var weapons = new WeaponSystem(WeaponTable.Load(data), level.World) { Player = soldier };
        var robots = new Robots(SkyNet.Data.GameTables.Ais, new EnemyModels(data), level.World, weapons, seed: 7) { Ai = RobotAi.Improved, HoldFire = true };
        robots.Populate(level.Map);
        robots.Nav = RobotNav.Bake(level.World, robots, [PlayerStart.Find(level.Map, level.Wld).Feet], null);
        var kept = keep.Select(k => robots.All.Where(r => !r.Hidden).MinBy(r => Flat(r.Position, new Vector3(k.X, 0f, k.Z)))!).ToList();
        robots.Retire(robots.All.Where(r => !kept.Contains(r)).Select(Robots.Id).ToList());
        return new Rig(robots, weapons, soldier, level.World);
    }

    private static Vector3? Floor(CollisionWorld w, Vector3 p) =>
        w.Raycast(p + new Vector3(0f, 100f, 0f), -Vector3.UnitY, 400f, out var hit) ? hit.Point : null;

    private static bool Clear(CollisionWorld w, Vector3 a, Vector3 b)
    {
        var d = b - a;
        var len = d.Length();
        return len < 1f || !w.Raycast(a, d / len, len - 1f, out _);
    }

    private static float Flat(Vector3 a, Vector3 b) => Vector2.Distance(new(a.X, a.Z), new(b.X, b.Z));

    /// <summary>How far off the robot's facing <paramref name="p"/> lies (radians).</summary>
    private static float Off(Enemy r, Vector3 p)
    {
        var d = MathF.Abs(MathF.Atan2(-(p.X - r.Position.X), -(p.Z - r.Position.Z)) - r.Yaw) % MathF.Tau;
        return d > MathF.PI ? MathF.Tau - d : d;
    }

    private static Shooter Aim(Soldier soldier, Vector3 at)
    {
        var eye = soldier.Feet + new Vector3(0f, 75f, 0f);
        var to = Vector3.Normalize(at - eye);
        return new Shooter(eye, MathF.Atan2(-to.X, -to.Z), MathF.Asin(to.Y));
    }

    /// <summary>He shoots one of a pair (MAP.230's rifle endoskeletons, MAP.233's raptors) from out
    /// of the other's sight: the other goes for him the tick the shot lands, with no suspicious hold
    /// (before: suspicious 1 s, then the noise's spot).</summary>
    [BehaviourDataTheory]
    [InlineData(230, 13026f, -51262f, 12642f, -51326f, Uzi)]
    [InlineData(233, 10745f, -10762f, 10927f, -10591f, Uzi)]
    [InlineData(233, 10745f, -10762f, 10927f, -10591f, Shotgun)]
    public void AWitnessReactsAtOnce(int map, float ax, float az, float bx, float bz, int weapon)
    {
        var rig = Make(map, (ax, az), (bx, bz));
        var victim = rig.Robots.All.Single(r => r.Alive && Flat(r.Position, new Vector3(ax, 0f, az)) < 30f);
        var witness = rig.Robots.All.Single(r => r.Alive && r != victim);

        // He stands 250-600 u from the victim with a clear line to it, out of the witness's cone or sight.
        Vector3? feet = null;
        for (var r = 250f; r <= 600f && feet == null; r += 50f)
        {
            for (var i = 0; i < 32 && feet == null; i++)
            {
                var a = i * MathF.Tau / 32;
                if (Floor(rig.World, victim.Position + new Vector3(MathF.Sin(a), 0f, MathF.Cos(a)) * r) is { } f
                    && Clear(rig.World, f + new Vector3(0f, 75f, 0f), victim.Centre) && (Off(witness, f) > 1.4f || !witness.LineOfSight(f)))
                {
                    feet = f;
                }
            }
        }

        Assert.NotNull(feet);
        rig.Soldier.Feet = feet.Value;
        rig.Weapons.Inventory.Grant(weapon);
        rig.Weapons.Inventory.AddAmmo(rig.Weapons.Inventory.Table.Weapons[weapon].Pool, 400);
        Vector3 Body() => Vector3.Transform(new Vector3(0f, 0f, 30f), victim.Root.World);
        rig.Weapons.Tick(new WeaponInput(false, false, false, Select: weapon), Aim(rig.Soldier, Body()), Dt);
        output.WriteLine($"MAP.{map}: {victim.Name} at {victim.Position:0}, {witness.Name} {Flat(victim.Position, witness.Position):0} u off; he is at {feet:0}");

        // Idle a second, then he fires until it is struck.
        var t = 0;
        for (; t < 60; t++)
        {
            rig.Robots.Tick(Dt, rig.Soldier.Feet);
            rig.Weapons.Tick(new WeaponInput(false, false, false), Aim(rig.Soldier, Body()), Dt);
        }

        for (; t < 60 * 6 && victim.Hp >= victim.MaxHp; t++)
        {
            rig.Robots.Tick(Dt, rig.Soldier.Feet);
            rig.Weapons.Tick(new WeaponInput(true, false, false), Aim(rig.Soldier, Body()), Dt);
            if (t % 15 == 0)
            {
                output.WriteLine($"{t * Dt,5:0.00} fire: victim {victim.Hp:0}/{victim.MaxHp:0}, witness {witness.Alertness}");
            }
        }

        Assert.True(victim.Hp < victim.MaxHp, "never hit");
        var struck = witness.Alertness;
        var states = new HashSet<RobotAlert>();
        for (var i = 0; i < 60 * 3; i++, t++)
        {
            rig.Robots.Tick(Dt, rig.Soldier.Feet);
            rig.Weapons.Tick(new WeaponInput(false, false, false), Aim(rig.Soldier, Body()), Dt);
            states.Add(witness.Alertness);
            if (i % 15 == 0)
            {
                output.WriteLine($"{t * Dt,5:0.00} witness {witness.Alertness} sees {witness.Sees} at {witness.Position:0}, {Flat(witness.Position, feet.Value):0} u from him");
            }
        }

        Assert.Contains(struck, new[] { RobotAlert.Investigate, RobotAlert.Combat });
        Assert.DoesNotContain(RobotAlert.Suspicious, states);
    }

    /// <summary>MAP.233: a raptor sees him, he walks round a corner and down the ramp out of its
    /// sight, and stands there. It goes where it lost him, on along his way down the ramp, and finds
    /// him (before: 12 s round where it lost him, walking there included, then back to its post).</summary>
    [BehaviourDataFact]
    public void TheSearchFindsHimRoundTheCorner()
    {
        var rig = Make(233, (10927f, -10591f));
        var raptor = rig.Robots.All.Single(r => r.Alive);
        var nav = rig.Robots.Nav!.QueryFor(raptor.NavBody!)!;
        var p1 = new Vector3(11057f, -576f, -10861f);
        var way = new List<Vector3>();
        Assert.Equal(PathResult.Found, nav.FindPath(p1, new Vector3(11427f, -703f, -11014f), way));
        Assert.True(raptor.LineOfSight(p1));

        rig.Soldier.Feet = way[0];
        for (var t = 0; t < 60 && raptor.Alertness != RobotAlert.Combat; t++)
        {
            rig.Robots.Tick(Dt, rig.Soldier.Feet);
        }

        Assert.Equal(RobotAlert.Combat, raptor.Alertness);
        var leg = 1;
        var order = new List<RobotAlert>();
        for (var t = 0; t < 60 * 50; t++)
        {
            // He walks the way at 250 u/s.
            if (leg < way.Count)
            {
                var to = way[leg] - rig.Soldier.Feet;
                var step = 250f * Dt;
                rig.Soldier.Feet = to.Length() <= step ? way[leg++] : rig.Soldier.Feet + Vector3.Normalize(to) * step;
            }

            rig.Robots.Tick(Dt, rig.Soldier.Feet);
            if (order.Count == 0 || order[^1] != raptor.Alertness)
            {
                order.Add(raptor.Alertness);
            }

            if (t % 30 == 0)
            {
                output.WriteLine($"{t * Dt,5:0.00} {raptor.Alertness} at {raptor.Position:0}, {Flat(raptor.Position, rig.Soldier.Feet):0} u from him, path {raptor.NavPath.Count}");
            }
        }

        var lost = order.IndexOf(RobotAlert.Search);
        Assert.True(lost > 0 && order.IndexOf(RobotAlert.Combat, lost) > lost, string.Join(" ", order));
    }
}
