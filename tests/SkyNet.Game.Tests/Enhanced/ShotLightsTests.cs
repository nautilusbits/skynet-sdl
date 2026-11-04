using System.Numerics;
using System.Text;
using RetroEng.Json;
using RetroEng.Physics;
using RetroEng.Render;
using RetroEng.Render.Lights;
using SkyNet.Game.Enhanced;
using SkyNet.Game.Weapons;

namespace SkyNet.Game.Tests.Enhanced;

public class ShotLightsTests
{
    private const float Dt = 1f / 60f;
    private static readonly WeaponTable Table = WeaponTable.Shipped;

    private static AmmoRecord Ammo(int index) => Table.Ammo[index];

    private sealed class Target(Vector3 centre, float radius) : IDamageable
    {
        public Vector3 Centre => centre;
        public float Radius => radius;
        public bool Alive => true;
        public void TakeDamage(in Damage damage) { }
    }

    private static readonly Vector3 BlastColour = LevelLights.Linear(GameLights.ExplosionSrgb);

    private static List<PointLight> Lit(WeaponSystem weapons, ShadowQuality shadows = ShadowQuality.High, PlayKind play = PlayKind.Campaign)
    {
        var lights = new List<PointLight>();
        GameLights.Append(lights, weapons, ShotLightTable.Default, shadows, play);
        return lights;
    }

    /// <summary>A wall facing −X at x = <paramref name="x"/>.</summary>
    private static CollisionWorld Wall(float x)
    {
        Vector3 a = new(x, -1000, -1000), b = new(x, -1000, 1000), c = new(x, 1000, 1000), d = new(x, 1000, -1000);
        return new CollisionWorld().Add(new TriangleGrid([new Triangle(a, b, c, 7), new Triangle(a, c, d, 7)]));
    }

    /// <summary>GitHub #17: the soldier's own muzzle flash shadows at SHADOWS High in a campaign; the
    /// bullet's impact on the wall never (its shadows looked noisy).</summary>
    [Fact]
    public void Own_flash_shadows_at_high_only()
    {
        var weapons = new WeaponSystem(Table, Wall(900f));
        weapons.Tick(new WeaponInput(Fire: true, Throw: false, Run: false), new Shooter(Vector3.Zero, -MathF.PI / 2f, 0f), Dt);
        Assert.Contains(weapons.Effects, e => e.Kind == EffectKind.OwnFlash);
        Assert.Contains(weapons.Effects, e => e.Kind == EffectKind.Impact);

        var high = Lit(weapons);
        Assert.True(high.Single(l => l.Colour != BlastColour).CastsShadow);
        Assert.False(high.Single(l => l.Colour == BlastColour).CastsShadow);
        Assert.DoesNotContain(Lit(weapons, ShadowQuality.Low), l => l.CastsShadow);
        Assert.DoesNotContain(Lit(weapons, play: PlayKind.Network), l => l.CastsShadow);
    }

    /// <summary>A bullet's hit lights no more than a hand laser's: it lit like a grenade (3.0 over
    /// 600 u) and an uzi's burst flooded the room (owner, 2026-11-03).</summary>
    [Fact]
    public void A_bullet_hit_lights_like_a_hand_laser_hit()
    {
        var weapons = new WeaponSystem(Table, Wall(900f));
        weapons.Tick(new WeaponInput(Fire: true, Throw: false, Run: false), new Shooter(Vector3.Zero, -MathF.PI / 2f, 0f), Dt);
        var hit = Lit(weapons).Single(l => l.Colour == BlastColour);

        var hand = ShotLightTable.DefaultClasses[ShotClass.Hand];
        Assert.InRange(hit.Range, 1f, hand.ImpactRadius);
        Assert.InRange(hit.Energy, 0f, LightFalloff.EnergyAtRef(hand.ImpactIntensity));
    }

    /// <summary>GitHub #17: a robot's flash, its bolt and a network player's laser light but never shadow.</summary>
    [Fact]
    public void Others_flashes_and_bolts_never_shadow()
    {
        var weapons = new WeaponSystem(Table, new CollisionWorld());
        var robot = new Target(new Vector3(0, 40, -200), 40f);
        var bolt = Table.Ammo.First(a => a.Family == AmmoFamily.Bolt);
        var bullet = Table.Ammo.First(a => a.Family == AmmoFamily.Hitscan);
        weapons.EnemyFire(robot, bolt, new Vector3(0, 40, -300), -Vector3.UnitZ, 1000f);
        weapons.EnemyFire(robot, bullet, new Vector3(0, 40, -300), -Vector3.UnitZ, 0f);
        Assert.True(weapons.RemoteFire(11, new Vector3(0, 40, -10), -Vector3.UnitZ, new Target(new Vector3(0, 40, 0), 40f)));
        var lit = false;
        for (var i = 0; i < 30; i++)
        {
            var lights = Lit(weapons);
            lit |= lights.Count >= 3;
            Assert.DoesNotContain(lights, l => l.CastsShadow);
            weapons.Tick(default, new Shooter(new Vector3(0, 75, 2000), 0f, 0f), Dt);
        }

        Assert.True(lit);
    }

    /// <summary>GitHub #17: explosions shadow, the newest <see cref="GameLights.ShadowedBlasts"/> of
    /// them; none in a network game.</summary>
    [Fact]
    public void Newest_explosions_shadow_up_to_the_cap()
    {
        var weapons = new WeaponSystem(Table, new CollisionWorld());
        const int Blasts = GameLights.ShadowedBlasts + 2;
        for (var i = 0; i < Blasts; i++)
        {
            weapons.Explosion(new Vector3(i * 100f, 0, -1000), 358, 150f);
        }

        var shadowed = Lit(weapons).Where(l => l.CastsShadow).Select(l => l.Position.X).ToList();
        var newest = Enumerable.Range(Blasts - GameLights.ShadowedBlasts, GameLights.ShadowedBlasts).Select(i => i * 100f);
        Assert.Equal(newest, shadowed);
        Assert.DoesNotContain(Lit(weapons, play: PlayKind.Network), l => l.CastsShadow);
    }

    [Fact]
    public void LasersGetTheirClassAndColourFromTheAmmoTable()
    {
        var t = ShotLightTable.Default;
        // Hand lasers (LASER1 25/50, LASER2 25/50): small, red and blue.
        var hand = t.For(Ammo(6))!.Value;
        Assert.Equal(ShotLightTable.DefaultClasses[ShotClass.Hand].Radius, hand.Range);
        Assert.Equal(LevelLights.Linear(ShotLightTable.Laser1Srgb), hand.Colour);
        Assert.Equal(LevelLights.Linear(ShotLightTable.Laser2Srgb), t.For(Ammo(8))!.Value.Colour);
        Assert.Equal(ShotClass.Hand, ShotLightTable.ClassOf(Ammo(9)));
        // The biggest hand laser (100) is heavy; the HK's and jeep's blasting lasers are vehicle class.
        Assert.Equal(ShotClass.Heavy, ShotLightTable.ClassOf(Ammo(10)));
        Assert.Equal(ShotClass.Vehicle, ShotLightTable.ClassOf(Ammo(18)));
        Assert.Equal(ShotClass.Vehicle, ShotLightTable.ClassOf(Ammo(22)));
        var vehicle = t.For(Ammo(22))!.Value;
        var heavy = t.For(Ammo(10))!.Value;
        Assert.True(vehicle.Range > heavy.Range && heavy.Range > hand.Range);
        Assert.True(vehicle.Energy > heavy.Energy && heavy.Energy > hand.Energy);
        // A robot's laser cannon (ammo 8, 25 points) lights as heavy.
        Assert.Equal(ShotLightTable.DefaultClasses[ShotClass.Heavy].Radius, t.For(Ammo(8), robot: true)!.Value.Range);
        // Rockets, bullets and lobbed bodies are not lasers.
        Assert.Null(t.For(Ammo(5)));
        Assert.Null(t.For(Ammo(1)));
        Assert.Null(t.For(Ammo(3)));
    }

    [Fact]
    public void ModFileOverridesClassesAndRows()
    {
        var json = """
            { "classes": { "hand": { "radius": 900, "impactLife": 0.3 } },
              "robotClass": "vehicle",
              "shots": [ { "model": "LASER2", "colour": [0, 1, 0] },
                         { "ammo": 9, "intensity": 2, "class": "heavy" },
                         { "ammo": 5, "radius": 777 } ] }
            """;
        var t = ShotLightTable.Parse(Encoding.UTF8.GetBytes(json));
        Assert.Equal(900f, t.For(Ammo(6))!.Value.Range);
        Assert.Equal(0.3f, t.For(Ammo(6))!.Value.ImpactLife);
        Assert.Equal(LightFalloff.EnergyAtRef(ShotLightTable.DefaultClasses[ShotClass.Hand].Intensity), t.For(Ammo(6))!.Value.Energy, 1e-4f);
        Assert.Equal(new Vector3(0, 1, 0), t.For(Ammo(8))!.Value.Colour);
        var nine = t.For(Ammo(9))!.Value;
        Assert.Equal(new Vector3(0, 1, 0), nine.Colour);
        Assert.Equal(LightFalloff.EnergyAtRef(2f), nine.Energy, 1e-4f);
        Assert.Equal(ShotLightTable.DefaultClasses[ShotClass.Heavy].Radius, nine.Range);
        Assert.Equal(ShotLightTable.DefaultClasses[ShotClass.Vehicle].Radius, t.For(Ammo(6), robot: true)!.Value.Range);
        // A row may light a rocket.
        Assert.Equal(777f, t.For(Ammo(5))!.Value.Range);
    }

    [Theory]
    [InlineData("""{ "shots": [ { "colour": [1, 0, 0] } ] }""")]
    [InlineData("""{ "classes": { "huge": { } } }""")]
    [InlineData("""{ "shots": [ { "ammo": 6, "radius": -5 } ] }""")]
    [InlineData("""{ "shots": [ { "ammo": 6, "colour": [1, 0] } ] }""")]
    [InlineData("""{ "lasers": [] }""")]
    public void BadFilesAreRefused(string json) =>
        Assert.Throws<JsonDataException>(() => ShotLightTable.Parse(Encoding.UTF8.GetBytes(json)));

    /// <summary>A deathmatch: somebody else's laser (RemoteFire, harmless) lights as it flies and
    /// flashes where it strikes, as the soldier's and the robots' do.</summary>
    [Fact]
    public void RemoteLaserLightsInFlightAndFlashesOnImpact()
    {
        var weapons = new WeaponSystem(Table, new CollisionWorld());
        var victim = new Target(new Vector3(0, 40, -900), 40f);
        weapons.Targets.Add(victim);
        var shooter = new Target(new Vector3(0, 40, 0), 40f);
        Assert.True(weapons.RemoteFire(11, new Vector3(0, 40, -10), -Vector3.UnitZ, shooter));
        var bolt = Assert.Single(weapons.Projectiles);
        var shot = ShotLightTable.Default.For(bolt.Ammo)!.Value;
        var lights = new List<PointLight>();
        var sawBolt = false;
        for (var i = 0; i < 120 && weapons.Projectiles.Count > 0; i++)
        {
            weapons.Tick(default, new Shooter(new Vector3(0, 75, 2000), 0f, 0f), Dt);
            lights.Clear();
            GameLights.Append(lights, weapons, ShotLightTable.Default);
            sawBolt |= lights.Any(l => l.Colour == shot.Colour && l.Range == shot.Range && l.Energy > 0f);
        }

        Assert.True(sawBolt);
        Assert.Empty(weapons.Projectiles);
        var flash = Assert.Single(weapons.Impacts);
        Assert.Equal(-860f, flash.At.Z, 20f);
        lights.Clear();
        GameLights.Append(lights, weapons, ShotLightTable.Default);
        var impact = Assert.Single(lights, l => l.Range == shot.ImpactRange);
        Assert.Equal(shot.Colour, impact.Colour);
        // The impact's warm explosion light gives way to the coloured flash.
        Assert.DoesNotContain(lights, l => l.Range >= GameLights.ExplosionRangeMin && l.Colour != shot.Colour);
        for (var i = 0; i < 60; i++)
        {
            weapons.Tick(default, new Shooter(new Vector3(0, 75, 2000), 0f, 0f), Dt);
        }

        lights.Clear();
        GameLights.Append(lights, weapons, ShotLightTable.Default);
        Assert.DoesNotContain(lights, l => l.Range == shot.ImpactRange);
        Assert.Empty(weapons.Impacts);
    }
}
