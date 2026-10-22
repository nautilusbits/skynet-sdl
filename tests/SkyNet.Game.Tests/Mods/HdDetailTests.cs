using System.Numerics;
using SkyNet.Game.Mods;

namespace SkyNet.Game.Tests.Mods;

/// <summary>DETAIL's HD texture quality: HIGH full, MED half, LOW a quarter of each side, shrunk
/// with a box filter (colour in linear light weighted by alpha, normals made unit again).</summary>
public class HdDetailTests
{
    [Theory]
    [InlineData(Detail.High, 1)]
    [InlineData(Detail.Medium, 2)]
    [InlineData(Detail.Low, 4)]
    public void Each_detail_divides_the_sides(Detail detail, int divisor) => Assert.Equal(divisor, HdDetail.Divisor(detail));

    [Theory]
    [InlineData(4, Detail.Low, 4)]
    [InlineData(4, Detail.Medium, 2)]
    [InlineData(4, Detail.High, 1)]
    [InlineData(2, Detail.Low, 2)]
    [InlineData(6, Detail.Low, 2)]
    [InlineData(3, Detail.Medium, 1)]
    [InlineData(1, Detail.Low, 1)]
    [InlineData(0, Detail.Low, 1)]
    public void A_record_texture_keeps_a_whole_scale(int scale, Detail detail, int factor) =>
        Assert.Equal(factor, HdDetail.Factor(scale, detail));

    [Fact]
    public void Colour_averages_in_linear_light()
    {
        byte[] rgba = [0, 0, 0, 255, 255, 255, 255, 255, 0, 0, 0, 255, 255, 255, 255, 255];

        var shrunk = HdDetail.Shrink(rgba, 2, 2, 1, 1, Texels.Colour);

        // Half black, half white: 0.5 linear is sRGB 188, not 128.
        Assert.Equal(new byte[] { 188, 188, 188, 255 }, shrunk);
    }

    [Fact]
    public void Colour_is_weighted_by_alpha_so_clear_texels_leave_no_fringe()
    {
        byte[] rgba = [255, 0, 0, 255, 0, 255, 0, 0];

        var shrunk = HdDetail.Shrink(rgba, 2, 1, 1, 1, Texels.Colour);

        Assert.Equal(new byte[] { 255, 0, 0, 128 }, shrunk);
    }

    [Fact]
    public void Opaque_colour_ignores_alpha()
    {
        byte[] rgba = [255, 0, 0, 255, 0, 0, 255, 0];

        var shrunk = HdDetail.Shrink(rgba, 2, 1, 1, 1, Texels.Opaque);

        Assert.Equal((shrunk[0], shrunk[2], shrunk[3]), (shrunk[2], shrunk[0], (byte)255));
        Assert.Equal(188, shrunk[0]);
    }

    [Fact]
    public void Data_averages_the_bytes()
    {
        byte[] rgba = [0, 10, 200, 0, 255, 30, 100, 255];

        Assert.Equal(new byte[] { 128, 20, 150, 128 }, HdDetail.Shrink(rgba, 2, 1, 1, 1, Texels.Data));
    }

    [Fact]
    public void Normals_are_averaged_then_made_unit_again()
    {
        // (0.6, 0, 0.8) and (-0.6, 0, 0.8): the mean (0, 0, 0.8) is flat once unit.
        byte[] rgba = [Encode(0.6f), 128, Encode(0.8f), 255, Encode(-0.6f), 128, Encode(0.8f), 255];

        var shrunk = HdDetail.Shrink(rgba, 2, 1, 1, 1, Texels.Normal);

        Assert.Equal(new byte[] { 128, 128, 255, 255 }, shrunk);
    }

    [Fact]
    public void A_tilted_normal_stays_unit_length()
    {
        byte[] rgba = [Encode(0.6f), 128, Encode(0.8f), 255, Encode(0f), 128, Encode(1f), 255];

        var s = HdDetail.Shrink(rgba, 2, 1, 1, 1, Texels.Normal);
        var n = new Vector3(s[0], s[1], s[2]) / 127.5f - Vector3.One;

        Assert.InRange(n.Length(), 0.99f, 1.01f);
        Assert.True(n.X > 0.25f);
    }

    [Fact]
    public void An_uneven_side_takes_every_texel_once()
    {
        // 3 x 1 to 1 x 1: all three bytes.
        byte[] rgba = [0, 0, 0, 0, 30, 0, 0, 0, 60, 0, 0, 0];

        Assert.Equal(30, HdDetail.Shrink(rgba, 3, 1, 1, 1, Texels.Data)[0]);
    }

    [Fact]
    public void An_image_shrinks_to_at_least_one_texel()
    {
        var image = (2, 1, new byte[] { 10, 10, 10, 10, 30, 30, 30, 30 });

        var (w, h, rgba) = HdDetail.Shrink(image, 4, Texels.Data);

        Assert.Equal((1, 1), (w, h));
        Assert.Equal(new byte[] { 20, 20, 20, 20 }, rgba);
    }

    [Fact]
    public void High_returns_the_image_itself()
    {
        var image = (2, 1, new byte[8]);

        Assert.Same(image.Item3, HdDetail.Shrink(image, 1, Texels.Data).Rgba);
    }

    private static byte Encode(float n) => (byte)MathF.Round(127.5f + 127.5f * n);
}
