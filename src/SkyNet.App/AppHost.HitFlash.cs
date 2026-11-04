using System.Drawing;
using System.Numerics;
using RetroEng.Render;
using SkyNet.Formats;
using SkyNet.Game;
using SkyNet.Game.Campaign;

/// <summary>
/// The red flash of a hit the soldier lived through (issue #22, <see cref="HitFlash"/>), over the 3D
/// view and under the HUD, as DOS remaps its view's pixels after the gun and before the panel.
/// <code>
///  original: the scene again as an inset over the view ──► ViewRemap.Table = SHADE.003's row
///  enhanced: a red fill at the row's strength (DmHud's flash)
/// </code>
/// </summary>
internal sealed partial class AppHost
{
    // DmHud's flash red; the strength is its alpha.
    private static readonly Vector4 FlashRed = new(0.8f, 0.1f, 0.05f, 1f);

    // One single-level table per SHADE.003 row, made on first use for the data they came from.
    private readonly TextureHandle[] flashTables = new TextureHandle[HitFlash.Rows];
    private GameData? flashData;

    private void DrawHitFlash(FrameSnapshot s, Level map)
    {
        if (s.HitFlashRow is not { } row)
        {
            return;
        }

        var canvas = new RectangleF(0f, 0f, renderer.CanvasWidth, Renderer.CanvasHeight);
        if (map.Enhanced)
        {
            renderer.FillRect(canvas, FlashRed with { W = s.HitFlashStrength });
            return;
        }

        // Not in stereo, as the scope.
        if (s.Views.Count != 1 || !FlashTables())
        {
            return;
        }

        var main = s.Views.Centre;
        var view = new View(main.ViewProjection, main.ClipToDirection, main.Position)
        {
            Lens = main.Lens,
            Style = main.Style,
            Remap = new ViewRemap { Table = flashTables[row] },
        };
        renderer.BeginInset(view, canvas, InsetContent.Scene, InsetLayer.UnderCanvas);
        renderer.EndInset();
    }

    // The rows' tables, again when a scope freed them or the data changed; false without SHADE.003.
    private bool FlashTables()
    {
        if (ReferenceEquals(flashData, data) && flashTables.All(renderer.Exists))
        {
            return true;
        }

        if (HitFlash.LoadTable(data) is not { } table)
        {
            return false;
        }

        for (var i = 0; i < HitFlash.Rows; i++)
        {
            if (renderer.Exists(flashTables[i]))
            {
                renderer.Destroy(flashTables[i]);
            }

            flashTables[i] = renderer.CreateShadeTable(table[(i * Palette.Colors)..((i + 1) * Palette.Colors)], 0);
        }

        flashData = data;
        return true;
    }
}
