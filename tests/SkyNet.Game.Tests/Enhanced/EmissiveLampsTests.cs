using System.Numerics;
using RetroEng.Render.Lights;
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
