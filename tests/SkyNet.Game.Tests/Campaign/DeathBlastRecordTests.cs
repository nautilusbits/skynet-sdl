using SkyNet.Game.Campaign;
using SkyNet.Game.Enemies;
using SkyNet.Game.Tests.Behaviour;

namespace SkyNet.Game.Tests.Campaign;

/// <summary>
/// A robot's death blast (EnemyKill's 75) goes through the same FUN_00124886 (v1.01) as a weapon's:
/// it strikes the map's breakable records too, padded by the model radius, so a robot dying beside
/// petrol pumps starts their chain.
/// </summary>
public class DeathBlastRecordTests
{
    [BehaviourDataFact]
    public void DeathBlastStrikesAMapRecord()
    {
        var s = new GameSession(BehaviourData.Data);
        s.StartMission(s.Table.ByNumber(1)!.Value);
        Assert.True(s.EnterMap(210, 0));
        var b = s.Behaviour!;

        // A breakable record on the map: the blast lands on its origin (d = 0: 75 + pad).
        var record = s.Weapons.Targets.All.First(t => s.Weapons.Targets.OwnerOf(t) >= 0 && t.Alive);
        var off = s.Weapons.Targets.OwnerOf(record);
        var hp = b.Hp(off);
        var dealt = Enemy.DeathBlastDamage(0f, record.BlastPad);

        s.Robots!.DeathBlastAt(record.BlastOrigin);

        Assert.True(dealt > 0);
        Assert.True(!b.IsDamageable(off) || b.Hp(off) <= hp - dealt + 0.5f, $"hp {hp} -> {b.Hp(off)}, dealt {dealt}");
    }
}
