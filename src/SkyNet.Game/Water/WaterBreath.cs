using RetroEng.Loop;
namespace SkyNet.Game.Water;

/// <summary>What one frame under (or out of) water asks for.</summary>
/// <param name="WentUnder">The head just went under: splash, and the fall slows to 1/16.</param>
/// <param name="CameUp">The head just came out: splash, and <paramref name="GetAir"/> after a long dive.</param>
/// <param name="Damage">Drowning points this frame (through the armour, no grunt).</param>
/// <param name="Bubbles">A bubbles sound puffs near the head.</param>
/// <param name="Drown">The drowning sound, when it is not already playing.</param>
public readonly record struct BreathFrame(bool WentUnder, bool CameUp, bool GetAir, float Damage, bool Bubbles, bool Drown);

/// <summary>
/// The held breath (SKYNET.EXE v1.01 FUN_0012ea6b, every frame while the map has water). Its clock
/// [0x38c00] counts 70 ticks a second (FUN_00124c63: + 0x460000 / period); the breath starts
/// again at every frame above water ([0x50000] := clock). Going under: sound 0x73 (splash), the
/// drown total [0x50008] zeroed and the vertical speed (v + 8) >> 4. Under: FUN_0012ec34 puffs
/// 0x76 (bubbles2) at 0x97/0x400 per frame, and from 0x348 ticks (12 s) the total grows by
/// dt × 0x5500 (85 points a second, 24.8) into FUN_00122b52 with no grunt, playing 0x74 (drown)
/// at 0x15f/0x400 per frame. Coming up: 0x73, and 0x7a (getair2) after 0x23 ticks (0.5 s) under.
/// The two chances are per DOS frame; here they are per second at DOS's 35 frames.
/// </summary>
public sealed class WaterBreath
{
    public const int SplashSound = 0x73, DrownSound = 0x74, BubblesSound = 0x76, GetAirSound = 0x7a;
    public const float TicksPerSecond = 70f;
    /// <summary>0x348 ticks of held breath (12.0 s).</summary>
    public const float AirSeconds = 0x348 / TicksPerSecond;
    /// <summary>0x23 ticks under before coming up gasps (0.5 s).</summary>
    public const float GaspSeconds = 0x23 / TicksPerSecond;
    /// <summary>0x5500 / 256 points a second.</summary>
    public const float DrownPerSecond = 0x5500 / 256f;
    /// <summary>The vertical speed as the head goes under: (v + 8) >> 4.</summary>
    public const float UnderSpeedDivisor = 16f;
    private const float DosFrames = 35f;
    private const float BubbleChance = 0x97 / 1024f, DrownChance = 0x15f / 1024f;

    private readonly SimRandom _random;
    private bool _under;

    public WaterBreath(SimRandom? random = null) => _random = random ?? new SimRandom(1);

    /// <summary>Seconds the head has been under.</summary>
    public float Under { get; private set; }
    public bool IsUnder => _under;

    /// <summary>Back to fresh air (a new map, a respawn).</summary>
    public void Reset()
    {
        _under = false;
        Under = 0f;
    }

    /// <summary>One frame of <paramref name="dt"/> seconds with the head <paramref name="under"/> water.</summary>
    public BreathFrame Tick(float dt, bool under)
    {
        if (!under)
        {
            var cameUp = _under;
            var gasp = cameUp && Under >= GaspSeconds;
            Reset();
            return new BreathFrame(false, cameUp, gasp, 0f, false, false);
        }

        if (!_under)
        {
            _under = true;
            Under = 0f;
            return new BreathFrame(true, false, false, 0f, false, false);
        }

        Under += dt;
        var bubbles = _random.NextSingle() < BubbleChance * DosFrames * dt;
        if (Under < AirSeconds)
        {
            return new BreathFrame(false, false, false, 0f, bubbles, false);
        }

        var drown = _random.NextSingle() < DrownChance * DosFrames * dt;
        return new BreathFrame(false, false, false, DrownPerSecond * dt, bubbles, drown);
    }
}
