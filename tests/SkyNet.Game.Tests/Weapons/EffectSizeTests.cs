using System.Numerics;
using SkyNet.Formats;
using SkyNet.Game.Behaviour;
using SkyNet.Game.Campaign;
using RetroEng.Physics;
using SkyNet.Game.Menu;
using SkyNet.Game.Tests.Behaviour;
using SkyNet.Game.Weapons;

namespace SkyNet.Game.Tests.Weapons;

/// <summary>
/// Issue #44: the ORIGINAL look draws an effect at DOS's size, a scale in 1/256 of its sprite's
/// texels (FUN_0012420f +0x1a; a map sprite passes 0x100, one texel per unit: 0x136f44): a bullet
/// puff its ammo's +0x10 (FUN_0012311f), a breaking record 4r (type 0, FUN_00124793) or its
/// destruction row's +8 (FUN_001244bd), a wreck's stage 4r at its plain origin (0x120833).
/// ENHANCED keeps the port's sizes.
/// </summary>
public class EffectSizeTests
{
    private const float Dt = 1f / 60f;

    [Fact]
    public void ABulletPuffCarriesItsAmmosDosSize()
    {
        var wall = new[]
        {
            new Triangle(new(300, -1000, -1000), new(300, -1000, 1000), new(300, 1000, 1000), 7),
            new Triangle(new(300, -1000, -1000), new(300, 1000, 1000), new(300, 1000, -1000), 7),
        };
        var weapons = new WeaponSystem(WeaponTable.Shipped, new CollisionWorld().Add(new TriangleGrid(wall)));
        weapons.Tick(new WeaponInput(Fire: true, Throw: false, Run: false, Select: 1), new Shooter(Vector3.Zero, -MathF.PI / 2f, 0f), Dt);

        var puff = Assert.Single(weapons.Effects, e => e.Kind == EffectKind.Impact);
        var ammo = WeaponTable.Shipped.Ammo[WeaponTable.Shipped.Weapons[1].AmmoType];
        Assert.Equal(ammo.EffectSize / 256f, puff.DosScale);
    }

    [Fact]
    public void TheOriginalLookDrawsTheDosScaleEnhancedTheSize()
    {
        var e = new EffectPose(EffectKind.Impact, 365, 0, 120f, 0, Vector3.Zero, DosScale: 0.5f);

        Assert.Equal(0.5f, WeaponRenderer.EffectScale(e, 57, Look.Original));
        Assert.Equal(120f / 57f, WeaponRenderer.EffectScale(e, 57, Look.Enhanced));
        Assert.Equal(120f / 57f, WeaponRenderer.EffectScale(e with { DosScale = 0f }, 57, Look.Original));
    }

    [BehaviourDataFact]
    public void AWrecksStageBlastsAtItsPlainOriginAtFourR()
    {
        var s = new GameSession(BehaviourData.Data);
        s.StartMission(s.Table.ByNumber(1)!.Value);
        foreach (var map in new[] { 210, 214, 220, 230, 240, 250, 260 })
        {
            Assert.True(s.EnterMap(map, 0));
            var b = s.Behaviour!;
            foreach (var car in s.Map!.Entities.Where(e => s.Weapons.Targets.ByOwner(e.FileOffset) != null && b.Hp(e.FileOffset) > 1f))
            {
                s.Weapons.Targets.ByOwner(car.FileOffset)!.TakeDamage(new Damage(1f, DamageKind.Direct, BehaviourModel.Position(car), true));
                if (b.StageOf(car.FileOffset) == 0)
                {
                    continue;
                }

                var fx = s.Weapons.Effects.Last(e => e.Kind == EffectKind.Explosion);
                var r = s.Data.LoadModel(s.Map.EntityName(car) + ".3D")!.Header.AsSpan()[0x0C..];
                Assert.Equal(BehaviourModel.Position(car), fx.Position);
                Assert.Equal(4 * (BitConverter.ToInt32(r) >> 8) / 256f, fx.DosScale);
                return;
            }
        }

        Assert.Fail("no staged car in mission 1");
    }
}
