namespace SkyNet.Formats.Tests;

public class ControlsDatTests
{
    [Fact]
    public void Reads_17_scancodes_and_keeps_the_trailer()
    {
        var b = new Bytes();
        int[] codes = [0x11, 0x1f, 0x4b, 0x4d, 0x1e, 0x20, 0x81, 0x36, 0x21, 0x32, 0x2a, 0x39, 0x1d, 0x48, 0x50, 0x47, 0x0f];
        foreach (var c in codes) b.U32(c);
        b.PadTo(ControlsDat.ShippedSize);
        var d = ControlsDat.Parse(b.ToArray())!;
        Assert.Equal(codes, d.Scancodes);
        Assert.Equal(ControlsDat.ShippedSize - ControlsDat.BindingsSize, d.Trailer.Length);
        Assert.Equal(0x11, d.ScancodeOf("forward"));
        Assert.Equal(0x0f, d.ScancodeOf("automap"));
        Assert.Equal(-1, d.ScancodeOf("nope"));
        Assert.Equal(17, ControlsDat.Actions.Length);
        Assert.Null(ControlsDat.Parse(new byte[67]));
    }

    /// <summary>SKYNET_CONTROLS: a CONTROLS.DAT from a DOS install (it is not in the data folder); nothing to check without it.</summary>
    [Fact]
    public void Real_controls_dat_parses_when_given()
    {
        var path = Environment.GetEnvironmentVariable("SKYNET_CONTROLS");
        if (string.IsNullOrWhiteSpace(path) || !File.Exists(path))
            return;
        var bytes = File.ReadAllBytes(path);
        Assert.Equal(ControlsDat.ShippedSize, bytes.Length);
        var d = ControlsDat.Parse(bytes)!;
        Assert.All(d.Scancodes, c => Assert.InRange(c, 1, 0xff));
    }
}
