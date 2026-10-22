using SkyNet.Game.Mods;

namespace SkyNet.Game.Tests.Mods;

/// <summary>The mods-folder mirror's plan (Android's mods sync): copy new and changed files,
/// delete what left the source, keep the rest, never touch the system mods' baked maps.</summary>
public sealed class ModMirrorTests
{
    private static readonly string[] System = ["skynet", "shock"];

    private static MirrorPlan Plan(MirrorFile[] source, MirrorFile[] owned, params string[] local) =>
        ModMirror.Plan(source, owned, local.Concat(owned.Select(f => f.Path)).ToHashSet().Contains, System);

    [Fact]
    public void NewFileIsCopied()
    {
        var file = new MirrorFile("props/models/CRATE.glb", 100, 5);

        var plan = Plan([file], []);

        Assert.Equal([file], plan.Copy);
        Assert.Empty(plan.Delete);
        Assert.Empty(plan.Keep);
    }

    [Theory]
    [InlineData(101, 5)]
    [InlineData(100, 6)]
    public void ChangedFileIsCopied(long size, long modified)
    {
        var was = new MirrorFile("props/mod.json", 100, 5);
        var now = was with { Size = size, Modified = modified };

        var plan = Plan([now], [was]);

        Assert.Equal([now], plan.Copy);
        Assert.Empty(plan.Keep);
    }

    [Fact]
    public void UnchangedFileIsKept()
    {
        var file = new MirrorFile("props/mod.json", 100, 5);

        var plan = Plan([file], [file]);

        Assert.Empty(plan.Copy);
        Assert.Empty(plan.Delete);
        Assert.Equal([file], plan.Keep);
    }

    [Fact]
    public void UnchangedButMissingFileIsCopiedAgain()
    {
        var file = new MirrorFile("props/mod.json", 100, 5);

        var plan = ModMirror.Plan([file], [file], _ => false, System);

        Assert.Equal([file], plan.Copy);
    }

    [Fact]
    public void RemovedFileIsDeleted()
    {
        var gone = new MirrorFile("old/textures/TEXTURE.001/002.png", 10, 1);
        var stays = new MirrorFile("props/mod.json", 100, 5);

        var plan = Plan([stays], [gone, stays]);

        Assert.Equal(["old/textures/TEXTURE.001/002.png"], plan.Delete);
        Assert.Equal([stays], plan.Keep);
    }

    [Fact]
    public void FileTheMirrorDidNotCopyIsNeverDeleted()
    {
        // A hi-res mod generated on the device: in the mods folder, not in the manifest.
        var plan = Plan([], [], "hires-realesrgan/mod.json");

        Assert.Empty(plan.Delete);
    }

    [Theory]
    [InlineData("skynet.zip")]
    [InlineData("SHOCK.ZIP")]
    [InlineData("skynet.pak")]
    [InlineData("skynet/maps/MAP.210.block")]
    public void BakeOutputIsNeverCopiedOrDeleted(string path)
    {
        // A desktop mods folder carries its own bake; an old manifest might list one.
        var file = new MirrorFile(path, 100, 5);

        var plan = Plan([file], [file with { Size = 99 }]);

        Assert.Empty(plan.Copy);
        Assert.Empty(plan.Delete);
        Assert.Empty(plan.Keep);
    }

    [Fact]
    public void BakeOutputGoneFromSourceIsNotDeleted()
    {
        var plan = Plan([], [new MirrorFile("skynet.zip", 100, 5)], "skynet.zip");

        Assert.Empty(plan.Delete);
    }

    [Fact]
    public void ModNamedLikeSystemModPrefixIsMirrored()
    {
        var file = new MirrorFile("skynet-hd/mod.json", 1, 1);

        var plan = Plan([file], []);

        Assert.Equal([file], plan.Copy);
    }

    [Theory]
    [InlineData("../escape.txt")]
    [InlineData("a/./b")]
    [InlineData("/abs")]
    [InlineData("a\\b")]
    [InlineData("tab\tname")]
    public void PathOutsideTheFolderIsIgnored(string path)
    {
        var plan = Plan([new MirrorFile(path, 1, 1)], [new MirrorFile(path, 1, 1)]);

        Assert.Empty(plan.Copy);
        Assert.Empty(plan.Delete);
    }

    [Fact]
    public void ManifestRoundTrips()
    {
        MirrorFile[] files = [new("props/mod.json", 100, 1_700_000_000_000), new("a b/c.png", 0, 0)];

        var read = ModMirror.Read(ModMirror.Write(files));

        Assert.Equal(files, read);
    }

    [Fact]
    public void ManifestSkipsBrokenLines()
    {
        var read = ModMirror.Read("garbage\nx\t1\tp\n5\t6\tok/file\n");

        Assert.Equal([new MirrorFile("ok/file", 5, 6)], read);
    }
}
