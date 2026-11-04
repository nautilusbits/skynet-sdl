using RetroEng.Content;
using SkyNet.Formats;
using SkyNet.Game.Baking;
using SkyNet.Game.Storage;

namespace SkyNet.Game.Tests.Storage;

/// <summary>Generated files an older build kept in the mods folder or beside the game move into
/// cache/ once; the baked maps keep mounting from there.</summary>
public sealed class CacheMigrationTests : IDisposable
{
    private readonly string _dir = Directory.CreateTempSubdirectory("skynet-cache-").FullName;

    public void Dispose() => Directory.Delete(_dir, recursive: true);

    private string Write(string relative, string text)
    {
        var path = Path.Combine(_dir, relative);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, text);
        return path;
    }

    /// <summary>A file moved onto itself (the settings folder beside the exe) stays.</summary>
    [Fact]
    public void AFileMovedOntoItselfStays()
    {
        var file = Write("SkyNetEdit.json", "{}");

        Assert.False(CacheMigration.Move(file, Path.Combine(_dir, ".", "SkyNetEdit.json"), CacheFile.Personal));
        Assert.Equal("{}", File.ReadAllText(file));
    }

    [Fact]
    public void OldFileMovesIntoTheCache()
    {
        var from = Write("console-history.txt", "fps");
        var to = Path.Combine(_dir, "cache", "console-history.txt");

        Assert.True(CacheMigration.Move(from, to, CacheFile.Personal));

        Assert.False(File.Exists(from));
        Assert.Equal("fps", File.ReadAllText(to));
        // Once: nothing left to move.
        Assert.False(CacheMigration.Move(from, to, CacheFile.Personal));
    }

    [Theory]
    [InlineData(CacheFile.Personal)]
    [InlineData(CacheFile.Rebuildable)]
    public void NewerCopyWinsAndTheOlderGoes(CacheFile kind)
    {
        // The cache's copy newer: it stays.
        var from = Write("SkyNetEdit.json", "old");
        var to = Write(Path.Combine("cache", "SkyNetEdit.json"), "new");
        File.SetLastWriteTimeUtc(from, DateTime.UtcNow.AddHours(-1));

        Assert.False(CacheMigration.Move(from, to, kind));
        Assert.False(File.Exists(from));
        Assert.Equal("new", File.ReadAllText(to));

        // An older build wrote again after the move: its copy is newer and replaces the cache's.
        Write("SkyNetEdit.json", "newest");
        File.SetLastWriteTimeUtc(to, DateTime.UtcNow.AddHours(-1));

        Assert.True(CacheMigration.Move(from, to, kind));
        Assert.False(File.Exists(from));
        Assert.Equal("newest", File.ReadAllText(to));
    }

    [Fact]
    public void AppDataHistoryIsCopiedStraightIntoTheCache()
    {
        var from = Write(Path.Combine("appdata", "console-history.txt"), "fps");
        var to = Path.Combine(_dir, "game");

        Assert.Equal(1, SkyNet.Game.Menu.SettingsStore.CopyFromAppData(Path.GetDirectoryName(from)!, to));

        Assert.Equal("fps", File.ReadAllText(Path.Combine(to, "cache", "console-history.txt")));
        Assert.False(File.Exists(Path.Combine(to, "console-history.txt")));
        // Once: the next start copies nothing.
        Assert.Equal(0, SkyNet.Game.Menu.SettingsStore.CopyFromAppData(Path.GetDirectoryName(from)!, to));
    }

    [Fact]
    public void ModsScreenStillListsTheSystemModFromTheCache()
    {
        Write(Path.Combine("cache", "skynet.zip"), "baked maps");
        Write(Path.Combine("mods", "tex", "textures", "a.png"), "png");

        var mods = SkyNet.Game.Mods.ModCatalog.Scan(Path.Combine(_dir, "mods"), Path.Combine(_dir, "cache"), ["skynet", "shock"]);

        Assert.Equal(["skynet", "tex"], mods.Select(m => m.Name));
        Assert.True(mods[0].System && mods[0].Zip && !mods[0].Folder);
        Assert.False(mods[1].System);
    }

    [Fact]
    public void OlderBuildsBakedMapsMoveToTheCacheAndStillMount()
    {
        var dir = Environment.GetEnvironmentVariable("SKYNET_DATA");
        if (string.IsNullOrWhiteSpace(dir) || !GameDataLocator.HasData(dir) || GameData.Open(dir) is not { } data)
        {
            return;
        }

        // An older build's user folder: the system ZIP and its stamp in mods/.
        var mods = Path.Combine(_dir, "mods");
        var cache = Path.Combine(_dir, "cache");
        int[] maps = [211];
        using (var old = new BlockStore(mods, cache, data))
        {
            Assert.Equal(1, old.Bake(maps));
            Assert.Empty(old.Stale(only: maps));
            old.Unmount();
            Directory.CreateDirectory(mods);
            File.Move(old.ZipPath, Path.Combine(mods, "skynet.zip"));
        }

        using var store = new BlockStore(mods, cache, data);

        // A skynet.zip without the stamp beside it is no baked ZIP of ours: it stays.
        Assert.Equal(0, store.MoveFromMods());
        Assert.True(File.Exists(Path.Combine(mods, "skynet.zip")));
        File.Move(Path.Combine(cache, "skynet.zip.stamp"), Path.Combine(mods, "skynet.zip.stamp"));

        // The start's check moves them: nothing to bake again, no system file left in mods/.
        Assert.Empty(store.Stale(only: maps));
        Assert.True(File.Exists(Path.Combine(cache, "skynet.zip")));
        Assert.True(File.Exists(Path.Combine(cache, "skynet.zip.stamp")));
        Assert.Empty(Directory.EnumerateFileSystemEntries(mods));
        Assert.Empty(new ContentRoot(mods).Discover());

        store.Mount();
        Assert.NotNull(store.Load(211));
    }
}
