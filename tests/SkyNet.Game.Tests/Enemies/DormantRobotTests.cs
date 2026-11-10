using System.Numerics;
using System.Reflection;
using SkyNet.Game.Collision;
using SkyNet.Game.Enemies;
using SkyNet.Game.Tests.Behaviour;
using SkyNet.Game.Weapons;

namespace SkyNet.Game.Tests.Enemies;

/// <summary>
/// Issues #54, #45, #56. A start marker with sub+2 ≠ 0 builds a dormant robot (0x12a439 → 0x143000:
/// record +0x14 bit 3, actor flag 0x800). Dormant, it is not drawn (0x136dee), not struck by shots
/// or blasts (0x13aa1e) and runs state 12 in place of its own (0x129db9). State 12 (0x14305e) wakes
/// it once the soldier is within sub+2: TEXTURE.367 half its radius toward him, sound 0x26, then
/// it is an ordinary robot of its type. It never explodes.
/// </summary>
public class DormantRobotTests
{
    private const float Dt = 1f / 35f;
    private const int Map = 283, Marker = 0x28fb, WarpBank = 367;

    [BehaviourDataFact]
    public void ADormantRobotWaitsUnseenThenTeleportsIn()
    {
        var data = BehaviourData.Data;
        var map = data.LoadMap(Map)!;
        var world = LevelCollision.Build(data, map, data.LoadWld(Map)).World;
        var weapons = new WeaponSystem(WeaponTable.Load(data), world) { Player = new Soldier() };
        var robots = new Robots(SkyNet.Data.GameTables.Ais, new EnemyModels(data), world, weapons, seed: 7) { HoldFire = true };
        robots.Populate(map);
        var robot = robots.All.Single(r => r.MarkerOffset == Marker);
        Assert.Equal(1000f, robot.DormantDistance);

        // Unseen and untouchable: no shot, no blast.
        Assert.False(robot.Alive);
        var hp = robot.Hp;
        robot.TakeDamage(new Damage(1e6f, DamageKind.Direct, robot.Position, true));
        robots.DeathBlastAt(robot.Position);
        Assert.Equal(hp, robot.Hp);
        Assert.False(robot.Dead);

        // Out of reach: it waits.
        robots.Tick(Dt, robot.Position + new Vector3(1010f, 0f, 0f));
        Assert.False(robot.Alive);
        Assert.False(robot.Dead);

        // Within sub+2: it comes in with the effect, alive.
        robots.Tick(Dt, robot.Position + new Vector3(1000f, 0f, 0f));
        Assert.True(robot.Alive);
        Assert.False(robot.Dead);
        Assert.Equal(0f, robot.DormantDistance);
        Assert.Contains(weapons.Effects, e => e.Bank == WarpBank && e.Kind == EffectKind.Warp);
    }

    /// <summary>The wake always shows: the ENHANCED look with PARTICLES on draws impacts and
    /// explosions as its own dust and fireballs, but the teleport-in has none, so its sprite stays.</summary>
    [Fact]
    public void TheWakeShowsWithParticlesOn()
    {
        var own = typeof(WeaponRenderer).GetMethod("Generated", BindingFlags.NonPublic | BindingFlags.Static)!;
        bool Generated(EffectKind k) => (bool)own.Invoke(null, [k])!;

        Assert.False(Generated(EffectKind.Warp));
        Assert.True(Generated(EffectKind.Impact));
        Assert.True(Generated(EffectKind.Explosion));
    }

    /// <summary>Only the root record carries the dormant bit (0x14300b); the ray and blast walk
    /// (0x13a95d) skips that record and a hidden robot's (0x13aa44, the root actor's 0x2000), not a
    /// dormant robot's segment records: its unseen gun can be shot off.</summary>
    [BehaviourDataFact]
    public void ADormantRobotsSegmentsCanBeStruck()
    {
        var data = BehaviourData.Data;
        var map = data.LoadMap(Map)!;
        var world = LevelCollision.Build(data, map, data.LoadWld(Map)).World;
        var robots = new Robots(SkyNet.Data.GameTables.Ais, new EnemyModels(data), world, seed: 7) { HoldFire = true };
        robots.Populate(map);
        var robot = robots.All.Single(r => r.MarkerOffset == Marker);
        var gun = robot.Segments[1];

        Assert.False(robot.Root.Alive);
        Assert.True(gun.Alive);
        var hp = gun.Hp;
        gun.TakeDamage(new Damage(5f, DamageKind.Direct, gun.Centre, true));
        Assert.Equal(hp - 5f, gun.Hp);
        Assert.False(robot.Dead);
    }
}
