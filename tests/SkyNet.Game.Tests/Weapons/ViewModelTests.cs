using System.Drawing;
using SkyNet.Game.Hud;
using SkyNet.Game.Weapons;

namespace SkyNet.Game.Tests.Weapons;

public class ViewModelTests
{
    [Theory]
    [InlineData(0.2f, 4, 0.05f)]      // UZI: rate 5, four frames
    [InlineData(0.5f, 7, 0.5f / 7f)]  // the pipe's swing
    [InlineData(1f, 5, 0.125f)]       // the rocket launcher: capped
    [InlineData(1f, 1, 0f)]           // one frame: no animation
    public void Frame_length_divides_the_cooldown(float cooldown, int frames, float expected) =>
        Assert.Equal(expected, ViewModelAnimation.FrameSeconds(cooldown, frames), 5);

    [Fact]
    public void A_shot_plays_frames_one_to_last_then_rests()
    {
        const float step = 0.05f;
        Assert.Equal(1, ViewModelAnimation.FrameAt(0f, step, 4));
        Assert.Equal(1, ViewModelAnimation.FrameAt(0.049f, step, 4));
        Assert.Equal(2, ViewModelAnimation.FrameAt(0.051f, step, 4));
        Assert.Equal(3, ViewModelAnimation.FrameAt(0.149f, step, 4));
        Assert.Equal(0, ViewModelAnimation.FrameAt(0.151f, step, 4));
        Assert.Equal(0, ViewModelAnimation.FrameAt(0.01f, step, 1));
    }

    [Fact]
    public void Held_fire_runs_the_cycle_back_to_back()
    {
        var a = new ViewModelAnimation();
        const float dt = 1f / 60f, cooldown = 0.2f;
        a.Tick(dt, 1, 4, cooldown);
        Assert.Equal(0, a.Frame);
        var seen = new List<int>();
        for (var shot = 0; shot < 2; shot++)
        {
            a.Fire();
            for (var t = 0; t < 12; t++) // 0.2 s at 60 Hz
            {
                a.Tick(dt, 1, 4, cooldown);
                seen.Add(a.Frame);
            }

            Assert.False(a.Firing);
        }

        // 1, 2, 3 in order, then the rest pose before the next shot.
        var first = seen.Take(12).ToList();
        Assert.Equal(1, first[0]);
        Assert.Equal([1, 2, 3, 0], first.Distinct());
        Assert.Equal(0, first[11]);
        Assert.Equal(seen.Take(12), seen.Skip(12));
    }

    [Fact]
    public void Changing_the_gun_drops_the_animation()
    {
        var a = new ViewModelAnimation();
        a.Tick(0f, 0, 7, 0.5f);
        a.Fire();
        a.Tick(0.1f, 0, 7, 0.5f);
        Assert.True(a.Firing);
        Assert.NotEqual(0, a.Frame);
        a.Tick(0.01f, 1, 4, 0.2f);
        Assert.False(a.Firing);
        Assert.Equal(0, a.Frame);
    }

    [Fact]
    public void A_throw_dips_and_returns()
    {
        var a = new ViewModelAnimation();
        a.Throw();
        a.Tick(ViewModelAnimation.ThrowSeconds / 2f, 1, 4, 0.2f);
        Assert.Equal(1f, a.ThrowDip, 3);
        a.Tick(ViewModelAnimation.ThrowSeconds, 1, 4, 0.2f);
        Assert.Equal(0f, a.ThrowDip);
    }

    [Fact]
    public void Hidden_on_the_vehicle_the_rocket_eye_and_without_a_gun()
    {
        Assert.True(new ViewModelState(1).Visible);
        Assert.False(new ViewModelState(1, OnFoot: false).Visible);
        Assert.False(new ViewModelState(1, RocketEye: true).Visible);
        Assert.False(new ViewModelState(1, Hidden: true).Visible);
        Assert.False(new ViewModelState(-1).Visible);
        Assert.Null(ViewModelLayout.CfaName(20));
        Assert.Null(ViewModelLayout.CfaName(15));
        Assert.Equal("WEAPON06.CFA", ViewModelLayout.CfaName(6));
    }

    [Theory]
    [InlineData(1269, 700)]
    [InlineData(1920, 1080)]
    [InlineData(1024, 768)]
    [InlineData(2560, 1080)]
    public void The_gun_stands_on_the_panel_at_the_crosshair_column(int w, int h)
    {
        var s = ViewModelLayout.Scale(w, h);
        var panel = DosHud.PanelPixels(w, h);
        Assert.Equal((h - panel) / 160f, s, 4);

        // A frame whose left edge is DOS x 160 starts on the crosshair's column.
        var centre = DosHud.ViewCentre(w, h);
        var r = ViewModelLayout.Place(w, h, 160, new Size(72, 87));
        Assert.Equal(centre.X, r.X, 0);
        Assert.Equal(MathF.Round(72 * s), r.Width, 1);
        // Its foot sinks Overlap DOS pixels behind the panel's top edge.
        Assert.Equal(h - panel + ViewModelLayout.Overlap * s, r.Bottom, 0.51);
        Assert.Equal(87 * s, r.Height, 1.01);
        // Whole pixels.
        Assert.Equal(MathF.Round(r.X), r.X);
        Assert.Equal(MathF.Round(r.Y), r.Y);
        // DOS's view centre (80 lines above the panel) is half the view above it.
        Assert.Equal(h - panel - 80 * s, centre.Y, 0.51);
    }

    [Fact]
    public void A_throw_lowers_the_rectangle()
    {
        var rest = ViewModelLayout.Place(1920, 1080, 156, new Size(72, 87));
        var low = ViewModelLayout.Place(1920, 1080, 156, new Size(72, 87), dip: 1f);
        Assert.Equal(rest.X, low.X);
        Assert.Equal(ViewModelLayout.ThrowDrop * ViewModelLayout.Scale(1920, 1080), low.Y - rest.Y, 0.51);
    }

    [Fact]
    public void The_executable_view_x_matches_the_godot_table()
    {
        var table = WeaponTable.Shipped;
        for (var r = 0; r < ViewModelLayout.GodotViewX.Count; r++)
        {
            Assert.Equal(ViewModelLayout.GodotViewX[r], table.Weapons[r].ViewX);
        }
    }
}
