using System.Reflection;
using RetroEng.Render;
using SkyNet.Game.Weapons;

namespace SkyNet.Game.Tests.Weapons;

public class WeaponRendererTests
{
    // Issue #4: a bolt's model (laser, plasma, rocket) carries its own light; drawn Lit it casts
    // the sun's shadow. Lit is the only shading the shadow pass draws. Render-free: the shading is
    // a private constant (a Renderer needs a window).
    [Fact]
    public void A_bolts_model_casts_no_shadow()
    {
        var field = typeof(WeaponRenderer).GetField("BoltShading", BindingFlags.NonPublic | BindingFlags.Static);

        Assert.NotNull(field);
        Assert.NotEqual(Shading.Lit, (Shading)field.GetValue(null)!);
    }
}
