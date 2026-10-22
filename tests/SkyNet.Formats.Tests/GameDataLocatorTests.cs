namespace SkyNet.Formats.Tests;

/// <summary>The data-folder search on throw-away folders holding empty probe files.</summary>
public sealed class GameDataLocatorTests : IDisposable
{
    readonly string _root = Path.Combine(Path.GetTempPath(), "skynet-formats-tests-" + Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); } catch (IOException) { }
    }

    string Make(string sub, params string[] files)
    {
        string d = Path.GetFullPath(Path.Combine(_root, sub));
        Directory.CreateDirectory(d);
        foreach (var f in files) File.WriteAllBytes(Path.Combine(d, f), []);
        return d;
    }

    /// <summary>Same folder: separators normalised, and letter case ignored (a case-insensitive file system finds gamedata as GAMEDATA).</summary>
    static void Same(string? expected, string? actual)
    {
        Assert.NotNull(actual);
        Assert.Equal(Path.GetFullPath(expected!), Path.GetFullPath(actual), ignoreCase: true);
    }

    [Fact]
    public void Finds_files_whatever_their_case()
    {
        string d = Make("lower", "mdmdmap2.bsa", "texture.044");
        Assert.Equal(GameKind.SkyNet, GameDataLocator.GameOf(d));
        Assert.EndsWith("texture.044", GameDataLocator.FindFile(d, "TEXTURE.044"));
        Assert.Null(GameDataLocator.FindFile(d, "TEXTURE.045"));
    }

    [Fact]
    public void Tells_the_games_apart()
    {
        string sky = Make("sky/gamedata", "MDMDMAP2.BSA");
        string fs = Make("sky/shock/GAMEDATA", "MDMDMAPS.BSA");
        Assert.Equal(BsaVariant.SkyNetFull, GameDataLocator.VariantFor(sky));
        Assert.Equal(BsaVariant.FutureShockFull, GameDataLocator.VariantFor(fs));
        Assert.Equal(GameKind.None, GameDataLocator.GameOf(_root));
        var g = GameData.Open(fs)!;
        Assert.Equal("MDMDMAPS.BSA", g.MapArchive);
        Assert.Equal(GameKind.FutureShock, g.Game);
    }

    [Fact]
    public void Locate_order_explicit_remembered_then_beside_the_exe()
    {
        string a = Make("a/gamedata", "MDMDMAP2.BSA");
        string b = Make("b/GAMEDATA", "MDMDMAP2.BSA");
        string exe = Make("b/bin");
        Same(a, GameDataLocator.Locate(a, b, [exe]));
        Same(b, GameDataLocator.Locate(Path.Combine(_root, "nothing"), b, [exe]));
        Same(b, GameDataLocator.Locate(null, null, [exe]));   // parent/GAMEDATA
        Assert.Null(GameDataLocator.Locate(null, null, [_root]));
    }

    [Fact]
    public void Resolve_accepts_the_install_root()
    {
        string data = Make("inst/gamedata", "MDMDMAP2.BSA");
        Same(data, GameDataLocator.ResolveDataDir(Path.Combine(_root, "inst") + Path.DirectorySeparatorChar));
        string fs = Make("fsonly/shock/GAMEDATA", "MDMDMAPS.BSA");
        Same(fs, GameDataLocator.ResolveDataDir(Path.Combine(_root, "fsonly")));
    }

    [Fact]
    public void Future_shock_borrows_skynets_front_end_and_hires_set()
    {
        string sky = Make("game/gamedata", "MDMDMAP2.BSA", "MDMDHRES.BSA", "MDMDIMGS.BSA", "FONT0001.FNT");
        string fs = Make("game/shock/GAMEDATA", "MDMDMAPS.BSA", "MDMDIMGS.BSA", "FONT0001.FNT");
        Same(fs, GameDataLocator.OtherGameDir(sky));
        Same(sky, GameDataLocator.OtherGameDir(fs));

        var g = GameData.Open(fs)!;
        Same(sky, g.SkyNetDir);
        var (hres, hkey) = g.HiresArchive();
        Same(Path.Combine(sky, "MDMDHRES.BSA"), hres);
        Assert.Equal(BsaVariant.SkyNetFull, hkey);
        var (imgs, ikey) = g.FrontArchive("MDMDIMGS.BSA");
        Same(Path.Combine(sky, "MDMDIMGS.BSA"), imgs);
        Assert.Equal(BsaVariant.SkyNetFull, ikey);
        Same(Path.Combine(sky, "FONT0001.FNT"), g.FrontPath("FONT0001.FNT"));

        // Without SkyNET beside it, Future Shock uses its own copies.
        string alone = Make("alone/GAMEDATA", "MDMDMAPS.BSA");
        var h = GameData.Open(alone)!;
        Assert.Null(h.SkyNetDir);
        Assert.Equal((Path.Combine(alone, "MDMDHRES.BSA"), BsaVariant.FutureShockFull), h.HiresArchive());

        // SkyNET always reads its own.
        var s = GameData.Open(sky)!;
        Assert.Equal((Path.Combine(sky, "MDMDHRES.BSA"), BsaVariant.SkyNetFull), s.HiresArchive());
    }
}
