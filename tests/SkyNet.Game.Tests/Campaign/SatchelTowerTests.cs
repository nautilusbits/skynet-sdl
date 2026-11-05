using System.Numerics;
using SkyNet.Formats;
using SkyNet.Game.Campaign;
using SkyNet.Game.Tests.Behaviour;

namespace SkyNet.Game.Tests.Campaign;

/// <summary>
/// Future Shock mission 7 (MAP.070, issue #36): a satchel (S = 15000) on a radar tower RADARTWR
/// (hp 14980) destroys it. DOS's blast on a record (FUN_00124886, v1.01) pads the distance with
/// the model's radius (.3D +0x0C, 150 units here), not the soldier's 40: 100 units off the
/// tower's origin it deals 15000 × (500 − 100 + 150) / 500 = 16512.
/// </summary>
public class SatchelTowerTests
{
    private const int TowerMap = 70, FsMission = 7, Satchel = 15000;
    private const int Tower = 0x1f6fd;

    /// <summary>A fact skipped unless Future Shock's data sits beside SKYNET_DATA's.</summary>
    private sealed class ShockDataFactAttribute : FactAttribute
    {
        public ShockDataFactAttribute()
        {
            if (BehaviourData.Dir is not { } dir || GameDataLocator.OtherGameDir(dir) == null)
            {
                Skip = "no Future Shock data beside SKYNET_DATA's SkyNET folder";
            }
        }
    }

    [ShockDataFact]
    public void OneSatchelFellsARadarTower()
    {
        var s = new GameSession(GameData.Open(GameDataLocator.OtherGameDir(BehaviourData.Dir!)!, BehaviourData.Dir)!);
        s.StartMission(s.Table.ByNumber(FsMission)!.Value);
        Assert.True(s.EnterMap(TowerMap, -1));
        var b = s.Behaviour!;
        Assert.True(b.IsDamageable(Tower));

        // The tower's origin (DOS 14080, -2420, 46203), the satchel 100 units below it on its post.
        s.Weapons.BlastAt(new Vector3(14080, 2420 - 100, -46203), Satchel);

        Assert.False(b.IsDamageable(Tower));
    }
}
