using System.Numerics;
using RetroEng.Render;
using SkyNet.Formats;
using SkyNet.Game.Baking;
using SkyNet.Game.Enhanced;
using SkyNet.Game.Storage;

namespace SkyNet.Game.Tests.Enhanced;

/// <summary>Where a map's baked light is kept: the cache, keyed by game, data fingerprint, bake
/// version and the map's block; none (or a bad file) is a miss that leaves the even ambient.</summary>
public sealed class IrradianceFilesTests : IDisposable
{
    private const string Print = "0123456789abcdef";
    private readonly string _dir = Directory.CreateTempSubdirectory("skynet-irradiance-").FullName;

    public void Dispose() => Directory.Delete(_dir, recursive: true);

    /// <summary>A tiny bake: an empty scene under a uniform sky of <paramref name="sky"/>.</summary>
    private static IrradianceVolume Volume(float sky) =>
        IrradianceBake.Bake([], [], new IrradianceBakeSettings(Vector3.Zero, new Vector3(100, 100, 100), 50f) { Sky = new Vector3(sky), Rays = 8, Bounces = 1 });

    private static float Light(IrradianceVolume v)
    {
        v.Sample(new Vector3(50, 50, 50), Vector3.UnitY, out var light);
        return light.X;
    }

    private static MapBlock Block(params BlockInput[] inputs) => new() { Number = 231, Inputs = inputs, SourceHash = MapBaker.SourceHashOf(inputs) };

    [Fact]
    public void PathIsInTheCacheKeyedByGameAndKey()
    {
        var key = IrradianceFiles.KeyOf(Print, "feedfacefeedface");

        Assert.Equal(Path.Combine("cache", "lighting", "skynet", "MAP.231", key + ".reiv"), IrradianceFiles.PathOf("cache", GameKind.SkyNet, key, 231));
        Assert.Equal(Path.Combine("cache", "lighting", "shock", "MAP.009", key + ".reiv"), IrradianceFiles.PathOf("cache", GameKind.FutureShock, key, 9));
    }

    [Fact]
    public void KeyChangesWithTheDataAndTheMap()
    {
        var key = IrradianceFiles.KeyOf(Print, "aaaaaaaaaaaaaaaa");

        Assert.Equal(16, key.Length);
        Assert.Equal(key, IrradianceFiles.KeyOf(Print, "aaaaaaaaaaaaaaaa"));
        Assert.NotEqual(key, IrradianceFiles.KeyOf("fedcba9876543210", "aaaaaaaaaaaaaaaa"));
        Assert.NotEqual(key, IrradianceFiles.KeyOf(Print, "bbbbbbbbbbbbbbbb"));
    }

    [Fact]
    public void AnEditedBlockHasAnotherHash()
    {
        var block = Block(new BlockInput("MAP.231", "00"));

        Assert.Equal(IrradianceFiles.MapHashOf(block), IrradianceFiles.MapHashOf(Block(new BlockInput("MAP.231", "00"))));
        Assert.NotEqual(IrradianceFiles.MapHashOf(block), IrradianceFiles.MapHashOf(Block(new BlockInput("MAP.231", "01"))));
        var lamp = new LampLight(Vector3.One, Vector3.One, 100f, 1f, false);
        Assert.NotEqual(IrradianceFiles.MapHashOf(block), IrradianceFiles.MapHashOf(new MapBlock { Number = 231, Inputs = block.Inputs, SourceHash = block.SourceHash, Lamps = [lamp] }));
    }

    [Fact]
    public void WrittenVolumeReadsBack()
    {
        var file = IrradianceFiles.PathOf(_dir, GameKind.SkyNet, IrradianceFiles.KeyOf(Print, "aa"), 231);

        Assert.True(IrradianceFiles.Write(Volume(0.5f), file));

        Assert.Equal(0.5f, Light(IrradianceFiles.Read(file)!), 1e-2f);
        Assert.False(File.Exists(file + ".tmp"));
    }

    [Fact]
    public void NoFileOrABadOneIsAMiss()
    {
        var file = IrradianceFiles.PathOf(_dir, GameKind.SkyNet, IrradianceFiles.KeyOf(Print, "aa"), 231);
        Assert.Null(IrradianceFiles.Read(file));

        Directory.CreateDirectory(Path.GetDirectoryName(file)!);
        File.WriteAllText(file, "not a volume");
        Assert.Null(IrradianceFiles.Read(file));

        // The enhanced look's own light carries none.
        Assert.Null(LookLighting.For(LightPreset.Interior, null, 1000f, 1f).Irradiance);
    }

    [Fact]
    public void ANewBakeOfAMapReplacesItsOlderKeys()
    {
        var old = IrradianceFiles.PathOf(_dir, GameKind.SkyNet, IrradianceFiles.KeyOf(Print, "aa"), 231);
        var other = IrradianceFiles.PathOf(_dir, GameKind.SkyNet, IrradianceFiles.KeyOf(Print, "aa"), 233);
        Assert.True(IrradianceFiles.Write(Volume(0.25f), old));
        Assert.True(IrradianceFiles.Write(Volume(0.25f), other));

        var edited = IrradianceFiles.PathOf(_dir, GameKind.SkyNet, IrradianceFiles.KeyOf(Print, "bb"), 231);
        Assert.True(IrradianceFiles.Write(Volume(0.5f), edited));

        // One file a map: the edited map's bake only; the other map keeps its own.
        Assert.Equal([edited], Directory.GetFiles(Path.GetDirectoryName(edited)!));
        Assert.True(File.Exists(other));
    }

    [Fact]
    public void LegacyFolderBesideTheGameGoesOnce()
    {
        var legacy = Path.Combine(_dir, IrradianceFiles.Folder);
        Directory.CreateDirectory(Path.Combine(legacy, "skynet", "cfbd571a1b412888-u"));
        File.WriteAllText(Path.Combine(legacy, "skynet", "cfbd571a1b412888-u", "MAP.231.reiv"), "old bake");

        Assert.True(IrradianceFiles.RemoveLegacy(_dir));
        Assert.False(Directory.Exists(legacy));
        Assert.False(IrradianceFiles.RemoveLegacy(_dir));

        // A folder holding anything but bakes is not ours to delete.
        Directory.CreateDirectory(legacy);
        File.WriteAllText(Path.Combine(legacy, "notes.txt"), "mine");
        Assert.False(IrradianceFiles.RemoveLegacy(_dir));
        Assert.True(File.Exists(Path.Combine(legacy, "notes.txt")));
    }

    [Fact]
    public void AFileWhoseSizeDoesNotMatchItsProbesIsAMiss()
    {
        var file = IrradianceFiles.PathOf(_dir, GameKind.SkyNet, IrradianceFiles.KeyOf(Print, "aa"), 231);
        Assert.True(IrradianceFiles.Write(Volume(0.5f), file));
        var bytes = File.ReadAllBytes(file);

        // Cut short.
        File.WriteAllBytes(file, bytes[..^1]);
        Assert.Null(IrradianceFiles.Read(file));

        // A header claiming a huge grid: never allocated.
        BitConverter.GetBytes(int.MaxValue).CopyTo(bytes, 24);
        File.WriteAllBytes(file, bytes);
        Assert.Null(IrradianceFiles.Read(file));
    }

    [Fact]
    public void FingerprintNamesTheReleaseNotTheListing()
    {
        DataFile[] files = [new("MDMDMAP2.BSA", 1, "aa"), new("MDMDOBJS.BSA", 2, "bb")];
        var print = DataVersions.Fingerprint(files);

        Assert.Equal(DataVersions.FingerprintLength, print.Length);
        Assert.All(print, c => Assert.True(char.IsAsciiHexDigitLower(c)));
        // Order and the optional HUD archive don't change it; a changed archive does.
        Assert.Equal(print, DataVersions.Fingerprint([files[1], files[0], new("MDMDHRES.BSA", 3, "cc")]));
        Assert.NotEqual(print, DataVersions.Fingerprint([files[0], new("MDMDOBJS.BSA", 2, "bc")]));
    }
}
