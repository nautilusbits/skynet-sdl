using System.Globalization;
using System.Numerics;

namespace SkyNet.Game.Hud;

/// <summary>Which art the HUD draws: the 320 × 200 set (MDMDIMGS.BSA) or the 640 × 480 one
/// (MDMDHRES.BSA, falling back to the low set per image). Both fill the same 320 × 40 layout.</summary>
public enum HudArtSet { LowRes, HiRes }

/// <summary>What the player rides: the crosshair and the secondary rocket record follow it.</summary>
public enum HudVehicle { None = 0, Jeep = 1, Hk = 2 }

/// <summary>
/// One frame's read-out for the DOS panel (FUN_00131fd4), as plain values the game fills in.
/// Weapon slots are DOS weapon records (WEAPONnn.IMG): the caller maps its own slots
/// (<see cref="HudRecords"/> has the Godot port's tables).
/// </summary>
public struct HudState
{
    /// <summary>Health points (DOS 1000 for the soldier, 150 in multiplayer) and the maximum.</summary>
    public float Health;
    public float MaxHealth;
    /// <summary>Armour 0..1; in a vehicle its hull fraction (the ARMOR bar shows the hull).</summary>
    public float Armour;
    /// <summary>Radiation dose this frame (DOS units): RADCOUNT SAFE &lt; 1, CAUTION &lt; 35, else DANGER.</summary>
    public float Radiation;
    /// <summary>The primary weapon's record (icon), −1 none; its pool, −1 for a weapon without one (pipe, detector).</summary>
    public int PrimaryRecord;
    public int PrimaryAmmo;
    /// <summary>The thrown item's record (or the vehicle's rockets), −1 none; its count, −1 hidden.</summary>
    public int SecondaryRecord;
    public int SecondaryAmmo;
    /// <summary>The compass bearing: clockwise 11-bit (0..2047) incl. the marker-7 north offset; −1 no compass.</summary>
    public int Bearing;
    public HudVehicle Vehicle;
    /// <summary>F6: the panel hidden (the crosshair and messages stay).</summary>
    public bool PanelHidden;
    /// <summary>No crosshair (scripted views, the rocket's eye).</summary>
    public bool CrosshairHidden;
    /// <summary>The aim point in screen pixels; null: the middle of the view above the panel (DOS (160,80)).</summary>
    public Vector2? AimPoint;

    public static HudState Empty => new()
    {
        MaxHealth = HudRecords.SoldierHealth,
        PrimaryRecord = -1, PrimaryAmmo = -1,
        SecondaryRecord = -1, SecondaryAmmo = -1,
        Bearing = -1,
    };
}

/// <summary>The Godot port's slot → DOS record tables (hud_panel.gd).</summary>
public static class HudRecords
{
    public const int SoldierHealth = 1000;
    /// <summary>Thrown item's ammo pool → its record (weapon table +0x4c): 14 pipe bomb, 15 molotov,
    /// 16 grenade, 18 canister, 19 satchel (record 17 is never handed out).</summary>
    public static readonly IReadOnlyDictionary<int, int> PoolRecord = new Dictionary<int, int> { [5] = 14, [6] = 15, [2] = 16, [8] = 18, [9] = 19 };
    /// <summary>The vehicle's secondary, its rockets (pool 11): jeep 22, HK 25.</summary>
    public static int VehicleSecondRecord(HudVehicle v) => v switch { HudVehicle.Jeep => 22, HudVehicle.Hk => 25, _ => -1 };
    public const int VehicleSecondPool = 11;
}

/// <summary>The panel's numbers and gauges (FUN_00132154, FUN_00132348, FUN_0013249c, FUN_001323a2).</summary>
public static class HudFormat
{
    public const int BarWidth = 41;
    public const int CompassWindow = 37;
    public const int CompassRibbon = 185;

    /// <summary>The primary count: 4 digits, clamped 0..9999; null for no pool.</summary>
    public static string? PrimaryCount(int ammo) => ammo < 0 ? null : Math.Min(ammo, 9999).ToString(CultureInfo.InvariantCulture);

    /// <summary>The secondary count: 3 digits, clamped 0..999; null when hidden.</summary>
    public static string? SecondaryCount(int count) => count < 0 ? null : Math.Min(count, 999).ToString(CultureInfo.InvariantCulture);

    /// <summary>RADCOUNT.CFA's frame: SAFE / CAUTION / DANGER (the fourth is never shown).</summary>
    public static int RadFrame(float dose) => dose < 1f ? 0 : dose < 35f ? 1 : 2;

    /// <summary>PANELBAR pixels filled for <paramref name="fraction"/>: round(41 × v), at least 1 while v &gt; 0.</summary>
    public static int BarFill(float fraction)
    {
        var v = float.IsNaN(fraction) ? 0f : Math.Clamp(fraction, 0f, 1f);
        var filled = Math.Clamp((int)MathF.Floor(BarWidth * v + 0.5f), 0, BarWidth);
        return v > 0f && filled < 1 ? 1 : filled;
    }

    /// <summary>COMPASS.IMG's offset in the window, ≤ 0: <c>(angle × −148 + 1024) &gt;&gt; 11</c>.</summary>
    public static int CompassScroll(int bearing) => (-148 * (bearing & 0x7FF) + 1024) >> 11;

    /// <summary>A clockwise bearing in radians (0 = north) plus the marker-7 offset in degrees, as 11-bit units.</summary>
    public static int Bearing(float clockwiseRadians, int northDegrees = 0)
    {
        var turn = clockwiseRadians / (2f * MathF.PI);
        var units = (int)MathF.Floor((turn - MathF.Floor(turn)) * 2048f);
        return (units + (Math.Clamp(northDegrees, 0, 359) << 11) / 360) & 0x7FF;
    }
}
