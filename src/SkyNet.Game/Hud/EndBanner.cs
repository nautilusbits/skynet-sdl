using System.Drawing;
using System.Numerics;
using RetroEng.Render;
using SkyNet.Formats;

namespace SkyNet.Game.Hud;

/// <summary>
/// The mission's end banner as SKYNET.EXE v1.01 draws it (FUN_00121fe3): WELLDONE.IMG "WELL DONE,
/// SOLDIER!" or FAILED.IMG "MISSION FAILED, SOLDIER!" loaded from the HUD's archive (MDMDIMGS.BSA,
/// MDMDHRES.BSA in the hi-res set) and blitted once with FUN_0013af00 flags 0x12 — centred on the
/// view centre 0x3e230/0x3e234 ((160, 80) of 320 × 200), index 0 transparent — over the last frame,
/// which stays as it was: no dimming, no text. The art is drawn 1:1 on DOS's screen, so here it is
/// blown up by the whole number nearest the window's lines per screen line (200, or 480 for the
/// hi-res art).
/// </summary>
public sealed class EndBanner : IDisposable
{
    private readonly Renderer _renderer;
    private HudArt? _art;
    private GameData? _data;

    public EndBanner(Renderer renderer) => _renderer = renderer;

    /// <summary>Queues <paramref name="image"/> on the canvas of a <paramref name="width"/> ×
    /// <paramref name="height"/> screen; false when the data has no such image.</summary>
    public bool Draw(GameData data, HudArtSet set, string image, int width, int height, HudStyle style = HudStyle.Classic)
    {
        if (width <= 0 || height <= 0)
        {
            return false;
        }

        // A level's renderer scope may have let the palette go: the art is made again.
        if (_art == null || _data != data || _art.Set != set || !_renderer.Exists(_art.Palette))
        {
            _art?.Dispose();
            _art = new HudArt(_renderer, data, set);
            _data = data;
        }

        if (_art.Image(image) is not { } art)
        {
            return false;
        }

        var at = Place(art.Width, art.Height, art.FromHires, width, height, style);
        var unit = Renderer.CanvasHeight / height;
        var target = new RectangleF(at.X * unit, at.Y * unit, at.Width * unit, at.Height * unit);
        _renderer.DrawImage(art.Handle, _art.Palette, art.Size, new RectangleF(0, 0, art.Width, art.Height), target, Vector4.One);
        return true;
    }

    /// <summary>Where a <paramref name="w"/> × <paramref name="h"/> banner lands in screen pixels:
    /// DOS subtracts half its size (w >> 1, h >> 1) from the view centre, here in whole art pixels.</summary>
    public static Rectangle Place(int w, int h, bool hires, int width, int height, HudStyle style = HudStyle.Classic)
    {
        var n = Math.Max(1, (int)MathF.Round(height / (hires ? 480f : DosHud.ScreenHeight)));
        var c = DosHud.ViewCentre(width, height, style);
        return new Rectangle((int)c.X - (w >> 1) * n, (int)c.Y - (h >> 1) * n, w * n, h * n);
    }

    public void Dispose()
    {
        _art?.Dispose();
        _art = null;
    }
}
