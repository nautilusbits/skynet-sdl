using System.Drawing;
using System.Numerics;
using SkyNet.Formats;
using SkyNet.Game.Hud;

namespace SkyNet.Game.Weapons;

/// <summary>
/// The laser rifle's scope screen (issue #12, SKYNET.EXE v1.01). Every frame, after the 3D view:
/// <code>
///  3D frame ──► FUN_001371b4: (w/2 × h/2) around the crosshair [0x6e230], each pixel through
///               the green table, doubled with neighbours averaged ──► buffer 0x54f08 (w × h)
///  gun drawn ─► FUN_00125d1a: record 7 held ─► FUN_00137178 copies the buffer onto the gun
///               at its corner + table 0x444a6[frame]
/// </code>
/// So the screen is the main view's centre at <see cref="Zoom"/>×, live, firing or not; hidden
/// with the gun (death, vehicles).
/// </summary>
public static class LaserScope
{
    public const int Record = 7;
    public const int Zoom = 2;

    /// <summary>The screen's size in 320 × 200 pixels (0x137100: 0x2c × 0x1e).</summary>
    public static readonly Size Pixels = new(44, 30);

    // Table 0x444a6: the screen's corner from the gun frame's, per animation frame.
    private static readonly Point[] Corners = [new(79, 17), new(88, 20), new(79, 17), new(88, 20)];

    // /g only: [0x444c6] added before the 640 × 480 scaling.
    private static readonly Size HiresShift = new(-2, -3);

    // The green ramp: brightest to darkest palette index.
    private const int Bright = 0xb2, Dark = 0xbe;

    /// <summary>Whether the screen is drawn: the laser rifle's view-model is.</summary>
    public static bool Shown(in ViewModelState state) => state.Visible && state.Record == Record;

    /// <summary>The screen in 320 × 200 pixels from the gun frame's top-left at animation
    /// <paramref name="frame"/>; past the table, its last.</summary>
    public static Rectangle Area(int frame, HudArtSet set)
    {
        var corner = Corners[Math.Clamp(frame, 0, Corners.Length - 1)];
        if (set == HudArtSet.HiRes)
        {
            corner += HiresShift;
        }

        return new Rectangle(corner, Pixels);
    }

    /// <summary>The green table (0x137100): each index by its 6-bit brightness
    /// avg(r, g, b) to <see cref="Dark"/> − brightness · 2/5, at most <see cref="Bright"/>.</summary>
    public static byte[] Remap(Palette palette)
    {
        var table = new byte[Palette.Colors];
        for (var i = 0; i < table.Length; i++)
        {
            // Back to the VGA's 6 bits (Palette.Parse widened them).
            var (r, g, b) = palette[i];
            var light = ((r >> 2) + (g >> 2) + (b >> 2)) / 3;
            table[i] = (byte)Math.Clamp(Dark - light * 2 / 5, Bright, Dark);
        }

        return table;
    }

    /// <summary>The scope's clip space from the main view's: <see cref="Zoom"/>× about the
    /// <paramref name="crosshair"/> (frame pixels), so each <paramref name="scope"/> pixel shows half a
    /// frame pixel. The crosshair sits above the panel, off the frame's middle (issue #18).
    /// <code>
    ///  ndc' = k · (ndc − crosshair's ndc),  k = Zoom · frame / scope   (in clip: − k · c · w)
    /// </code></summary>
    public static Matrix4x4 Magnify(Vector2 crosshair, Size frame, SizeF scope)
    {
        var k = new Vector2(Zoom * frame.Width / scope.Width, Zoom * frame.Height / scope.Height);
        var centre = new Vector2(2f * crosshair.X / frame.Width - 1f, 1f - 2f * crosshair.Y / frame.Height);

        // Row vectors: the w row moves the crosshair to the clip origin, scaled with the rest.
        var zoom = Matrix4x4.CreateScale(k.X, k.Y, 1f);
        zoom.M41 = -k.X * centre.X;
        zoom.M42 = -k.Y * centre.Y;
        return zoom;
    }

    /// <summary>The scope's ground where nothing is drawn: the cleared frame's index
    /// (<see cref="Level.NightSkyIndex"/>) through the green table, as DOS's buffer holds it (issue #23).</summary>
    public static byte Background(Palette palette) => Remap(palette)[Level.NightSkyIndex];
}
