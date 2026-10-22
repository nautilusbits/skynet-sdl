using RetroEng.Render;

namespace SkyNet.Game.Tests;

/// <summary>Which HD textures the renderer may let go once uploaded: all but the fire sprites, which
/// meet a second palette (their flames cut out) after the first.</summary>
public class TextureStorePixelsTests
{
    [Theory]
    [InlineData(208, 2, false, HdPixels.Kept)]      // a burning barrel's sprite: the mask palette too
    [InlineData(216, 5, false, HdPixels.Kept)]      // a flame
    [InlineData(192, 0, false, HdPixels.Kept)]      // drawn at full light: a fire
    [InlineData(208, 2, true, HdPixels.Released)]   // the same record on a model's face: one palette
    [InlineData(214, 7, false, HdPixels.Released)]  // a pickup
    [InlineData(230, 14, true, HdPixels.Released)]  // a wall
    public void Only_fire_sprites_keep_their_pixels(int bank, int record, bool opaque, HdPixels expected) =>
        Assert.Equal(expected, TextureStore.PixelsOf(bank, record, opaque));
}
