using System.Numerics;

namespace SkyNet.Game.Campaign;

/// <summary>
/// Mission 5's torpedo ride (FUN_00132e00, called by the main loop every frame). MAP.253's tube
/// hatches open and its 0xF0 drops the soldier on MAP.250 at set 10, inside the tube. No trigger of
/// the map follows: whenever the map register is 250 and the previous map 253, the view keys lock
/// at once; from the second frame the view leaves the eye along the body's heading at 700 u/s,
/// rolling 90°/s about the flight line, with TORPEDO.WAV (125); once it is 2100 units from the body
/// (about three seconds) the map changes to 254, marker set 0. The body never moves. Ported from
/// the Godot port's main.gd (_torpedo_step).
/// </summary>
public sealed class TorpedoRide
{
    public const int Map = 250, From = 253, ToMap = 254, ToSet = 0;
    public const float Speed = 700f;
    public const float RollRate = MathF.Tau * 0x200 / 2048f;
    public const float Reach = 0x834;
    public const int Sound = 125;

    public enum RidePhase { Armed, Swim, Out }

    /// <summary>DOS's own test: on MAP.250 with MAP.253 as the map before (whatever marker set).</summary>
    public static bool Due(int map, int previous) => map == Map && previous == From;

    public RidePhase Phase { get; private set; } = RidePhase.Armed;
    /// <summary>The detached view's position (valid from <see cref="RidePhase.Swim"/>).</summary>
    public Vector3 View { get; private set; }
    public Vector3 Direction { get; private set; } = -Vector3.UnitZ;
    /// <summary>Radians about the flight line, right-handed.</summary>
    public float Roll { get; private set; }
    /// <summary>The view is not the player's (keys locked, weapon and HUD not drawn).</summary>
    public bool Riding => Phase != RidePhase.Out;
    /// <summary>The view and roll at the previous tick's end (render interpolation; never simulated).</summary>
    private Vector3 _previousView;
    private float _previousRoll;

    /// <summary>A tick's start: the view now becomes the one the frames glide from.</summary>
    public void KeepPose() => (_previousView, _previousRoll) = (View, Roll);

    /// <summary>The view and roll between the last two ticks (<see cref="Interpolation"/>).</summary>
    public Vector3 ViewAt(float alpha) => Interpolation.Lerp(_previousView, View, alpha);
    public float RollAt(float alpha) => Interpolation.LerpAngle(_previousRoll, Roll, alpha);

    /// <summary>One frame with the body at <paramref name="feet"/>/<paramref name="eye"/> facing
    /// <paramref name="yaw"/> (0 faces −Z). True on the frame the view is out: change to
    /// <see cref="ToMap"/> set <see cref="ToSet"/>. <paramref name="startSound"/> is true on the
    /// frame TORPEDO.WAV starts.</summary>
    public bool Step(float dt, Vector3 feet, Vector3 eye, float yaw, out bool startSound)
    {
        startSound = false;
        switch (Phase)
        {
            case RidePhase.Armed:
                View = eye;
                Direction = new Vector3(-MathF.Sin(yaw), 0f, -MathF.Cos(yaw));
                Roll = 0f;
                KeepPose();
                Phase = RidePhase.Swim;
                startSound = true;
                return false;
            case RidePhase.Swim:
                View += Direction * (Speed * dt);
                Roll = (Roll + RollRate * dt) % MathF.Tau;
                if (Vector3.Distance(View, feet) >= Reach)
                {
                    Phase = RidePhase.Out;
                    return true;
                }

                return false;
            default:
                return false;
        }
    }
}
