using System.Numerics;
using RetroEng.Loop;
using SkyNet.Game.Collision;
using SkyNet.Game.Enemies;
using SkyNet.Game.Tests.Behaviour;
using SkyNet.Game.Tests.Demos;
using SkyNet.Game.Weapons;

namespace SkyNet.Game.Tests.Enemies;

/// <summary>DOS's robots: the robots and whole demo runs hash as the goldens taken on the build
/// before any robot mod.</summary>
public class DosParityTests
{
    private const float Dt = 1f / 60f;
    private const int Ticks = 2400;

    // Taken on 0b27460 (v0.11.0), before the smart-AI mod.
    internal const uint RobotGolden = 299528374u;
    internal const ulong Demo210Golden = 12408997402076544067ul, Demo220Golden = 12032974729950146403ul; // 0.42.2: god mode covers the jeep hull

    /// <summary>MAP.210's robots against a soldier who circles the first walker, every robot field
    /// the session hash keeps, hashed every tick. <paramref name="prepare"/> fits the robots out once
    /// placed (a nav mesh).</summary>
    internal static (uint Hash, int Shots, float Taken) RobotRun(RobotAi ai = RobotAi.Classic, Action<Robots, RetroEng.Physics.CollisionWorld>? prepare = null)
    {
        var data = BehaviourData.Data;
        var map = data.LoadMap(210)!;
        var world = LevelCollision.Build(data, map, data.LoadWld(210)).World;
        var soldier = new Soldier();
        var weapons = new WeaponSystem(WeaponTable.Load(data), world) { Player = soldier };
        var robots = new Robots(SkyNet.Data.GameTables.Ais, new EnemyModels(data), world, weapons, seed: 7) { Ai = ai };
        robots.Populate(map);
        prepare?.Invoke(robots, world);
        robots.Tick(Dt, null);
        var centre = robots.All.First(r => r.State == 7).Position;
        var h = new StateHash();
        for (var t = 0; t < Ticks; t++)
        {
            var a = t * 0.004f;
            var feet = centre + new Vector3(MathF.Cos(a), 0f, MathF.Sin(a)) * (400f + 200f * MathF.Sin(t * 0.002f));
            feet.Y = robots.All[0].SurfaceAt(feet + new Vector3(0f, 200f, 0f));
            soldier.Feet = feet;
            robots.Tick(Dt, feet);
            weapons.Tick(default, new Shooter(feet + new Vector3(0f, 75f, 0f), 0f, 0f), Dt);
            foreach (var r in robots.All)
            {
                h.Add(r.Position);
                h.Add(r.Yaw);
                h.Add(r.Hp);
                h.Add(r.Dead);
                h.Add(r.Sees);
                h.Add(r.ShotsFired);
                h.Add(r.Root.Frame);
                h.Add(r.Root.Yaw);
                h.Add(r.Root.Pitch);
            }

            h.Add(soldier.Taken);
        }

        return (h.Value32, robots.All.Sum(r => r.ShotsFired), soldier.Taken);
    }

    private static ulong DemoRun(int map)
    {
        var (_, prints) = DemoTests.Record(map, 3000, god: true);
        var h = new StateHash();
        foreach (var p in prints)
        {
            h.Add(p);
        }

        return h.Value;
    }

    [BehaviourDataFact]
    public void TheRobotsTickAsDos()
    {
        var (hash, shots, taken) = RobotRun();
        Assert.True(shots > 0 && taken > 0f, $"{shots} shots, {taken} taken: the run has no fight");
        Assert.Equal(RobotGolden, hash);
    }

    [BehaviourDataTheory]
    [InlineData(210)]
    [InlineData(220)]
    public void ADemoRunHashesAsDos(int map) =>
        Assert.Equal(map == 210 ? Demo210Golden : Demo220Golden, DemoRun(map));
}
