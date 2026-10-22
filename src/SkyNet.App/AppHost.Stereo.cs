using System.Globalization;
using RetroEng.Commands;
using RetroEng.Platform;
using RetroEng.Render;
using SkyNet.Game.Menu;

internal sealed partial class AppHost
{
    /// <summary>--stereo's output for this run (not saved), until the console's <c>stereo</c> sets one.</summary>
    private StereoOutput? stereoRun;
    private bool stereoRunRead;

    /// <summary>The stereo output of this frame: --stereo, else the setting; the phone viewer's with its lens
    /// (--viewer, else the setting's preset or custom coefficients) and the screen's width (--screen-mm, else
    /// the phone's) that puts each eye under its lens.</summary>
    private StereoOutput StereoNow()
    {
        if (!stereoRunRead)
        {
            (stereoRun, stereoRunRead) = (options.Stereo, true);
        }

        var output = stereoRun ?? StereoOutput.Parse(settings.Stereo) ?? StereoOutput.Mono;
        return output.Layout == StereoLayout.Viewer
            ? output with { Lens = LensProfile.Preset(options.Viewer) ?? settings.Lens(), ScreenMm = options.ScreenMm ?? window.WidthMm }
            : output;
    }

    /// <summary>3D VIEW's BACKLIGHT on the platform's window (Android; the desktop has no hook).</summary>
    private readonly WindowBrightness windowBrightness = new(PlatformHooks.WindowBrightness);

    /// <summary>The settings' MODE last applied: 3D VIEW changing it ends --stereo's for this run.</summary>
    private string? appliedStereo;

    private void ApplyStereoSettings(MenuSettings s)
    {
        if (appliedStereo != null && appliedStereo != s.Stereo)
        {
            (stereoRun, stereoRunRead) = (null, true);
        }

        appliedStereo = s.Stereo;
    }

    /// <summary>The eyes' rig from the settings (world units); --canvas-depth for this run.</summary>
    private StereoRig StereoRigNow() => new(
        Math.Clamp(settings.StereoSeparation, 0, MenuSettings.MaxStereoSeparation),
        Math.Clamp(settings.StereoDepth, MenuSettings.MinStereoDepth, MenuSettings.MaxStereoDepth),
        Math.Clamp(options.CanvasDepth ?? settings.CanvasDepth, 0, MenuSettings.MaxCanvasDepth));

    /// <summary>The frame's 3D VIEW on the window: the head tracking and its aim (--head / --aim, else the
    /// settings), the phone viewer's tap-to-fire touch, the screen's brightness. The canvas's eye layout is set by the frame build.</summary>
    private void ApplyStereoFrame(StereoOutput stereo)
    {
        window.TrackHead(options.Head ?? settings.HeadTracking, options.Aim ?? settings.Aim);
        window.TapOnly(stereo.Layout == StereoLayout.Viewer);
        windowBrightness.Update(settings.ScreenBrightness, stereo.Layout);
    }

    /// <summary>The console's <c>stereo</c>, <c>viewer</c> and <c>head</c>: show or set the stereo output, the
    /// phone viewer's lens and the head tracking (saved, as the rest of the settings the console changes).</summary>
    private void RegisterStereoCommand()
    {
        var c = CultureInfo.InvariantCulture;
        commands.Add(new Command("stereo", "stereo [off|sbs|sbsr|sbsfull|crossview|tab|tabr|int|intr|viewer] [separation] [depth] [canvas depth]", args =>
        {
            if (args.OptionalWord(0) is { } name)
            {
                var output = StereoOutput.Parse(name) ?? throw new CommandUsageException();
                settings.Stereo = output.Name;
                stereoRun = null;
                stereoRunRead = true;
                if (args.OptionalWord(1) is { } separation)
                {
                    settings.StereoSeparation = Math.Clamp(int.Parse(separation, c), 0, MenuSettings.MaxStereoSeparation);
                }

                if (args.OptionalWord(2) is { } depth)
                {
                    settings.StereoDepth = Math.Clamp(int.Parse(depth, c), MenuSettings.MinStereoDepth, MenuSettings.MaxStereoDepth);
                }

                if (args.OptionalWord(3) is { } canvas)
                {
                    settings.CanvasDepth = Math.Clamp(int.Parse(canvas, c), 0, MenuSettings.MaxCanvasDepth);
                }

                SaveStereo();
            }

            return $"stereo {StereoNow().Name}, separation {settings.StereoSeparation}, depth {settings.StereoDepth}, canvas depth {settings.CanvasDepth}";
        })
        { Help = "the stereo output (both eyes on one screen), the eyes' distance apart, the depth shown at the screen and the HUD's depth (0: at the screen)", Arguments = i => i == 0 ? StereoOutput.Names : [] });

        commands.Add(new Command("viewer", "viewer [generic|cardboard1|cardboard2|flat|custom] [k1 k2]", args =>
        {
            if (args.OptionalWord(0) is { } name)
            {
                name = name.ToLowerInvariant();
                if (Array.IndexOf(LensProfile.Names, name) < 0)
                {
                    throw new CommandUsageException();
                }

                settings.Viewer = name;
                if (args.OptionalWord(1) is { } k1 && args.OptionalWord(2) is { } k2)
                {
                    settings.Viewer = "custom";
                    settings.LensK1 = Math.Clamp((int)MathF.Round(float.Parse(k1, c) * 100f), 0, MenuSettings.MaxLensK);
                    settings.LensK2 = Math.Clamp((int)MathF.Round(float.Parse(k2, c) * 100f), 0, MenuSettings.MaxLensK);
                }

                SaveStereo();
            }

            var lens = settings.Lens();
            return string.Create(c, $"viewer {settings.Viewer}: k1 {lens.K1:0.00}, k2 {lens.K2:0.00} (stereo viewer shows it)");
        })
        { Help = "the phone viewer's lens: a preset, or custom with its two distortion coefficients", Arguments = i => i == 0 ? LensProfile.Names : [] });

        commands.Add(new Command("head", "head [off|gyro|scripted] [coupled|decoupled]", args =>
        {
            if (args.OptionalWord(0) is { } name)
            {
                settings.HeadTracking = Enum.TryParse<HeadTracking>(name, true, out var head) ? head : throw new CommandUsageException();
                if (args.OptionalWord(1) is { } aim)
                {
                    settings.Aim = Enum.TryParse<AimCoupling>(aim, true, out var coupling) ? coupling : throw new CommandUsageException();
                }

                SaveStereo();
            }

            return $"head {(options.Head ?? settings.HeadTracking).ToString().ToLowerInvariant()}, aim {(options.Aim ?? settings.Aim).ToString().ToLowerInvariant()}";
        })
        { Help = "head tracking (the phone's gyroscope; scripted: a slow sweep) and whether it turns the aim", Arguments = i => i == 0 ? ["off", "gyro", "scripted"] : i == 1 ? ["coupled", "decoupled"] : [] });
    }

    /// <summary>The 3D VIEW settings into the settings file (an interactive run only).</summary>
    private void SaveStereo()
    {
        if (!interactive)
        {
            return;
        }

        var stored = SettingsStore.Load(settingsPath);
        (stored.Stereo, stored.StereoSeparation, stored.StereoDepth, stored.CanvasDepth) = (settings.Stereo, settings.StereoSeparation, settings.StereoDepth, settings.CanvasDepth);
        (stored.Viewer, stored.LensK1, stored.LensK2, stored.HeadTracking, stored.Aim) = (settings.Viewer, settings.LensK1, settings.LensK2, settings.HeadTracking, settings.Aim);
        SettingsStore.Save(stored, settingsPath);
    }
}
