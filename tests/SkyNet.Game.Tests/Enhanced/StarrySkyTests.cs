using System.Numerics;
using System.Text;
using RetroEng.Content;
using RetroEng.Json;
using SkyNet.Data;
using SkyNet.Game.Enhanced;

namespace SkyNet.Game.Tests.Enhanced;

/// <summary>The enhanced night: its stars, the Milky Way, the moon disc, the clouds and the mod table.</summary>
public class StarrySkyTests
{
    private static readonly NightLook Look = NightLook.Defaults;

    [Fact]
    public void Stars_keep_the_dos_ones_and_add_faint_ones_over_the_horizon()
    {
        (Vector3, Vector3)[] dos = [(Vector3.UnitY, Vector3.One), (Vector3.Normalize(new Vector3(1f, 0.2f, 0f)), new Vector3(1f, 0.9f, 0.8f))];
        var stars = StarrySky.Stars(Look, dos);
        Assert.Equal(dos[0].Item1, stars[0].Way);
        Assert.Equal(dos[1].Item1, stars[1].Way);
        Assert.InRange(stars.Count, 2 + StarrySky.FaintStars / 3, 2 + StarrySky.FaintStars);
        Assert.All(stars, s => Assert.True(s.Way.Y >= 0f && s.Pixels >= 1f));

        // Many faint, a few bright.
        var faint = stars.Skip(2).Count(s => s.Colour.Length() < 0.5f);
        Assert.True(faint > (stars.Count - 2) * 0.8f);
        Assert.Equal(2, StarrySky.Stars(Look with { Stars = 0f }, dos).Count);
    }

    [Fact]
    public void Star_mesh_is_soft_dots_a_pixel_scale_across()
    {
        var stars = StarrySky.Stars(Look, []);
        var (vertices, indices) = StarrySky.StarMesh(stars, 1000f, 2f);
        Assert.Equal(stars.Count * 7, vertices.Length);
        Assert.Equal(stars.Count * 18, indices.Length);
        var rim = Vector3.Distance(vertices[1].Position, vertices[0].Position);
        Assert.Equal(2f * stars[0].Pixels, rim, 3);
        Assert.Equal(0u, vertices[1].Colour >> 24);   // rim: no light
    }

    [Fact]
    public void Band_is_dark_under_the_horizon()
    {
        var (vertices, indices) = StarrySky.BandMesh(Look, 1000f);
        Assert.All(indices, i => Assert.InRange(i, 0, vertices.Length - 1));
        Assert.All(vertices.Where(v => v.Position.Y < 0f), v => Assert.Equal(0u, v.Colour & 0xffffffu));
        Assert.Contains(vertices, v => (v.Colour & 0xffffffu) != 0u);
    }

    [Fact]
    public void Moon_darkens_to_its_limb_and_keeps_its_maria()
    {
        var centre = StarrySky.MoonLight(null, 0, Vector2.Zero);
        var limb = StarrySky.MoonLight(null, 0, new Vector2(0.98f, 0f));
        Assert.True(limb.X < centre.X);

        // A dark texel (a mare) reads darker than a bright one at the same place.
        byte[] dark = [.. Enumerable.Repeat((byte)40, 4)], bright = [.. Enumerable.Repeat((byte)200, 4)];
        Assert.True(StarrySky.MoonLight(dark, 2, Vector2.Zero).X < StarrySky.MoonLight(bright, 2, Vector2.Zero).X);
    }

    [Fact]
    public void Moon_mesh_is_the_disc_then_a_fading_halo()
    {
        var (places, colours, indices) = StarrySky.MoonMesh(Look, null, 0);
        var rings = StarrySky.MoonRings + StarrySky.HaloRings;
        Assert.Equal(1 + rings * StarrySky.MoonSegments, places.Length);
        Assert.Equal(colours.Length, places.Length);
        Assert.All(indices, i => Assert.InRange(i, 0, places.Length - 1));
        Assert.Equal(StarrySky.HaloReach, places[^1].Length(), 3);
        Assert.Equal(0u, colours[^1] & 0xffffffu);   // the halo's edge: no light
    }

    [Fact]
    public void Clouds_lit_on_the_moons_side_and_none_without_cover()
    {
        var deck = StarrySky.CloudsOf(Look, Level.MoonDirection());
        Assert.NotNull(deck);
        Assert.Equal(Look.Cover, deck.Coverage);
        var moon = Level.MoonDirection();
        Assert.True(Vector3.Dot(deck.UnderlightWay, Vector3.Normalize(new Vector3(moon.X, 0f, moon.Z))) > 0.99f);
        Assert.Null(StarrySky.CloudsOf(Look with { Cover = 0f }, moon));
    }

    [Fact]
    public void Table_round_trips_and_a_mod_row_overrides_its_map()
    {
        var file = NightSkyTable.Base;
        var read = TableSchemas.NightSkies.Read(JsonItem.Parse(Encoding.UTF8.GetBytes(TableSchemas.NightSkies.Write(file).ToString())));
        Assert.Equal(file.Rows.ToArray(), read.Rows.ToArray());
        Assert.Equal(Look, NightSkyTable.Of(null).For(213));
        Assert.Equal(Look, NightSkyTable.Of(null).For(605));   // an arena: the defaults

        var root = Directory.CreateTempSubdirectory("skynet-night-").FullName;
        try
        {
            Directory.CreateDirectory(Path.Combine(root, "tables"));
            var row = NightSkyTable.RowOf(230, Look with { Stars = 2f, Cover = 0f });
            var json = $"{{\"rows\": [{TableSchemas.NightSkies.Write(NightSkyTable.Base with { Rows = [row] }).Member("rows").Items[0]}]}}";
            File.WriteAllText(Path.Combine(root, "tables", "night_skies.json"), json);
            using var vfs = new VirtualFileSystem();
            vfs.Mount(new FolderMount(root), 0, "m0");

            var table = NightSkyTable.Of(vfs);
            Assert.Equal(2f, table.For(231).Stars);
            Assert.Equal(0f, table.For(231).Cover);
            Assert.Equal(Look, table.For(210));
        }
        finally
        {
            Directory.Delete(root, true);
        }
    }
}
