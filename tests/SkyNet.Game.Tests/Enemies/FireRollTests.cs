using System.Numerics;
using SkyNet.Game.Collision;
using SkyNet.Game.Enemies;
using SkyNet.Game.Tests.Behaviour;
using SkyNet.Game.Weapons;
using RetroEng.Physics;

namespace SkyNet.Game.Tests.Enemies;

/// <summary>
/// DOS robot fire (v1.01 0x137d10): within range of the actor's origin and in the aim cosine
/// (or within 140 and 45°), a roll each DOS frame of rand &amp; 0x3ff against rate × the difficulty's
/// fire factor (0x13c17a). The bolt flies at |speed| u/s (0x13c055 → 0x1388fe, moved by dt at
/// 0x123161), straight at the target; a negative speed swaps the muzzle's X each shot (0x137d66).
/// </summary>
public class FireRollTests
{
    private const float Dt = 1f / 60f, DosFps = 35f, Seconds = 20f;

    private static (Robots Robots, WeaponSystem Weapons, Soldier Soldier) Open(Vector3 feet)
    {
        var world = new CollisionWorld();
        var data = BehaviourData.Data;
        var soldier = new Soldier { Feet = feet };
        var weapons = new WeaponSystem(WeaponTable.Load(data), world) { Player = soldier };
        var robots = new Robots(SkyNet.Data.GameTables.Ais, new EnemyModels(data), world, weapons) { Difficulty = Difficulty.High };
        return (robots, weapons, soldier);
    }

    [BehaviourDataFact]
    public void ATurretFiresByTheDosRoll()
    {
        var (robots, weapons, soldier) = Open(new Vector3(0f, 0f, -600f));
        var turret = robots.Add(SkyNet.Data.GameTables.Ais.Types.First(t => t.Name == "hvytrrt").Index, Vector3.Zero);
        var bolt = 0f;
        for (var t = 0f; t < Seconds; t += Dt)
        {
            robots.Tick(Dt, soldier.Feet);
            if (weapons.Projectiles.Count > 0 && bolt == 0f)
            {
                bolt = weapons.Projectiles[0].Velocity.Length();
            }

            weapons.Tick(new WeaponInput(false, false, false), new Shooter(soldier.Feet, 0f, 0f), Dt);
        }

        // Each gun: rate of 1024 a frame at 35 frames a second (rate 100: 68 in 20 s).
        var expected = turret.Segments.Sum(p => p.Fire is { } f ? Seconds * DosFps * f.Rate / 1024f : 0f);
        Assert.InRange(turret.ShotsFired, expected * 0.8f, expected * 1.2f);
        Assert.Equal(800f, bolt, 0.5f);
    }

    [BehaviourDataFact]
    public void ANegativeSpeedSwapsTheMuzzleEachShot()
    {
        var (robots, weapons, soldier) = Open(new Vector3(0f, -60f, -300f));
        var scout = robots.Add(SkyNet.Data.GameTables.Ais.Types.First(t => t.Name == "scout").Index, Vector3.Zero);
        var starts = new List<float>();
        for (var t = 0f; t < Seconds && starts.Count < 2; t += Dt)
        {
            var before = weapons.Projectiles.Count;
            robots.Tick(Dt, soldier.Feet);
            if (weapons.Projectiles.Count > before)
            {
                starts.Add(weapons.Projectiles[^1].Position.X - scout.Position.X);
            }
        }

        Assert.Equal(2, starts.Count);
        Assert.True(starts[0] * starts[1] < 0f, $"both shots from one side: {starts[0]:0}, {starts[1]:0}");
    }
}
