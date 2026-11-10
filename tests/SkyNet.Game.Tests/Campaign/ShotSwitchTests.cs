using System.Numerics;
using SkyNet.Formats;
using SkyNet.Game.Campaign;
using SkyNet.Game.Player;
using SkyNet.Game.Tests.Behaviour;
using SkyNet.Game.Weapons;

namespace SkyNet.Game.Tests.Campaign;

/// <summary>A shot switch (state bit 0x02) and a blasting shot. DOS's blast FUN_00124886 hits only
/// variant-1 records with hit points left (sub+0xe &gt; 0): the shot's direct hit flips the chain
/// once, the blast does not flip it back (issue #56).</summary>
public class ShotSwitchTests
{
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

    /// <summary>Future Shock MAP.071: FLORPAD1 @0491e (state 0x02, no hit points) lowers the
    /// CATWLK02 @04645 (act 0x5B, 128 units). The shotgun's shell hits it and blasts by it.</summary>
    [ShockDataFact]
    public void ShotgunLowersMap071Platform()
    {
        const int Pad = 0x0491e, Platform = 0x04645;
        var data = GameData.Open(GameDataLocator.OtherGameDir(BehaviourData.Dir!)!, BehaviourData.Dir)!;
        var session = new GameSession(data);
        session.StartAt(71);
        var pad = session.Weapons.Targets.ByOwner(Pad);
        Assert.NotNull(pad);

        pad.TakeDamage(new Damage(50f, DamageKind.Direct, pad.Centre, true));
        pad.TakeDamage(new Damage(50f, DamageKind.Blast, pad.Centre, false));
        for (var t = 0f; t < 3f; t += GameSession.Dt)
        {
            session.Tick(default, new PlayerActions(0, 1, 2, 3, 4, 5, 6), new WeaponInput(false, false, false), false);
        }

        Assert.Equal(128f, session.Behaviour!.MoverProgress(Platform).Progress);
    }
}
