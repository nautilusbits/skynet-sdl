using System.Drawing;
using System.Numerics;
using SkyNet.Game.Vehicles;
using SkyNet.Game.Weapons;

namespace SkyNet.Game.Net;

/// <summary>One player the motion detector marks: where on the screen (pixels), how far (world
/// units), the name as the scanner prints it, and whether the body is dead.</summary>
public readonly record struct DetectorMark(Vector2 At, float Distance, string Name, bool Dead);

/// <summary>
/// The marks over the 3D view, after SKYNET.EXE v1.01 FUN_00132b00. In DOS that is the TERMINATOR's
/// machine vision: it runs only in a network game and only while [0x30a54] bit 4 is set, which
/// 0x134a39 sets every frame for a machine avatar (7 or 13) that is alive and not under water. It
/// walks the actor list, takes the 3D distance (FUN_0014d775) from the eye to each player present
/// and marks the ones 50..2500 units away (<see cref="Near"/>..<see cref="Far"/>) whose projected
/// point falls inside the viewport, with no line-of-sight test (through walls), in colour 0xb8:
/// <list type="bullet">
/// <item>a dot at the point (FUN_00011da0, glyph 0x12), flashing (0xb4, the same green lit) while
/// it sits under the crosshair, DOS x 0x8d..0xb3 of 320;</item>
/// <item>the name (Trg_String "#P") at (x+4, y−6) and the range, units / 0x28 then "M", at
/// (x+4, y+2), in 320 × 200 pixels;</item>
/// <item>a dead body (its flag 4) gets Ded_String "#P TERMINATED" at (x−0x30, y) instead.</item>
/// </list>
/// <para>The HUMAN's MOTION DETECTOR (weapon record 13) is not these marks but FUN_0011ff00, a
/// top-down plot on the scanner's own screen (<see cref="Scan"/>):</para>
/// <code>
///   WEAPON13.CFA (72 x 82)      x 19..59, y 12..33: the screen
///   +---------------------+
///   |  .        o <- 0xb3 |     each mover / shooter / driver / machine, 128 units a pixel
///   |      X  <- 0x0c     |     the one nearest the line ahead
///   |         ^ (39, 32)  |     the holder, facing up
///   +---------------------+
///   |        NAME         |     (39, 38): the X's name
/// </code>
/// <para>The sweep (WEAPON13.CFA's 7 frames at record 13's 18 a second) plays MOTLOOP2.RAW each
/// time it starts over (<see cref="ScannerSweep"/>).</para>
/// </summary>
public static class MotionDetector
{
    /// <summary>DOS 0x32 .. 0x9c4 (0x132b72: <c>jg</c> past 2500, <c>jl</c> under 50): nothing is marked
    /// inside 50 units nor past 2500.</summary>
    public const float Near = 50f, Far = 2500f;
    /// <summary>World units a metre in the read-out (0x28).</summary>
    public const float UnitsPerMetre = 40f;
    /// <summary>The marked point stands this far above the feet (motion_detector.gd).</summary>
    public const float Lift = 50f;
    /// <summary>The dot's side in DOS pixels.</summary>
    public const float DotDosPixels = 3f;
    /// <summary>Half the flash window under the crosshair, as fractions of the screen: DOS x
    /// 0x8d..0xb3 of 320, and 20 of 200 rows each way.</summary>
    public const float FlashHalfWidth = (0xb3 - 0x8d) / 2f / 320f, FlashHalfHeight = 20f / 200f;

    /// <summary>Palette 0xb8 of SKYNET.COL, and 0xb4 (the same green, lit) for the flash.</summary>
    public static readonly Vector4 Colour = Rgb(0x43, 0x8f, 0x43), Lit = Rgb(0x37, 0xe7, 0x37);

    /// <summary>What the scanner marks from <paramref name="eye"/>: the visible bodies inside the
    /// range whose point projects (through <paramref name="viewProjection"/>) into the view, the
    /// <paramref name="width"/> × <paramref name="viewHeight"/> pixels above the panel.</summary>
    public static List<DetectorMark> Marks(IEnumerable<DmAvatar> avatars, Vector3 eye, in Matrix4x4 viewProjection, float width,
        float viewHeight, float screenHeight)
    {
        var marks = new List<DetectorMark>();
        foreach (var a in avatars)
        {
            if (!a.Visible)
            {
                continue;
            }

            var at = a.Position + new Vector3(0f, Lift, 0f);
            var clip = Vector4.Transform(new Vector4(at, 1f), viewProjection);
            if (clip.W <= 1e-3f)
            {
                continue; // behind the eye
            }

            var dist = Vector3.Distance(eye, at);
            if (dist < Near || dist > Far)
            {
                continue;
            }

            var sx = (clip.X / clip.W * 0.5f + 0.5f) * width;
            var sy = (0.5f - clip.Y / clip.W * 0.5f) * screenHeight;
            if (sx < 0f || sy < 0f || sx > width || sy > viewHeight)
            {
                continue; // DOS clips to the viewport
            }

            marks.Add(new DetectorMark(new Vector2(sx, sy), dist, a.Name.ToUpperInvariant(), !a.Alive));
        }

        return marks;
    }

    /// <summary>The range read-out: whole metres, then "M".</summary>
    public static string Range(float distance) => string.Create(System.Globalization.CultureInfo.InvariantCulture, $"{(int)(distance / UnitsPerMetre)}M");

    /// <summary>A mark under the crosshair flashes: lit for 4 of every 8 frames at 60 a second.</summary>
    public static bool Flashes(Vector2 at, Vector2 crosshair, float width, float height, double clock) =>
        ((long)(clock * 60.0) & 7) < 4
        && MathF.Abs(at.X - crosshair.X) <= FlashHalfWidth * width
        && MathF.Abs(at.Y - crosshair.Y) <= FlashHalfHeight * height;

    private static Vector4 Rgb(int r, int g, int b) => new(r / 255f, g / 255f, b / 255f, 1f);

    // ---- the scanner's plot (FUN_0011ff00) ----

    /// <summary>The scanner's screen in WEAPON13.CFA's low frame (table 0x38504: x 39 −20..+21,
    /// y 32 −20..+2), the clip of the plot.</summary>
    public static readonly Rectangle Screen = Rectangle.FromLTRB(19, 12, 60, 34);
    /// <summary>The holder's point on the screen, and the top-centre of the target's name.</summary>
    public static readonly Point Holder = new(39, 32), NameAt = new(39, 38);
    /// <summary>World units a pixel of the plot (FUN_00120265: (v + 0x40) &gt;&gt; 7).</summary>
    public const float UnitsPerPixel = 128f;
    /// <summary>The distance ahead is scaled by 180 / 200 (0xe666, [0x5f158]) before the pixels.</summary>
    public const float AheadScale = 0.9f;
    /// <summary>Palette indices of a blip (0xb3) and of the target and its name (0x0c).</summary>
    public const int BlipIndex = 0xb3, TargetIndex = 0x0c;

    /// <summary>WEAPON13.CFA's frames (both sets) and record 13's animation rate (+0x2c).</summary>
    public const int SweepFrames = 7;
    public const float SweepFps = ViewModelAnimation.ScannerFps;
    public const float SweepSeconds = SweepFrames / SweepFps;
    /// <summary>MOTLOOP2.RAW (0x61), played as the sweep starts over (0x11ffff).</summary>
    public const int SweepSound = 0x61;

    /// <summary>The scanner's plot for a holder at <paramref name="eye"/> facing <paramref name="yaw"/>
    /// (the player's: ahead −sin, −cos): the others who move, shoot, drive or are machines, rotated
    /// into the holder's frame and clipped to <see cref="Screen"/>; the one nearest the line ahead
    /// (|x| ≤ ahead, smallest |x|, then the nearest, not past the screen's top) is the target.</summary>
    public static ScannerPlot Scan(IEnumerable<DmAvatar> avatars, Vector3 eye, float yaw)
    {
        var right = new Vector2(MathF.Cos(yaw), -MathF.Sin(yaw));
        var ahead = new Vector2(-MathF.Sin(yaw), -MathF.Cos(yaw));
        var blips = new List<ScannerBlip>();
        var (target, bestSide, bestAhead) = (-1, float.MaxValue, float.MaxValue);
        string? name = null;
        foreach (var a in avatars)
        {
            if (!Shows(a))
            {
                continue;
            }

            // FUN_00120265: the offset in the holder's frame, ahead scaled, then pixels up the screen.
            var d = new Vector2(a.Position.X - eye.X, a.Position.Z - eye.Z);
            var side = Vector2.Dot(d, right);
            var forward = Vector2.Dot(d, ahead) * AheadScale;
            var at = new Point(Holder.X + Pixels(side), Holder.Y + Pixels(-forward));

            // 0x12012c: in front (|x| ≤ ahead) and not above the screen; the most central, then the nearest.
            var front = at.Y >= Screen.Top && MathF.Abs(side) <= forward;
            if (front && (MathF.Abs(side) < bestSide || MathF.Abs(side) == bestSide && forward < bestAhead))
            {
                (bestSide, bestAhead) = (MathF.Abs(side), forward);
                (target, name) = (Screen.Contains(at) ? blips.Count : -1, a.Name.ToUpperInvariant());
            }

            if (Screen.Contains(at))
            {
                blips.Add(new ScannerBlip(at, false));
            }
        }

        if (target >= 0)
        {
            blips[target] = blips[target] with { Target = true };
        }

        return new ScannerPlot(blips, name);
    }

    /// <summary>0x1200f1: present, and driving (0x20), moving, firing or holding a scanner (0x10) or a
    /// machine (0x400).</summary>
    private static bool Shows(DmAvatar a) =>
        a.Visible && a.Alive
        && (a.Vehicle != VehicleMode.Foot || (a.Flags & Mover) != 0 || a.Class == DmClass.Terminator);

    private const int Mover = DmProtocol.FlagMoving | DmProtocol.FlagFiring | DmProtocol.FlagScanner;

    /// <summary>The scanner is in the hand of <paramref name="a"/> (its pose's word).</summary>
    public static bool Holds(DmAvatar a) => a.Visible && a.Alive && (a.Flags & DmProtocol.FlagScanner) != 0;

    /// <summary>World units to plot pixels, rounded as DOS shifts: (v + 0x40) &gt;&gt; 7.</summary>
    private static int Pixels(float units) => (int)MathF.Floor((units + UnitsPerPixel / 2f) / UnitsPerPixel);
}

/// <summary>A dot on the scanner's screen, in WEAPON13.CFA's low-frame pixels; the target is redrawn in 0x0c.</summary>
public readonly record struct ScannerBlip(Point At, bool Target);

/// <summary>The scanner's dots and the target's name (null: nobody ahead).</summary>
public sealed record ScannerPlot(IReadOnlyList<ScannerBlip> Blips, string? Target);

/// <summary>
/// The scanner's sweep clock (FUN_0011ff00's [0x44446]): from the draw it is taken into the hand it
/// counts WEAPON13.CFA's frames at 18 a second, and each time it stands on frame 0 DOS plays
/// MOTLOOP2.RAW (restarting it); here, once a cycle.
/// </summary>
public sealed class ScannerSweep
{
    private float _t = -1f;

    /// <summary>Advances <paramref name="seconds"/>; true when the sweep starts (over): time to beep.</summary>
    public bool Tick(float seconds)
    {
        if (_t < 0f)
        {
            _t = 0f;
            return true;
        }

        var cycle = Cycle(_t);
        _t += seconds;
        return Cycle(_t) > cycle;
    }

    /// <summary>The scanner left the hand: the next one starts a sweep.</summary>
    public void Reset() => _t = -1f;

    private static long Cycle(float t) => (long)MathF.Floor(t / MotionDetector.SweepSeconds);
}

/// <summary>
/// The others' scanners heard here: DOS sends the holder's sweep sound to every machine
/// (FUN_00122e41 → FUN_00119209). The wire carries <see cref="DmProtocol.FlagScanner"/> instead, and
/// each machine runs a sweep per holder: a beep at the holder as the scanner comes into the hand and
/// once a sweep after.
/// </summary>
public sealed class ScannerBeeps
{
    private readonly Dictionary<int, ScannerSweep> _sweeps = [];
    private readonly List<DmAvatar> _due = [];
    private readonly HashSet<int> _seen = [];

    /// <summary>Advances <paramref name="seconds"/>; the holders whose sweep beeps now.</summary>
    public IReadOnlyList<DmAvatar> Tick(IEnumerable<DmAvatar> avatars, float seconds)
    {
        _due.Clear();
        _seen.Clear();
        foreach (var a in avatars)
        {
            if (!MotionDetector.Holds(a))
            {
                continue;
            }

            _seen.Add(a.Id);
            if (!_sweeps.TryGetValue(a.Id, out var sweep))
            {
                _sweeps[a.Id] = sweep = new ScannerSweep();
            }

            if (sweep.Tick(seconds))
            {
                _due.Add(a);
            }
        }

        // Put away (or gone): the next one starts a sweep.
        foreach (var id in _sweeps.Keys.Where(id => !_seen.Contains(id)).ToList())
        {
            _sweeps.Remove(id);
        }

        return _due;
    }
}
