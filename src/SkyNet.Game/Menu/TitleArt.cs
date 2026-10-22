using SkyNet.Formats;

namespace SkyNet.Game.Menu;

/// <summary>The game's title picture (SKYNTRMP.IMG through SKYNTRMP.COL, the front archive's: Future
/// Shock reads SkyNET's copy, as its menu does) as RGBA8 for the start-up splash and the editor's Help >
/// About; read from the player's data, never shipped.</summary>
public static class TitleArt
{
    private const int BytesPerPixel = 4;

    /// <summary>The start-up splash's size (the engine's SplashWindow takes the picture's).</summary>
    public const int SplashWidth = 640, SplashHeight = 360;

    /// <summary>The picture's size and RGBA8 pixels (rows top-down); null when the data lacks it.</summary>
    public static (int Width, int Height, byte[] Rgba)? Load(GameData data)
    {
        var (path, variant) = data.FrontArchive("MDMDIMGS.BSA");
        if (!File.Exists(path))
        {
            return null;
        }

        using var archive = BsaArchive.Open(path, variant);
        var colBytes = archive.Read(MenuLayout.TitlePalette) ?? data.ColBytes(MenuLayout.TitlePalette);
        if (archive.Read(MenuLayout.TitleImage) is not { } imgBytes || ImgFile.Parse(imgBytes) is not { } img
            || colBytes == null || Palette.Parse(colBytes) is not { } palette)
        {
            return null;
        }

        // Palette indices ─► RGBA, opaque.
        var rgba = new byte[img.Width * img.Height * BytesPerPixel];
        for (var i = 0; i < img.Pixels.Length; i++)
        {
            var (r, g, b) = palette[img.Pixels[i]];
            (rgba[i * 4], rgba[i * 4 + 1], rgba[i * 4 + 2], rgba[i * 4 + 3]) = (r, g, b, byte.MaxValue);
        }

        return (img.Width, img.Height, rgba);
    }

    /// <summary>The picture for the start-up splash: <see cref="SplashWidth"/> x <see cref="SplashHeight"/>,
    /// scaled with whole pixels to cover it and its middle kept (320 x 200 doubled, 20 rows cut top and
    /// bottom); null when the data lacks it.</summary>
    public static (int Width, int Height, byte[] Rgba)? Splash(GameData data)
    {
        if (Load(data) is not { } art)
        {
            return null;
        }

        var splash = Cover(art, SplashWidth, SplashHeight);
        Shade(splash, VeilTop, VeilFloor);
        return splash;
    }

    // The splash's text (the engine's, bottom left, white) over a darkened bottom: from 60 % down to 35 % bright.
    private const float VeilTop = 0.6f, VeilFloor = 0.35f;

    /// <summary>Rows below <paramref name="top"/> (a fraction of the height) darkened towards <paramref name="floor"/> at the bottom.</summary>
    public static void Shade((int Width, int Height, byte[] Rgba) art, float top, float floor)
    {
        var first = (int)(art.Height * top);
        for (var y = first; y < art.Height; y++)
        {
            var light = 1f - (1f - floor) * (y - first + 1) / (art.Height - first);
            for (var i = y * art.Width * BytesPerPixel; i < (y + 1) * art.Width * BytesPerPixel; i += BytesPerPixel)
            {
                for (var c = 0; c < 3; c++)
                {
                    art.Rgba[i + c] = (byte)(art.Rgba[i + c] * light);
                }
            }
        }
    }

    /// <summary><paramref name="art"/> scaled (nearest) to cover width x height, cropped to its middle.</summary>
    public static (int Width, int Height, byte[] Rgba) Cover((int Width, int Height, byte[] Rgba) art, int width, int height)
    {
        var scale = MathF.Max((float)width / art.Width, (float)height / art.Height);
        var (x0, y0) = ((art.Width * scale - width) * 0.5f, (art.Height * scale - height) * 0.5f);
        var rgba = new byte[width * height * BytesPerPixel];
        for (var y = 0; y < height; y++)
        {
            var sy = Math.Clamp((int)((y + y0) / scale), 0, art.Height - 1);
            for (var x = 0; x < width; x++)
            {
                var sx = Math.Clamp((int)((x + x0) / scale), 0, art.Width - 1);
                Array.Copy(art.Rgba, (sy * art.Width + sx) * BytesPerPixel, rgba, (y * width + x) * BytesPerPixel, BytesPerPixel);
            }
        }

        return (width, height, rgba);
    }
}
