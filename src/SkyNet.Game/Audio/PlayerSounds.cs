namespace SkyNet.Game.Audio;

/// <summary>A sound the soldier's own body makes this tick (2D, DOS's volume and pitch).</summary>
public readonly record struct BodySound(int Id, SoundBank.DosOverlap Overlap);

/// <summary>
/// The soldier's footsteps and landings as SKYNET.EXE v1.01 plays them (FUN_0012b18c, the walk):
/// <list type="bullet">
/// <item>The ground's sound is the MAP's, not the face's: the maptype (marker type 6) picks a row
/// of the music table (VA 0x4fbf9, 0x24 bytes a row), whose first word is the surface,
/// and FUN_0012c133 copies that surface's three sounds out of the tables at 0x4d150 (landing),
/// 0x4d160 (left foot) and 0x4d170 (right foot). Surfaces: 0 ground (jmpcon, lfwgrv/rfwgrv),
/// 1 metal (jmpmet, lfwmet/rfwmet), 2 wood (jmpwod, lfwwod/rfwwod), 3 sewer (jmpsew,
/// lfwsew/rfwsew).</item>
/// <item>A step: a phase of 2048 a stride advances by the forward speed × 10 per second (sideways
/// speed does not count) while the soldier is on the ground (vertical speed within 5 u/s); the
/// left foot sounds when it passes 0, the right when it passes 1024, each restarting itself
/// (flags 6). Wading the feet sound leftwatr/rightwtr; under water there are none.</item>
/// <item>A landing faster than 100 u/s plays the surface's landing sound (flags 2: not over
/// itself). A jump makes no sound when it starts (FUN_0012be58).</item>
/// <item>A hit the soldier lives through grunts one of grunt1/2/5/6 (list 0x39187, FUN_00122b52),
/// not over itself; his death grunt9 (<see cref="DeathGrunt"/>).</item>
/// </list>
/// The phase is cosmetic state: it is not saved or hashed and draws no random numbers.
/// </summary>
public sealed class PlayerSounds
{
    public const int Ground = 0, Metal = 1, Wood = 2, Sewer = 3;
    /// <summary>The music table's surface word per maptype (v1.01 0x4fbf9 + row × 0x24).</summary>
    public static readonly IReadOnlyList<int> SurfaceByMapType = [0, 1, 3, 2, 1, 1, 1, 0, 0, 1, 0, 2];
    /// <summary>Landing sound per surface (0x4d150).</summary>
    public static readonly IReadOnlyList<int> Landing = [23, 24, 25, 86];
    /// <summary>Left foot per surface (0x4d160).</summary>
    public static readonly IReadOnlyList<int> LeftFoot = [70, 74, 72, 84];
    /// <summary>Right foot per surface (0x4d170).</summary>
    public static readonly IReadOnlyList<int> RightFoot = [71, 75, 73, 85];
    public const int LeftWade = 0x7b, RightWade = 0x7c;
    /// <summary>The pain grunts (list 0x39187: a count, then the ids).</summary>
    public static readonly IReadOnlyList<int> PainGrunts = [18, 101, 104, 105];
    public const int DeathGrunt = 108;
    /// <summary>A landing slower than this is silent (0x6400 in DOS's 8.8 speed).</summary>
    public const float LandingMinSpeed = 100f;
    /// <summary>Feet count as on the ground within this vertical speed (0x500 in 8.8).</summary>
    public const float GroundedSpeed = 5f;
    public const int StridePhase = 2048;
    public const float PhasePerUnit = 10f;

    private float _phase;

    /// <summary>The map's surface (<see cref="Ground"/>..<see cref="Sewer"/>).</summary>
    public int Surface { get; set; } = Ground;

    /// <summary>Where the stride is (0..2048).</summary>
    public float Phase => _phase;

    /// <summary>The surface of a maptype; out of the table's range: the ground (maptype 0's).</summary>
    public static int SurfaceOf(int mapType) =>
        SurfaceByMapType[mapType >= 0 && mapType < SurfaceByMapType.Count ? mapType : 0];

    /// <summary>A new map: its surface, the stride from its start.</summary>
    public void Enter(int mapType)
    {
        Surface = SurfaceOf(mapType);
        _phase = 0f;
    }

    /// <summary>One tick of the soldier on foot. <paramref name="forwardSpeed"/> is the horizontal
    /// speed along the heading (negative backing up), <paramref name="verticalSpeed"/> the body's
    /// (0 on a floor), <paramref name="landedSpeed"/> the fall speed of a landing this tick (0 none).
    /// The sounds to play go into <paramref name="into"/>.</summary>
    public void Tick(float dt, float forwardSpeed, bool onFloor, float verticalSpeed, float landedSpeed,
        bool wading, bool submerged, List<BodySound> into)
    {
        if (landedSpeed > LandingMinSpeed)
        {
            into.Add(new BodySound(Landing[Surface], SoundBank.DosOverlap.Skip));
        }

        if (forwardSpeed == 0f || !onFloor || MathF.Abs(verticalSpeed) > GroundedSpeed)
        {
            return;
        }

        var before = _phase;
        var after = before + forwardSpeed * PhasePerUnit * dt;
        // Which half-stride marks it crossed (forwards or backwards): 0 is the left foot, 1024 the right.
        var half = StridePhase / 2f;
        var crossedLeft = Math.Floor(before / StridePhase) != Math.Floor(after / StridePhase);
        var crossedRight = Math.Floor((before - half) / StridePhase) != Math.Floor((after - half) / StridePhase);
        _phase = after - MathF.Floor(after / StridePhase) * StridePhase;
        if (submerged || !(crossedLeft || crossedRight))
        {
            return;
        }

        var left = crossedLeft;
        var id = wading ? (left ? LeftWade : RightWade) : (left ? LeftFoot : RightFoot)[Surface];
        into.Add(new BodySound(id, SoundBank.DosOverlap.Restart));
    }

    /// <summary>The pain grunt for a draw of the cosmetic generator (0 ≤ draw &lt; 16, DOS's
    /// <c>rand &amp; 0xf</c> folded into the list as FUN_00122b52 folds it).</summary>
    public static int PainGrunt(int draw)
    {
        var last = PainGrunts.Count - 1;
        var i = draw & 0xf;
        while (i > last)
        {
            i -= last;
        }

        return PainGrunts[i];
    }
}
