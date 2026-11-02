using System.Drawing;
using System.Numerics;
using SkyNet.Formats;
using SkyNet.Game.Hud;
using SkyNet.Game.Tests.Behaviour;
using SkyNet.Game.Weapons;

namespace SkyNet.Game.Tests.Weapons;

// Issue #12: the laser rifle's green scope screen (SKYNET.EXE v1.01 FUN_00125d1a, 0x137100, 0x1371b4).
public class LaserScopeTests
{
    [Fact]
    public void Shown_only_with_the_laser_rifle_in_hand()
    {
        Assert.True(LaserScope.Shown(new ViewModelState(LaserScope.Record)));
        Assert.False(LaserScope.Shown(new ViewModelState(8)));
        Assert.False(LaserScope.Shown(new ViewModelState(LaserScope.Record, OnFoot: false)));
        Assert.False(LaserScope.Shown(new ViewModelState(LaserScope.Record, Hidden: true)));
        Assert.False(LaserScope.Shown(new ViewModelState(LaserScope.Record, RocketEye: true)));
    }

    // Table 0x444a6: the screen's corner from the gun frame's, per animation frame (recoil shifts it).
    [Theory]
    [InlineData(0, 79, 17)]
    [InlineData(1, 88, 20)]
    [InlineData(2, 79, 17)]
    [InlineData(3, 88, 20)]
    [InlineData(9, 88, 20)]
    public void Screen_follows_the_frame(int frame, int x, int y) =>
        Assert.Equal(new Rectangle(x, y, 44, 30), LaserScope.Area(frame, HudArtSet.LowRes));

    // /g: [0x444c6] = (−2, −3) more, the size scaled with the screen (FUN_0012ad2e).
    [Fact]
    public void Hires_screen_moves_up_left() =>
        Assert.Equal(new Rectangle(77, 14, 44, 30), LaserScope.Area(0, HudArtSet.HiRes));

    // FUN_00137100: index = clamp(0xbe − avg(r, g, b)·2/5, 0xb2, 0xbe) on the 6-bit palette.
    [Theory]
    [InlineData(0, 0, 0, 0xbe)]
    [InlineData(63, 63, 63, 0xb2)]
    [InlineData(20, 20, 20, 0xb6)]
    [InlineData(0, 0, 63, 0xb6)]
    [InlineData(30, 30, 30, 0xb2)]
    public void Remap_ramps_by_brightness(byte r, byte g, byte b, int expected)
    {
        var raw = new byte[Palette.RawSize];
        raw[3] = r;
        raw[4] = g;
        raw[5] = b;
        var remap = LaserScope.Remap(Palette.Parse(raw)!);
        Assert.Equal(expected, remap[1]);
        Assert.Equal(0xbe, remap[0]);
    }

    // The ramp's ends in SKYNET.COL: 0xb2 the brightest green, 0xbe the darkest.
    [BehaviourDataFact]
    public void Ramp_is_green_in_the_game_palette()
    {
        var palette = BehaviourData.Data.LoadPalette()!;
        var bright = palette[0xb2];
        var dark = palette[0xbe];
        Assert.True(bright.G > bright.R && bright.G > bright.B);
        Assert.True(bright.G > dark.G);
    }

    // Issue #18: the scope magnifies about the crosshair, which DOS puts above the panel (the
    // projection's centre shifted up), not about the frame's middle.
    [Theory]
    [InlineData(1280, 720, 640, 280)]
    [InlineData(790, 498, 395, 194)]
    [InlineData(800, 600, 400, 300)]
    public void Magnify_centres_the_crosshair(int width, int height, float x, float y)
    {
        var scope = new SizeF(88f, 72f);
        var zoom = LaserScope.Magnify(new Vector2(x, y), new Size(width, height), scope);

        // A frame pixel → its clip point (w = 3, as a perspective's) → the scope's NDC.
        Vector2 Scope(float px, float py)
        {
            const float w = 3f;
            var clip = new Vector4((2f * px / width - 1f) * w, (1f - 2f * py / height) * w, 0f, w);
            var to = Vector4.Transform(clip, zoom);
            return new Vector2(to.X / to.W, to.Y / to.W);
        }

        AssertNear(Vector2.Zero, Scope(x, y));

        // Each scope pixel shows half a frame pixel: half the scope's width away reaches its edge.
        AssertNear(new Vector2(1f, 0f), Scope(x + scope.Width / (2f * LaserScope.Zoom), y));
        AssertNear(new Vector2(0f, -1f), Scope(x, y + scope.Height / (2f * LaserScope.Zoom)));
    }

    // Issue #23: where nothing is drawn (the cleared sky) the scope shows the sky's green, not the
    // gun art's monitor under it; DOS's buffer holds the whole frame, the clear colour too.
    [BehaviourDataFact]
    public void Sky_shows_its_green()
    {
        var palette = BehaviourData.Data.LoadPalette()!;
        Assert.Equal(LaserScope.Remap(palette)[Level.NightSkyIndex], LaserScope.Background(palette));
    }

    private static void AssertNear(Vector2 expected, Vector2 actual)
    {
        Assert.InRange(actual.X, expected.X - 1e-4f, expected.X + 1e-4f);
        Assert.InRange(actual.Y, expected.Y - 1e-4f, expected.Y + 1e-4f);
    }
}
