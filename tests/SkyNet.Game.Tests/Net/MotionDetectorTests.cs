using System.Numerics;
using RetroEng.Physics;
using SkyNet.Formats;
using SkyNet.Game.Net;
using SkyNet.Game.Tests.Behaviour;
using SkyNet.Game.Water;
using SkyNet.Game.Weapons;

namespace SkyNet.Game.Tests.Net;

/// <summary>DOS FUN_00132b00's marks (the TERMINATOR's machine vision, and the HUMAN's motion
/// detector here) and the vision's palette.</summary>
public class MotionDetectorTests
{
    private const int Width = 1280, Height = 720, View = 560;

    private static DmAvatar At(int id, string name, Vector3 feet)
    {
        var a = new DmAvatar(id, name, DmClass.Human);
        a.SpawnAt(feet, 0f);
        return a;
    }

    /// <summary>The eye at the origin looking down −Z, the view centred above the panel.</summary>
    private static Matrix4x4 ViewProjection(Vector3 eye)
    {
        var view = Matrix4x4.CreateLookAt(eye, eye - Vector3.UnitZ, Vector3.UnitY);
        var projection = Matrix4x4.CreatePerspectiveFieldOfView(MathF.PI / 3f, Width / (float)Height, 4f, 60000f);
        return view * projection;
    }

    [Fact]
    public void The_scanner_marks_the_bodies_in_range_and_in_view()
    {
        var eye = new Vector3(0f, 50f, 0f);
        var ahead = At(2, "bob", new Vector3(0f, 0f, -1000f));
        var far = At(3, "far", new Vector3(0f, 0f, -2600f));
        var close = At(4, "close", new Vector3(0f, 0f, -30f));
        var behind = At(5, "behind", new Vector3(0f, 0f, 1000f));
        var aside = At(6, "aside", new Vector3(2000f, 0f, -300f));
        var dead = At(7, "dead", new Vector3(300f, 0f, -1500f));
        dead.Die();
        var gone = At(8, "gone", new Vector3(-200f, 0f, -800f));
        gone.Die();
        gone.Step(DmAvatar.HideAfterDeath + 0.1f);

        var marks = MotionDetector.Marks([ahead, far, close, behind, aside, dead, gone], eye, ViewProjection(eye), Width, View, Height);

        Assert.Equal(["BOB", "DEAD"], marks.Select(m => m.Name));
        var bob = marks[0];
        Assert.False(bob.Dead);
        Assert.Equal(1000f, bob.Distance, 0.5f);
        Assert.Equal("25M", MotionDetector.Range(bob.Distance)); // 0x28 units a metre
        Assert.Equal(Width / 2f, bob.At.X, 0.5f);
        Assert.Equal(Height / 2f, bob.At.Y, 0.5f);
        Assert.True(marks[1].Dead);
        Assert.True(marks[1].At.X > Width / 2f);
    }

    [Fact]
    public void A_mark_under_the_panel_is_clipped_and_one_under_the_crosshair_flashes()
    {
        var eye = new Vector3(0f, 50f, 0f);
        // Below the line of sight: on the screen, but under the view's bottom edge (behind the panel).
        var low = At(2, "low", new Vector3(0f, -450f, -1000f));
        Assert.Empty(MotionDetector.Marks([low], eye, ViewProjection(eye), Width, View, Height));

        var centre = new Vector2(Width / 2f, Height / 2f);
        Assert.True(MotionDetector.Flashes(centre, centre, Width, Height, 0.0));
        Assert.False(MotionDetector.Flashes(centre, centre, Width, Height, 5.0 / 60.0)); // the dark half of the 8-frame beat
        Assert.False(MotionDetector.Flashes(centre + new Vector2(100f, 0f), centre, Width, Height, 0.0));
    }

    [Fact]
    public void The_detector_joins_the_cycle_while_owned_and_taking_it_puts_a_gun_back_in_hand()
    {
        var inv = new Inventory(WeaponTable.Shipped);
        inv.Grant(WeaponTable.SuperUzi);
        Assert.True(inv.Select(WeaponTable.SuperUzi));
        inv.Tick(1f);
        Assert.True(inv.Cycle(1));
        Assert.NotEqual(WeaponTable.MotionDetector, inv.Primary); // not owned: the ring is 0..12

        inv.Grant(WeaponTable.MotionDetector);
        Assert.True(inv.Select(WeaponTable.SuperUzi));
        Assert.True(inv.Cycle(1));
        Assert.Equal(WeaponTable.MotionDetector, inv.Primary);
        Assert.True(inv.Cycle(1));
        Assert.Equal(WeaponTable.Pipe, inv.Primary);
        Assert.True(inv.Cycle(-1));
        Assert.Equal(WeaponTable.MotionDetector, inv.Primary);

        inv.Take(WeaponTable.MotionDetector);
        Assert.False(inv.Owns(WeaponTable.MotionDetector));
        Assert.NotEqual(WeaponTable.MotionDetector, inv.Primary);
        Assert.True(inv.Owns(inv.Primary));
    }

    [Fact]
    public void The_scanner_fires_nothing()
    {
        var weapons = new WeaponSystem(WeaponTable.Shipped, new CollisionWorld());
        weapons.Inventory.Grant(WeaponTable.MotionDetector);
        weapons.Inventory.Select(WeaponTable.MotionDetector);
        weapons.Inventory.Tick(1f);
        Assert.NotEqual(FireResult.Fired, weapons.Fire(default));
    }

    [Fact]
    public void The_marks_reach_from_50_to_2500_units_whatever_stands_between()
    {
        // 0x132b72: past 0x9c4 or under 0x32 is skipped; no line-of-sight test, so walls do not hide.
        var eye = new Vector3(0f, 50f, 0f);
        static DmAvatar Ahead(int id, float d) => At(id, id.ToString(System.Globalization.CultureInfo.InvariantCulture), new Vector3(0f, 0f, -d));
        var marks = MotionDetector.Marks([Ahead(1, 49f), Ahead(2, 50f), Ahead(3, 2500f), Ahead(4, 2501f)], eye, ViewProjection(eye), Width, View, Height);
        Assert.Equal(["2", "3"], marks.Select(m => m.Name));
        Assert.Equal("62M", MotionDetector.Range(marks[1].Distance));
    }

    [BehaviourDataFact]
    public void The_vision_palette_is_skynttrm_col_over_skynet_col()
    {
        var data = BehaviourData.Data;
        var palette = data.LoadPalette()!;
        var machine = Palette.Parse(data.ColBytes(DmHud.MachinePaletteFile)!)!;
        var fit = WaterLook.Fit(palette, i => machine[i])!.Value;
        Assert.Equal(DmHud.MachinePalette.X, fit.X, 0.01f);
        Assert.Equal(DmHud.MachinePalette.Y, fit.Y, 0.01f);
        Assert.Equal(DmHud.MachinePalette.Z, fit.Z, 0.01f);
        Assert.Equal(DmHud.MachinePalette.W, fit.W, 0.01f);
        // A grey and the HUD's green through the tint land where the palette puts them.
        foreach (var i in new[] { 16, 88, 0xb3 })
        {
            var (r, g, b) = palette[i];
            var (tr, tg, tb) = machine[i];
            var t = DmHud.MachinePalette;
            Assert.Equal(tr, (1f - t.W) * r + t.W * t.X * 255f, 6f);
            Assert.Equal(tg, (1f - t.W) * g + t.W * t.Y * 255f, 6f);
            Assert.Equal(tb, (1f - t.W) * b + t.W * t.Z * 255f, 6f);
        }
    }
}
