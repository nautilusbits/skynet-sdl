using System.Collections.Immutable;

namespace SkyNet.Formats;

/// <summary>
/// A 256-colour palette (.COL), expanded to 8-bit RGB. On disk: 768 bytes of RGB, 776 bytes
/// (8-byte header + RGB), or anything longer, of which the last 768 bytes are taken. Components are
/// 6-bit VGA DAC values when every one of the 768 bytes is at most 63, expanded with
/// <c>(c &lt;&lt; 2) | (c &gt;&gt; 4)</c>; otherwise they are already 8-bit.
/// </summary>
public sealed class Palette
{
    public const int Colors = 256;
    public const int RawSize = 768;

    /// <summary>768 bytes, R G B per index, 8 bits each. Index 0 is transparent only where the caller says so.</summary>
    public ImmutableArray<byte> Rgb { get; }
    /// <summary>True when the file held 6-bit DAC values.</summary>
    public bool WasSixBit { get; }

    Palette(byte[] rgb, bool sixBit)
    {
        Rgb = ImmutableArray.Create(rgb);
        WasSixBit = sixBit;
    }

    /// <summary>Parse a .COL file; null when it is shorter than 768 bytes.</summary>
    public static Palette? Parse(ReadOnlySpan<byte> bytes)
    {
        if (bytes.Length < RawSize)
            return null;
        int off = bytes.Length == RawSize ? 0 : bytes.Length == RawSize + 8 ? 8 : bytes.Length - RawSize;
        var src = bytes.Slice(off, RawSize);
        bool sixBit = true;
        foreach (byte b in src)
        {
            if (b > 63) { sixBit = false; break; }
        }
        var rgb = new byte[RawSize];
        for (int i = 0; i < RawSize; i++)
            rgb[i] = sixBit ? (byte)((src[i] << 2) | (src[i] >> 4)) : src[i];
        return new Palette(rgb, sixBit);
    }

    public (byte R, byte G, byte B) this[int index] => (Rgb[index * 3], Rgb[index * 3 + 1], Rgb[index * 3 + 2]);

    /// <summary>Colour as 0xAARRGGBB, opaque (alpha 0 for index 0 when <paramref name="transparentZero"/>).</summary>
    public uint Argb(int index, bool transparentZero = false)
    {
        var (r, g, b) = this[index];
        uint a = transparentZero && index == 0 ? 0u : 0xFFu;
        return (a << 24) | ((uint)r << 16) | ((uint)g << 8) | b;
    }
}

/// <summary>
/// HAZE.000: the outdoor depth-cue table, 64 rows of 256 bytes. Row <c>s</c> maps every palette
/// index to its shaded index at cue step <c>s</c>; row 0 maps everything to index 31
/// (<see cref="HazeIndex"/>). The Godot port reads none of the file, it only fades to index 31.
/// </summary>
public sealed class HazeTable
{
    public const int Steps = 64;
    public const int Size = Steps * 256;
    /// <summary>The colour every pixel reaches at row 0 (the Godot port's HAZE_INDEX).</summary>
    public const int HazeIndex = 31;

    readonly byte[] _table;

    HazeTable(byte[] table) => _table = table;

    /// <summary>Parse HAZE.NNN / SHADE.NNN-style 64×256 tables; null unless exactly 16384 bytes.</summary>
    public static HazeTable? Parse(ReadOnlySpan<byte> bytes) =>
        bytes.Length == Size ? new HazeTable(bytes.ToArray()) : null;

    public ReadOnlySpan<byte> Row(int step) => _table.AsSpan(step * 256, 256);

    public byte Map(int step, int index) => _table[step * 256 + index];

    /// <summary>
    /// The table for drawing with <paramref name="used"/> when it was made for <paramref name="made"/>:
    /// every step landing on an index the two palettes colour differently goes to the nearest
    /// colour (of <paramref name="made"/>'s at that index) among the indices they agree on, 0 aside.
    /// Future Shock's HAZE.000 lands on 0x12-0x16, greys in SHOCK.COL, blood reds in SKYNET.COL.
    /// </summary>
    public HazeTable Repoint(Palette made, Palette used)
    {
        var to = new byte[256];
        bool same = true;
        for (int k = 0; k < 256; k++)
        {
            to[k] = (byte)k;
            if (made[k] == used[k])
            {
                continue;
            }

            same = false;
            to[k] = (byte)Nearest(made[k], made, used);
        }

        if (same)
        {
            return this;
        }

        var table = new byte[Size];
        for (int i = 0; i < Size; i++)
        {
            table[i] = to[_table[i]];
        }

        return new HazeTable(table);
    }

    /// <summary>The index other than 0 nearest <paramref name="c"/> that both palettes colour alike.</summary>
    static int Nearest((byte R, byte G, byte B) c, Palette made, Palette used)
    {
        int best = HazeIndex, bestDistance = int.MaxValue;
        for (int j = 1; j < 256; j++)
        {
            if (made[j] != used[j])
            {
                continue;
            }

            var (r, g, b) = used[j];
            int d = (r - c.R) * (r - c.R) + (g - c.G) * (g - c.G) + (b - c.B) * (b - c.B);
            if (d < bestDistance)
            {
                (best, bestDistance) = (j, d);
            }
        }

        return best;
    }
}
