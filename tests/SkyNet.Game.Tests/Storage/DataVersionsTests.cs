using SkyNet.Formats;
using SkyNet.Game.Storage;
using SkyNet.Game.Tests.Audio;

namespace SkyNet.Game.Tests.Storage;

/// <summary>The data version check (issue #20): archives fingerprinted, the hash cache, the
/// comparison with the known releases.</summary>
public sealed class DataVersionsTests : IDisposable
{
    private readonly string _dir = Directory.CreateTempSubdirectory("skynet-dataver-").FullName;

    public void Dispose() => Directory.Delete(_dir, recursive: true);

    private string Cache => Path.Combine(_dir, "cache", DataVersions.CacheName);

    private string Data(params (string Name, string Text)[] files)
    {
        var dir = Path.Combine(_dir, "data");
        Directory.CreateDirectory(dir);
        foreach (var (name, text) in files)
        {
            File.WriteAllText(Path.Combine(dir, name), text);
        }

        return dir;
    }

    private static DataFile F(string name, string hash) => new(name, 1, hash);

    [AudioDataFact]
    public void The_owners_skynet_data_is_known()
    {
        var check = DataVersions.Check(AudioTestData.Dir!, GameKind.SkyNet, Cache);

        Assert.True(check.Known, string.Join(", ", check.Differs));
        Assert.Contains(check.Files, f => f.Name == GameDataLocator.ProbeSkyNet);
    }

    [AudioDataFact]
    public void The_owners_future_shock_data_is_known_when_beside_skynet()
    {
        if (GameDataLocator.OtherGameDir(AudioTestData.Dir!) is not { } shock)
        {
            return;
        }

        var check = DataVersions.Check(shock, GameKind.FutureShock, Cache);

        Assert.True(check.Known, string.Join(", ", check.Differs));
    }

    [Fact]
    public void Unknown_data_names_every_archive_and_skips_other_files()
    {
        var dir = Data(("MDMDMAP2.BSA", "map"), ("mdmdobjs.bsa", "objs"), ("WLD.210", "wld"));

        var check = DataVersions.Check(dir, GameKind.SkyNet, Cache);

        Assert.False(check.Known);
        Assert.Equal(["MDMDMAP2.BSA", "MDMDOBJS.BSA"], check.Files.Select(f => f.Name));
        Assert.Equal(3, check.Files[0].Size);
        Assert.Equal("60be9861750facbfad8758254a2f76c0cfe78d54459a3bc187d49b1401fcd8e8", check.Files[0].Sha256);
        // Both changed, and the release's other six missing but the optional MDMDHRES.BSA.
        Assert.Equal(7, check.Differs.Count);
        Assert.Contains("MDMDMAP2.BSA", check.Differs);
        Assert.Contains("MDMDSFXS.BSA", check.Differs);
    }

    [Fact]
    public void A_file_is_hashed_once_until_it_changes()
    {
        var dir = Data(("MDMDMAP2.BSA", "aaaa"));
        var path = Path.Combine(dir, "MDMDMAP2.BSA");
        var first = DataVersions.Check(dir, GameKind.SkyNet, Cache).Files[0].Sha256;
        var stamp = File.GetLastWriteTimeUtc(path);

        // Same size and time: the cached hash stands, the file is not read.
        File.WriteAllText(path, "bbbb");
        File.SetLastWriteTimeUtc(path, stamp);
        Assert.Equal(first, DataVersions.Check(dir, GameKind.SkyNet, Cache).Files[0].Sha256);

        // A new time: hashed again.
        File.SetLastWriteTimeUtc(path, stamp.AddMinutes(1));
        Assert.NotEqual(first, DataVersions.Check(dir, GameKind.SkyNet, Cache).Files[0].Sha256);
    }

    [Fact]
    public void A_broken_cache_file_is_ignored()
    {
        var dir = Data(("MDMDMAP2.BSA", "aaaa"));
        Directory.CreateDirectory(Path.GetDirectoryName(Cache)!);
        File.WriteAllText(Cache, "garbage\nmore\tgarbage\n");

        Assert.Single(DataVersions.Check(dir, GameKind.SkyNet, Cache).Files);
    }

    // A cache line as HashCache writes it: game, name, size, write time, hash.
    private static string Line(GameKind game, string path, string hash)
    {
        var info = new FileInfo(path);
        return $"{game}\t{info.Name}\t{info.Length}\t{info.LastWriteTimeUtc.Ticks}\t{hash}";
    }

    private static readonly string FakeHash = new('a', 64);

    [Fact]
    public void The_cache_keys_by_game_and_name_never_by_path()
    {
        var dir = Data(("MDMDMAP2.BSA", "aaaa"));
        DataVersions.Check(dir, GameKind.SkyNet, Cache);

        var text = File.ReadAllText(Cache);
        Assert.DoesNotContain(_dir, text, StringComparison.OrdinalIgnoreCase);
        Assert.StartsWith("SkyNet\tMDMDMAP2.BSA\t4\t", text);

        // A well-formed line is taken as is.
        File.WriteAllText(Cache, Line(GameKind.SkyNet, Path.Combine(dir, "MDMDMAP2.BSA"), FakeHash));
        Assert.Equal(FakeHash, DataVersions.Check(dir, GameKind.SkyNet, Cache).Files[0].Sha256);
    }

    [Fact]
    public void A_hash_that_is_not_64_lowercase_hex_is_dropped()
    {
        var dir = Data(("MDMDMAP2.BSA", "map"));
        var path = Path.Combine(dir, "MDMDMAP2.BSA");
        Directory.CreateDirectory(Path.GetDirectoryName(Cache)!);

        foreach (var bad in new[] { FakeHash.ToUpperInvariant(), "abc", new string('g', 64) })
        {
            File.WriteAllText(Cache, Line(GameKind.SkyNet, path, bad));
            Assert.Equal("60be9861750facbfad8758254a2f76c0cfe78d54459a3bc187d49b1401fcd8e8",
                DataVersions.Check(dir, GameKind.SkyNet, Cache).Files[0].Sha256);
        }
    }

    [Fact]
    public void An_oversized_cache_file_is_ignored()
    {
        var dir = Data(("MDMDMAP2.BSA", "map"));
        Directory.CreateDirectory(Path.GetDirectoryName(Cache)!);
        File.WriteAllText(Cache, Line(GameKind.SkyNet, Path.Combine(dir, "MDMDMAP2.BSA"), FakeHash) + "\n" + new string('#', 70_000));

        Assert.NotEqual(FakeHash, DataVersions.Check(dir, GameKind.SkyNet, Cache).Files[0].Sha256);
    }

    [Fact]
    public void Saving_keeps_this_runs_entries_and_the_other_games()
    {
        var dir = Data(("MDMDMAP2.BSA", "a"), ("MDMDOBJS.BSA", "b"));
        DataVersions.Check(dir, GameKind.FutureShock, Cache);
        DataVersions.Check(dir, GameKind.SkyNet, Cache);

        File.Delete(Path.Combine(dir, "MDMDOBJS.BSA"));
        DataVersions.Check(dir, GameKind.SkyNet, Cache);

        var lines = File.ReadAllLines(Cache);
        Assert.Single(lines, l => l.StartsWith("SkyNet\t", StringComparison.Ordinal));
        Assert.Equal(2, lines.Count(l => l.StartsWith("FutureShock\t", StringComparison.Ordinal)));
    }

    [Fact]
    public void Oversized_and_surplus_archives_are_skipped_and_differ()
    {
        // MDMD00..MDMD30, then MDMDBIG (too large) fill the cap; MDMDZZ is one over it.
        var dir = Data([.. Enumerable.Range(0, DataVersions.MaxArchives - 1).Select(i => ($"MDMD{i:D2}.BSA", "x")), ("MDMDZZ.BSA", "z")]);
        using (var big = File.Create(Path.Combine(dir, "MDMDBIG.BSA")))
        {
            big.SetLength(DataVersions.MaxArchiveSize + 1);
        }

        var check = DataVersions.Check(dir, GameKind.SkyNet, Cache);

        Assert.Equal(DataVersions.MaxArchives - 1, check.Files.Count);
        Assert.Equal(["MDMDBIG.BSA", "MDMDZZ.BSA"], check.Skipped);
        Assert.Contains("MDMDBIG.BSA", check.Differs);
        Assert.Contains("MDMDZZ.BSA", check.Differs);
    }

    [Fact]
    public void Differs_lists_changed_missing_and_extra_files_of_the_nearest_release()
    {
        DataFile[] near = [F("A.BSA", "1"), F("B.BSA", "2"), F("C.BSA", "3")];
        DataFile[] far = [F("A.BSA", "9"), F("B.BSA", "9"), F("C.BSA", "9")];
        DataFile[] found = [F("A.BSA", "1"), F("B.BSA", "x"), F("D.BSA", "4")];

        Assert.Equal(["B.BSA", "C.BSA", "D.BSA"], DataVersions.Differs(found, [far, near]));
        Assert.Empty(DataVersions.Differs(near, [far, near]));
        Assert.Equal(["A.BSA", "B.BSA", "C.BSA"], DataVersions.Differs(near, []));
    }

    // Players' archives, from their logs (#41, #42): every one of them saw UNKNOWN GAME DATA.
    private static readonly DataFile SkyNetImages =
        new("MDMDIMGS.BSA", 5424890, "590aa45d52b272b50c732ec809b8484603e9c83a78d09b1f765c39ca6341a878");
    private static readonly DataFile ShockBriefs =
        new("MDMDBRIF.BSA", 171857, "1ace67ac45bf487c78c62826cc8f87573b3df54f28306613fcb55fa35c6d3a44");
    private static readonly DataFile ShockBriefsB =
        new("MDMDBRIF.BSA", 171857, "f8c28ee2793d828def715377334d3f5c66e2821661ffb7e900423312d5af7298");

    // A known release of game with one archive swapped for the player's.
    private static List<DataFile> Swapped(GameKind game, DataFile file) =>
        KnownData.Of(game)[0].Select(f => f.Name == file.Name ? file : f).ToList();

    [Fact]
    public void The_players_skynet_release_is_known()
    {
        Assert.Empty(DataVersions.Differs(Swapped(GameKind.SkyNet, SkyNetImages), KnownData.Of(GameKind.SkyNet)));
    }

    [Fact]
    public void Future_shock_before_v130_is_known()
    {
        Assert.Empty(DataVersions.Differs(Swapped(GameKind.FutureShock, ShockBriefs), KnownData.Of(GameKind.FutureShock)));
        Assert.Empty(DataVersions.Differs(Swapped(GameKind.FutureShock, ShockBriefsB), KnownData.Of(GameKind.FutureShock)));
    }

    [Fact]
    public void The_hires_archive_may_be_missing()
    {
        // MDMDHRES.BSA (the 640 x 480 HUD art) is optional: without it the game plays, low-res.
        DataFile[] release = [F("MDMDMAP2.BSA", "1"), F("MDMDHRES.BSA", "2")];

        Assert.Empty(DataVersions.Differs([F("MDMDMAP2.BSA", "1")], [release]));
        Assert.Equal(["MDMDHRES.BSA"], DataVersions.Differs([F("MDMDMAP2.BSA", "1"), F("MDMDHRES.BSA", "x")], [release]));
    }
}
