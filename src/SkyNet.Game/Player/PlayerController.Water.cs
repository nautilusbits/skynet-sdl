namespace SkyNet.Game.Player;

/// <summary>
/// The soldier in water (SKYNET.EXE v1.01 FUN_0012c15d, every frame from FUN_0012ae00): no swimming.
/// Wading (surface at least 5 units above the feet, flag 0x10000 in [0x33200], set by FUN_0012ea6b)
/// halves the top speeds (250/400/210/310 → 125/200/105/155) and quarters the acceleration
/// (1500 → 375); submerged (surface at or above the eye, 0x4000000 in [0x30a50] and 0x8000 in
/// [0x33200], FUN_00121083) also drops gravity 392 → 150, the jump 177 → 100 and halves the mouse look.
/// </summary>
public sealed partial class PlayerController
{
    public const float WadeSpeedScale = 0.5f;
    public const float WadeAccel = 375f;
    public const float WadeFeetDepth = 5f;
    public const float SubmergedGravity = 150f;
    public const float SubmergedJump = 100f;
    public const float SubmergedLookScale = 0.5f;

    /// <summary>The map's water surface (world Y); null on a dry map.</summary>
    public float? WaterSurface { get; set; }

    /// <summary>The surface at least <see cref="WadeFeetDepth"/> above the feet.</summary>
    public bool Wading => WaterSurface is { } s && s - Position.Y >= WadeFeetDepth;

    /// <summary>The surface at or above the eye (the breath runs, the palette turns).</summary>
    public bool Submerged => WaterSurface is { } s && s >= EyePosition.Y;

    private float SpeedScale => Wading ? WadeSpeedScale : 1f;
    private float AccelNow => Wading ? WadeAccel : GroundAccel;
    private float GravityNow => Wading && Submerged ? SubmergedGravity : Gravity;
    private float JumpNow => Wading && Submerged ? SubmergedJump : JumpSpeed;
    private float LookScale => Wading && Submerged ? SubmergedLookScale : 1f;
}
