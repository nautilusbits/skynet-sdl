using System.Numerics;
using System.Reflection;
using RetroEng.Platform;
using RetroEng.Render;
using SkyNet.Formats;
using SkyNet.Game.Enhanced;
using Face = SkyNet.Game.Enhanced.ReflectionPlacement.Face;

namespace SkyNet.Game.Tests.Enhanced;

/// <summary>The enhanced look's reflection probes: where they go (<see cref="ReflectionPlacement"/>),
/// their cache file (<see cref="ReflectionFiles"/>) and the quality per platform (<see cref="LookLighting.ReflectionsOf"/>).</summary>
public class ReflectionTests
{
    private const float Far = 4000f;

    // A square floor of side 'side' at height y, centred on (x, z), as two triangles' faces.
    private static IEnumerable<Face> Floor(float x, float z, float side, float y = 0f)
    {
        var h = side / 2f;
        var (min, max) = (new Vector3(x - h, y, z - h), new Vector3(x + h, y, z + h));
        yield return new Face(new Vector3(x - h / 3f, y, z - h / 3f), side * side / 2f, min, max);
        yield return new Face(new Vector3(x + h / 3f, y, z + h / 3f), side * side / 2f, min, max);
    }

    [Fact]
    public void A_room_gets_one_probe_an_eye_over_its_floor_under_its_ceiling()
    {
        var floors = Floor(500f, 500f, 1000f).ToList();
        var ceilings = Floor(500f, 500f, 1000f, 300f).ToList();

        var probe = Assert.Single(ReflectionPlacement.Place(floors, ceilings, outdoor: false, Far));

        Assert.InRange(probe.Position.X, 0f, 1000f);
        Assert.InRange(probe.Position.Z, 0f, 1000f);
        Assert.Equal(ReflectionPlacement.EyeHeight, probe.Position.Y);
        Assert.True(probe.Influence.Max.Y >= 300f && probe.Influence.Min.Y <= 0f);
        Assert.True(probe.Influence.Min.X <= 0f && probe.Influence.Max.X >= 1000f);
        Assert.Equal(probe.Influence, probe.Projection);
        Assert.Equal(ReflectionPlacement.Size, probe.Size);
        Assert.Equal(Far, probe.Far);
    }

    [Fact]
    public void Two_rooms_apart_get_a_probe_each_inside_its_own()
    {
        var floors = Floor(0f, 0f, 1000f).Concat(Floor(5000f, 0f, 1000f)).ToList();

        var probes = ReflectionPlacement.Place(floors, [], outdoor: false, Far).OrderBy(p => p.Position.X).ToList();

        Assert.Equal(2, probes.Count);
        Assert.InRange(probes[0].Position.X, -500f, 500f);
        Assert.InRange(probes[1].Position.X, 4500f, 5500f);
        Assert.True(probes[0].Influence.Max.X < probes[1].Influence.Min.X);
    }

    // A ring of floors round a hole (a courtyard, a shaft): the probe stands over a floor, not in the void.
    [Fact]
    public void A_probe_stands_over_a_floor_not_over_a_hole()
    {
        var floors = new[] { (0f, 0f), (300f, 0f), (600f, 0f), (0f, 300f), (600f, 300f), (0f, 600f), (300f, 600f), (600f, 600f) }
            .SelectMany(c => Floor(c.Item1, c.Item2, 300f)).ToList();

        var probe = Assert.Single(ReflectionPlacement.Place(floors, [], outdoor: false, Far));

        Assert.Contains(floors, f => probe.Position.X >= f.Min.X && probe.Position.X <= f.Max.X && probe.Position.Z >= f.Min.Z && probe.Position.Z <= f.Max.Z);
    }

    // Two storeys over one plan: a probe each, at its own floor's eye height.
    [Fact]
    public void Storeys_get_a_probe_each()
    {
        var floors = Floor(0f, 0f, 1000f).Concat(Floor(0f, 0f, 1000f, 600f)).ToList();
        var ceilings = Floor(0f, 0f, 1000f, 400f).Concat(Floor(0f, 0f, 1000f, 1000f)).ToList();

        var probes = ReflectionPlacement.Place(floors, ceilings, outdoor: false, Far).OrderBy(p => p.Position.Y).ToList();

        Assert.Equal(2, probes.Count);
        Assert.Equal(ReflectionPlacement.EyeHeight, probes[0].Position.Y);
        Assert.Equal(600f + ReflectionPlacement.EyeHeight, probes[1].Position.Y);
        Assert.True(probes[0].Influence.Max.Y < 600f);
    }

    [Fact]
    public void A_big_interior_gets_no_more_than_eight()
    {
        var floors = Enumerable.Range(0, 40).SelectMany(i => Floor(i % 8 * 2000f, i / 8 * 2000f, 1000f)).ToList();

        Assert.Equal(ReflectionPlacement.MaxProbes, ReflectionPlacement.Place(floors, [], outdoor: false, Far).Count);
    }

    [Theory]
    [InlineData(3000f, 1)]
    [InlineData(9000f, 2)]
    public void Outdoors_one_or_two_over_the_built_area(float apart, int count)
    {
        var floors = Floor(0f, 0f, 1000f).Concat(Floor(apart, 0f, 1000f)).ToList();

        Assert.Equal(count, ReflectionPlacement.Place(floors, [], outdoor: true, Far).Count);
    }

    [Fact]
    public void No_floor_no_probe() => Assert.Empty(ReflectionPlacement.Place([], [], outdoor: false, Far));

    [Fact]
    public void The_placement_is_the_same_every_time()
    {
        var floors = Enumerable.Range(0, 12).SelectMany(i => Floor(i * 700f % 3100f, i * 1300f % 2900f, 600f)).ToList();

        var a = ReflectionPlacement.Place(floors, [], outdoor: false, Far).Select(p => p.Position);
        var b = ReflectionPlacement.Place(floors, [], outdoor: false, Far).Select(p => p.Position);

        Assert.Equal(a, b);
    }

    // The cache's key: the data, the version, the look and the map each give another file.
    [Fact]
    public void The_cache_path_is_keyed_by_game_data_version_look_and_map()
    {
        var look = ReflectionFiles.LookOf(["skynet-hd"], "High", 100, "High");
        var path = ReflectionFiles.PathOf("cache", GameKind.SkyNet, "cfbd571a1b412888", look, 231).Replace('\\', '/');

        Assert.Equal($"cache/reflections/skynet/cfbd571a1b412888-v{ReflectionFiles.CaptureVersion}/{look}/MAP.231.reprobes", path);
        Assert.NotEqual(look, ReflectionFiles.LookOf([], "High", 100, "High"));
        Assert.NotEqual(look, ReflectionFiles.LookOf(["skynet-hd"], "Low", 100, "High"));
        Assert.NotEqual(look, ReflectionFiles.LookOf(["skynet-hd"], "High", 80, "High"));
        Assert.NotEqual(look, ReflectionFiles.LookOf(["skynet-hd"], "High", 100, "Off"));
        Assert.NotEqual(path, ReflectionFiles.PathOf("cache", GameKind.FutureShock, "cfbd571a1b412888", look, 231).Replace('\\', '/'));
        Assert.Equal(16, look.Length);
    }

    [Fact]
    public void Probes_round_trip_through_the_file()
    {
        ReflectionProbe[] probes = [Probe(new Vector3(1f, 2f, 3f), 0.25f), Probe(new Vector3(-50f, 75f, 900f), 0.5f)];
        using var stream = new MemoryStream();

        ReflectionFiles.Save(stream, probes);
        stream.Position = 0;
        var read = ReflectionFiles.Load(stream);

        Assert.Equal(probes.Length, read.Count);
        for (var i = 0; i < probes.Length; i++)
        {
            Assert.Equal(probes[i].Position, read[i].Position);
            Assert.Equal(probes[i].Influence, read[i].Influence);
            Assert.Equal(probes[i].Size, read[i].Size);
            Assert.Equal(Bytes(probes[i]), Bytes(read[i]));
        }
    }

    private static byte[] Bytes(ReflectionProbe probe)
    {
        using var stream = new MemoryStream();
        probe.Save(stream);
        return stream.ToArray();
    }

    [Fact]
    public void A_file_that_does_not_read_is_a_miss()
    {
        var file = Path.Combine(Path.GetTempPath(), $"probes-{Guid.NewGuid():N}{ReflectionFiles.Extension}");
        try
        {
            File.WriteAllBytes(file, [1, 2, 3, 4, 5, 6, 7, 8, 9]);
            Assert.Null(ReflectionFiles.Read(file));
            Assert.Null(ReflectionFiles.Read(file + ".missing"));

            Assert.True(ReflectionFiles.Write(file, [Probe(Vector3.Zero, 1f)]));
            Assert.Single(ReflectionFiles.Read(file)!);
        }
        finally
        {
            File.Delete(file);
        }
    }

    // Desktop: probes and the screen's reflections; phones: probes alone; SHADOWS Off or DETAIL Low
    // (the cheap look asked for): probes alone too.
    [Theory]
    [InlineData(Os.Windows, ShadowQuality.High, Detail.High, ReflectionQuality.High)]
    [InlineData(Os.Linux, ShadowQuality.Low, Detail.Medium, ReflectionQuality.High)]
    [InlineData(Os.Android, ShadowQuality.High, Detail.High, ReflectionQuality.Low)]
    [InlineData(Os.Windows, ShadowQuality.Off, Detail.High, ReflectionQuality.Low)]
    [InlineData(Os.Windows, ShadowQuality.High, Detail.Low, ReflectionQuality.Low)]
    public void The_quality_follows_the_platform_and_the_cheap_look(Os os, ShadowQuality shadows, Detail detail, ReflectionQuality quality)
    {
        var reflections = LookLighting.ReflectionsOf([Probe(Vector3.Zero, 1f)], os, shadows, detail)!;

        Assert.Equal(quality, reflections.Quality);
        Assert.Single(reflections.Probes);
    }

    [Fact]
    public void No_probes_no_reflections()
    {
        Assert.Null(LookLighting.ReflectionsOf(null, Os.Windows, ShadowQuality.High, Detail.High));
        Assert.Null(LookLighting.ReflectionsOf([], Os.Windows, ShadowQuality.High, Detail.High));
    }

    // A probe of the smallest size filled with one grey (the engine's constructor is internal).
    private static ReflectionProbe Probe(Vector3 position, float grey)
    {
        const int size = ReflectionProbe.MinSize;
        var count = (int)typeof(ReflectionProbe).GetMethod("TexelCount", BindingFlags.NonPublic | BindingFlags.Static)!.Invoke(null, [size])!;
        var texels = new Half[count * 4];
        for (var i = 0; i < texels.Length; i++)
        {
            texels[i] = (Half)(i % 4 == 3 ? 1f : grey);
        }

        var box = new Bounds(position - new Vector3(100f), position + new Vector3(100f));
        var make = typeof(ReflectionProbe).GetConstructor(BindingFlags.NonPublic | BindingFlags.Instance,
            [typeof(Vector3), typeof(Bounds), typeof(Bounds), typeof(int), typeof(Half[])])!;
        return (ReflectionProbe)make.Invoke([position, box, box, size, texels]);
    }

}
