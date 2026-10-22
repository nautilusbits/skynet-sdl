namespace SkyNet.Formats.Tests;

/// <summary>
/// The heightmap fit for every outdoor map of SkyNET (and of Future Shock, when its data sits beside
/// SkyNET's) compared with the Godot port's LevelLoader._best_fit_wld
/// (GodotReference/godot_dump_wldfit.txt: map, chosen WLD, points on the ground, points).
/// </summary>
public class WldFitTests
{
    /// <summary>A fact skipped unless Future Shock's data sits beside SKYNET_DATA's.</summary>
    private sealed class ShockDataFactAttribute : FactAttribute
    {
        public ShockDataFactAttribute()
        {
            if (TestData.Dir is not { } dir || GameDataLocator.OtherGameDir(dir) == null)
            {
                Skip = $"no Future Shock data beside {TestData.EnvVar}'s SkyNET folder";
            }
        }
    }

    static readonly string[] Reference = File.ReadAllLines(Path.Combine(AppContext.BaseDirectory, "GodotReference", "godot_dump_wldfit.txt"));

    static void Compare(GameData gd, string archive)
    {
        var wlds = gd.WldCandidates().ToList();
        var refs = Reference.Select(l => l.Split(' ')).Where(l => l[1] == archive).ToList();
        Assert.NotEmpty(refs);
        using var a = gd.OpenMapArchive();
        foreach (var r in refs)
        {
            var map = MapFile.Parse(a.Read(r[2])!)!;
            Assert.True(map.IsOutdoor);
            var pts = WldFit.GroundPoints(map);
            var fit = WldFit.BestFit(map, wlds);
            Assert.Equal(int.Parse(r[5]), pts.Count);
            if (r[3] == "-")
                Assert.Null(fit);
            else
                Assert.Equal(new WldFitResult(r[3], int.Parse(r[4]), int.Parse(r[5])), fit);
        }
    }

    [DataFact]
    public void Every_skynet_outdoor_map_fits_as_in_godot()
    {
        var gd = TestData.Open();
        Compare(gd, "MDMDMAP2.BSA");
        // Every SkyNET outdoor map ships its own heightmap, so the fit is never used there.
        Assert.All(Reference.Select(l => l.Split(' ')).Where(l => l[1] == "MDMDMAP2.BSA"),
            r => Assert.Equal(r[2][4..], gd.LoadWldFor(int.Parse(r[2][4..]))!.Value.Suffix));
    }

    [ShockDataFact]
    public void Future_shock_maps_without_a_wld_borrow_the_best_fit()
    {
        var fs = GameData.Open(GameDataLocator.OtherGameDir(TestData.Dir!)!)!;
        Assert.Equal(GameKind.FutureShock, fs.Game);
        Compare(fs, "MDMDMAPS.BSA");
        Assert.Null(fs.LoadWld(40));
        Assert.Null(fs.LoadWld(80));
        Assert.Equal("030", fs.LoadWldFor(40)!.Value.Suffix);
        Assert.Equal("170", fs.LoadWldFor(80)!.Value.Suffix);
    }
}
