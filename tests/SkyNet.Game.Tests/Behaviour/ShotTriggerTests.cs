using System.Numerics;
using SkyNet.Formats;
using SkyNet.Game.Behaviour;

namespace SkyNet.Game.Tests.Behaviour;

/// <summary>
/// Records a shot sets off. ObjHit (v1.01 0x139819) walks the chain on every hit of a record with
/// state bit 1 (value 2), hit points or not, and destroys only a variant-1 record with hit points
/// (hp &lt; 1: it returns before the drain). Act 0x5F (handler 0x1385a0) drops a record in free
/// fall straight down the level's Y: the accumulator sub+0x10 runs to p6 &lt;&lt; 4 and the record
/// moves a sixteenth of it, p6 = 128 units.
/// </summary>
public class ShotTriggerTests
{
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

    private static LevelBehaviour Shock(int map, EffectLog log)
    {
        var data = GameData.Open(GameDataLocator.OtherGameDir(BehaviourData.Dir!)!, BehaviourData.Dir)!;
        var b = new LevelBehaviour(data.LoadMap(map)!, BehaviourModel.LoadTransfrm(data), new MissionProgress(7), log);
        b.Start(Harness.Away, Harness.Away);
        return b;
    }

    private static void Settle(LevelBehaviour b)
    {
        for (var t = 0f; t < 10f; t += Harness.Dt)
        {
            b.Tick(Harness.Dt, new PlayerProbe(Harness.Away, Harness.Away));
        }
    }

    /// <summary>SkyNET MAP.281's BUTTON07 @02e10 (hp 0, state 2, 0xEF): a weapon target.</summary>
    [BehaviourDataFact]
    public void SkyNetShotButtonIsAWeaponTarget()
    {
        var h = new Harness(281);
        Assert.True(h.B.IsDamageable(0x02e10));
        Assert.NotEmpty(h.Shoot(0x02e10, 10f));
        Assert.True(h.B.IsDamageable(0x02e10));
    }

    /// <summary>Future Shock MAP.141's wall target FLORPAD1 @03db4 (hp 0, state 2, 0xEF → the
    /// ELEV1A lift @03d06): a weapon target that is never destroyed.</summary>
    [ShockDataFact]
    public void ShockWallTargetIsAWeaponTarget()
    {
        var log = new EffectLog();
        var b = Shock(141, log);
        Assert.True(b.IsDamageable(0x03db4));

        b.Hit(0x03db4, 1000f);
        Settle(b);
        Assert.Contains("move@03d06:slideY-128", log.Drain());
        Assert.True(b.TryGetPose(0x03db4, out var pad) && pad.Visible);
    }

    /// <summary>Future Shock MAP.141: the rod FENCEP @072d5 (hp 152, state 4) gives way and drops
    /// the block OUTP129J @0b0b0 (0x5F, hp 0) 128 units to the floor; the block stays.</summary>
    [ShockDataFact]
    public void ShockRodDropsTheBlockToTheFloor()
    {
        var log = new EffectLog();
        var b = Shock(141, log);
        Assert.False(b.IsDamageable(0x0b0b0));

        b.Hit(0x072d5, 1000f);
        Settle(b);
        Assert.Contains("destroyed@072d5", log.Drain());
        Assert.True(b.TryGetPose(0x0b0b0, out var block) && block.Visible && block.Solid);
        Assert.Equal(new Vector3(0f, -128f, 0f), block.Delta.Translation);
    }
}
