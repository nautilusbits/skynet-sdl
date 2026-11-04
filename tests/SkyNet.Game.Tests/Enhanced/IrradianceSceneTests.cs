using System.Numerics;
using RetroEng.Render;
using SkyNet.Formats;
using SkyNet.Game.Baking;
using SkyNet.Game.Enhanced;
using SkyNet.Game.Tests.Audio;

namespace SkyNet.Game.Tests.Enhanced;

/// <summary>The irradiance bake's input: albedo, glow, winding, the volume's box and spacing, the
/// lamps and the sky per preset.</summary>
public sealed class IrradianceSceneTests
{
    /// <summary>0 black (transparent), 1 white, 2 mid grey, the rest dark.</summary>
    private static Palette TestPalette()
    {
        var rgb = new byte[Palette.RawSize];
        for (var i = 3; i < 256; i++)
        {
            (rgb[i * 3], rgb[i * 3 + 1], rgb[i * 3 + 2]) = (20, 20, 30);
        }

        (rgb[3], rgb[4], rgb[5]) = (255, 255, 255);
        (rgb[6], rgb[7], rgb[8]) = (128, 128, 128);
        return Palette.Parse(rgb) ?? throw new InvalidOperationException();
    }

    [Fact]
    public void AlbedoIsTheMeanLinearColourOfTheOpaqueTexels()
    {
        // White and mid grey; the transparent texels (0) count for nothing.
        var albedo = IrradianceScene.Albedo([1, 2, 0, 0], TestPalette());

        var expected = (1f + Srgb.ToLinear(128 / 255f)) / 2f;
        Assert.Equal(expected, albedo.X, 1e-5f);
        Assert.Equal(albedo.X, albedo.Z, 1e-6f);
        Assert.Equal(Vector3.Zero, IrradianceScene.Albedo([0, 0], TestPalette()));
    }

    [Fact]
    public void OnlyLampArtGlows()
    {
        var palette = TestPalette();
        var table = EmissionClassifier.LitTable(palette);
        var wall = Enumerable.Repeat((byte)3, 100).ToArray();
        var strip = (byte[])wall.Clone();
        strip[0] = strip[1] = 1;

        Assert.Equal(Vector3.Zero, IrradianceScene.Emission(wall, palette, table));
        // Two white texels of 100: a glow of 2 % white.
        Assert.Equal(0.02f * IrradianceScene.EmissionGain, IrradianceScene.Emission(strip, palette, table).X, 1e-5f);
    }

    [Fact]
    public void TrianglesFaceTheirDrawnSide()
    {
        var up = Vertex.PackNormal(Vector3.UnitY);
        // Wound so cross(b - a, c - a) points down, but drawn facing up: turned over.
        Vertex[] quad = [new(new(0, 0, 0), default, Vertex.White, up), new(new(1, 0, 0), default, Vertex.White, up), new(new(0, 0, 1), default, Vertex.White, up)];

        var (positions, indices) = IrradianceScene.Mesh(quad, [0u, 1u, 2u], null)!.Value;

        var n = Vector3.Cross(positions[indices[1]] - positions[indices[0]], positions[indices[2]] - positions[indices[0]]);
        Assert.True(n.Y > 0f);
        Assert.Null(IrradianceScene.Mesh(quad, [0u, 1u, 2u], _ => false));
    }

    [Fact]
    public void SpacingKeepsTheProbesWithinBudget()
    {
        // A small room: the closest spacing.
        Assert.Equal(IrradianceScene.MinSpacing, IrradianceScene.SpacingFor(Vector3.Zero, new Vector3(500, 200, 500)));

        // A big outdoor area: wider, a multiple of the step, within the budget and the axis cap.
        var max = new Vector3(40000, 3000, 20000);
        var spacing = IrradianceScene.SpacingFor(Vector3.Zero, max);
        var (x, y, z) = IrradianceScene.ProbesOf(max, spacing);
        Assert.Equal(0f, spacing % IrradianceScene.SpacingStep);
        Assert.InRange((long)x * y * z, 1, IrradianceScene.ProbeBudget);
        Assert.InRange(Math.Max(x, Math.Max(y, z)), 2, IrradianceVolume.MaxProbesPerAxis);
    }

    [Fact]
    public void OutdoorVolumeHoldsTheGroundAroundTheBuiltArea()
    {
        var ground = new Terrain.TerrainGeometry(
            [new(new(0, -300, 0), default), new(new(100, 800, 100), default), new(new(99999, -5000, 0), default)], [0, 1, 2], []);

        var (min, max) = IrradianceScene.Outdoor(new Vector3(0, 0, 0), new Vector3(100, 200, 100), ground);

        Assert.Equal(-IrradianceScene.OutdoorMargin, min.X);
        Assert.Equal(100 + IrradianceScene.OutdoorMargin, max.Z);
        // The ground in the footprint reaches the box; the far vertex (outside it) does not.
        Assert.Equal(-300f, min.Y);
        Assert.Equal(800f + IrradianceScene.OutdoorHeadroom, max.Y);
    }

    [Fact]
    public void InteriorsHaveNoSunNightsTheMoon()
    {
        var interior = IrradianceScene.SettingsFor(LightPreset.Interior, Vector3.Zero, Vector3.One * 100, 48f, null);
        var night = IrradianceScene.SettingsFor(LightPreset.Night, Vector3.Zero, Vector3.One * 100, 48f, null);

        Assert.Equal(Vector3.Zero, interior.Sun);
        Assert.True(interior.Sky.X > 0f && interior.Sky.X < LookLighting.IndoorAmbient.X);
        Assert.Equal(LookLighting.MoonLightDirection(), night.SunDirection);
        Assert.Equal(LookLighting.MoonLight * LookLighting.MoonMood, night.Sun);
    }

    /// <summary>The bake stores the light as the frame lights it, unscaled: the frame's ambient
    /// (IrradianceScale) scales the bounce. A bake gained ×8 read back as plain light lit every
    /// interior 8× (the units checked: bounced ≈ 0.1 × direct with albedo ≈ 0.13).</summary>
    [Fact]
    public void TheBakeIsUnscaled()
    {
        var interior = IrradianceScene.SettingsFor(LightPreset.Interior, Vector3.Zero, Vector3.One * 100, 48f, null);
        Assert.Equal(LookLighting.IndoorAmbient * (LookLighting.InteriorAmbientMood * IrradianceScene.InteriorSkyShare), interior.Sky);
    }

    [AudioDataFact]
    public void TheBakesLampsAreTheFramesLamps()
    {
        var data = AudioTestData.Open();
        var map = data.LoadMap(242)!;
        var block = MapBaker.Bake(data, 242)!;
        var input = IrradianceScene.Build(data, block, map, 242)!;

        // The frame's lamps (EnhancedEffects.Collect) at their steady energies.
        var frame = new List<PointLight>();
        new LevelLights(LevelLights.MapLightsOf(map, data, 242), block.Lamps, 242).AppendSteady(frame);
        Assert.Equal(frame, input.Lamps);
    }

    [Fact]
    public void BakeSeesFiresAtTheirMeanFlicker()
    {
        var art = new LampLight(Vector3.Zero, EmissiveLamps.Colour, 900f, 15f, Flicker: false);
        var fire = new LampLight(Vector3.One, EmissiveLamps.FireColour, 900f, 30f, Flicker: true, Phase: 1f);
        var off = new MapLight(7, Vector3.One, Vector3.One, 800f, 5f, 40, StartsOn: false);
        var lights = new LevelLights([off], [art, fire], overrides: []);
        var steady = new List<PointLight>();

        lights.AppendSteady(steady);

        // The light that starts off is left out; the art lamp as is; the fire at its mean.
        Assert.Equal(2, steady.Count);
        Assert.Equal(15f, steady[0].Energy);
        Assert.Equal(30f * EmissiveLamps.MeanFlicker, steady[1].Energy, 1e-4f);
    }

    [AudioDataFact]
    public void AnInteriorMapBakesFromItsBlock()
    {
        var data = AudioTestData.Open();
        var map = data.LoadMap(242)!;
        var block = MapBaker.Bake(data, 242)!;

        var input = IrradianceScene.Build(data, block, map, 242)!;

        Assert.Equal(LightPreset.Interior, input.Preset);
        Assert.True(input.Triangles > 100 && input.Lamps.Length > 0);
        Assert.All(input.Meshes, m => Assert.InRange(m.Albedo.X, 0f, 1f));
        // The volume holds the map's meshes: every lamp is inside it.
        Assert.All(input.Lamps, l => Assert.True(Vector3.Clamp(l.Position, input.Settings.Min, input.Settings.Max) == l.Position));
        var (x, y, z) = IrradianceScene.ProbesOf(input.Settings.Max - input.Settings.Min, input.Settings.Spacing);
        Assert.InRange((long)x * y * z, 8, IrradianceScene.ProbeBudget);
    }
}
