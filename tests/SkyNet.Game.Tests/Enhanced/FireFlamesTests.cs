using SkyNet.Game.Enhanced;
using SkyNet.Game.Menu;

namespace SkyNet.Game.Tests.Enhanced;

public class FireFlamesTests
{
    private const int Flame = 27659;     // 216/011
    private const int CampFire = 27904;  // 218/000 (MAP.218's office fire)
    private const int Tyres = 26627;     // 208/003 burning tyres
    private const int Barrel = 26369;    // 206/001
    private const int Crate = 100;

    [Theory]
    [InlineData(Flame)]
    [InlineData(CampFire)]
    [InlineData(Tyres)]
    public void FlamesOnDropTheDosFireSprite(int sprite)
    {
        Assert.Equal(FireSpriteLook.Hidden, FireFlames.SpriteLook(sprite, ParticleLevel.Low));
        Assert.Equal(FireSpriteLook.Hidden, FireFlames.SpriteLook(sprite, ParticleLevel.High));
    }

    [Fact]
    public void BarrelKeepsItsBodyUnderTheFlame() =>
        Assert.Equal(FireSpriteLook.Masked, FireFlames.SpriteLook(Barrel, ParticleLevel.High));

    [Theory]
    [InlineData(Flame)]
    [InlineData(CampFire)]
    [InlineData(Barrel)]
    public void ParticlesOffKeepsTheDosSprite(int sprite) =>
        Assert.Equal(FireSpriteLook.Dos, FireFlames.SpriteLook(sprite, ParticleLevel.Off));

    [Fact]
    public void NonFireIsDos() => Assert.Equal(FireSpriteLook.Dos, FireFlames.SpriteLook(Crate, ParticleLevel.High));

    [Fact]
    public void ParticlesOffBurnsNoFlame()
    {
        Assert.False(FireFlames.Burns(ParticleLevel.Off));
        Assert.True(FireFlames.Burns(ParticleLevel.Low));
    }
}
