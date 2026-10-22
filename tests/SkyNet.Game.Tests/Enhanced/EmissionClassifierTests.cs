using SkyNet.Formats;
using SkyNet.Game.Enhanced;
using Xunit.Abstractions;

namespace SkyNet.Game.Tests.Enhanced;

/// <summary>Runs only when SKYNET_DATA points at a SkyNET data folder.</summary>
sealed class EmissionDataFactAttribute : FactAttribute
{
    public EmissionDataFactAttribute()
    {
        if (EmissionClassifierTests.Data() == null)
        {
            Skip = "SKYNET_DATA does not point at SkyNET's data";
        }
    }
}

public class EmissionClassifierTests(ITestOutputHelper output)
{
    /// <summary>A palette of 8-bit colours: 0 black, 1 white, 2 mid grey, 3 saturated red, 4 dull red,
    /// 5 the cut's edge (214 grey), 6 just under it (213), the rest dark.</summary>
    private static Palette TestPalette()
    {
        var rgb = new byte[Palette.RawSize];
        void Set(int i, byte r, byte g, byte b) => (rgb[i * 3], rgb[i * 3 + 1], rgb[i * 3 + 2]) = (r, g, b);
        for (var i = 7; i < 256; i++) Set(i, 20, 20, 30);
        Set(1, 255, 255, 255);
        Set(2, 128, 128, 128);
        Set(3, 255, 0, 0);
        Set(4, 200, 0, 0);
        Set(5, 214, 214, 214);
        Set(6, 213, 213, 213);
        return Palette.Parse(rgb) ?? throw new InvalidOperationException();
    }

    internal static GameData? Data()
    {
        var dir = Environment.GetEnvironmentVariable("SKYNET_DATA");
        return string.IsNullOrWhiteSpace(dir) || !GameDataLocator.HasData(dir) ? null : GameData.Open(dir);
    }

    [Fact]
    public void TexelIsLitByLumaOrBySaturation()
    {
        Assert.Equal(214, EmissionClassifier.Cut8);
        Assert.True(EmissionClassifier.IsLit(255, 255, 255));
        Assert.False(EmissionClassifier.IsLit(128, 128, 128));
        // Pure red: luma 76 but 85 % of 255 = 216 ≥ 214.
        Assert.True(EmissionClassifier.IsLit(255, 0, 0));
        // 85 % of 250 = 212: under the cut.
        Assert.False(EmissionClassifier.IsLit(250, 0, 0));
        // The cut's edge in Godot's integer luma: (214·256) >> 8 = 214.
        Assert.True(EmissionClassifier.IsLit(214, 214, 214));
        Assert.False(EmissionClassifier.IsLit(213, 213, 213));
    }

    [Fact]
    public void LampRecordIsEmissiveWithItsLitColour()
    {
        var palette = TestPalette();
        // 64 × 64 dark wall with an 8 × 32 strip: 256 / 4096 = 6.25 % lit.
        var pixels = new byte[64 * 64];
        Array.Fill(pixels, (byte)9);
        for (var y = 0; y < 8; y++)
            for (var x = 16; x < 48; x++)
                pixels[y * 64 + x] = (byte)(x < 32 ? 1 : 3);
        var r = EmissionClassifier.Classify(pixels, palette, wantMask: true);
        Assert.True(r.Emissive);
        Assert.Equal(256, r.LitTexels);
        Assert.Equal(4096, r.CountedTexels);
        Assert.Equal(256f / 4096f, r.LitFraction, 1e-6f);
        // Half white, half red.
        Assert.Equal(1f, r.MeanLitColour.X, 1e-5f);
        Assert.Equal(0.5f, r.MeanLitColour.Y, 1e-5f);
        Assert.Equal(0.5f, r.MeanLitColour.Z, 1e-5f);
        Assert.NotNull(r.Mask);
        Assert.Equal(pixels.Length, r.Mask!.Length);
        Assert.Equal(1, r.Mask[16]);
        Assert.Equal(3, r.Mask[40]);
        Assert.Equal(0, r.Mask[8 * 64 + 20]);
        Assert.Equal(256, r.Mask.Count(b => b != 0));
    }

    [Fact]
    public void TooLittleOrTooMuchLightIsNotALamp()
    {
        var palette = TestPalette();
        var pixels = new byte[64 * 64];
        Array.Fill(pixels, (byte)2);
        // 16 lit texels: 0.39 % < 0.4 %.
        for (var i = 0; i < 16; i++) pixels[i * 7] = 1;
        var few = EmissionClassifier.Classify(pixels, palette, wantMask: true);
        Assert.False(few.Emissive);
        Assert.Null(few.Mask);
        Assert.Equal(16, few.LitTexels);
        // 17 lit: 0.415 % — a lamp.
        pixels[999] = 1;
        Assert.True(EmissionClassifier.Classify(pixels, palette).Emissive);
        // A pale wall: 25 % lit.
        for (var i = 0; i < 1024; i++) pixels[i] = 5;
        var pale = EmissionClassifier.Classify(pixels, palette);
        Assert.False(pale.Emissive);
        Assert.Equal(0.25f, pale.LitFraction, 1e-6f);
        // Exactly 22 % is still a lamp (the bounds are inclusive).
        var edge = new byte[100];
        Array.Fill(edge, (byte)2);
        for (var i = 0; i < 22; i++) edge[i] = 1;
        Assert.True(EmissionClassifier.Classify(edge, palette).Emissive);
    }

    [Fact]
    public void IndexZeroIsTransparentAndDoesNotCount()
    {
        var palette = TestPalette();
        // A sprite: 90 transparent texels, 10 opaque of which 1 lit = 10 % of the counted.
        var pixels = new byte[100];
        for (var i = 90; i < 99; i++) pixels[i] = 6;
        pixels[99] = 1;
        var r = EmissionClassifier.Classify(pixels, palette);
        Assert.Equal(10, r.CountedTexels);
        Assert.Equal(0.1f, r.LitFraction, 1e-6f);
        Assert.True(r.Emissive);
        // Godot's way (index 0 an opaque black texel): 1 %.
        var godot = EmissionClassifier.Classify(pixels, palette, countIndexZero: true);
        Assert.Equal(100, godot.CountedTexels);
        Assert.Equal(0.01f, godot.LitFraction, 1e-6f);
        // All transparent: nothing counted, not emissive.
        var none = EmissionClassifier.Classify(new byte[64], palette);
        Assert.False(none.Emissive);
        Assert.Equal(0, none.CountedTexels);
        Assert.Equal(0f, none.LitFraction);
    }

    /// <summary>Every textured face of MAP.214's meshes (variant 1, MDMDOBJS then MDMDENMS), classified
    /// with SKYNET.COL; reports the emissive records, and the count over the whole banks used.</summary>
    [EmissionDataFact]
    public void Map214EmissiveRecords()
    {
        var data = Data()!;
        var map = data.LoadMap(214);
        var paletteBytes = data.PaletteBytes();
        Assert.NotNull(map);
        Assert.NotNull(paletteBytes);
        var palette = Palette.Parse(paletteBytes!)!;
        var lit = EmissionClassifier.LitTable(palette);

        using var objects = data.OpenArchive("MDMDOBJS.BSA");
        using var enemies = data.Exists("MDMDENMS.BSA") ? data.OpenArchive("MDMDENMS.BSA") : null;
        var used = new SortedSet<(int Bank, int Record)>();
        var models = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var e in map!.Entities)
        {
            if (e.Variant != MapEntityVariant.Mesh || e.NameIndex < 0 || e.NameIndex >= map.Names.Length
                || string.IsNullOrEmpty(map.Names[e.NameIndex]) || !models.Add(map.Names[e.NameIndex]))
            {
                continue;
            }

            var file = map.Names[e.NameIndex] + ".3D";
            var bytes = objects.Read(file) is { Length: > 0 } b ? b : enemies?.Read(file);
            if (bytes is not { Length: > 0 } || Model3D.Parse(bytes) is not { } model)
            {
                continue;
            }

            foreach (var f in model.Faces)
            {
                if (!f.IsSolidColour)
                {
                    used.Add((f.TextureBank, f.TextureRecord));
                }
            }
        }

        var banks = new Dictionary<int, TextureBank?>();
        TextureBank? Bank(int n)
        {
            if (!banks.TryGetValue(n, out var b))
            {
                banks[n] = b = data.ReadLoose($"TEXTURE.{n:D3}") is { } bytes ? TextureBank.Parse(bytes) : null;
            }

            return b;
        }

        var emissive = new List<string>();
        foreach (var (bank, record) in used)
        {
            if (Bank(bank) is not { } b || b.Records.Length == 0)
            {
                continue;
            }

            var rec = b.Records[Math.Clamp(record, 0, b.Records.Length - 1)];
            var r = EmissionClassifier.Classify(rec, palette, lit);
            if (r.Emissive)
            {
                var c = r.MeanLitColour * 255f;
                emissive.Add($"{bank:D3}/{record:D3} {rec.Width}x{rec.Height} lit {r.LitFraction:P2} colour ({c.X:F0},{c.Y:F0},{c.Z:F0})");
            }
        }

        var bankTotal = 0;
        var bankEmissive = 0;
        foreach (var n in used.Select(u => u.Bank).Distinct())
        {
            if (Bank(n) is not { } b)
            {
                continue;
            }

            foreach (var rec in b.Records)
            {
                bankTotal++;
                if (EmissionClassifier.Classify(rec, palette, lit).Emissive)
                {
                    bankEmissive++;
                }
            }
        }

        output.WriteLine($"MAP.214: {models.Count} models, {used.Count} textured records used, {emissive.Count} emissive");
        foreach (var line in emissive)
        {
            output.WriteLine("  " + line);
        }

        output.WriteLine($"banks used ({string.Join(", ", used.Select(u => u.Bank).Distinct())}): {bankEmissive} of {bankTotal} records emissive");
        Assert.True(used.Count > 0);
        Assert.NotEmpty(emissive);
    }
}
