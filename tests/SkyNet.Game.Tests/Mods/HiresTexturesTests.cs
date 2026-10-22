using SkyNet.Upscale;
using SkyNet.Formats;
using SkyNet.Game.Menu;
using SkyNet.Game.Mods;
using SkyNet.Game.Tests.Menu;

namespace SkyNet.Game.Tests.Mods;

/// <summary>GENERATE HI-RES TEXTURES' record pipeline (tools/upscale/upscale.py's) on tiny records
/// with a nearest upscaler, and the MODS screen's button with a fake job (no download, no GPU).</summary>
public class HiresTexturesTests
{
    private static readonly MenuFrame Frame = new(1280, 800);

    private static byte[] Palette()
    {
        var p = new byte[768];
        for (var i = 0; i < 256; i++)
        {
            (p[i * 3], p[i * 3 + 1], p[i * 3 + 2]) = ((byte)i, (byte)(i * 2), (byte)(255 - i));
        }

        return p;
    }

    /// <summary>Nearest by a factor; keeps what it was given.</summary>
    private sealed class Nearest(int factor = 4) : IImageUpscaler
    {
        public List<RgbImage> Inputs { get; } = [];
        public int Factor => factor;

        public IUpscaledBatch Upscale(IReadOnlyList<RgbImage> images, Action<int>? done, CancellationToken cancel)
        {
            Inputs.AddRange(images);
            var output = images.Select(im =>
            {
                var rgb = new byte[im.Width * im.Height * factor * factor * 3];
                for (var y = 0; y < im.Height * factor; y++)
                {
                    for (var x = 0; x < im.Width * factor; x++)
                    {
                        im.Rgb.AsSpan(((y / factor) * im.Width + x / factor) * 3, 3).CopyTo(rgb.AsSpan((y * im.Width * factor + x) * 3));
                    }
                }

                return new RgbImage(im.Width * factor, im.Height * factor, rgb);
            }).ToList();
            done?.Invoke(output.Count);
            return new Results(output);
        }

        private sealed class Results(List<RgbImage> images) : IUpscaledBatch
        {
            public RgbImage this[int index] => images[index];
            public void Dispose() { }
        }
    }

    private static (byte, byte, byte) Px(RgbImage im, int x, int y) => (im.Rgb[(y * im.Width + x) * 3], im.Rgb[(y * im.Width + x) * 3 + 1], im.Rgb[(y * im.Width + x) * 3 + 2]);

    private static byte[] Run(TextureStack.Record rec, Nearest up)
    {
        byte[]? result = null;
        var frames = HiresTextures.Upscale([rec], Palette(), up, (_, rgba) => result = rgba, null, CancellationToken.None);
        Assert.Equal(rec.Frames, frames);
        return result!;
    }

    [Fact]
    public void A_sprite_is_edge_padded_its_hole_filled_cropped_and_cut_out_by_nearest()
    {
        // 3 x 1: index 10, a hole, index 20.
        var rec = new TextureStack.Record(200, 5, 3, 1, 1, [10, 0, 20], 0, Sprite: true);
        var up = new Nearest();

        var rgba = Run(rec, up);

        var input = Assert.Single(up.Inputs);
        Assert.Equal((3 + 16, 1 + 16), (input.Width, input.Height));
        // The hole took its neighbours' mean (15, 30, 240); edge copies left and right.
        Assert.Equal(((byte)15, (byte)30, (byte)240), Px(input, 9, 8));
        Assert.Equal(((byte)10, (byte)20, (byte)245), Px(input, 0, 0));
        Assert.Equal(((byte)20, (byte)40, (byte)235), Px(input, 18, 16));

        Assert.Equal(12 * 4 * 4, rgba.Length);
        for (var y = 0; y < 4; y++)
        {
            for (var x = 0; x < 12; x++)
            {
                var o = (y * 12 + x) * 4;
                var (r, g, b, a) = (rgba[o], rgba[o + 1], rgba[o + 2], rgba[o + 3]);
                switch (x / 4)
                {
                    case 0: Assert.Equal((10, 20, 245, 255), (r, g, b, a)); break;
                    // The hole: alpha 0, palette index 31's colour.
                    case 1: Assert.Equal((31, 62, 224, 0), (r, g, b, a)); break;
                    default: Assert.Equal((20, 40, 235, 255), (r, g, b, a)); break;
                }
            }
        }
    }

    [Fact]
    public void A_wall_is_padded_by_wrapping()
    {
        var rec = new TextureStack.Record(210, 0, 3, 1, 1, [1, 2, 3], 0, Sprite: false);
        var up = new Nearest();

        Run(rec, up);

        var input = Assert.Single(up.Inputs);
        // x = 7 is source x -1 → 2 (index 3); x = 11 is past the right edge → 0 (index 1).
        Assert.Equal(((byte)3, (byte)6, (byte)252), Px(input, 7, 0));
        Assert.Equal(((byte)1, (byte)2, (byte)254), Px(input, 11, 3));
    }

    [Fact]
    public void Animated_frames_go_through_one_by_one_and_come_out_stacked()
    {
        var rec = new TextureStack.Record(214, 1, 1, 1, 2, [40, 50], 100, Sprite: true);
        var up = new Nearest();

        var rgba = Run(rec, up);

        Assert.Equal(2, up.Inputs.Count);
        Assert.Equal(4 * 8 * 4, rgba.Length);
        Assert.Equal(40, rgba[0]);
        Assert.Equal(50, rgba[4 * 4 * 4]);
    }

    [Fact]
    public void Holes_grow_inwards_from_their_opaque_neighbours()
    {
        // 5 x 1: A, hole, hole, hole, B (no wrap): the outer holes first, then the middle.
        float[] rgb = [100, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 200, 0, 0];
        TexturePipeline.FillHoles(rgb, [true, false, false, false, true], 5, 1, wrap: false);

        Assert.Equal([100f, 100f, 150f, 200f, 200f], [rgb[0], rgb[3], rgb[6], rgb[9], rgb[12]]);
    }

    [Fact]
    public void Png_writer_writes_a_valid_header()
    {
        var png = PngWriter.Encode(2, 1, 4, [1, 2, 3, 255, 4, 5, 6, 0]);

        Assert.Equal([0x89, (byte)'P', (byte)'N', (byte)'G'], png[..4]);
        Assert.Equal(6, png[25]);   // colour type RGBA
        Assert.Equal(0, PngWriter.Encode(2, 1, 1, [7, 8])[25]);   // grey
    }

    private static byte[] Grey(int w, int h, Func<int, int, byte> at)
    {
        var rgba = new byte[w * h * 4];
        for (var y = 0; y < h; y++)
        {
            for (var x = 0; x < w; x++)
            {
                var v = at(x, y);
                (rgba[(y * w + x) * 4], rgba[(y * w + x) * 4 + 1], rgba[(y * w + x) * 4 + 2], rgba[(y * w + x) * 4 + 3]) = (v, v, v, 255);
            }
        }

        return rgba;
    }

    [Fact]
    public void Relief_of_an_even_surface_faces_straight_out()
    {
        var (normal, roughness) = ReliefMaps.Make(Grey(16, 16, (_, _) => 128), 16, 16, 1, 4, wrap: true);

        Assert.All(Enumerable.Range(0, 256), i => Assert.Equal((128, 128, 255), (normal[i * 3], normal[i * 3 + 1], normal[i * 3 + 2])));
        Assert.All(roughness, r => Assert.InRange(r, (byte)(ReliefMaps.MinRoughness * 255), (byte)255));
    }

    [Fact]
    public void Relief_tilts_away_from_the_brighter_side()
    {
        // Brighter to the right (+u) and further down (+v) in the middle of a clamped ramp.
        var (normal, _) = ReliefMaps.Make(Grey(32, 32, (x, y) => (byte)(x * 4 + y * 2)), 32, 32, 1, 4, wrap: false);
        var o = (16 * 32 + 16) * 3;

        Assert.True(normal[o] < 128);
        Assert.True(normal[o + 1] < 128);
        Assert.True(normal[o] < normal[o + 1]);
        Assert.True(normal[o + 2] > 200);
    }

    // ---------------------------------------------------------------- the MODS button

    private sealed class FakeJob : IMenuJob
    {
        public string Status { get; set; } = "UPSCALING 1 / 2";
        public MenuJobState State { get; set; } = MenuJobState.Running;
        public string? Error => null;
        public string ModName => HiresTextures.ModName;
        public bool Cancelled { get; private set; }

        public void Cancel()
        {
            Cancelled = true;
            State = MenuJobState.Cancelled;
            Status = "CANCELLED";
        }
    }

    private static ModEntry Mod(string name) => new(name, false, true, false, null, null, ModCarries.Textures);

    private static (MenuSystem Menu, List<FakeJob> Jobs, List<ModEntry> Found) OpenMods(MenuSettings settings)
    {
        List<FakeJob> jobs = [];
        List<ModEntry> found = [Mod("a")];
        var menu = new MenuSystem(new SettingsRecorder(), settings, new MenuContext
        {
            ModList = () => [.. found],
            GenerateHires = () =>
            {
                var job = new FakeJob();
                jobs.Add(job);
                return job;
            },
        }, null);
        menu.OpenTitle();
        foreach (var id in new[] { "options", "mods" })
        {
            menu.Current!.Select(id);
            menu.Update(new MenuInput { Accept = true }, Frame);
        }

        Assert.Equal("mods", menu.Current!.Name);
        return (menu, jobs, found);
    }

    private static void Generate(MenuSystem menu, bool yes)
    {
        menu.Current!.Select("generate");
        menu.Update(new MenuInput { Accept = true }, Frame);
        Assert.Equal("generatehires", menu.Current!.Name);
        menu.Current.Select(yes ? "yes" : "no");
        menu.Update(new MenuInput { Accept = true }, Frame);
        Assert.Equal("mods", menu.Current!.Name);
    }

    [Fact]
    public void Generate_asks_first_and_no_starts_nothing()
    {
        var (menu, jobs, _) = OpenMods(new MenuSettings());

        Generate(menu, yes: false);

        Assert.Empty(jobs);
    }

    [Fact]
    public void Esc_cancels_a_running_job_and_the_screen_stays()
    {
        var (menu, jobs, _) = OpenMods(new MenuSettings());
        Generate(menu, yes: true);
        var screen = (ModsScreen)menu.Current!;
        Assert.Equal("UPSCALING 1 / 2", screen.Note);

        menu.Update(new MenuInput { Back = true }, Frame);

        Assert.True(Assert.Single(jobs).Cancelled);
        Assert.Equal("mods", menu.Current!.Name);
        menu.Update(new MenuInput(), Frame);
        Assert.Null(screen.Job);
        Assert.Equal("CANCELLED", screen.Note);
    }

    [Fact]
    public void A_finished_job_turns_its_mod_on_and_the_list_is_read_again()
    {
        var settings = new MenuSettings();
        var (menu, jobs, found) = OpenMods(settings);
        Generate(menu, yes: true);
        var screen = (ModsScreen)menu.Current!;

        found.Add(Mod(HiresTextures.ModName));
        jobs[0].State = MenuJobState.Done;
        jobs[0].Status = "HI-RES TEXTURES READY";
        menu.Update(new MenuInput(), Frame);

        Assert.Equal([HiresTextures.ModName], settings.Mods);
        Assert.Equal([HiresTextures.ModName, "a"], screen.Rows.Select(r => r.Name));
        Assert.NotNull(screen.Find("row1"));
        Assert.Equal("HI-RES TEXTURES READY", screen.Note);
    }
}
