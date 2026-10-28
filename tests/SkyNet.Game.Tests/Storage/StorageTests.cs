using SkyNet.Game.Menu;
using SkyNet.Game.Storage;

namespace SkyNet.Game.Tests.Storage;

/// <summary>Android's storage: the remembered folder, the start's plan, picked-folder paths and
/// the moves an older install needs.</summary>
public sealed class StorageTests : IDisposable
{
    private readonly string _dir = Directory.CreateTempSubdirectory("skynet-storage-").FullName;

    public void Dispose() => Directory.Delete(_dir, recursive: true);

    // A desktop-like install: root/gamedata with the map archive.
    private string Install(string name, string data = "gamedata")
    {
        var root = Path.Combine(_dir, name);
        Directory.CreateDirectory(Path.Combine(root, data));
        File.WriteAllBytes(Path.Combine(root, data, "MDMDMAP2.BSA"), []);
        return root;
    }

    private string Write(string path, string text)
    {
        var full = Path.Combine(_dir, path);
        Directory.CreateDirectory(Path.GetDirectoryName(full)!);
        File.WriteAllText(full, text);
        return full;
    }

    [Fact]
    public void NothingYetAsksForAccess()
    {
        var choice = new StorageChoice(Path.Combine(_dir, "app"));

        Assert.Equal(StorageKind.AskAccess, choice.Plan(FolderAccess.Askable, Path.Combine(_dir, "copy")).Kind);
        Assert.Equal(StorageKind.CopyFlow, choice.Plan(FolderAccess.Unavailable, Path.Combine(_dir, "copy")).Kind);
    }

    [Fact]
    public void CopyFoundAtAnyDepth()
    {
        // The old launcher copied the picked root under gamedata: gamedata/gamedata.
        var copy = Install(Path.Combine("files", "gamedata"));
        var choice = new StorageChoice(Path.Combine(_dir, "app"));

        var plan = choice.Plan(FolderAccess.Askable, copy);

        Assert.Equal(StorageKind.Copied, plan.Kind);
        Assert.Equal(Path.Combine(copy, "gamedata"), plan.DataDir);
    }

    [Fact]
    public void ChosenFolderPlaysInPlace()
    {
        var root = Install("SkyNET");
        var choice = new StorageChoice(Path.Combine(_dir, "app"));
        choice.Choose(root);

        var plan = choice.Plan(FolderAccess.Granted, Path.Combine(_dir, "copy"));

        Assert.Equal(StorageKind.InPlace, plan.Kind);
        Assert.Equal(Path.Combine(root, "gamedata"), plan.DataDir);
        Assert.Equal(root, choice.Folder);
    }

    [Fact]
    public void ChosenFolderLostWithoutAccessOrData()
    {
        var root = Install("SkyNET");
        var choice = new StorageChoice(Path.Combine(_dir, "app"));
        choice.Choose(root);

        Assert.Equal(StorageKind.Lost, choice.Plan(FolderAccess.Askable, Path.Combine(_dir, "copy")).Kind);

        Directory.Delete(root, recursive: true);
        Assert.Equal(StorageKind.Lost, choice.Plan(FolderAccess.Granted, Path.Combine(_dir, "copy")).Kind);

        choice.Forget();
        Assert.Null(choice.Folder);
        Assert.Equal(StorageKind.AskAccess, choice.Plan(FolderAccess.Askable, Path.Combine(_dir, "copy")).Kind);
    }

    [Theory]
    [InlineData(TreePaths.ExternalStorage, "primary:SkyNET", "/storage/emulated/0/SkyNET")]
    [InlineData(TreePaths.ExternalStorage, "primary:", "/storage/emulated/0")]
    [InlineData(TreePaths.ExternalStorage, "home:SkyNET", "/storage/emulated/0/Documents/SkyNET")]
    [InlineData(TreePaths.ExternalStorage, "1234-ABCD:Games/SkyNET", "/storage/1234-ABCD/Games/SkyNET")]
    [InlineData(TreePaths.Downloads, "raw:/storage/emulated/0/Download/SkyNET", "/storage/emulated/0/Download/SkyNET")]
    [InlineData(TreePaths.Downloads, "msf:42", null)]
    [InlineData(TreePaths.Downloads, "raw:/storage/../data/data", null)]
    [InlineData("com.example.cloud", "primary:SkyNET", null)]
    public void TreePathResolves(string authority, string document, string? path) =>
        Assert.Equal(path, TreePaths.Resolve(authority, document, "/storage/emulated/0"));

    [Fact]
    public void PrimaryRootFromAppFolder()
    {
        Assert.Equal("/storage/emulated/0", TreePaths.PrimaryRoot("/storage/emulated/0/Android/data/net.example/files"));
        Assert.Null(TreePaths.PrimaryRoot("/data/user/0/net.example/files"));
    }

    [Fact]
    public void UserFilesFollowNewerOnly()
    {
        var from = Path.Combine(_dir, "old");
        var to = Path.Combine(_dir, "new");
        Write(Path.Combine("old", SettingsStore.FileName), "old settings");
        Write(Path.Combine("old", "saves", "slot1.sav"), "save 1");
        Write(Path.Combine("old", "saves", "shock", "slot2.sav"), "save 2");
        Write(Path.Combine("old", "unrelated.txt"), "x");
        var kept = Write(Path.Combine("new", "saves", "slot1.sav"), "newer save 1");
        File.SetLastWriteTimeUtc(kept, DateTime.UtcNow.AddHours(1));

        var copied = StorageMigration.CopyUserFiles(from, to);

        Assert.Equal(2, copied);
        Assert.Equal("old settings", File.ReadAllText(Path.Combine(to, SettingsStore.FileName)));
        Assert.Equal("newer save 1", File.ReadAllText(kept));
        Assert.True(File.Exists(Path.Combine(to, "saves", "shock", "slot2.sav")));
        Assert.False(File.Exists(Path.Combine(to, "unrelated.txt")));
    }

    [Fact]
    public void MoveTreeKeepsTargetAndEmptiesSource()
    {
        var from = Path.Combine(_dir, "internal-mods");
        var to = Path.Combine(_dir, "external-mods");
        Write(Path.Combine("internal-mods", "skynet.zip"), "baked");
        Write(Path.Combine("internal-mods", "skynet-hd", "textures", "a.png"), "a");
        Write(Path.Combine("external-mods", "skynet-hd", "textures", "a.png"), "b");

        var moved = StorageMigration.MoveTree(from, to);

        Assert.Equal(1, moved);
        Assert.Equal("baked", File.ReadAllText(Path.Combine(to, "skynet.zip")));
        Assert.Equal("b", File.ReadAllText(Path.Combine(to, "skynet-hd", "textures", "a.png")));
        Assert.False(Directory.Exists(from));
        Assert.Equal(0, StorageMigration.MoveTree(from, to));
    }

    [Fact]
    public void MoveTreeReplacesHalfCopy()
    {
        // A move killed mid-copy left a short target: it is copied again, not kept.
        var from = Path.Combine(_dir, "internal-mods");
        var to = Path.Combine(_dir, "external-mods");
        Write(Path.Combine("internal-mods", "skynet.zip"), "baked maps");
        Write(Path.Combine("external-mods", "skynet.zip"), "bak");

        var moved = StorageMigration.MoveTree(from, to);

        Assert.Equal(1, moved);
        Assert.Equal("baked maps", File.ReadAllText(Path.Combine(to, "skynet.zip")));
        Assert.False(File.Exists(Path.Combine(to, "skynet.zip.part")));
        Assert.False(Directory.Exists(from));
    }

    [Fact]
    public void MoveTreeKeepsSourceOnFailure()
    {
        // A target that cannot be written (a folder in its place): the source file stays.
        var from = Path.Combine(_dir, "internal-mods");
        var to = Path.Combine(_dir, "external-mods");
        var source = Write(Path.Combine("internal-mods", "skynet.zip"), "baked");
        Directory.CreateDirectory(Path.Combine(to, "skynet.zip"));

        Assert.ThrowsAny<Exception>(() => StorageMigration.MoveTree(from, to));
        Assert.Equal("baked", File.ReadAllText(source));
    }

    [Theory]
    [InlineData("MAP.210", true)]
    [InlineData("..x", true)]
    [InlineData("", false)]
    [InlineData(".", false)]
    [InlineData("..", false)]
    [InlineData("../x", false)]
    [InlineData("a/b", false)]
    [InlineData("a\\b", false)]
    [InlineData("a\0b", false)]
    [InlineData("/etc", false)]
    public void ChildStaysInFolder(string name, bool ok)
    {
        var child = TreePaths.Child(_dir, name);

        Assert.Equal(ok, child != null);
        if (child != null)
        {
            Assert.Equal(Path.Combine(_dir, name), child);
        }
    }

    [Fact]
    public void SameFolderIgnoresSpelling()
    {
        var a = Path.Combine(_dir, "SkyNET");
        Directory.CreateDirectory(a);

        Assert.True(StorageMigration.SameFolder(a + Path.DirectorySeparatorChar, Path.Combine(_dir, "x", "..", "SkyNET")));
        Assert.False(StorageMigration.SameFolder(a, Path.Combine(_dir, "other")));
    }

    [Fact]
    public void WritableProbe()
    {
        Assert.True(StorageChoice.IsWritable(_dir));
        Assert.False(StorageChoice.IsWritable(Path.Combine(_dir, "missing")));
    }
}
