namespace SkyNet.Formats.Tests;

public class PaletteTests
{
    [Fact]
    public void Six_bit_values_expand_by_replicating_the_top_bits()
    {
        var raw = new byte[768];
        raw[0] = 63; raw[1] = 32; raw[2] = 1;
        var p = Palette.Parse(raw)!;
        Assert.True(p.WasSixBit);
        Assert.Equal(((byte)255, (byte)130, (byte)4), p[0]);
    }

    [Fact]
    public void Any_value_above_63_means_eight_bit()
    {
        var raw = new byte[768];
        raw[0] = 63; raw[767] = 64;
        var p = Palette.Parse(raw)!;
        Assert.False(p.WasSixBit);
        Assert.Equal(63, p.Rgb[0]);
        Assert.Equal(64, p.Rgb[767]);
    }

    [Fact]
    public void Header_and_trailing_layouts()
    {
        var f776 = new byte[776];
        f776[0] = 200;            // header byte: neither read nor counted in the 6-bit test
        f776[8] = 10;
        var p = Palette.Parse(f776)!;
        Assert.True(p.WasSixBit);
        Assert.Equal(40, p.Rgb[0]);

        var f800 = new byte[800]; // other sizes: the last 768 bytes
        f800[32] = 1;
        Assert.Equal(4, Palette.Parse(f800)!.Rgb[0]);
        Assert.Null(Palette.Parse(new byte[767]));
    }

    [Fact]
    public void Argb_and_transparent_zero()
    {
        var raw = new byte[768];
        raw[0] = 63;
        var p = Palette.Parse(raw)!;
        Assert.Equal(0xFFFF0000u, p.Argb(0));
        Assert.Equal(0x00FF0000u, p.Argb(0, transparentZero: true));
    }

    [Fact]
    public void Haze_needs_exactly_64_rows()
    {
        Assert.Null(HazeTable.Parse(new byte[100]));
        var t = new byte[HazeTable.Size];
        t[5 * 256 + 7] = 42;
        Assert.Equal(42, HazeTable.Parse(t)!.Map(5, 7));
    }

    [Fact]
    public void Haze_steps_off_the_entries_the_palette_repaints()
    {
        // Made for grey at 18 and 23; drawn with a palette whose 18 is blood red.
        var made = new byte[768];
        (made[18 * 3], made[18 * 3 + 1], made[18 * 3 + 2]) = (51, 51, 53);
        (made[23 * 3], made[23 * 3 + 1], made[23 * 3 + 2]) = (51, 51, 51);
        (made[40 * 3], made[40 * 3 + 1], made[40 * 3 + 2]) = (200, 10, 10);
        var used = (byte[])made.Clone();
        (used[18 * 3], used[18 * 3 + 1], used[18 * 3 + 2]) = (150, 16, 9);

        var t = new byte[HazeTable.Size];
        t[50 * 256 + 91] = 18;
        t[50 * 256 + 92] = 23;
        var haze = HazeTable.Parse(t)!;

        var fixedHaze = haze.Repoint(Palette.Parse(made)!, Palette.Parse(used)!);
        Assert.Equal(23, fixedHaze.Map(50, 91));
        Assert.Equal(23, fixedHaze.Map(50, 92));
        Assert.Equal(0, fixedHaze.Map(50, 93));

        // The palette it was made for: the table as it is.
        Assert.Equal(18, haze.Repoint(Palette.Parse(made)!, Palette.Parse(made)!).Map(50, 91));
    }
}
