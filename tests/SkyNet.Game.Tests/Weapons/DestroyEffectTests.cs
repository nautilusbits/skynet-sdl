using System.Numerics;
using SkyNet.Formats;
using SkyNet.Game.Campaign;
using SkyNet.Game.Tests.Behaviour;
using SkyNet.Game.Weapons;

namespace SkyNet.Game.Tests.Weapons;

/// <summary>
/// Issue #44: a crate broken with the pipe showed its explosion 60 over its middle. DOS
/// (FUN_00124793 → FUN_001244bd, v1.01) puts a listed type's first effect at the record's origin
/// pulled half the model's radius toward the viewer (−forward · r/2), and never skips an effect
/// near the eye: FUN_0012420f pushes it out to 12 units.
/// </summary>
public class DestroyEffectTests
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

    // Future Shock MAP.013: a stacked CRATC03 (radius 53) at (1862, 54, −2144).
    private const int Map = 13, Crate = 0x32F8;
    private static readonly Vector3 CrateOrigin = new(1862f, 54f, -2144f);
    private const float CrateRadius = 53f;

    private static GameSession Session()
    {
        var data = GameData.Open(GameDataLocator.OtherGameDir(BehaviourData.Dir!)!, BehaviourData.Dir)!;
        var s = new GameSession(data);
        s.StartMission(s.Table.ByNumber(1)!.Value);
        Assert.True(s.EnterMap(Map, 0));
        return s;
    }

    /// <summary>Stands <paramref name="away"/> east of the crate looking west at it, and breaks it.</summary>
    private static Effect Break(GameSession s, float away)
    {
        s.Player.Spawn(CrateOrigin + new Vector3(away, -54f, 0f), MathF.PI / 2f);
        s.Player.Pitch = 0f;
        s.Tick(default, default, default, false);
        s.Weapons.Targets.ByOwner(Crate)!.TakeDamage(new Damage(50f, DamageKind.Melee, CrateOrigin, true));
        return Assert.Single(s.Weapons.Effects, e => e.Kind == EffectKind.Explosion);
    }

    [ShockDataFact]
    public void ACrateBlowsUpOnItsNearSide()
    {
        var s = Session();
        var fx = Break(s, 160f);

        var forward = new Vector3(-1f, 0f, 0f);
        Assert.True(Vector3.Distance(CrateOrigin - forward * (CrateRadius / 2f), fx.Position) < 1f, $"at {fx.Position}");
    }

    [ShockDataFact]
    public void ACratesBlastHasItsRowsDosSize()
    {
        // A listed type's effects take the destruction row's +8 (FUN_001244bd), in 1/256 texels.
        var s = Session();
        var type = s.Map!.TryGetEntityAt(Crate, out var crate) ? crate.Link!.Value.DestroyType : -1;
        var fx = Break(s, 160f);

        Assert.Equal(SkyNet.Data.GameTables.Destruction[type].Spread / 256f, fx.DosScale);
    }

    [ShockDataFact]
    public void APipeBlowStillShowsTheBlast()
    {
        // The pipe's blow from 20 off the crate's face: the effect lands about 30 from the eye.
        var s = Session();
        var fx = Break(s, 63f);

        Assert.True(Vector3.Distance(s.Player.EyePosition, fx.Position) >= WeaponSystem.NearMin - 0.01f);
    }
}
