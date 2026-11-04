using System.Numerics;
using RetroEng.Render;
using RetroEng.Render.Lights;
using SkyNet.Formats;
using SkyNet.Game.Enhanced;
using Xunit.Abstractions;

namespace SkyNet.Game.Tests.Enhanced;

public class EmissiveLampsTests(ITestOutputHelper output)
{
    /// <summary>A 16 × 16 mask lit in a 4 × 4 patch at texels 6-9.</summary>
    private static LitMask PatchMask()
    {
        var lit = new bool[16 * 16];
        for (var y = 6; y < 10; y++)
            for (var x = 6; x < 10; x++)
                lit[y * 16 + x] = true;
        return new LitMask(16, 16, lit, 64);
    }

    /// <summary>A 400 × 400 quad at <paramref name="corners"/>, UV 0-1 over it, one normal.</summary>
    private static EmissiveSurface Quad(Vector3[] corners, Vector3 normal) =>
        new(PatchMask(), corners, [new(0, 0), new(1, 0), new(1, 1), new(0, 1)], [normal, normal, normal, normal], [0, 1, 2, 0, 2, 3]);

    private static EmissiveMesh Ceiling(Matrix4x4 world, float size = 400f) =>
        new(world, new Vector3(0, 300, -size), new Vector3(size, 300, 0),
            [Quad([new(0, 300, 0), new(size, 300, 0), new(size, 300, -size), new(0, 300, -size)], -Vector3.UnitY)]);

    [Fact]
    public void CeilingPatchGivesOneLampHungUnderIt()
    {
        var lamps = EmissiveLamps.Build([Ceiling(Matrix4x4.Identity)]);
        var lamp = Assert.Single(lamps);
        // The patch spans texels 6-9 of 16: 150-250 u across the 400 u quad, centred at 200.
        Assert.Equal(200f, lamp.Position.X, 8f);
        Assert.Equal(-200f, lamp.Position.Z, 8f);
        Assert.Equal(300f - EmissiveLamps.Drop, lamp.Position.Y, 1e-3f);
        Assert.False(lamp.Lifted);
        Assert.False(lamp.Flicker);
        Assert.Equal(EmissiveLamps.Colour, lamp.Colour);
        Assert.Equal(LightFalloff.EnergyAtRef(EmissiveLamps.IntensityAtRef), lamp.Energy, 1e-4f);
        Assert.InRange(lamp.Range, EmissiveLamps.RangeMin, EmissiveLamps.RangeMax);
    }

    /// <summary>FS MAP.129's corridor ceilings (CORC3230) tile 479/1, a 6-texel strip at x 37-42 of
    /// 64, four times across a quad: 14 samples a side fell at u = k/7 of a tile, all beside the
    /// strip, and the corridor got no lamp.</summary>
    [Fact]
    public void TiledStripGivesALamp()
    {
        var lit = new bool[64 * 64];
        for (var y = 6; y < 58; y++)
        {
            for (var x = 37; x < 43; x++)
            {
                lit[y * 64 + x] = true;
            }
        }

        var strip = new LitMask(64, 64, lit, 96);
        var quad = new EmissiveSurface(strip, [new(0, 300, 0), new(256, 300, 0), new(256, 300, -256), new(0, 300, -256)],
            [new(4, 2), new(4, 0), new(0, 0), new(0, 2)], [-Vector3.UnitY, -Vector3.UnitY, -Vector3.UnitY, -Vector3.UnitY], [0, 1, 2, 0, 2, 3]);
        var mesh = new EmissiveMesh(Matrix4x4.Identity, new Vector3(0, 300, -256), new Vector3(256, 300, 0), [quad]);
        var lamp = Assert.Single(EmissiveLamps.Build([mesh]));
        Assert.Equal(300f - EmissiveLamps.Drop, lamp.Position.Y, 1e-3f);
    }

    [Fact]
    public void PlacementMovesTheLamp()
    {
        var lamp = Assert.Single(EmissiveLamps.Build([Ceiling(Matrix4x4.CreateTranslation(1000, 50, 0))]));
        Assert.Equal(1200f, lamp.Position.X, 8f);
        Assert.Equal(290f, lamp.Position.Y, 1e-3f);
    }

    [Fact]
    public void WallPatchLendsItsLampToTheCeiling()
    {
        // A wall facing +Z from y 0 to 400; the mesh's top is 400.
        var wall = new EmissiveMesh(Matrix4x4.Identity, new Vector3(0, 0, 0), new Vector3(400, 400, 0),
            [Quad([new(0, 400, 0), new(400, 400, 0), new(400, 0, 0), new(0, 0, 0)], Vector3.UnitZ)]);
        var lamp = Assert.Single(EmissiveLamps.Build([wall]));
        Assert.True(lamp.Lifted);
        Assert.Equal(400f - EmissiveLamps.CeilingGap, lamp.Position.Y, 1e-3f);
        Assert.Equal(EmissiveLamps.Drop, lamp.Position.Z, 1e-3f);
        Assert.Equal(LightFalloff.EnergyAtRef(EmissiveLamps.IntensityAtRef * EmissiveLamps.WallScale), lamp.Energy, 1e-4f);
    }

    [Fact]
    public void UnlitSurfaceGivesNoLamp()
    {
        var mesh = Ceiling(Matrix4x4.Identity);
        var dark = mesh with { Surfaces = [mesh.Surfaces[0] with { Mask = new LitMask(16, 16, new bool[256], 64) }] };
        Assert.Empty(EmissiveLamps.Build([dark]));
    }

    [Fact]
    public void LampsAreCappedBrightestFirst()
    {
        var meshes = Enumerable.Range(0, EmissiveLamps.MaxLamps + 10)
            .Select(i => Ceiling(Matrix4x4.CreateTranslation(i * 2000f, 0, 0), 400f + i))
            .ToList();
        var lamps = EmissiveLamps.Build(meshes);
        Assert.Equal(EmissiveLamps.MaxLamps, lamps.Count);
        // The weight is the lit area in the mesh's own frame: the larger quads: the 10 smallest are dropped.
        Assert.All(lamps, l => Assert.True(l.Position.X >= 10 * 2000f));
    }

    [Fact]
    public void CopiesOfOneFittingShareOneLamp()
    {
        // Two copies of a ceiling placed 100 u apart: one world cell, one lamp (was one each).
        var lamps = EmissiveLamps.Build([Ceiling(Matrix4x4.Identity), Ceiling(Matrix4x4.CreateTranslation(100, 0, 0))]);
        var lamp = Assert.Single(lamps);
        Assert.Equal(250f, lamp.Position.X, 8f);
    }

    [Fact]
    public void FloorPanelLightsTheFloor()
    {
        // A lit floor panel at y 0 facing up, its mesh 400 u tall: its lamp lights the floor around it.
        var floor = new EmissiveMesh(Matrix4x4.Identity, new Vector3(0, 0, -400), new Vector3(400, 400, 0),
            [Quad([new(0, 0, -400), new(400, 0, -400), new(400, 0, 0), new(0, 0, 0)], Vector3.UnitY)]);
        var lamp = Assert.Single(EmissiveLamps.Build([floor]));
        var light = LampSpots.Derive(lamp, lamp.Colour);
        var at = new Vector3(500, 0, -200);
        var to = Vector3.Normalize(at - light.Position);
        Assert.InRange(lamp.Position.Y, 1f, 400f - EmissiveLamps.CeilingGap);
        Assert.True(LightFalloff.Light(light.Position, light.Range, light.Energy, at, Vector3.UnitY) * light.ConeFactor(to) > 0f);
    }

    [Fact]
    public void LitBulbSpriteGivesASteadyLamp()
    {
        // 204/18 (26130) a lit pendant; 204/17 (26129) the same one switched off.
        var lamp = Assert.Single(EmissiveLamps.Bulbs([new FireSprite(26130, new Vector3(10, 0, 30), 600f, 3), new FireSprite(26129, Vector3.Zero, 600f, 4)]));
        Assert.False(lamp.Flicker);
        Assert.Equal(26130, lamp.Texture);
        Assert.InRange(lamp.Position.Y, 1f, 300f);
        Assert.False(LampSpots.IsSpot(lamp));
    }

    /// <summary>MAP.604: the map light 3872, 400, −5180 sits 59 u under the lit pendant's bulb
    /// (3870, 459, −5182) with twice its energy, so its glow lit the walls under the bulb.</summary>
    [Fact]
    public void MapLightUnderABulbHangsAtIt()
    {
        var bulb = Assert.Single(EmissiveLamps.Bulbs([new FireSprite(26130, new Vector3(3870, 450, -5182), 152f, 3)]));
        var under = new MapLight(17, new Vector3(3872, 400, -5180), Vector3.One, 2050f, 45f, 63, true);
        var apart = new MapLight(18, new Vector3(4400, 400, -5180), Vector3.One, 800f, 45f, 63, true);
        var lights = new List<PointLight>();
        new LevelLights([under, apart], [bulb], 604, []).Append(lights, 0f, null);
        Assert.Equal(bulb.Position, lights[0].Position);
        Assert.Equal(45f, lights[0].Energy);
        Assert.Equal(apart.Position, lights[1].Position);
    }

    /// <summary>Future Shock's MAP.129 has 685 lit cells over its mesh copies: every lit place keeps
    /// a lamp in its world cell (the 128 cap kept 128).</summary>
    [Fact]
    public void BigMapKeepsALampEveryLitPlace()
    {
        if (FutureShock() is not { } data)
        {
            return;
        }

        var map = data.LoadMap(129)!;
        var kept = EmissiveLamps.ForMap(data, map, 129, LevelLights.ArtLampMaxFraction).Where(l => !l.Flicker).ToList();
        var palette = data.LoadPalette()!;
        var litTable = EmissionClassifier.LitTable(palette);
        var places = new List<Vector3>();
        foreach (var e in map.Entities.Where(e => e.Variant == SkyNet.Formats.MapEntityVariant.Mesh && e.NameIndex >= 0 && e.NameIndex < map.Names.Length))
        {
            if (data.LoadModel(map.Names[e.NameIndex] + ".3D") is not { } model)
            {
                continue;
            }

            var mesh = EmissiveLamps.FromModel(model, EmissiveLamps.PlacementOf(e), w =>
                data.LoadTextureBank(w >> 7) is { Records.IsEmpty: false } b && b.Records[Math.Clamp(w & 0x7F, 0, b.Records.Length - 1)] is { IsEmpty: false } r
                    ? EmissiveLamps.MaskOf(r, palette, litTable, LevelLights.ArtLampMaxFraction) : null);
            if (mesh != null)
            {
                places.AddRange(EmissiveLamps.Build([mesh]).Select(l => l.Position));
            }
        }

        var unlit = places.Count(p => !kept.Any(l => Vector3.Distance(l.Position, p) < 2f * EmissiveLamps.Cell));
        output.WriteLine($"FS MAP.129: {places.Count} lit cells, {kept.Count} lamps, {unlit} places without one");
        Assert.True(kept.Count < EmissiveLamps.MaxLamps);
        Assert.Equal(0, unlit);
    }

    /// <summary>SkyNET MAP.232's ceiling 274/12 peaks just under the cut: the shipped lamp data
    /// makes it a lamp. MAP.604's lit pendants 204/18 light.</summary>
    [EmissionDataFact]
    public void ShippedRowsAndBulbsLight()
    {
        var data = EmissionClassifierTests.Data()!;
        var on232 = EmissiveLamps.ForMap(data, data.LoadMap(232)!, 232, LevelLights.ArtLampMaxFraction);
        Assert.Contains(on232, l => l.Texture == (274 << 7 | 12));
        var on604 = EmissiveLamps.ForMap(data, data.LoadMap(604)!, 604, LevelLights.ArtLampMaxFraction);
        Assert.Contains(on604, l => l.Texture == 26130 && !l.Flicker);
    }

    /// <summary>Future Shock MAP.031's barrack ceiling (ROMD0211) carries cyan ring panels, 471/2,
    /// whose brightest texels (51, 199, 199) peak under the emissive cut: the shipped lamp data makes
    /// them a lamp over the room (it was black under them).</summary>
    [Fact]
    public void CeilingRingsLightTheBarrack()
    {
        if (FutureShock() is not { } data)
        {
            return;
        }

        var feet = new Vector3(11040, -63, -11616);
        var lamps = EmissiveLamps.ForMap(data, data.LoadMap(31)!, 31, LevelLights.ArtLampMaxFraction);
        var ring = lamps.Where(l => l.Texture == (471 << 7 | 2)).OrderBy(l => Vector3.Distance(l.Position, feet)).FirstOrDefault();
        output.WriteLine($"FS MAP.031: nearest 471/2 lamp {ring.Position}, range {ring.Range}, facing {ring.Facing}");
        Assert.Equal(471 << 7 | 2, ring.Texture);
        Assert.True(ring.Position.Y > feet.Y);
        Assert.InRange(Vector2.Distance(new(ring.Position.X, ring.Position.Z), new(feet.X, feet.Z)), 0f, ring.Range);
    }

    private static GameData? FutureShock() =>
        EmissionClassifierTests.Data() is not null && Environment.GetEnvironmentVariable("SKYNET_DATA") is { } dir
        && GameDataLocator.OtherGameDir(dir) is { } fs ? GameData.Open(fs, dir) : null;

    [Fact]
    public void FireLampsFollowGodotsRule()
    {
        // A 216/005 flame 128 u tall: intensity clamp(128/450) = 0.28, range 1152, light at 0.35 h.
        var lamps = EmissiveLamps.Fires([new FireSprite(27653, new Vector3(10, 20, 30), 128f, 7), new FireSprite(1, Vector3.Zero, 100f, 0)]);
        var f = Assert.Single(lamps);
        Assert.True(f.Flicker);
        Assert.Equal(LightFalloff.EnergyAtRef(128f / 450f), f.Energy, 1e-3f);
        Assert.Equal(1152f, f.Range, 1e-3f);
        Assert.Equal(20f + 128f * 0.35f, f.Position.Y, 1e-3f);
        // A burning barrel: flame base 0.55 h, then h 0.6, intensity floored at 0.18.
        var barrel = Assert.Single(EmissiveLamps.Fires([new FireSprite(26626, Vector3.Zero, 100f, 1)]));
        Assert.Equal(55f + 60f * 0.35f, barrel.Position.Y, 1e-3f);
        Assert.Equal(LightFalloff.EnergyAtRef(0.18f), barrel.Energy, 1e-3f);
        Assert.Equal(540f, barrel.Range, 1e-3f);
        Assert.InRange(EmissiveLamps.FlickerScale(1.3f, f.Phase), 0.64f, 1.0f);
    }

    /// <summary>Godot logged 68 lamps on MAP.214 (2026-09-05). Here the classifier reads the record
    /// 1:1 and the corridor strips 448/4-5 are 22.9 % lit, over EMISSION_MAX 0.22, so only the CORB
    /// corridors, consoles, buttons and doors light; with the cap at 0.25 the CORA strips join.</summary>
    [EmissionDataFact]
    public void RealMapsGiveLamps()
    {
        var data = EmissionClassifierTests.Data()!;
        foreach (var number in new[] { 214, 211, 210 })
        {
            var map = data.LoadMap(number)!;
            var lamps = EmissiveLamps.ForMap(data, map, number);
            var art = lamps.Where(l => !l.Flicker).ToList();
            var fires = lamps.Count(l => l.Flicker);
            var relaxed = EmissiveLamps.ForMap(data, map, number, maxFraction: 0.25f).Where(l => !l.Flicker).ToList();
            output.WriteLine($"MAP.{number}: {art.Count} art lamps ({art.Count(l => l.Lifted)} from walls, lifted), {fires} fire lamps; " +
                $"lit cap 0.25: {relaxed.Count} art lamps ({relaxed.Count(l => l.Lifted)} lifted)");
            Assert.All(lamps, l => Assert.InRange(l.Range, EmissiveLamps.FireRangeMin, EmissiveLamps.RangeMax));
            if (number == 214)
            {
                Assert.InRange(art.Count, 10, EmissiveLamps.MaxLamps);
                Assert.True(relaxed.Count > art.Count);
            }

            // Determinism: the same map gives the same lamps.
            Assert.Equal(lamps, EmissiveLamps.ForMap(data, map, number));
        }
    }
}
