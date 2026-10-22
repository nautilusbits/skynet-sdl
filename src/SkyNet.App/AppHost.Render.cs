using System.Diagnostics;
using System.Drawing;
using System.Globalization;
using System.Numerics;
using RetroEng.Audio;
using RetroEng.Commands;
using RetroEng.Diagnostics;
using RetroEng.Loop;
using RetroEng.Physics;
using RetroEng.Platform;
using RetroEng.Render;
using SkyNet.Data;
using SkyNet.Formats;
using SkyNet.Game;
using SkyNet.Game.Audio;
using SkyNet.Game.Baking;
using SkyNet.Game.Campaign;
using SkyNet.Game.Collision;
using SkyNet.Game.Demos;
using SkyNet.Game.DevTools;
using SkyNet.Game.Enhanced;
using SkyNet.Game.Enemies;
using SkyNet.Game.Hud;
using SkyNet.Game.Menu;
using SkyNet.Game.Player;
using SkyNet.Game.Vehicles;
using SkyNet.Game.Weapons;
using static AppTools;
using SkyNet.Game.Net;

internal sealed partial class AppHost
{
    /// <summary>The snapshot a single-threaded loop captures into and draws from at once.</summary>
    private readonly FrameSnapshot soloFrame = new();
    /// <summary>The alpha <see cref="DrawFrame"/> hands the capture (a field: no closure per frame).</summary>
    private float captureAlpha;
    private Action<FrameSnapshot>? captureCall;
    /// <summary>The last frame build's time (Stopwatch ticks), set by the build, read by the next capture.</summary>
    private long lastBuildTicks;
    // POINT CLOUD's parameters: DISPLAY's and TERMINATOR VISION's (the frame build picks one each frame).
    private readonly PointCloudStyle displayCloud = ViewStyles.PointCloud(), machineCloud = ViewStyles.MachineCloud();
    // The renderer's GPU memory for the debug overlay: read on main (SampleGpu), shown by the capture.
    private RenderMemory gpuMemory;
    private long gpuSampled;
    private readonly Lock gpuLock = new();

    /// <summary>Main, after a present: the renderer's GPU memory, at most every
    /// <see cref="GpuSampleSeconds"/> and only while the debug overlay is on (summed on each read).</summary>
    private void SampleGpu()
    {
        var now = Stopwatch.GetTimestamp();
        if (!displays.Debug || Stopwatch.GetElapsedTime(gpuSampled, now).TotalSeconds < GpuSampleSeconds)
        {
            return;
        }

        gpuSampled = now;
        var memory = renderer.Memory;
        lock (gpuLock)
        {
            gpuMemory = memory;
        }
    }

    private const double GpuSampleSeconds = 0.25;

    private RenderMemory GpuMemory()
    {
        lock (gpuLock)
        {
            return gpuMemory;
        }
    }

    // The frame every mode draws: the map (when one is shown and no full-screen menu hides it) with
    // the robots, the weapon, the cockpit and the HUD, then the menus, the developer displays and
    // the console, presented. The simulation catches what the frame needs (CaptureFrame) and the frame
    // build draws it (BuildFrame): at once on one thread, or on the frame-build thread while the
    // simulation goes on (a frame with menus or the console up waits: they are drawn live).
    internal void DrawFrame(float alpha)
    {
        if (frameBuild == null)
        {
            CaptureFrame(soloFrame, alpha);
            BuildFrame(soloFrame);
            return;
        }

        captureAlpha = alpha;
        captureCall ??= s => CaptureFrame(s, captureAlpha);
        frameBuild.Submit(captureCall, LiveUi() ? FrameSync.Wait : FrameSync.Overlap);
    }

    // What the frame build would read live: the menus (their toast too), the console while any of it
    // shows, the touch controls and the resource churn's draws.
    bool LiveUi() =>
        menu.IsOpen || menu.Toast != null || devConsole.IsOpen || devConsole.Slide > 0f || window.Touch != null || churn != null
        || automap.IsOpen;

    // The simulation's half of a frame: the bookkeeping, the views and a copy of everything the
    // frame draws, into s.
    void CaptureFrame(FrameSnapshot s, float alpha)
    {
        if (Frozen())
        {
            alpha = 1f;
        }

        if (playback != null && loop is { OneTickPerFrame: true })
        {
            timedemoFrames++;
        }

        var now = Stopwatch.GetTimestamp();
        frameTimes.Add(Stopwatch.GetElapsedTime(lastFrame, now).TotalMilliseconds);
        frameStats.Add((float)frameTimes[^1]);
        lastFrame = now;
        if (loadHitches.Frame(now) is { } hitches)
        {
            Log.Info("Level", hitches);
        }

        var allocated = GC.GetAllocatedBytesForCurrentThread();
        (allocatedPerFrame, allocatedAtFrame) = (allocated - allocatedAtFrame, allocated);
        s.Live = LiveUi();
        s.AnimationMs = AnimationMs();
        if (capture != null && level != null)
        {
            // The level's animated records by the capture's clock too.
            level.ClockMs = s.AnimationMs;
        }

        // 3D VIEW: the canvas's eye layout, the head tracking, the viewer's touch (before anything is drawn).
        s.Stereo = StereoNow();
        s.StereoGrid = options.StereoGrid;
        ApplyStereoFrame(s.Stereo);
        var (eye, viewYaw, viewPitch, view) = player.View(alpha);
        if (vehicles.Riding && !player.Noclip)
        {
            (eye, _, _, view) = vehicles.Controller.View(alpha);
        }
        else if (dm is { DeathView: > 0f } dying)
        {
            // The deathmatch's death view (fly_camera.gd begin_death_view): the eye sinks to the floor,
            // the head tips over and looks along the ground until the server's respawn.
            var (eyeHeight, pitch, roll) = dying.DeathPose(player.Eye, viewPitch);
            eye.Y += eyeHeight - player.Eye;
            var ahead = PlayerController.Direction(viewYaw, pitch);
            view = Matrix4x4.CreateLookAt(eye, eye + ahead, Vector3.Transform(Vector3.UnitY, Quaternion.CreateFromAxisAngle(ahead, roll)));
        }

        var riding = session.Torpedo is { Phase: TorpedoRide.RidePhase.Swim };
        if (riding && session.Torpedo is { } torpedo)
        {
            // The torpedo ride: the view leaves the body along the flight line, rolling about it.
            eye = torpedo.ViewAt(alpha);
            var up = Vector3.Transform(Vector3.UnitY, Quaternion.CreateFromAxisAngle(torpedo.Direction, torpedo.RollAt(alpha)));
            view = Matrix4x4.CreateLookAt(eye, eye + torpedo.Direction, up);
        }
        else if (weapons.RocketEyeAt(alpha) is { } rocket)
        {
            // The rocket eye: the camera rides 120 behind the rocket, the soldier keeps his own look.
            eye = rocket.Eye;
            view = Matrix4x4.CreateLookAt(eye, eye + rocket.Forward, Vector3.UnitY);
        }

        // Head tracking, decoupled: the head looks off the body's aim (the camera alone turns).
        view = window.HeadLook.Apply(view);
        var (width, height) = FrameSize();
        (s.Eye, s.Width, s.Height) = (eye, width, height);

        // DOS centres the view above the panel: (160, 80) of its 320 x 200 screen. The projection's
        // centre is shifted up to DosHud.ViewCentre, where the crosshair is drawn (the MODERN HUD has
        // no panel: the frame's centre).
        s.HudStyle = settings.HudStyle;
        s.PxPerMm = UiMetrics.PxPerMm(renderer.Target.Width, window.WidthMm, settings.UiScale);
        // The enhanced look's scene: reversed, infinite depth (the original keeps its own).
        s.ReversedZ = renderer.Lighting != null;
        // DISPLAY's RENDERER, or TERMINATOR VISION's point cloud while a machine's player sees through it:
        // caught per frame, so a death, a HUMAN's life or the match's end gives the setting back.
        s.MachinePoints = ViewStyles.MachinePoints(dm);
        s.Style = ViewStyles.StyleOf(settings.Style, s.MachinePoints);
        // Stereo: an eye of the full side-by-side layout has half the frame's width.
        var aspect = renderer.AspectRatio * s.Stereo.EyeAspect;
        // Mono below 4:3 sees more down, not less across (stereo keeps its lens-tuned field).
        var fov = s.Stereo.IsStereo ? FieldOfView : UiMetrics.FovY(FieldOfView, aspect);
        var projection = s.ReversedZ
            ? Projection.ReversedInfinite(fov, aspect, Near)
            : Matrix4x4.CreatePerspectiveFieldOfView(fov, aspect, Near, FarPlane);
        var shiftY = 0f;
        if (height > 0)
        {
            var centre = DosHud.ViewCentre(width, height, s.HudStyle);
            shiftY = 1f - 2f * centre.Y / height;
            projection.M32 = -shiftY;
        }

        var rotation = view with { M41 = 0f, M42 = 0f, M43 = 0f };
        Matrix4x4.Invert(rotation * projection, out var clipToDirection);
        s.ViewProjection = view * projection;
        // The point lights' grid covers the shifted view with a symmetric frustum (taller, as wide).
        var lens = new Lens(view, Projection.CoveringFovY(fov, shiftY), renderer.AspectRatio / (1f + MathF.Abs(shiftY)), Near, FarPlane);
        s.Views = s.Stereo.IsStereo
            ? StereoRigNow().Views(view, projection, eye, s.Stereo, lens)
            : FrameViews.Mono(new View(view * projection, clipToDirection, eye) { Lens = lens });

        // A full-screen menu (the title, the mission screen) hides the game; the Esc menu darkens it.
        var covered = menu.Current is { OverGame: false };
        s.Sky = level != null && !covered ? level.SkyColour : new Vector4(0f, 0f, 0f, 1f);
        s.Level = level != null && !covered ? level : null;
        if (s.Level != null)
        {
            CaptureMap(s, alpha, riding, width, height);
            MarkSeen(s.ViewProjection, eye);
        }

        s.Automap = s.Level != null && automap.IsOpen;

        CaptureLights(s, covered);
        CaptureDevDisplays(s, s.Level != null, height);
        s.ConsolePixel = UiMetrics.ConsolePixel(height, consoleFont?.LineHeight ?? 0, s.PxPerMm);
        rendered++;

        // The exit's present time is the later half's, as the frame time (AppHost.Run).
        if (rendered == options.Frames / 2)
        {
            renderer.ResetPresentTime();
        }

        s.Shot = rendered == options.Frames ? options.Screenshot : pendingShot;
        pendingShot = null;
        s.CaptureShot = capture?.Take();
        soundCapture?.Step();

        // The draw section is the capture's (the build's when on one thread), present the last build's.
        sections.Add(Section.Scene, Stopwatch.GetTimestamp() - now);
        sections.Add(Section.Render, Interlocked.Exchange(ref lastBuildTicks, 0));
        sections.EndFrame();
        var stats = renderer.Stats;
        uploadsPerFrame = renderer.UploadSubmits - uploadsAtFrame;
        uploadsAtFrame = renderer.UploadSubmits;
        Prof.Plot("Draw calls", stats.DrawCalls);
        Prof.Plot("Triangles", stats.Triangles);
        Prof.Plot("Upload submits", uploadsPerFrame);
    }

    // The map's part of a frame: the level's moving parts, the robots, the others' bodies, the
    // cockpit, the weapons' effects, the drops, the panel and the deathmatch's read-outs.
    void CaptureMap(FrameSnapshot s, float alpha, bool riding, int width, int height)
    {
        var map = s.Level!;
        s.PixelAngle = UiMetrics.FovY(FieldOfView, renderer.AspectRatio) / Math.Max(renderer.Target.Height, 1);
        map.WaterSurface = session.Water?.SurfaceAt(alpha);
        map.WaterPass = session.Torpedo is not { Riding: true };
        map.Drops = session.Drops;
        map.Capture(s.LevelFrame, alpha);
        s.Robots = session.Robots != null ? robotView : null;
        if (s.Robots != null)
        {
            s.Robots.Capture(session.Robots!, s.RobotFrame, alpha);
        }

        if (dm != null)
        {
            dm.Capture(map, renderer, s.Bodies);
        }
        else
        {
            s.Bodies.Clear();
        }
        s.Cockpit = vehicles.Riding && !player.Noclip && weapons.RocketEye == null && !riding;
        if (s.Cockpit)
        {
            (s.CockpitMode, s.CockpitWorld) = (vehicles.Controller.Mode, vehicles.Controller.CockpitWorld(alpha));
        }

        // The ride's view has neither the weapon nor the panel.
        s.Riding = riding;
        if (!riding)
        {
            var inventory = weapons.Inventory;
            WeaponRenderer.Capture(weapons, s.Weapons, alpha);
            s.Drops.Clear();
            s.Drops.AddRange(session.Drops);
            s.DmItems.Clear();
            if (dm != null)
            {
                // The arena's items, from the server's list.
                s.DmItems.AddRange(dm.Items);
            }

            var frameSeconds = menu.PausesGame || devConsole.IsOpen || options.Freeze ? 0f : (float)(frameTimes[^1] / 1000.0);
            s.ViewModel = new ViewModelState(inventory.Primary, OnFoot: !vehicles.Riding, RocketEye: weapons.RocketEye != null,
                Hidden: !soldier.Alive || session.Phase != SessionPhase.Playing || dm is { OwnView: false });
            viewModel.Tick(frameSeconds, s.ViewModel);
            s.ViewModelPose = viewModel.Pose();
            s.Hud = vehicles.Hud(HudState.Empty with
            {
                Health = soldier.Health,
                MaxHealth = dm?.MaxHealth ?? HudRecords.SoldierHealth,
                Armour = soldier.Armour,
                PrimaryRecord = inventory.Primary,
                PrimaryAmmo = inventory.AmmoFor(inventory.Primary),
                SecondaryRecord = inventory.Thrown,
                SecondaryAmmo = inventory.AmmoFor(inventory.Thrown),
                // player.Yaw turns counter-clockwise; the compass wants a clockwise bearing.
                Bearing = HudFormat.Bearing(-player.Yaw, northDegrees),
                CrosshairHidden = weapons.RocketEye != null || session.Phase != SessionPhase.Playing || dm != null && !soldier.Alive,
            });
            hud.Capture(s.HudText);
        }

        // DOS swaps the whole VGA palette to SKYNTWTR.COL while the head is under.
        s.Underwater = session.Breath.IsUnder;

        // DOS's end banner (FUN_00121fe3): WELLDONE.IMG / FAILED.IMG; the text when the data lacks the art.
        s.Banner = session.BannerImage;
        s.BannerText = s.Banner != null ? session.Phase == SessionPhase.Failed ? "MISSION FAILED" : "WELL DONE"
            : session.Phase == SessionPhase.CampaignOver ? "THE END" : null;

        // The deathmatch's read-outs, the scoreboard while TAB is held, the end of the round.
        s.Deathmatch = dm != null;
        if (dm != null)
        {
            s.ScoreBoard = dm.ShowScores || input.IsHeld(RawKey.Tab);
            var gun = riding ? null : viewModel.Placed(s.ViewModel, s.ViewModelPose, width, height, s.HudStyle);
            SkyNet.Game.Net.DmHud.Capture(dm, s.DmHud, width, height, s.ViewProjection, s.Eye, gun, s.HudStyle);
        }
    }

    // The enhanced look's point lights for this frame: the level's map lights (live from its
    // behaviour), art lamps and fires, and the weapons' flashes, blasts and bolts; the build keeps the
    // most important for the eye, up to the level's budget. None in the original look (or with no map shown).
    void CaptureLights(FrameSnapshot s, bool covered)
    {
        s.Lights.Clear();
        s.Storm = default;
        s.Rain.Table = null;
        s.Air.Clouds = null;
        s.Air.Haze = null;
        s.Air.Dust = null;
        s.Air.Wind = null;
        s.LightsOn = renderer.Lighting != null && level != null && !covered;
        if (renderer.Lighting == null)
        {
            // The original look: no storm (and none left over for the next switch).
            effects.Storm.Tick(0f, null, level, StormSky.Indoor, s.Eye);
        }

        if (!s.LightsOn)
        {
            // No weather shown: no rain or wind heard either.
            effects.Air.Tick(0f, Weather.None, level, StormSky.Indoor, s.Eye, null);
            effects.Rain.Tick(0f, null, level, StormSky.Indoor, s.Eye, null);
            weatherSound.Rain(null, 0f);
            weatherSound.Wind(null, 0f, 1f);
            return;
        }

        using var zone = Prof.Zone(AppZones.GatherLights);
        effects.Collect(s.Lights, level!, data, s.AnimationMs / 1000f, session.Behaviour, weapons);
        (s.LightReach, s.LightBudget) = (level!.Far, level.LightBudget);

        // A mod's weather: its own clock (held while the game is), presentation only.
        var sky = level.Outdoor ? StormSky.Outdoor : StormSky.Indoor;
        var (weather, dt) = (Weather.Current, StormSeconds());
        s.Storm = effects.Storm.Tick(dt, weather.Storm, level, sky, s.Eye);
        s.Sky = Storm.SkyOf(s.Sky, s.Storm);
        var air = effects.Air.Tick(dt, weather, level, sky, s.Eye, session.World);
        var rain = effects.Rain.Tick(dt, weather.Rain, level, sky, s.Eye, session.World, air.Wind);
        effects.Air.Capture(s.Air);
        effects.Rain.Capture(s.Rain);
        weatherSound.Rain(rain.Sound, rain.Hiss);
        weatherSound.Wind(air.Sound, air.Howl, air.Pitch);
    }

    // The seconds the storm moves on this frame: the last frame's, none while the game is held.
    float StormSeconds()
    {
        const float MaxStep = 0.25f;
        if (menu.PausesGame || devConsole.IsOpen || options.Freeze || frameTimes.Count == 0)
        {
            return 0f;
        }

        return MathF.Min((float)(capture?.Step ?? frameTimes[^1] / 1000.0), MaxStep);
    }

    // The FPS readout and the debug overlay with its pointer, while on. Their text is made again four
    // times a second (the numbers are averages; making it every frame would make garbage every frame),
    // the pointer's search once a second.
    void CaptureDevDisplays(FrameSnapshot s, bool inView, int height)
    {
        s.OverlayOn = overlayView != null && (displays.Fps || displays.Debug);
        s.OverlayDebug = displays.Debug;
        s.Pointer = null;
        if (!s.OverlayOn)
        {
            return;
        }

        const double TextRefresh = 0.25, PointerRefresh = 1.0;
        var now = Stopwatch.GetTimestamp();
        var refresh = Stopwatch.GetElapsedTime(overlayMade, now).TotalSeconds >= TextRefresh;
        if (refresh)
        {
            overlayMade = now;
            fpsText = OverlayText.FpsReadout(frameStats.Summary(), renderer.Stats.DrawCalls);
        }

        if (displays.Debug)
        {
            if (Stopwatch.GetElapsedTime(pointerMade, now).TotalSeconds >= PointerRefresh)
            {
                pointerMade = now;
                objectives = level != null ? pointer.Find(session) : ObjectiveReport.None;
            }

            if (refresh || overlayLines.Count == 0)
            {
                overlayLines = OverlayText.Lines(OverlaySample());
            }

            if (inView && objectives.Target is { } target)
            {
                s.Pointer = target with { Distance = Vector3.Distance(player.Position, target.Position) };
            }
        }

        (s.OverlayLines, s.FpsText) = (overlayLines, fpsText);
        s.OverlayTop = consoleView?.Bottom(devConsole) ?? 0f;
    }

    // The frame build's half: everything drawn from s (and, in a live frame, the menus and the
    // console from their own state), then presented.
    void BuildFrame(FrameSnapshot s)
    {
        var start = Stopwatch.GetTimestamp();
        renderer.CanvasOutput = s.Stereo;
        renderer.StereoGrid = s.StereoGrid;
        renderer.ReversedZ = s.ReversedZ;
        renderer.PointCloud = s.MachinePoints ? machineCloud : displayCloud;
        renderer.Style = s.Style;
        effects.Sky.Apply(renderer, s.Storm, s.Air, s.Level, s.Level?.Particles ?? ParticleLevel.Off);
        var (width, height) = (s.Width, s.Height);
        if (s.Level is { } map && s.Automap)
        {
            DrawAutomap(map);
        }
        else if (s.Level is { } shown)
        {
            BuildMap(s, shown, width, height);
        }

        if (s.Live)
        {
            using var menuZone = Prof.Zone(AppZones.DrawMenu);
            menu.Draw(menuPainter);
        }

        using (Prof.Zone(AppZones.DrawOverlay))
        {
            BuildDevDisplays(s);
        }

        if (s.Live)
        {
            using var consoleZone = Prof.Zone(AppZones.DrawConsole);
            consoleView?.Draw(devConsole, s.ConsolePixel);
            churn?.Draw();
        }

        using (Prof.Zone(AppZones.Present))
        {
            if (!s.LightsOn)
            {
                renderer.Lights = default;
            }
            else
            {
                effects.Apply(renderer, s.Lights, s.Eye, s.LightReach, s.LightBudget);
            }

            renderer.Present(s.Views, s.Sky, s.Shot ?? s.CaptureShot);
            // One thread: main shows the window after this frame.
            if (frameBuild == null)
            {
                Presented();
                SampleGpu();
            }
        }

        FrameCapture.Mirror(s.Shot, s.CaptureShot);

        if (s.Shot != null)
        {
            Log.Info("App", $"Screenshot: {s.Shot}");
        }

        Interlocked.Add(ref lastBuildTicks, Stopwatch.GetTimestamp() - start);
    }

    // A 3D gun's muzzle flash (ViewModel.DrawModel), made once: the current weapon renderer's.
    Action<Vector3, float, Vector3>? drawFlash;

    void BuildMap(FrameSnapshot s, Level map, int width, int height)
    {
        using (Prof.Zone(AppZones.DrawLevel))
        {
            map.Wind = s.Air.Wind;
            map.Draw(s.LevelFrame, s.Eye, s.PixelAngle);
            effects.Storm.Draw(renderer, s.Storm, s.Eye, map.Far);
            effects.Dust.Draw(renderer, s.Air, s.Eye, map.Particles);
            effects.Rain.Draw(renderer, s.Rain, s.Eye, map.Particles);
            effects.Glow.Draw(renderer, s.Air, s.Lights, s.Eye, s.LightReach);
        }

        if (s.Robots != null)
        {
            using var robotZone = Prof.Zone(AppZones.DrawRobots);
            s.Robots.Draw(s.RobotFrame, s.AnimationMs);
        }

        s.Bodies.View?.Draw(s.Bodies, s.AnimationMs);
        if (s.Cockpit)
        {
            using var cockpitZone = Prof.Zone(AppZones.DrawCockpit);
            cockpitView.Draw(s.CockpitMode, s.CockpitWorld, s.AnimationMs, map.Enhanced);
        }

        if (!s.Riding)
        {
            using (Prof.Zone(AppZones.DrawWeapon))
            {
                // A mod's 3D gun draws its own muzzle flash (its sidecar's flashSize): not the DOS one too.
                var gun3D = s.Views.Count == 1 && s.ViewModel.Visible && SkyNet.Game.Mods.ViewModelModels.Drawn(s.ViewModel.Record, map.Enhanced) != null;
                weaponView.Draw(s.Weapons, s.Eye, gun3D ? OwnFlashes.Hidden : OwnFlashes.Shown);
                weaponView.DrawDrops(s.Drops, s.Eye, s.AnimationMs);
                if (s.Deathmatch)
                {
                    weaponView.DrawDrops(s.DmItems, s.Eye, s.AnimationMs);
                }

                // A mod's 3D gun in the enhanced look (one view: not in stereo), else the DOS art.
                if (s.Views.Count != 1 || !viewModel.DrawModel(s.ViewModel, s.ViewModelPose, s.Views.Centre, map.Enhanced,
                        width, height, s.HudStyle, drawFlash ??= (at, size, eye) => weaponView.DrawFlash(at, size, eye)))
                {
                    viewModel.Draw(s.ViewModel, s.ViewModelPose, width, height, s.HudStyle);
                }
            }

            using var hudZone = Prof.Zone(AppZones.DrawHud);
            hud.Style = s.HudStyle;
            hud.PxPerMm = s.PxPerMm;
            hud.Draw(s.Hud, s.HudText, width, height, TouchArea(s, width, height));
        }

        if (s.Underwater)
        {
            map.DrawUnderwater();
        }

        // The on-screen controls while fingers play (or --touch): a live frame.
        if (s.Live && window.Touch is { } touch)
        {
            TouchControlsView.Draw(renderer, touch, new Vector2(width, height), bannerFont);
        }

        // DOS's end banner over the frozen frame, centred on the view centre; the text only when the
        // data lacks the art, and after the campaign.
        if (s.Banner is not { } banner || !endBanner.Draw(data, hud.ArtSet, banner, width, height, s.HudStyle))
        {
            if (s.BannerText != null)
            {
                DrawBanner(s.BannerText, width, height);
            }
        }

        if (s.Deathmatch)
        {
            dmHud.Draw(s.DmHud, hud, bannerFont, width, height, s.ScoreBoard);
        }
    }

    // The on-screen buttons' area in the lower half (the pause button at the top is left out): the
    // MODERN HUD's message box keeps off it. Empty with no touch controls.
    RectangleF TouchArea(FrameSnapshot s, int width, int height)
    {
        var area = RectangleF.Empty;
        if (!s.Live || window.Touch is not { } touch)
        {
            return area;
        }

        foreach (var button in touch.Layout(new Vector2(width, height)))
        {
            if (button.Area.Top < height / 2f)
            {
                continue;
            }

            area = area.IsEmpty ? button.Area : RectangleF.Union(area, button.Area);
        }

        return area;
    }

    void BuildDevDisplays(FrameSnapshot s)
    {
        if (!s.OverlayOn || overlayView == null)
        {
            return;
        }

        if (s.OverlayDebug)
        {
            overlayView.DrawLines(s.OverlayLines, s.OverlayTop, s.ConsolePixel);
            if (s.Pointer is { } target)
            {
                overlayView.DrawPointer(target, s.ViewProjection, s.ConsolePixel);
            }
        }

        // The overlay's first line has the same numbers: the readout shows without it.
        if (!s.OverlayDebug && s.FpsText != null)
        {
            overlayView.DrawFps(s.FpsText, s.ConsolePixel);
        }
    }

    // What the overlay shows now.
    OverlaySample OverlaySample()
    {
        zoneTimes.Copy(zoneList);
        var stats = renderer.Stats;
        var robots = session.Robots?.All ?? [];
        var paths = session.Behaviour?.Paths.All ?? [];
        var c = vehicles.Controller;
        var deg = 180f / MathF.PI;
        var ride = vehicles.Mode switch
        {
            VehicleMode.Jeep => string.Create(CultureInfo.InvariantCulture, $"jeep: body yaw {Wrap(c.CarYaw * deg):0}, gun {c.AimYaw * deg:0}/{c.AimPitch * deg:0}, speed {c.Speed:0}"),
            VehicleMode.Hk => string.Create(CultureInfo.InvariantCulture, $"HK: speed {c.Speed:0}"),
            _ => weapons.RocketEye != null ? "on foot, riding a rocket" : session.Torpedo is { Riding: true } ? "torpedo ride" : "on foot",
        };
        return new OverlaySample
        {
            Frame = frameStats.Summary(),
            Sections =
            [
                ("tick", sections.Milliseconds(Section.Scripts)),
                ("draw", sections.Milliseconds(Section.Scene)),
                ("present", sections.Milliseconds(Section.Render)),
                ("audio", sections.Milliseconds(Section.Audio)),
            ],
            Zones = zoneList,
            Memory = MemorySample.Now(allocatedPerFrame, GpuMemory()),
            Renderer = new RendererSample(stats.DrawCalls, stats.Triangles, stats.GpuWait, renderer.TextureCount, renderer.MeshCount,
                uploadsPerFrame, renderer.UploadSubmits),
            System = new SystemSample(renderer.Gpu.Device, GpuBackends.Name(renderer.Gpu.Backend), renderer.Gpu.DriverVersion,
                window.Size.Width, window.Size.Height, windowMode.ToString().ToLowerInvariant(), renderer.VSync != VSync.Off),
            Player = level == null ? null : new PlayerSample(session.MapNumber, session.Mission?.Number ?? 0, session.Phase.ToString(), player.Position,
                Wrap(player.Yaw * deg), player.Pitch * deg, player.Noclip, ride, soldier.Health, soldier.Armour,
                string.Create(CultureInfo.InvariantCulture, $"far {level.Far:0} cue {level.CueStart:0}{(farOverride != null ? " (far override)" : "")}")),
            Robots = level == null ? null : new RobotSample(robots.Count(r => r.Alive), robots.Count(r => r.Dead), robots.Count(r => r.Hidden && !r.Dead),
                paths.Count(v => v.Started && !v.Finished && !v.Dead), paths.Count(v => v.Finished), paths.Count),
            Objectives = level == null ? null : objectives,
        };
    }

    void DrawBanner(string text, int width, int height)
    {
        if (bannerFont is not { } font)
        {
            return;
        }

        var unit = Renderer.CanvasHeight / Math.Max(height, 1);
        var pixel = DosHud.PixelScale(width, height) * 2 * unit;
        var at = new Vector2(width * unit / 2f, height * unit * 0.3f);
        TextBatcher.DrawShadowed(renderer, font, text, at, pixel, new Vector4(0.42f, 0.92f, 0.48f, 1f), new Vector4(0f, 0f, 0f, 0.8f),
            align: TextAlign.Centre);
    }
}
