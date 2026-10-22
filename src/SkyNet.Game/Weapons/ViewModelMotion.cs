using System.Numerics;
using SkyNet.Game.Mods;

namespace SkyNet.Game.Weapons;

/// <summary>
/// How a 3D gun in hand (<see cref="ViewModelModels"/>) moves, from the state the DOS art's
/// animation keeps (<see cref="ViewModelAnimation"/>), without a GPU. Its space is the camera's:
/// world units, x right, y up, ahead −Z. A shot kicks the gun back and its muzzle up, fast in and
/// eased out over the shot's animation (<see cref="Kick"/>); the pipe swings across and down
/// instead; a throw lowers it as it lowers the art (<see cref="DipDrop"/>); it sways a little at rest.
/// DOS draws frames only: the motion is the port's.
/// </summary>
public static class ViewModelMotion
{
    /// <summary>The longest side of a model whose sidecar's scale is 0 (world units: about 40 cm).</summary>
    public const float FitLength = 16f;
    /// <summary>How far a throw lowers the gun at its lowest (world units).</summary>
    public const float DipDrop = 10f;
    /// <summary>Where in the shot's animation (0..1) the kick peaks.</summary>
    public const float KickPeak = 0.15f;

    /// <summary>What the motion reads for one frame.</summary>
    /// <param name="Fire">The shot's animation, 0..1 (<see cref="ViewModelAnimation.FireProgress"/>); −1 none.</param>
    /// <param name="ThrowDip">0 rest .. 1 lowest (<see cref="ViewModelAnimation.ThrowDip"/>).</param>
    /// <param name="Clock">Seconds, for the sway.</param>
    /// <param name="Melee">The pipe: a shot is a swing.</param>
    public readonly record struct Input(float Fire, float ThrowDip, float Clock, bool Melee);

    public static readonly Input Rest = new(-1f, 0f, 0f, false);

    /// <summary>The frame a gun is drawn on: its aspect, how far below the view centre the panel's top
    /// is (a share of the frame's height: half the view above the panel), and the tangent of half the
    /// projection's vertical field of view.</summary>
    public readonly record struct Frame(float Aspect, float Gap, float TanHalfFovY)
    {
        /// <summary>The frame the sidecar's <see cref="ViewModelSidecar.Offset"/> is for: 4:3, the
        /// classic panel, the game's 70 degrees.</summary>
        public static readonly Frame Reference = Of(640, 480, Hud.HudStyle.Classic, MathF.Tan(35f * MathF.PI / 180f));

        /// <summary>A <paramref name="width"/> × <paramref name="height"/> frame with <paramref name="style"/>'s panel.</summary>
        public static Frame Of(int width, int height, Hud.HudStyle style, float tanHalfFovY) =>
            new(width / (float)Math.Max(height, 1),
                (height - Hud.DosHud.PanelPixels(width, height, style)) / (2f * Math.Max(height, 1)), tanHalfFovY);
    }

    /// <summary>The kick of a shot <paramref name="fire"/> through its animation: 0 → 1 at
    /// <see cref="KickPeak"/>, then eased back to 0 at the end; 0 when none plays.</summary>
    public static float Kick(float fire)
    {
        if (!(fire > 0f) || fire >= 1f)
        {
            return 0f;
        }

        if (fire < KickPeak)
        {
            return fire / KickPeak;
        }

        var u = 1f - (fire - KickPeak) / (1f - KickPeak);
        return u * u;
    }

    /// <summary>How far (world units, up) a gun <paramref name="depth"/> units ahead moves on
    /// <paramref name="frame"/> to keep its place above the panel as on <see cref="Frame.Reference"/>:
    /// the change of the panel's gap below the view centre, in the frame's height at that depth.</summary>
    public static float PanelLift(in Frame frame, float depth) =>
        (Frame.Reference.Gap - frame.Gap) * 2f * MathF.Max(depth, 0f) * frame.TanHalfFovY;

    /// <summary>The move for a <paramref name="lift"/>: up or down, and sideways along the line of
    /// <paramref name="shot"/> (the muzzle offset, x right, y down) to the view centre, so the shot
    /// still leaves the barrel. Example: shot (16, 14), lift −7 → (8, −7, 0). A line flatter than
    /// <see cref="MaxSlide"/> keeps the gun in place: the rocket launcher's (18, 1) slid it off the screen.</summary>
    private static Vector3 Lift(float lift, Vector2 shot)
    {
        if (shot.Y > 0f && shot.X > shot.Y * MaxSlide)
        {
            return Vector3.Zero;
        }

        return new(shot.Y > 0f ? -lift * shot.X / shot.Y : 0f, lift, 0f);
    }

    /// <summary>The flattest shot line (sideways per down) a lowered gun follows.</summary>
    private const float MaxSlide = 2f;

    /// <summary>How much of a shot shows the flash when the art has no fire frames to time it.</summary>
    public const float OneFrameFlash = 0.5f;

    /// <summary>Whether a shot's DOS flash shows at the muzzle: on its first fire frame, or for a gun
    /// whose art has none (frame 0 while <paramref name="fire"/> runs) in the shot's first half; the
    /// pipe has none.</summary>
    public static bool Flashes(int frame, float fire, bool melee) =>
        !melee && (frame == 1 || (frame == 0 && fire >= 0f && fire < OneFrameFlash));

    /// <summary>The model's space to the camera's (row vectors): its box centre to the origin,
    /// sized (the sidecar's scale, or <see cref="FitLength"/>), turned (the sidecar's rotation),
    /// moved (kick or swing about the pivot, throw dip, sway), placed at the sidecar's offset for
    /// <paramref name="frame"/> (<see cref="Frame.Reference"/> when null): <see cref="ViewModelSidecar.OffsetFor"/>
    /// its aspect when the sidecar has an <c>offsetWide</c>, else <see cref="ViewModelSidecar.Offset"/>
    /// lifted by as much as the panel's top moved against the view centre (<see cref="PanelLift"/>),
    /// sideways too along the line of <paramref name="shot"/> (the weapon's muzzle offset, x right, y down).</summary>
    public static Matrix4x4 Local(ViewModelSidecar s, (Vector3 Min, Vector3 Max) box, in Input input, Frame? frame = null,
        Vector2 shot = default)
    {
        var f = frame ?? Frame.Reference;
        var offset = s.OffsetWide != null ? s.OffsetFor(f.Aspect) : s.Offset + Lift(PanelLift(f, -s.Offset.Z), shot);
        const float Degrees = MathF.PI / 180f;
        var (min, max) = box;
        var size = max - min;
        var longest = MathF.Max(size.X, MathF.Max(size.Y, size.Z));
        var scale = s.Scale > 0f ? s.Scale : longest > 1e-6f ? FitLength / longest : 1f;
        var turn = Matrix4x4.CreateRotationX(s.Rotation.X * Degrees) * Matrix4x4.CreateRotationY(s.Rotation.Y * Degrees)
            * Matrix4x4.CreateRotationZ(s.Rotation.Z * Degrees);

        // Pitch up lifts the muzzle (−Z towards +Y); yaw left turns it towards −X.
        float pitch, yaw = 0f;
        var move = Vector3.Zero;
        if (input.Melee)
        {
            // The swing: across to the left and down, and back.
            var a = input.Fire is > 0f and < 1f ? MathF.Sin(MathF.PI * input.Fire) : 0f;
            yaw = s.Swing * 0.6f * a;
            pitch = -s.Swing * 0.5f * a;
            move = new Vector3(-6f, -2f, -4f) * a;
        }
        else
        {
            var k = Kick(input.Fire);
            pitch = s.RecoilPitch * k;
            move.Z += s.RecoilBack * k;
        }

        move.Y -= DipDrop * Math.Clamp(input.ThrowDip, 0f, 1f);
        if (s.Sway > 0f)
        {
            move += new Vector3(MathF.Sin(input.Clock * 1.3f), 0.5f * MathF.Sin(input.Clock * 2.6f), 0f) * s.Sway;
        }

        // The model centred, sized and turned; the motion's turn about the pivot there.
        var centre = (min + max) / 2f;
        var placed = Matrix4x4.CreateTranslation(-centre) * Matrix4x4.CreateScale(scale) * turn;
        var pivot = s.Pivot is { } p ? Vector3.Transform(p, placed) : Vector3.Zero;
        return placed * Matrix4x4.CreateTranslation(-pivot)
            * Matrix4x4.CreateRotationX(pitch * Degrees) * Matrix4x4.CreateRotationY(yaw * Degrees)
            * Matrix4x4.CreateTranslation(pivot + offset + move);
    }
}
