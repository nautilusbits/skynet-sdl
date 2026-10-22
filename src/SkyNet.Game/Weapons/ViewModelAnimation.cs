using System.Drawing;

namespace SkyNet.Game.Weapons;

/// <summary>What the view-model needs from the game for one frame.</summary>
/// <param name="Record">The held gun's DOS record (WEAPONnn.CFA); −1 none.</param>
/// <param name="OnFoot">False in the jeep or the HK: their guns have no view-model.</param>
/// <param name="RocketEye">The view follows a rocket.</param>
/// <param name="Hidden">Anything else that takes the gun away: death, a scripted view, the torpedo ride.</param>
public readonly record struct ViewModelState(int Record, bool OnFoot = true, bool RocketEye = false, bool Hidden = false)
{
    public bool Visible => Record >= 0 && OnFoot && !RocketEye && !Hidden;
}

/// <summary>
/// The view-model's frame clock, without a GPU (fly_camera.gd _process): frame 0 is the idle
/// pose; a shot plays frames 1..n−1 once and returns to 0. The Godot port runs each gun at a
/// hand-tuned rate; here the animation is timed to the weapon's cooldown, a frame lasting
/// <c>cooldown / n</c> (at most <see cref="MaxFrameSeconds"/>), so held fire runs the cycle back
/// to back and one shot's animation ends before the next can start. The pipe's swing is its fire
/// animation. Changing the held gun drops the animation.
/// <para>A throw lowers the gun for <see cref="ThrowSeconds"/> (<see cref="ThrowDip"/>, 0..1..0):
/// the port's own cue — neither DOS nor the Godot port is known to animate the throw.</para>
/// </summary>
public sealed class ViewModelAnimation
{
    public const float MaxFrameSeconds = 1f / 8f;
    public const float ThrowSeconds = 0.3f;
    /// <summary>The motion detector's sweep runs while it is in hand, record 13's +0x2c frames a
    /// second (SKYNET.EXE v1.01 FUN_0011ff00), no shot needed.</summary>
    public const float ScannerFps = 18f;

    /// <summary><see cref="Clock"/> starts over after an hour (float precision).</summary>
    private const float ClockWrap = 3600f;

    private float _t;
    private float _cycle;
    /// <summary>Seconds since the last shot (−1 none) and how long a shot moves a 3D gun.</summary>
    private float _shot = -1f, _shotCycle;
    private float _throw = -1f;

    public int Record { get; private set; } = -1;
    public int Frame { get; private set; }
    public bool Firing { get; private set; }

    /// <summary>Seconds of animation run (paused when the game is), for a 3D gun's sway.</summary>
    public float Clock { get; private set; }

    /// <summary>How far the shot's animation has run, 0..1 (frames 1..n−1); −1 when none plays
    /// (a 3D gun's kick, <see cref="ViewModelMotion"/>).</summary>
    public float FireProgress => _shot >= 0f && _shotCycle > 0f ? Math.Clamp(_shot / _shotCycle, 0f, 1f) : -1f;

    /// <summary>The shortest and longest shot a 3D gun kicks for when its art has no fire frames
    /// (one frame: the machine gun, the laser cannon): the shot lasts its cooldown, held in here.</summary>
    public const float MinShotSeconds = 0.06f, MaxShotSeconds = 0.2f;

    /// <summary>How far the throw has lowered the gun: 0 rest, 1 lowest.</summary>
    public float ThrowDip => _throw < 0f ? 0f : MathF.Sin(MathF.PI * Math.Clamp(_throw / ThrowSeconds, 0f, 1f));

    /// <summary>Seconds a fire frame lasts for a gun of <paramref name="frames"/> frames.</summary>
    public static float FrameSeconds(float cooldown, int frames) =>
        frames <= 1 ? 0f : MathF.Min(MathF.Max(cooldown, 1e-3f) / frames, MaxFrameSeconds);

    /// <summary>The frame shown <paramref name="t"/> seconds after a shot: 1, 2, … n−1, then 0
    /// (the animation over).</summary>
    public static int FrameAt(float t, float frameSeconds, int frames)
    {
        if (frames <= 1 || frameSeconds <= 0f || t < 0f)
        {
            return 0;
        }

        var k = 1 + (int)MathF.Floor(t / frameSeconds);
        return k < frames ? k : 0;
    }

    /// <summary>The held gun fired (or the pipe swung): the animation starts over.</summary>
    public void Fire()
    {
        Firing = true;
        _t = 0f;
        _shot = 0f;
    }

    /// <summary>A thrown item left the hand.</summary>
    public void Throw() => _throw = 0f;

    /// <summary>Advances <paramref name="dt"/> seconds for the held <paramref name="record"/>,
    /// whose CFA has <paramref name="frames"/> frames and whose shots are
    /// <paramref name="cooldown"/> seconds apart.</summary>
    public void Tick(float dt, int record, int frames, float cooldown)
    {
        if (record != Record)
        {
            Record = record;
            Firing = false;
            _t = 0f;
            _shot = -1f;
        }

        Clock = (Clock + dt) % ClockWrap;
        _cycle = FrameSeconds(cooldown, frames) * Math.Max(frames - 1, 0);
        _shotCycle = _cycle > 0f ? _cycle : Math.Clamp(cooldown, MinShotSeconds, MaxShotSeconds);
        if (_shot >= 0f)
        {
            _shot += dt;
            if (_shot >= _shotCycle)
            {
                _shot = -1f;
            }
        }

        if (_throw >= 0f)
        {
            _throw += dt;
            if (_throw >= ThrowSeconds)
            {
                _throw = -1f;
            }
        }

        // The scanner sweeps on its own, looping its frames.
        if (record == WeaponTable.MotionDetector && frames > 1)
        {
            _t += dt;
            Frame = (int)(_t * ScannerFps) % frames;
            return;
        }

        if (!Firing)
        {
            Frame = 0;
            return;
        }

        _t += dt;
        Frame = FrameAt(_t, FrameSeconds(cooldown, frames), frames);
        if (Frame == 0)
        {
            Firing = false;
        }
    }

    /// <summary>Back to the idle pose (a load, a respawn).</summary>
    public void Reset()
    {
        Firing = false;
        Frame = 0;
        _t = 0f;
        _shot = -1f;
        _throw = -1f;
    }
}

/// <summary>
/// Where the gun goes on the screen. DOS draws a 320 × 160 view above its 40-line panel, the gun's
/// frame with its left edge at the record's view X (+0x00) and its foot on the panel, aimed at
/// the view's centre (160, 80). Here one DOS pixel of the view is <c>(view height) / 160</c>
/// screen pixels on both axes (the view's height is what the panel leaves, as
/// <see cref="Hud.DosHud.PanelPixels"/> says), and DOS x 160 is the crosshair's column
/// (<see cref="Hud.DosHud.ViewCentre"/>), so the art points where it did; the frame sinks
/// <see cref="Overlap"/> DOS pixels behind the panel (fly_camera.gd HUD_OVERLAP). A hi-res
/// (640 × 480) frame is drawn into the rectangle of its 320 × 200 twin: the hi-res art has square
/// pixels where the low set's are tall, so neither half nor any header offset places it
/// (fly_camera.gd _layout_viewmodel).
/// </summary>
public static class ViewModelLayout
{
    public const int ViewLines = 160;
    public const int ScreenCentreX = 160;
    public const float Overlap = 14f;
    /// <summary>How far a throw lowers the gun, in DOS pixels.</summary>
    public const float ThrowDrop = 24f;

    /// <summary>Screen pixels per DOS pixel of the gun.</summary>
    public static float Scale(int width, int height)
    {
        // Below 4:3 the gun keeps its 4:3 size across (the view widens down, UiMetrics.FovY).
        var h = Math.Min(height, width * 3 / 4);
        return Math.Max(h - Hud.DosHud.PanelPixels(width, h), 1) / (float)ViewLines;
    }

    /// <summary>The gun's rectangle in screen pixels, edges on whole pixels, for a frame of
    /// <paramref name="size"/> DOS pixels (the 320 × 200 art's size) at <paramref name="viewX"/>.</summary>
    public static RectangleF Place(int width, int height, int viewX, Size size, Hud.HudStyle style = Hud.HudStyle.Classic, float dip = 0f)
    {
        var s = Scale(width, height);
        var centre = Hud.DosHud.ViewCentre(width, height, style);
        var foot = height - Hud.DosHud.PanelPixels(width, height, style);
        var left = MathF.Round(centre.X + (viewX - ScreenCentreX) * s);
        var right = MathF.Round(centre.X + (viewX - ScreenCentreX + size.Width) * s);
        var bottom = MathF.Round(foot + (Overlap + dip * ThrowDrop) * s);
        var top = MathF.Round(foot + (Overlap + dip * ThrowDrop - size.Height) * s);
        return new RectangleF(left, top, right - left, bottom - top);
    }

    /// <summary>The Godot port's view X per on-foot record 0..13 (fly_camera.gd _weapons vx), for
    /// a record whose table row has none (the shipped table's agree with it).</summary>
    public static readonly IReadOnlyList<int> GodotViewX = [62, 156, 154, 154, 64, 156, 175, 168, 160, 152, 160, 128, 156, 168];

    /// <summary>The CFA of an on-foot record (0..13); null for the thrown items and the vehicle guns.</summary>
    public static string? CfaName(int record) => record is >= 0 and <= WeaponTable.MotionDetector ? $"WEAPON{record:D2}.CFA" : null;
}
