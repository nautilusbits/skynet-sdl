using System.Numerics;
using System.Text;
using RetroEng.Json;
using RetroEng.Render;
using RetroEng.Render.Lights;
using SkyNet.Data;
using SkyNet.Formats;
using SkyNet.Game.Enhanced;
using SkyNet.Game.Tests.Behaviour;
using Xunit.Abstractions;

namespace SkyNet.Game.Tests.Enhanced;

public class LampSpotsTests(ITestOutputHelper output)
{
    private static LitMask AllLit() => new(4, 4, Enumerable.Repeat(true, 16).ToArray(), 16);

    private static EmissiveSurface Quad(Vector3[] corners, Vector3 normal) =>
        new(AllLit(), corners, [new(0, 0), new(1, 0), new(1, 1), new(0, 1)], [normal, normal, normal, normal], [0, 1, 2, 0, 2, 3]) { Texture = 26626 };

    private static LampLight Lamp(Vector3 facing, float coherence, bool flicker = false) =>
        new(new Vector3(0, 200, 0), Vector3.One, 900f, 10f, flicker, Facing: facing, Coherence: coherence, Model: "LAMP", Texture: 300, Entity: 77);

    [Fact]
    public void FlatCeilingPatchIsACoherentSpotDown()
    {
        var mesh = new EmissiveMesh(Matrix4x4.Identity, new Vector3(0, 300, -400), new Vector3(400, 300, 0),
            [Quad([new(0, 300, 0), new(400, 300, 0), new(400, 300, -400), new(0, 300, -400)], -Vector3.UnitY)]) { Model = "CEIL", Entity = 5 };
        var lamp = Assert.Single(EmissiveLamps.Build([mesh]));
        Assert.Equal(1f, lamp.Coherence, 1e-4f);
        Assert.Equal(-Vector3.UnitY, lamp.Facing);
        Assert.Equal(("CEIL", 26626, 5), (lamp.Model, lamp.Texture, lamp.Entity));
        var light = LampSpots.Derive(lamp, Vector3.One);
        Assert.True(light.IsSpot);
        Assert.Equal(-Vector3.UnitY, light.SpotDirection);
        Assert.Equal(MathF.Cos(LampSpots.CeilingOuter * MathF.PI / 180f), light.SpotCosOuter, 1e-5f);
        Assert.Equal(1f, light.ConeFactor(-Vector3.UnitY));
        Assert.Equal(0f, light.ConeFactor(Vector3.UnitX));
    }

    [Fact]
    public void BoxLitAllRoundStaysOmni()
    {
        // Two opposite faces of a lit box in one cell: their normals cancel.
        var mesh = new EmissiveMesh(Matrix4x4.Identity, new Vector3(0, 0, -100), new Vector3(100, 100, 0),
        [
            Quad([new(0, 100, 0), new(100, 100, 0), new(100, 100, -100), new(0, 100, -100)], Vector3.UnitY),
            Quad([new(0, 0, -100), new(100, 0, -100), new(100, 0, 0), new(0, 0, 0)], -Vector3.UnitY),
        ]);
        var lamp = Assert.Single(EmissiveLamps.Build([mesh]));
        Assert.True(lamp.Coherence < LampSpots.MinCoherence);
        Assert.False(LampSpots.Derive(lamp, Vector3.One).IsSpot);
    }

    [Fact]
    public void DerivationRules()
    {
        Assert.True(LampSpots.Derive(Lamp(-Vector3.UnitY, 0.95f), Vector3.One).IsSpot);
        var wall = LampSpots.Derive(Lamp(Vector3.UnitZ, 0.9f), Vector3.One);
        Assert.Equal(Vector3.UnitZ, wall.SpotDirection);
        Assert.Equal(MathF.Cos(LampSpots.WallOuter * MathF.PI / 180f), wall.SpotCosOuter, 1e-5f);
        Assert.False(LampSpots.Derive(Lamp(-Vector3.UnitY, 0.5f), Vector3.One).IsSpot);
        Assert.False(LampSpots.Derive(Lamp(Vector3.Zero, 1f), Vector3.One).IsSpot);
        Assert.False(LampSpots.Derive(Lamp(-Vector3.UnitY, 1f, flicker: true), Vector3.One).IsSpot);
    }

    [Fact]
    public void OverridesParseMatchAndApply()
    {
        var json = """
            { "lamps": [
                { "model": "lamp.3d", "kind": "omni" },
                { "map": 210, "key": 77, "kind": "spot", "direction": [1, 0, 0], "cone": 40, "inner": 10,
                  "colour": [1, 0, 0], "intensity": 0.5, "range": 1234 },
                { "texture": "2/44", "range": 50 },
                { "map": 210, "key": 900, "cone": 50 } ] }
            """;
        var rows = LampSpots.Parse(Encoding.UTF8.GetBytes(json));
        Assert.Equal(4, rows.Count);
        Assert.Equal("lamp", rows[0].Model);
        Assert.Equal(2 << 7 | 44, rows[2].Texture);

        // On MAP.211 only the model row applies: a spot forced omni.
        var lamp = Lamp(-Vector3.UnitY, 1f);
        var lights = new LevelLights([], [lamp], 211, rows);
        Assert.Equal(0, lights.SpotCount);

        // On MAP.210 the keyed row follows and makes it a red spot along +X.
        lights = new LevelLights([], [lamp], 210, rows);
        Assert.Equal(1, lights.SpotCount);
        var all = new List<PointLight>();
        lights.Append(all, 0f, null);
        var l = Assert.Single(all);
        Assert.Equal(Vector3.UnitX, l.SpotDirection);
        Assert.Equal(MathF.Cos(40f * MathF.PI / 180f), l.SpotCosOuter, 1e-5f);
        Assert.Equal(MathF.Cos(10f * MathF.PI / 180f), l.SpotCosInner, 1e-5f);
        Assert.Equal(new Vector3(1, 0, 0), l.Colour);
        Assert.Equal(LightFalloff.EnergyAtRef(0.5f), l.Energy, 1e-4f);
        // The texture row (2/44 = word 300, the lamp's) comes after and sets the range.
        Assert.Equal(50f, l.Range);

        // A map light by its record: a cone alone makes it a spot (down), its energy still live.
        var mapLight = new MapLight(900, new Vector3(0, 300, 0), Vector3.One, 800f, 20f, 40, true);
        lights = new LevelLights([mapLight], [], 210, rows);
        all.Clear();
        lights.Append(all, 0f, null);
        var m = Assert.Single(all);
        Assert.Equal(-Vector3.UnitY, m.SpotDirection);
        Assert.Equal(20f, m.Energy);
        Assert.Equal(1, lights.SpotCount);
    }

    [Theory]
    [InlineData("""{ "lamps": [ { "kind": "cone" } ] }""")]
    [InlineData("""{ "lamps": [ { "direction": [0, 0, 0] } ] }""")]
    [InlineData("""{ "lamps": [ { "texture": "x" } ] }""")]
    [InlineData("""{ "lamps": [ { "colour": 3 } ] }""")]
    [InlineData("""{ "lamps": [ { "shape": "round" } ] }""")]
    [InlineData("""{ "lights": [] }""")]
    public void BadFilesAreRefused(string json) =>
        Assert.Throws<JsonDataException>(() => LampSpots.Parse(Encoding.UTF8.GetBytes(json)));

    /// <summary>How many lamps go spot on the campaign's maps, a deathmatch arena and Future Shock's
    /// first maps with lamps (the counts are reported, a few bounds checked).</summary>
    [BehaviourDataFact]
    public void SpotCountsOnRealMaps()
    {
        var data = BehaviourData.Data;
        var counts = new Dictionary<int, int>();
        foreach (var number in new[] { 210, 211, 230, 252, 605 })
        {
            if (data.LoadMap(number) is not { } map)
            {
                output.WriteLine($"MAP.{number}: not in the data");
                continue;
            }

            counts[number] = Report("SkyNET", data, map, number);
        }

        Assert.True(counts.GetValueOrDefault(252) > 0);
        if (GameDataLocator.OtherGameDir(BehaviourData.Dir!) is { } fsDir && GameData.Open(fsDir, BehaviourData.Dir) is { } fs)
        {
            var found = 0;
            for (var number = 0; number < 1000 && found < 3; number++)
            {
                if (fs.LoadMap(number) is { } map && Report("Future Shock", fs, map, number) > 0)
                {
                    found++;
                }
            }

            Assert.True(found > 0);
        }
        else
        {
            output.WriteLine("No Future Shock data beside SkyNET's");
        }
    }

    private int Report(string game, GameData data, MapFile map, int number)
    {
        var lamps = EmissiveLamps.ForMap(data, map, number, LevelLights.ArtLampMaxFraction);
        var lights = new LevelLights(LevelLights.MapLightsOf(map), lamps, number, []);
        var art = lamps.Count(l => !l.Flicker);
        output.WriteLine($"{game} MAP.{number:D3}: {art} art lamps, {lights.SpotCount} spot " +
            $"({lamps.Count(l => LampSpots.IsSpot(l) && -l.Facing.Y >= LampSpots.CeilingDown)} ceiling), {art - lights.SpotCount} omni, " +
            $"{lamps.Count(l => l.Flicker)} fires, {lights.MapLights.Count} map lights");
        Assert.InRange(lights.SpotCount, 0, art);
        return lights.SpotCount;
    }
}
