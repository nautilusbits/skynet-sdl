using System.Numerics;
using SkyNet.Game.Collision;
using SkyNet.Game.Enemies;
using SkyNet.Game.Tests.Behaviour;
using SkyNet.Game.Weapons;

namespace SkyNet.Game.Tests.Enemies;

/// <summary>
/// Issue #35. DOS's blast (FUN_00124886, v1.01) strikes every robot with hit points, the one the
/// shot struck included, its distance taken to the robot's origin and padded by its model's radius
/// (.3D +0x0C; RAPTOR 57.5). A shotgun shell (S 50) at MED deals 62.5 direct plus 107.5 − d of
/// blast: a raptor (200 hp) falls in 2-3 shells, not 4.
/// </summary>
public class RobotBlastTests
{
    private const float Dt = 1f / 35f;
    private const int Shotgun = 4;

    [BehaviourDataFact]
    public void ShotgunFellsARaptorInThreeShells()
    {
        var data = BehaviourData.Data;
        var map = data.LoadMap(210)!;
        var world = LevelCollision.Build(data, map, data.LoadWld(210)).World;
        var soldier = new Soldier();
        var weapons = new WeaponSystem(WeaponTable.Load(data), world) { Player = soldier };
        var robots = new Robots(SkyNet.Data.GameTables.Ais, new EnemyModels(data), world, weapons, seed: 7);
        robots.Populate(map);
        var raptor = robots.All.First(r => r.Name == "raptor");

        // 150 units off, level with its centre, aimed at it.
        var eye = raptor.Centre + new Vector3(150f, 0f, 0f);
        var to = raptor.Centre - eye;
        var shooter = new Shooter(eye, MathF.Atan2(-to.X, -to.Z), 0f);
        soldier.Feet = eye + new Vector3(0f, -38f, 0f);
        weapons.Inventory.Grant(Shotgun);
        weapons.Inventory.AddAmmo(weapons.Inventory.Table.Weapons[Shotgun].Pool, 50);
        weapons.Tick(new WeaponInput(false, false, false, Select: Shotgun), shooter, Dt);
        var shells = 0;
        weapons.Fired += (_, thrown) => shells += thrown ? 0 : 1;
        for (var t = 0f; t < 10f && !raptor.Dead; t += Dt)
        {
            weapons.Tick(new WeaponInput(Fire: true, Throw: false, Run: false), shooter, Dt);
        }

        Assert.True(raptor.Dead);
        Assert.InRange(shells, 2, 3);
        Assert.Equal(57.5f, ((IDamageable)raptor).BlastPad, 0.1f);
    }

    /// <summary>A robot's death blast (75) is queued on the effect clock (0x12433a) and struck by the
    /// same FUN_00124886: 80 units from a raptor's origin it deals (75 + 57.5 − 80)·256/75·75 &gt;&gt; 8
    /// = 52, where the soldier's +40 would leave it below S/2 (0).</summary>
    [BehaviourDataFact]
    public void DeathBlastPadsARaptorByItsRadius()
    {
        var data = BehaviourData.Data;
        var map = data.LoadMap(210)!;
        var world = LevelCollision.Build(data, map, data.LoadWld(210)).World;
        var weapons = new WeaponSystem(WeaponTable.Load(data), world) { Player = new Soldier() };
        var robots = new Robots(SkyNet.Data.GameTables.Ais, new EnemyModels(data), world, weapons, seed: 7);
        robots.Populate(map);
        var raptor = robots.All.First(r => r.Name == "raptor");
        var hp = raptor.Hp;

        robots.DeathBlastAt(raptor.Position + new Vector3(0f, 80f, 0f));

        Assert.Equal(hp - 52f, raptor.Hp, 0.5f);
    }
}
