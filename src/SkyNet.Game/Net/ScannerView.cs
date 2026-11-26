using System.Drawing;
using System.Numerics;
using RetroEng.Render;
using SkyNet.Game.Hud;
using SkyNet.Game.Weapons;

namespace SkyNet.Game.Net;

/// <summary>
/// FUN_0011ff00's plot drawn over the scanner's art (WEAPON13.CFA), the same in a deathmatch
/// (<see cref="DmHud"/>) and in the campaign, in both looks (the art has no 3D view-model):
/// <code>
///   gun (screen pixels) ──► + art pixel × s
///   blip      a palette 0xb3 square, s on a side
///   target    the same in 0x0c, its name centred under the screen (deathmatch only)
/// </code>
/// s = <see cref="ViewModelLayout.Scale"/>, one pixel of the art's low frame.
/// </summary>
public static class ScannerView
{
    /// <summary>Draws <paramref name="plot"/> on the art at <paramref name="gun"/> of a
    /// <paramref name="width"/> × <paramref name="height"/> screen, in <paramref name="hud"/>'s palette and font.</summary>
    public static void Draw(Renderer renderer, ScannerPlot plot, RectangleF gun, DosHud hud, int width, int height)
    {
        if (width <= 0 || height <= 0)
        {
            return;
        }

        var unit = Renderer.CanvasHeight / height;
        var s = ViewModelLayout.Scale(width, height);
        var (blip, target) = (hud.Colour(MotionDetector.BlipIndex), hud.Colour(MotionDetector.TargetIndex));
        foreach (var b in plot.Blips)
        {
            var r = Blip(b, gun, s);
            renderer.FillRect(new RectangleF(r.X * unit, r.Y * unit, r.Width * unit, r.Height * unit), b.Target ? target : blip);
        }

        if (plot.Target is not { } name || hud.TextFont is not { } font)
        {
            return;
        }

        // The target's name, centred on the bar under the screen.
        var at = new Vector2(gun.X + (MotionDetector.NameAt.X + 0.5f) * s, gun.Y + MotionDetector.NameAt.Y * s);
        var pixel = Math.Max(1, (int)MathF.Round(s / hud.TextGrid));
        TextBatcher.DrawShadowed(renderer, font, name, new Vector2(MathF.Round(at.X), MathF.Round(at.Y)) * unit, pixel * unit, target, DmHud.Shadow,
            align: TextAlign.Centre);
    }

    /// <summary>A blip's square in screen pixels: its art pixel on the gun drawn at <paramref name="gun"/>,
    /// <paramref name="scale"/> screen pixels an art pixel.</summary>
    public static RectangleF Blip(ScannerBlip blip, RectangleF gun, float scale) =>
        new(gun.X + blip.At.X * scale, gun.Y + blip.At.Y * scale, scale, scale);
}
