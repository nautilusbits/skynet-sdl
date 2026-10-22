using System.Numerics;

namespace SkyNet.Game.Automap;

/// <summary>The automap's held controls this frame (DOS: arrows turn and tilt, + and − zoom).</summary>
[Flags]
public enum AutomapKeys
{
    None = 0,
    TurnLeft = 1,
    TurnRight = 2,
    TiltUp = 4,
    TiltDown = 8,
    ZoomIn = 16,
    ZoomOut = 32,
}

/// <summary>
/// The automap's eye as SKYNET.EXE v1.01 places it (0x13e137 on open, 0x13e092 each frame, keys
/// 0x13dd70). On open it keeps the soldier's eye and a unit offset behind and above it,
/// (−forward.x, 1, −forward.z): 45° up for a level look. Each frame that offset is turned by the
/// orbit angle, scaled by the distance and added to the kept eye; the view looks the soldier's
/// way plus the orbit, pitched down on its own (Up / Down change only the look, not the place).
/// <code>
///        eye ●  ─┐ pitch (look down, 22.5°..67.5°)
///           / ╲  ┘
///    offset/   ╲
///  × dist /     ╲
///        ● soldier's eye at open (the arrow 40 below it)
/// </code>
/// Angles are DOS's 2048 to a turn; the distance is the offset's scale (0x100..0x300, 0x200 at
/// first) and is kept from one opening to the next, as DOS's static is.
/// </summary>
internal sealed class AutomapCamera
{
    /// <summary>DOS's angle units to a full turn.</summary>
    public const int Turn = 2048;
    /// <summary>Turn and tilt: 256 units a second (45°).</summary>
    public const float AngleRate = 256f;
    /// <summary>Zoom: 250 distance units a second.</summary>
    public const float ZoomRate = 250f;
    public const float StartPitch = 0xC0;
    public const float MinPitch = 0x80;
    public const float MaxPitch = 0x180;
    public const float StartDistance = 0x200;
    public const float MinDistance = 0x100;
    public const float MaxDistance = 0x300;

    private Vector3 _target;
    private Vector3 _offset = new(0f, 1f, 1f);
    private float _yaw;

    /// <summary>The orbit about the kept eye (DOS units; + turns left).</summary>
    public float Orbit { get; private set; }

    /// <summary>The look's pitch down (DOS units).</summary>
    public float Pitch { get; private set; } = StartPitch;

    public float Distance { get; private set; } = StartDistance;

    /// <summary>Where the eye was when the map opened (the arrow hangs 40 below it).</summary>
    public Vector3 Target => _target;

    /// <summary>The soldier's yaw when the map opened (the arrow's heading), radians.</summary>
    public float Heading => _yaw;

    /// <summary>Opening: the soldier's eye and look (<paramref name="forward"/>, unit); the orbit
    /// and the pitch start again, the distance stays.</summary>
    public void Open(Vector3 eye, float yaw, Vector3 forward)
    {
        _target = eye;
        _yaw = yaw;
        _offset = new Vector3(-forward.X, 1f, -forward.Z);
        Orbit = 0f;
        Pitch = StartPitch;
    }

    /// <summary>One frame of held controls over <paramref name="seconds"/>.</summary>
    public void Step(AutomapKeys keys, float seconds)
    {
        var angle = AngleRate * seconds;
        Orbit = (Orbit + Axis(keys, AutomapKeys.TurnLeft, AutomapKeys.TurnRight) * angle) % Turn;
        Pitch = Math.Clamp(Pitch + Axis(keys, AutomapKeys.TiltUp, AutomapKeys.TiltDown) * angle, MinPitch, MaxPitch);
        Distance = Math.Clamp(Distance + Axis(keys, AutomapKeys.ZoomOut, AutomapKeys.ZoomIn) * ZoomRate * seconds, MinDistance, MaxDistance);
    }

    /// <summary>The eye: the offset turned by the orbit, scaled by the distance, from the kept eye.</summary>
    public Vector3 Eye => _target + Vector3.Transform(_offset, Matrix4x4.CreateRotationY(Radians(Orbit))) * Distance;

    /// <summary>The look's yaw (the soldier's plus the orbit) and pitch (negative: down), radians.</summary>
    public (float Yaw, float Pitch) Look => (_yaw + Radians(Orbit), -Radians(Pitch));

    public static float Radians(float units) => units * (2f * MathF.PI / Turn);

    /// <summary>+1 for the first control, −1 for the second, 0 for both or none.</summary>
    private static int Axis(AutomapKeys keys, AutomapKeys plus, AutomapKeys minus) =>
        (keys.HasFlag(plus) ? 1 : 0) - (keys.HasFlag(minus) ? 1 : 0);
}
