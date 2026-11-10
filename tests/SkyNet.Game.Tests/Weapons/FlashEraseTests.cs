using SkyNet.Formats;
using SkyNet.Game.Menu;
using SkyNet.Game.Tests.Behaviour;
using SkyNet.Game.Weapons;

namespace SkyNet.Game.Tests.Weapons;

/// <summary>
/// Issue #60: the enhanced look draws its own muzzle flash, so the one DOS painted into the guns'
/// firing frames goes there (both art sets); the original look keeps every frame as DOS drew it.
/// </summary>
public class FlashEraseTests
{
    /// <summary>The firing frames DOS painted a flash into, per record.</summary>
    private static readonly (int Record, int[] Frames)[] Flashed =
    [
        (1, [1, 3]), (2, [1, 3]), (3, [1, 3]), (4, [1]), (6, [1, 2]), (7, [1, 3]), (8, [1, 3]),
        (9, [1, 2]), (10, [2]), (11, [2]), (12, [1, 3]),
    ];

    /// <summary>The plasma guns: a cyan glow, the others a fire flash.</summary>
    private static readonly int[] Plasma = [9, 10, 11];

    /// <summary>Glow pixels allowed where the plasma pistol's glow meets its own glass.</summary>
    private const int Stray = 8;

    public static TheoryData<string> Sets => ["MDMDIMGS.BSA", "MDMDHRES.BSA"];

    [BehaviourDataTheory]
    [MemberData(nameof(Sets))]
    public void OriginalFramesStayAsDosDrewThem(string archive)
    {
        var palette = BehaviourData.Data.LoadPalette()!;
        foreach (var (record, cfa) in Guns(archive))
        {
            for (var f = 0; f < cfa.Frames.Length; f++)
            {
                Assert.Equal(cfa.Frames[f].ToArray(), FlashErase.Pixels(record, cfa, f, palette, Look.Original));
            }
        }
    }

    [BehaviourDataTheory]
    [MemberData(nameof(Sets))]
    public void EnhancedFramesLoseThePaintedFlash(string archive)
    {
        var palette = BehaviourData.Data.LoadPalette()!;
        foreach (var (record, cfa) in Guns(archive))
        {
            var frames = Flashed.FirstOrDefault(e => e.Record == record).Frames ?? [];
            for (var f = 0; f < cfa.Frames.Length; f++)
            {
                var pixels = FlashErase.Pixels(record, cfa, f, palette, Look.Enhanced);
                if (!frames.Contains(f))
                {
                    Assert.Equal(cfa.Frames[f].ToArray(), pixels);
                    continue;
                }

                // No flash colour (one the rest frame never uses) is left, and the gun stays.
                var rest = cfa.Frames[0].ToHashSet();
                var left = pixels.Count(p => !rest.Contains(p) && Glows(palette, p, record));
                Assert.True(left <= Stray, $"{archive} WEAPON{record:D2} frame {f}: {left} flash pixels left");
                Assert.True(pixels.Count(p => p != 0) > cfa.Frames[0].Count(p => p != 0) * 3 / 4,
                    $"{archive} WEAPON{record:D2} frame {f}: gun lost");
            }
        }
    }

    private static IEnumerable<(int Record, CfaFile Cfa)> Guns(string archive)
    {
        var (path, variant) = BehaviourData.Data.FrontArchive(archive);
        using var bsa = BsaArchive.Open(path, variant);
        for (var record = 0; record <= WeaponTable.MotionDetector; record++)
        {
            if (bsa.Read(ViewModelLayout.CfaName(record)!) is { } bytes && CfaFile.Parse(bytes) is { } cfa)
            {
                yield return (record, cfa);
            }
        }
    }

    /// <summary>A plasma glow's bright cyan, or a flash's hot core (yellows, oranges, reds).</summary>
    private static bool Glows(Palette palette, byte index, int record)
    {
        var (r, g, b) = (palette.Rgb[index * 3], palette.Rgb[index * 3 + 1], palette.Rgb[index * 3 + 2]);
        return Plasma.Contains(record) ? b >= 0xC0 && g >= 0xC0 && r <= 0x60 : r >= 0xC0 && b <= 0x60;
    }
}
