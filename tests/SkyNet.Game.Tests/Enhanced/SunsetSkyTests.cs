using System.Numerics;
using System.Text;
using RetroEng.Content;
using RetroEng.Json;
using RetroEng.Render;
using SkyNet.Data;
using SkyNet.Game.Enhanced;
using SkyNet.Game.Menu;

namespace SkyNet.Game.Tests.Enhanced;

/// <summary>The enhanced dusk sky: its gradient and glow, the haze matching its horizon, its mesh,
/// its clouds, and the mod table it is read from.</summary>
public class SunsetSkyTests
{
    private static readonly SunsetLook Look = SunsetLook.Defaults;
    private static readonly Vector3 Sun = SunsetSky.SunOf(new Vector3(-1f, -0.5f, 0f), Vector3.UnitY, Look.SunElevation);

    private static Vector3 Way(float headingDeg, float elevationDeg)
    {
        float h = headingDeg * MathF.PI / 180f, e = elevationDeg * MathF.PI / 180f;
        return new Vector3(MathF.Sin(h) * MathF.Cos(e), MathF.Sin(e), MathF.Cos(h) * MathF.Cos(e));
    }

    private static Vector3 Linear(Vector3 c) => new(Srgb.ToLinear(c.X), Srgb.ToLinear(c.Y), Srgb.ToLinear(c.Z));

    [Fact]
    public void Sun_takes_the_lights_heading_at_its_own_height()
    {
        Assert.Equal(1f, Sun.Length(), 3);
        Assert.Equal(Look.SunElevation, MathF.Asin(Sun.Y) * 180f / MathF.PI, 2);
        Assert.True(Sun.X > 0.99f);   // light goes −X: the sun stands at +X
        Assert.True(SunsetSky.SunOf(null, new Vector3(0f, -1f, -1f), 3f).Z > 0.99f);
    }

    [Fact]
    public void Away_from_the_sun_the_horizon_is_the_haze()
    {
        // Behind the sun's back, at and under the horizon: exactly the fog's colour (no band).
        var haze = SunsetSky.HazeOf(Look);
        var horizon = Linear(new Vector3(haze.X, haze.Y, haze.Z));
        foreach (var e in new[] { 0f, -5f, -60f })
        {
            var c = SunsetSky.ColourAt(Look, Way(-90f, e), Sun);
            Assert.True(Vector3.Distance(horizon, c) < 1e-3f, $"{e}: {c} vs {horizon}");
        }
    }

    [Fact]
    public void Sky_darkens_up_to_the_zenith()
    {
        float Luma(float e) => Vector3.Dot(SunsetSky.ColourAt(Look, Way(-90f, e), Sun), new Vector3(0.2126f, 0.7152f, 0.0722f));
        Assert.True(Luma(0f) > Luma(SunsetSky.LowElevation));
        Assert.True(Luma(SunsetSky.LowElevation) > Luma(45f));
        Assert.True(Luma(45f) > Luma(89f));
    }

    [Fact]
    public void Glow_is_brightest_at_the_sun_and_none_under_the_horizon()
    {
        var atSun = SunsetSky.ColourAt(Look, Sun, Sun);
        var aside = SunsetSky.ColourAt(Look, Way(90f - 60f, Look.SunElevation), Sun);
        var behind = SunsetSky.ColourAt(Look, Way(-90f, Look.SunElevation), Sun);
        Assert.True(atSun.X > aside.X && aside.X > behind.X);

        var under = SunsetSky.ColourAt(Look, Way(90f, -5f), Sun);
        var haze = SunsetSky.HazeOf(Look);
        Assert.True(Vector3.Distance(Linear(new Vector3(haze.X, haze.Y, haze.Z)), under) < 1e-3f);
    }

    [Fact]
    public void Mesh_is_a_closed_sphere_round_the_eye()
    {
        var (vertices, indices) = SunsetSky.Mesh(Look, Sun);
        Assert.Equal(2 + (SunsetSky.Rings - 1) * SunsetSky.Segments, vertices.Length);
        Assert.Equal(SunsetSky.Segments * 6 * (SunsetSky.Rings - 1), indices.Length);
        Assert.All(vertices, v => Assert.Equal(SunsetSky.Radius, v.Position.Length(), 1));
        Assert.All(indices, i => Assert.InRange(i, 0, vertices.Length - 1));
        Assert.Equal(Sun * SunsetSky.Radius, vertices[0].Position);
    }

    [Fact]
    public void Clouds_lit_from_the_suns_side_and_none_without_cover()
    {
        var deck = SunsetSky.CloudsOf(Look, Sun);
        Assert.NotNull(deck);
        Assert.Equal(Look.Cover, deck.Coverage);
        Assert.True(Vector3.Dot(deck.UnderlightWay, Vector3.UnitX) > 0.99f);
        Assert.Null(SunsetSky.CloudsOf(Look with { Cover = 0f }, Sun));
    }

    [Fact]
    public void Haze_alone_keeps_the_maps_own_deck()
    {
        var deck = SunsetSky.CloudsOf(Look, Sun)!;
        var lit = new Lighting(-Sun, Vector3.One, Vector3.One, Vector3.One, ShadowQuality.Off, 0f, 0f, Vector4.Zero, 0f) { Clouds = deck };
        var air = new AirView { Haze = HazeAirTable.Defaults };
        var hazed = WeatherSky.Clouded(lit, lit, air, null, 0f, Vector3.Zero, 0f, ParticleLevel.Low);
        Assert.Equal(deck.Coverage, hazed.Clouds!.Coverage);
        Assert.True(hazed.Clouds.Horizon.W > 0f);
    }

    [Fact]
    public void Table_round_trips_and_covers_the_dusk_decades()
    {
        var file = SunsetTable.Base;
        Assert.Equal(Level.DuskMaps, file.Rows.Select(r => r.Map));
        var read = TableSchemas.Skies.Read(JsonItem.Parse(Encoding.UTF8.GetBytes(TableSchemas.Skies.Write(file).ToString())));
        Assert.Equal(file.Rows.ToArray(), read.Rows.ToArray());
        Assert.Equal(Look, SunsetTable.Of(null).For(253));
    }

    [Fact]
    public void Mod_row_overrides_a_map_its_decade_keeps_the_rest()
    {
        var root = Directory.CreateTempSubdirectory("skynet-skies-").FullName;
        try
        {
            Directory.CreateDirectory(Path.Combine(root, "tables"));
            var row = SunsetTable.RowOf(251, Look with { Cover = 0.9f, SunElevation = 10f });
            var json = $"{{\"rows\": [{TableSchemas.Skies.Write(SunsetTable.Base with { Rows = [row] }).Member("rows").Items[0]}]}}";
            File.WriteAllText(Path.Combine(root, "tables", "skies.json"), json);
            using var vfs = new VirtualFileSystem();
            vfs.Mount(new FolderMount(root), 0, "m0");

            var table = SunsetTable.Of(vfs);
            Assert.Equal(0.9f, table.For(251).Cover);
            Assert.Equal(10f, table.For(251).SunElevation);
            Assert.Equal(Look, table.For(250));
        }
        finally
        {
            Directory.Delete(root, true);
        }
    }
}
