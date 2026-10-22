using System.Numerics;
using RetroEng.Physics;
using RetroEng.Render;
using RetroEng.Render.Lights;
using SkyNet.Formats;
using SkyNet.Game.Behaviour;
using SkyNet.Game.Enhanced;
using SkyNet.Game.Tests.Behaviour;
using SkyNet.Game.Weapons;
using Xunit.Abstractions;

namespace SkyNet.Game.Tests.Enhanced;

public class LevelLightsTests(ITestOutputHelper output)
{
    private const float Dt = 1f / 60f;

    /// <summary>A behaviour's light state by hand.</summary>
    private sealed class States : ILightStates
    {
        public readonly Dictionary<int, (bool On, int Intensity)> Lights = [];
        public bool Tracks(int offset) => Lights.ContainsKey(offset);
        public bool On(int offset) => Lights[offset].On;
        public int Intensity(int offset) => Lights[offset].Intensity;
    }

    private static readonly MapLight Lamp = new(100, new Vector3(0, 200, 0), Vector3.One, 800f, LightFalloff.EnergyAtRef(0.3f), 40, true);

    [Fact]
    public void UntrackedLightKeepsItsStartingState()
    {
        Assert.Equal(Lamp.Energy, LevelLights.EnergyNow(Lamp, null));
        Assert.Equal(0f, LevelLights.EnergyNow(Lamp with { StartsOn = false }, new States()));
    }

    [Fact]
    public void LightEventsSwitchAndScaleTheLight()
    {
        var states = new States();
        states.Lights[Lamp.Offset] = (false, 40);
        Assert.Equal(0f, LevelLights.EnergyNow(Lamp, states));
        // Toggled on (act 0x01), then an intensity act halved the word: the energy halves with it.
        states.Lights[Lamp.Offset] = (true, 40);
        Assert.Equal(Lamp.Energy, LevelLights.EnergyNow(Lamp, states), 1e-4f);
        states.Lights[Lamp.Offset] = (true, 20);
        Assert.Equal(Lamp.Energy / 2f, LevelLights.EnergyNow(Lamp, states), 1e-4f);

        var level = new LevelLights([Lamp], []);
        var lights = new List<PointLight>();
        level.Append(lights, 0f, states);
        Assert.Equal(Lamp.Energy / 2f, Assert.Single(lights).Energy, 1e-4f);
        states.Lights[Lamp.Offset] = (false, 20);
        lights.Clear();
        level.Append(lights, 0f, states);
        Assert.Empty(lights);
    }

    [Fact]
    public void FireLampsFlickerArtLampsHoldSteady()
    {
        var art = new LampLight(Vector3.Zero, EmissiveLamps.Colour, 900f, 15f, Flicker: false);
        var fire = new LampLight(Vector3.One, EmissiveLamps.FireColour, 900f, 30f, Flicker: true, Phase: 1f);
        var level = new LevelLights([], [art, fire]);
        var a = new List<PointLight>();
        var b = new List<PointLight>();
        level.Append(a, 0.1f, null);
        level.Append(b, 0.37f, null);
        Assert.Equal(a[0].Energy, b[0].Energy);
        Assert.NotEqual(a[1].Energy, b[1].Energy);
        Assert.Equal(30f * EmissiveLamps.FlickerScale(0.37f, 1f), b[1].Energy, 1e-4f);
        // Godot's colours are sRGB; the lights take them linear.
        Assert.Equal(Srgb.ToLinear(EmissiveLamps.FireColour.Y), b[1].Colour.Y, 1e-5f);
    }

    /// <summary>Every variant-2 light of a map becomes one, its range from the enable word; indoors
    /// at most 0.3 at 150 u, outdoors the street lamps' 0.15-0.9, warm.</summary>
    [BehaviourDataFact]
    public void MapLightsPerMap()
    {
        var data = BehaviourData.Data;
        foreach (var number in new[] { 210, 211, 214, 252 })
        {
            var map = data.LoadMap(number)!;
            var lights = LevelLights.MapLightsOf(map);
            var lamps = EmissiveLamps.ForMap(data, map, number, LevelLights.ArtLampMaxFraction);
            output.WriteLine($"MAP.{number} ({(map.IsOutdoor ? "outdoor" : "interior")}): {lights.Count} map lights " +
                $"({lights.Count(l => l.StartsOn)} on, ranges {lights.Select(l => l.Range).DefaultIfEmpty().Min():0}-{lights.Select(l => l.Range).DefaultIfEmpty().Max():0}), " +
                $"{lamps.Count(l => !l.Flicker)} art lamps, {lamps.Count(l => l.Flicker)} fire lamps");
            Assert.Equal(map.Entities.Count(e => e.Variant == MapEntityVariant.Light), lights.Count);
            Assert.All(lights, l => Assert.InRange(l.Range, LightFalloff.RangeMin, LightFalloff.RangeMax));
            if (map.IsOutdoor)
            {
                Assert.All(lights, l => Assert.InRange(l.Energy, LightFalloff.EnergyAtRef(LevelLights.OutdoorLampMin) - 1e-3f,
                    LightFalloff.EnergyAtRef(LevelLights.OutdoorLampMax) + 1e-3f));
                Assert.All(lights, l => Assert.True(l.Colour.Z < l.Colour.X));
            }
            else
            {
                Assert.All(lights, l => Assert.True(l.Energy <= LightFalloff.EnergyAtRef(LevelLights.MapLightMaxEnhanced) + 1e-3f));
            }
        }

        // MAP.210 has its 32 street lamps; MAP.214 its corridor fittings.
        Assert.Equal(32, LevelLights.MapLightsOf(data.LoadMap(210)!).Count);
        Assert.NotEmpty(LevelLights.MapLightsOf(data.LoadMap(214)!));
    }

    /// <summary>A light with the toggle act, flipped by its chain: the behaviour turns it and the
    /// enhanced light follows.</summary>
    [BehaviourDataFact]
    public void ToggledMapLightFollowsTheBehaviour()
    {
        foreach (var number in Enumerable.Range(200, 100))
        {
            if (BehaviourData.Data.LoadMap(number) is not { } map)
            {
                continue;
            }

            var e = map.Entities.FirstOrDefault(x => x.Variant == MapEntityVariant.Light && x.ActType == ActRules.LightToggle);
            if (e == null)
            {
                continue;
            }

            var h = new Harness(number);
            if (!h.B.HasLight(e.FileOffset))
            {
                continue;
            }

            var light = LevelLights.MapLightsOf(map).Single(l => l.Offset == e.FileOffset);
            var states = new BehaviourLights(h.B);
            var before = h.B.LightOn(e.FileOffset);
            Assert.Equal(before, LevelLights.EnergyNow(light, states) > 0f);
            h.B.Flip(e.FileOffset);
            h.B.Tick(Harness.Dt, new PlayerProbe(Harness.Away, Harness.Away));
            Assert.NotEqual(before, h.B.LightOn(e.FileOffset));
            Assert.Equal(!before, LevelLights.EnergyNow(light, states) > 0f);
            output.WriteLine($"MAP.{number} light @{e.FileOffset:X}: {(before ? "on" : "off")} -> {(before ? "off" : "on")}");
            return;
        }

        output.WriteLine("No toggled light on MAP.200-299");
    }

    [Fact]
    public void ShotGivesAFlashLightThatFades()
    {
        var weapons = new WeaponSystem(WeaponTable.Shipped, new CollisionWorld());
        var shooter = new Shooter(Vector3.Zero, 0f, 0f);
        weapons.Tick(new WeaponInput(Fire: true, Throw: false, Run: false), shooter, Dt);
        var lights = new List<PointLight>();
        GameLights.Append(lights, weapons);
        var flash = lights.Single(l => l.Range == GameLights.FlashRange);
        Assert.InRange(flash.Energy, 0f, LightFalloff.EnergyAtRef(GameLights.FlashIntensity));
        Assert.True(flash.Energy > LightFalloff.EnergyAtRef(GameLights.FlashIntensity) * 0.5f);
        for (var i = 0; i < 10; i++)
        {
            weapons.Tick(default, shooter, Dt);
        }

        lights.Clear();
        GameLights.Append(lights, weapons);
        Assert.DoesNotContain(lights, l => l.Range == GameLights.FlashRange);
    }

    [Fact]
    public void ExplosionLightFadesDownItsFrames()
    {
        var weapons = new WeaponSystem(WeaponTable.Shipped, new CollisionWorld());
        weapons.Explosion(new Vector3(0, 0, -1000), 358, 150f);
        var lights = new List<PointLight>();
        GameLights.Append(lights, weapons);
        var start = Assert.Single(lights);
        Assert.Equal(LightFalloff.EnergyAtRef(GameLights.ExplosionIntensity), start.Energy, 1e-3f);
        Assert.Equal(750f, start.Range, 1e-3f);
        var last = start.Energy;
        for (var t = 0; t < 30; t++)
        {
            weapons.Tick(default, new Shooter(Vector3.Zero, 0f, 0f), Dt);
            lights.Clear();
            GameLights.Append(lights, weapons);
            var now = lights.Count == 0 ? 0f : lights[0].Energy;
            Assert.True(now <= last);
            last = now;
        }

        Assert.Equal(0f, last);
    }

    [Fact]
    public void BoltLightRampsUpAwayFromTheShooter()
    {
        var weapons = new WeaponSystem(WeaponTable.Shipped, new CollisionWorld());
        var ammo = WeaponTable.Shipped.Ammo.First(a => a.Family == AmmoFamily.Bolt);
        weapons.EnemyFire(new Source(), ammo, new Vector3(0, 0, -200), -Vector3.UnitZ, 1000f);
        var p = Assert.Single(weapons.Projectiles);
        Assert.Equal(0f, GameLights.BoltLight(p)!.Value.Energy);
        p.Age = 0.13f; // 130 u: half way up the ramp
        Assert.Equal(LightFalloff.EnergyAtRef(GameLights.BoltIntensity) * 0.5f, GameLights.BoltLight(p)!.Value.Energy, 1e-3f);
        p.Age = 1f;
        Assert.Equal(LightFalloff.EnergyAtRef(GameLights.BoltIntensity), GameLights.BoltLight(p)!.Value.Energy, 1e-3f);
        Assert.True(GameLights.BoltLight(p)!.Value.Range >= GameLights.BoltRangeMin);
    }

    [Fact]
    public void GatherSortsByImportanceAndKeepsTheBudget()
    {
        var gather = new LightGather();
        gather.Begin();
        // 300 lamps in a row along −Z, the same energy: the nearest win.
        for (var i = 0; i < 300; i++)
        {
            gather.All.Add(new PointLight(new Vector3(0, 0, -100f * (i + 1)), Vector3.One, 500f, 50f));
        }

        // A bright explosion further out outranks the dim ones near it; one far past the reach and a
        // dark one drop out.
        gather.All.Add(new PointLight(new Vector3(0, 0, -5000), Vector3.One, 600f, 450f));
        gather.All.Add(new PointLight(new Vector3(0, 0, -90000), Vector3.One, 600f, 450f));
        gather.All.Add(new PointLight(new Vector3(0, 0, -10), Vector3.One, 600f, 0f));
        var lights = gather.Finish(Vector3.Zero, 50000f).Span;
        Assert.Equal(ClusterGrid.MaxLights, lights.Length);
        Assert.Equal(-100f, lights[0].Position.Z);
        for (var i = 1; i < lights.Length; i++)
        {
            Assert.True(LightGather.Importance(lights[i - 1], Vector3.Zero) >= LightGather.Importance(lights[i], Vector3.Zero));
        }

        Assert.Contains(lights.ToArray(), l => l.Position.Z == -5000f);
        Assert.DoesNotContain(lights.ToArray(), l => l.Position.Z == -90000f || l.Energy == 0f);
        Assert.Equal(300 + 1 - ClusterGrid.MaxLights, gather.OverBudget);

        // A smaller budget (ENHANCED OPTIONS' LIGHTS) keeps fewer.
        Assert.Equal(64, gather.Finish(Vector3.Zero, 50000f, 64).Length);
    }

    private sealed class Source : IDamageable
    {
        public Vector3 Centre => new(0, 0, -200);
        public float Radius => 30f;
        public bool Alive => true;
        public void TakeDamage(in Damage damage) { }
    }
}
