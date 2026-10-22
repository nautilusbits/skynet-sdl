using System.Numerics;
using System.Text;
using RetroEng.Models;
using SkyNet.Game.Mods;
using SkyNet.Game.Weapons;

namespace SkyNet.Game.Tests.Mods;

[Collection(ModsCollection.Name)]
public class ViewModelModelsTests
{
    private static byte[] Json(string text) => Encoding.UTF8.GetBytes(text);

    [Theory]
    [InlineData(0, "PIPE")]
    [InlineData(1, "UZI")]
    [InlineData(2, "ASSAULT_RIFLE")]
    [InlineData(5, "GRENADE_LAUNCHER")]
    [InlineData(12, "SUPER_UZI")]
    public void A_held_weapon_is_named_by_the_weapon_table(int record, string name) =>
        Assert.Equal(name, ViewModelModels.FileName(record));

    [Theory]
    [InlineData(-1)]
    [InlineData(13)]  // the motion detector: its plot is drawn on its art
    [InlineData(14)]  // a thrown item: never in hand
    [InlineData(20)]  // a vehicle gun
    public void Records_without_a_view_model_file(int record) => Assert.Null(ViewModelModels.FileName(record));

    [Fact]
    public void An_empty_sidecar_is_the_default()
    {
        Assert.Equal(ViewModelSidecar.Default, ViewModelSidecar.Parse(Json("{}")));
    }

    [Fact]
    public void A_sidecar_reads_every_key()
    {
        var s = ViewModelSidecar.Parse(Json(
            """{"offset":[1,-2,-30],"offsetWide":[2,-1,-30],"scale":40,"rotation":[0,180,0],"recoilBack":3,"recoilPitch":6,"swing":50,"sway":0,"pivot":[0,-1,0.5],"muzzle":[0,0.1,-3],"flashSize":9,"minLight":0.2,"comment":"x","licence":"CC0"}"""));

        Assert.Equal(new Vector3(1, -2, -30), s.Offset);
        Assert.Equal(40f, s.Scale);
        Assert.Equal(new Vector3(0, 180, 0), s.Rotation);
        Assert.Equal((3f, 6f, 50f, 0f), (s.RecoilBack, s.RecoilPitch, s.Swing, s.Sway));
        Assert.Equal(new Vector3(2, -1, -30), s.OffsetWide);
        Assert.Equal(new Vector3(0, -1, 0.5f), s.Pivot);
        Assert.Equal(new Vector3(0, 0.1f, -3), s.Muzzle);
        Assert.Equal((9f, 0.2f), (s.FlashSize, s.MinLight));
    }

    [Fact]
    public void The_defaults_have_a_light_floor_and_no_muzzle()
    {
        var s = ViewModelSidecar.Default;
        Assert.Equal(0.35f, s.MinLight);
        Assert.Null(s.Muzzle);
        Assert.Null(s.Pivot);
        Assert.Null(s.OffsetWide);
    }

    [Theory]
    [InlineData("""{"fit":"height"}""")]
    [InlineData("""{"offset":[1,2]}""")]
    [InlineData("""{"scale":-1}""")]
    [InlineData("""{"recoilBack":"far"}""")]
    [InlineData("""{"minLight":1.5}""")]
    [InlineData("""{"muzzle":[0,0]}""")]
    public void A_bad_sidecar_does_not_read(string json) =>
        Assert.ThrowsAny<FormatException>(() => ViewModelSidecar.Parse(Json(json)));

    /// <summary>A 1 x 1 x 4 box, its long side along Z (the barrel), centred on (0, 0, -1).</summary>
    private static readonly GltfPrimitive Box = new(
        [new(-0.5f, -0.5f, -3f), new(0.5f, 0.5f, 1f), new(0.5f, -0.5f, 1f)], null, null, [0, 1, 2], -1);

    [Fact]
    public void A_mod_file_is_drawn_in_the_enhanced_look_only()
    {
        var root = Directory.CreateTempSubdirectory("skynet-mods-").FullName;
        try
        {
            var dir = Path.Combine(root, "m", "models", "viewmodels");
            Directory.CreateDirectory(dir);
            File.WriteAllBytes(Path.Combine(dir, "UZI.glb"), Gltf.WriteGlb([Box], [], []));
            File.WriteAllBytes(Path.Combine(dir, "UZI.json"), Json("""{"recoilBack":5}"""));
            File.WriteAllBytes(Path.Combine(dir, "SHOTGUN.glb"), Gltf.WriteGlb([Box], [], []));
            File.WriteAllBytes(Path.Combine(dir, "SHOTGUN.json"), Json("""{"nope":1}"""));
            File.WriteAllBytes(Path.Combine(dir, "PIPE.glb"), [1, 2, 3]);
            ModContent.Mount(root, ["m"]);

            var uzi = ViewModelModels.Drawn(1, enhanced: true);
            Assert.NotNull(uzi);
            Assert.Equal(5f, uzi.Sidecar.RecoilBack);
            Assert.Equal((new Vector3(-0.5f, -0.5f, -3f), new Vector3(0.5f, 0.5f, 1f)), uzi.Box);
            Assert.Same(uzi, ViewModelModels.Drawn(1, enhanced: true));
            // The light floor (minLight's default) is in the surfaces' emission.
            Assert.All(uzi.Model.Surfaces, x => Assert.True(x.Maps));

            Assert.Null(ViewModelModels.Drawn(1, enhanced: false));  // the original look: the DOS art
            Assert.Null(ViewModelModels.Drawn(4, enhanced: true));   // a bad sidecar: the DOS art
            Assert.Null(ViewModelModels.Drawn(0, enhanced: true));   // a broken file
            Assert.Null(ViewModelModels.Drawn(2, enhanced: true));   // none

            // In models/viewmodels/: not a replacement of a DOS model named the same (UZI.3D).
            Assert.Null(ModContent.Model("UZI"));

            // A remount reads the files again.
            ModContent.Mount(root, ["m"]);
            Assert.NotSame(uzi, ViewModelModels.Drawn(1, enhanced: true));
        }
        finally
        {
            ModContent.Unmount();
            Directory.Delete(root, recursive: true);
        }

        Assert.Null(ViewModelModels.Drawn(1, enhanced: true));
    }

    private static readonly (Vector3, Vector3) UnitBox = (new Vector3(-0.5f, -0.5f, -3f), new Vector3(0.5f, 0.5f, 1f));

    private static readonly ViewModelSidecar Still = ViewModelSidecar.Default with { Sway = 0f };

    private static Vector3 At(Vector3 p, Matrix4x4 m) => Vector3.Transform(p, m);

    private static void Near(Vector3 expected, Vector3 actual, float tolerance = 1e-3f) =>
        Assert.True(Vector3.Distance(expected, actual) < tolerance, $"expected {expected}, got {actual}");

    [Fact]
    public void At_rest_the_box_centre_sits_on_the_offset_and_scale_zero_fits_the_long_side()
    {
        var m = ViewModelMotion.Local(Still, UnitBox, ViewModelMotion.Rest);

        Near(Still.Offset, At(new Vector3(0, 0, -1), m));
        // The 4-unit side becomes FitLength long, along -Z still.
        var length = Vector3.Distance(At(new Vector3(0, 0, -3), m), At(new Vector3(0, 0, 1), m));
        Assert.Equal(ViewModelMotion.FitLength, length, 3);

        var scaled = ViewModelMotion.Local(Still with { Scale = 10f }, UnitBox, ViewModelMotion.Rest);
        Assert.Equal(40f, Vector3.Distance(At(new Vector3(0, 0, -3), scaled), At(new Vector3(0, 0, 1), scaled)), 3);
    }

    [Fact]
    public void A_wide_view_slides_to_offsetWide()
    {
        var s = Still with { Offset = new Vector3(6, -9, -24), OffsetWide = new Vector3(8, -7, -24) };
        Assert.Equal(new Vector3(6, -9, -24), s.OffsetFor(4f / 3f));
        Assert.Equal(new Vector3(8, -7, -24), s.OffsetFor(16f / 9f));
        Assert.Equal(new Vector3(8, -7, -24), s.OffsetFor(21f / 9f));    // wider: as 16:9
        Assert.Equal(new Vector3(6, -9, -24), s.OffsetFor(1f));          // narrower: as 4:3
        Near(new Vector3(7, -8, -24), s.OffsetFor((4f / 3f + 16f / 9f) / 2f));
        Assert.Equal(new Vector3(6, -9, -24), (s with { OffsetWide = null }).OffsetFor(16f / 9f));

        // The box centre goes there (no automatic lift with offsetWide given).
        var wide = ViewModelMotion.Frame.Of(1280, 720, SkyNet.Game.Hud.HudStyle.Classic, 1f);
        Near(new Vector3(8, -7, -24), At(new Vector3(0, 0, -1), ViewModelMotion.Local(s, UnitBox, ViewModelMotion.Rest, wide)));
    }

    [Fact]
    public void Without_offsetWide_the_gun_rises_as_far_as_the_panel_does()
    {
        var s = Still with { Offset = new Vector3(6, -9, -24) };
        var centre = new Vector3(0, 0, -1);
        Near(s.Offset, At(centre, ViewModelMotion.Local(s, UnitBox, ViewModelMotion.Rest, ViewModelMotion.Frame.Of(640, 480, SkyNet.Game.Hud.HudStyle.Classic, 1f))));

        // 16:9: the panel's top is higher on the view (78 % of the frame against 83 %), the view
        // centre too; the gun keeps its foot's distance above the panel: it rises by the gap's change.
        var wide = ViewModelMotion.Frame.Of(1280, 720, SkyNet.Game.Hud.HudStyle.Classic, 1f);
        Assert.True(wide.Gap < ViewModelMotion.Frame.Reference.Gap);
        var lifted = At(centre, ViewModelMotion.Local(s, UnitBox, ViewModelMotion.Rest, wide));
        Assert.Equal((ViewModelMotion.Frame.Reference.Gap - wide.Gap) * 2f * 24f, lifted.Y - s.Offset.Y, 3);
        Assert.Equal((s.Offset.X, s.Offset.Z), (lifted.X, lifted.Z));

        // The MODERN HUD has no panel: the gun goes down to the frame's foot.
        var modern = At(centre, ViewModelMotion.Local(s, UnitBox, ViewModelMotion.Rest, ViewModelMotion.Frame.Of(1280, 720, SkyNet.Game.Hud.HudStyle.Modern, 1f)));
        Assert.True(modern.Y < s.Offset.Y - 1f);
    }

    [Fact]
    public void The_lift_slides_the_gun_along_the_shots_line()
    {
        // The laser cannon's shot leaves 16 right and 14 down of the eye and flies to the view
        // centre; a gun lowered for the MODERN HUD moves right too, so the shot still leaves its barrel.
        var s = Still with { Offset = new Vector3(6, -9, -24) };
        var centre = new Vector3(0, 0, -1);
        var shot = new Vector2(16, 14);
        var frame = ViewModelMotion.Frame.Of(1280, 720, SkyNet.Game.Hud.HudStyle.Modern, 1f);
        var moved = At(centre, ViewModelMotion.Local(s, UnitBox, ViewModelMotion.Rest, frame, shot)) - s.Offset;

        Assert.True(moved.Y < -1f);
        Assert.Equal(-moved.Y * 16f / 14f, moved.X, 3);
        Assert.Equal(0f, moved.Z, 3);

        // A shot from the eye (hitscan) has no line: the gun only goes down.
        var plain = At(centre, ViewModelMotion.Local(s, UnitBox, ViewModelMotion.Rest, frame, Vector2.Zero)) - s.Offset;
        Assert.Equal(0f, plain.X, 3);
    }

    [Fact]
    public void A_shot_near_eye_height_keeps_the_gun_in_place()
    {
        // The rocket launcher's shot leaves 18 right and 1 down: its line is too flat to follow
        // down, so the gun stays where the sidecar puts it (it slid far off the screen).
        var s = Still with { Offset = new Vector3(11, -8, -18) };
        var frame = ViewModelMotion.Frame.Of(1280, 720, SkyNet.Game.Hud.HudStyle.Modern, 1f);
        var moved = At(new Vector3(0, 0, -1), ViewModelMotion.Local(s, UnitBox, ViewModelMotion.Rest, frame, new Vector2(18, 1))) - s.Offset;

        Assert.Equal(Vector3.Zero, moved);
    }

    [Fact]
    public void A_pivot_holds_still_while_the_gun_turns_about_it()
    {
        var grip = new Vector3(0, -0.5f, 1f);
        var s = Still with { RecoilBack = 0f, RecoilPitch = 20f, Pivot = grip };
        var rest = ViewModelMotion.Local(s, UnitBox, ViewModelMotion.Rest);
        var kick = ViewModelMotion.Local(s, UnitBox, ViewModelMotion.Rest with { Fire = ViewModelMotion.KickPeak });

        Near(At(grip, rest), At(grip, kick));
        var muzzle = new Vector3(0, 0, -3);
        Assert.True(At(muzzle, kick).Y > At(muzzle, rest).Y + 1f);
        // At rest the box centre still sits on the offset.
        Near(s.Offset, At(new Vector3(0, 0, -1), rest));

        // Without a pivot the box centre holds still.
        var centred = Still with { RecoilBack = 0f, RecoilPitch = 20f };
        Near(At(new Vector3(0, 0, -1), ViewModelMotion.Local(centred, UnitBox, ViewModelMotion.Rest)),
            At(new Vector3(0, 0, -1), ViewModelMotion.Local(centred, UnitBox, ViewModelMotion.Rest with { Fire = ViewModelMotion.KickPeak })));
    }

    [Theory]
    [InlineData(0, -1f, false, false)]
    [InlineData(1, 0.1f, false, true)]   // the shot's first frame
    [InlineData(2, 0.6f, false, false)]
    [InlineData(1, 0.1f, true, false)]   // the pipe: no flash
    [InlineData(0, 0.2f, false, true)]   // a one-frame gun early in its shot
    [InlineData(0, 0.9f, false, false)]  // ... and late in it
    public void The_flash_shows_on_the_first_fire_frame(int frame, float fire, bool melee, bool shown) =>
        Assert.Equal(shown, ViewModelMotion.Flashes(frame, fire, melee));

    [Fact]
    public void The_kick_rises_fast_then_decays_to_rest()
    {
        Assert.Equal(0f, ViewModelMotion.Kick(-1f));
        Assert.Equal(0f, ViewModelMotion.Kick(0f));
        Assert.Equal(1f, ViewModelMotion.Kick(ViewModelMotion.KickPeak), 4);
        Assert.Equal(0f, ViewModelMotion.Kick(1f));
        Assert.True(ViewModelMotion.Kick(0.4f) > ViewModelMotion.Kick(0.7f));
        Assert.True(ViewModelMotion.Kick(0.7f) > 0f);
    }

    [Fact]
    public void A_shot_pushes_the_gun_back_and_its_muzzle_up()
    {
        var s = Still with { RecoilBack = 3f, RecoilPitch = 10f };
        var rest = ViewModelMotion.Local(s, UnitBox, ViewModelMotion.Rest);
        var kick = ViewModelMotion.Local(s, UnitBox, ViewModelMotion.Rest with { Fire = ViewModelMotion.KickPeak });

        var centre = new Vector3(0, 0, -1);
        Near(At(centre, rest) + new Vector3(0, 0, 3f), At(centre, kick));
        var muzzle = new Vector3(0, 0, -3);
        Assert.True(At(muzzle, kick).Y > At(muzzle, rest).Y + 1f);

        // Over: back at rest.
        Near(At(muzzle, rest), At(muzzle, ViewModelMotion.Local(s, UnitBox, ViewModelMotion.Rest with { Fire = 1f })));
    }

    [Fact]
    public void A_throw_lowers_the_gun()
    {
        var rest = ViewModelMotion.Local(Still, UnitBox, ViewModelMotion.Rest);
        var dip = ViewModelMotion.Local(Still, UnitBox, ViewModelMotion.Rest with { ThrowDip = 1f });
        Near(At(Vector3.Zero, rest) - new Vector3(0, ViewModelMotion.DipDrop, 0), At(Vector3.Zero, dip));
    }

    [Fact]
    public void The_pipe_swings_left_and_down_and_back()
    {
        var pipe = ViewModelMotion.Rest with { Melee = true };
        var rest = ViewModelMotion.Local(Still, UnitBox, pipe);
        var mid = ViewModelMotion.Local(Still, UnitBox, pipe with { Fire = 0.5f });
        var end = ViewModelMotion.Local(Still, UnitBox, pipe with { Fire = 1f });

        var tip = new Vector3(0, 0, -3);
        Assert.True(At(tip, mid).X < At(tip, rest).X - 1f);
        Assert.True(At(tip, mid).Y < At(tip, rest).Y - 1f);
        Near(At(tip, rest), At(tip, end));

        // A gun's shot does not swing.
        var gun = ViewModelMotion.Local(Still with { RecoilBack = 0, RecoilPitch = 0 }, UnitBox, ViewModelMotion.Rest with { Fire = 0.5f });
        Near(At(tip, rest), At(tip, gun));
    }

    [Fact]
    public void Sway_moves_the_gun_a_little_over_time()
    {
        var s = ViewModelSidecar.Default;
        Assert.True(s.Sway > 0f);
        var a = At(Vector3.Zero, ViewModelMotion.Local(s, UnitBox, ViewModelMotion.Rest with { Clock = 0.7f }));
        var b = At(Vector3.Zero, ViewModelMotion.Local(s, UnitBox, ViewModelMotion.Rest with { Clock = 2.1f }));
        Assert.InRange(Vector3.Distance(a, b), 1e-3f, 3f * s.Sway);

        var still = At(Vector3.Zero, ViewModelMotion.Local(Still, UnitBox, ViewModelMotion.Rest with { Clock = 2.1f }));
        Near(At(Vector3.Zero, ViewModelMotion.Local(Still, UnitBox, ViewModelMotion.Rest)), still);
    }
}

public class ViewModelFireTests
{
    [Fact]
    public void Fire_progress_runs_over_the_shot_animation()
    {
        var a = new ViewModelAnimation();
        a.Tick(0.01f, 1, 4, 0.2f);
        Assert.Equal(-1f, a.FireProgress);

        a.Fire();
        a.Tick(0.075f, 1, 4, 0.2f);  // frames of 0.05 s: the cycle is 0.15 s
        Assert.Equal(0.5f, a.FireProgress, 3);
        Assert.Equal(0.075f + 0.01f, a.Clock, 4);

        a.Tick(0.1f, 1, 4, 0.2f);
        Assert.Equal(-1f, a.FireProgress);
    }

    [Fact]
    public void A_gun_with_one_art_frame_still_kicks()
    {
        // The machine gun's and the laser cannon's CFA have one frame: no fire frames to play,
        // yet a 3D gun needs the shot to kick it and flash at its muzzle.
        var a = new ViewModelAnimation();
        a.Tick(0.01f, 3, 1, 0.1f);
        a.Fire();
        a.Tick(0.02f, 3, 1, 0.1f);
        Assert.Equal(0, a.Frame);
        Assert.InRange(a.FireProgress, 0.01f, 0.5f);
        Assert.True(ViewModelMotion.Flashes(a.Frame, a.FireProgress, melee: false));

        a.Tick(0.2f, 3, 1, 0.1f);
        Assert.Equal(-1f, a.FireProgress);
        Assert.False(ViewModelMotion.Flashes(a.Frame, a.FireProgress, melee: false));
    }
}
