using System.Collections.Immutable;
using SkyNet.Data;
using SkyNet.Formats;

namespace SkyNet.Game.Weapons;

/// <summary>How an ammo type flies (ammo +0x00, the callback): an instant ray, a lobbed body under
/// gravity, or a straight bolt drawn with a .3D model.</summary>
public enum AmmoFamily { Hitscan, Gravity, Bolt }

/// <summary>
/// One ammo-type record (SKYNET.EXE v1.01 0x40828, stride 0x32). <see cref="Damage"/> keeps the sign:
/// negative is a blast of |damage| besides the direct hit. <see cref="Sprite"/> is the in-flight
/// sprite index (bank &lt;&lt; 7 | record) of a gravity type, <see cref="Model"/> the .3D of a bolt;
/// <see cref="ImpactBank"/> is the effect bank (-1 none). <see cref="Reach"/> (+0x24) is set on the
/// pipe's type only: the melee reach.
/// </summary>
public sealed record AmmoRecord(int Index, AmmoFamily Family, string Model, int Sprite, int ImpactBank, int Damage,
    int EffectSize, int LifeTicks, int FireSound, int ImpactSound, int Reach, int Flags, int Pieces, int PieceType)
{
    public const int FlagBounce = 0x01, FlagFollowCamera = 0x08, FlagHoming = 0x10;
    /// <summary>The ammo lifetimes count about 35 ticks a second (120 = 3.4 s rockets).</summary>
    public const float TicksPerSecond = 35f;

    public bool Bounces => (Flags & FlagBounce) != 0;
    public bool FollowCamera => (Flags & FlagFollowCamera) != 0;
    public bool IsMelee => Reach > 0;
    public float Life => LifeTicks / TicksPerSecond;
    /// <summary>|damage|: the direct hit and, when <see cref="Blasts"/>, the blast strength.</summary>
    public float Strength => Math.Abs(Damage);
    public bool Blasts => Damage < 0;
}

/// <summary>
/// One weapon record (SKYNET.EXE v1.01 0x43714, stride 0x60). <see cref="Speed"/> (+0x18) is the
/// launch speed along the shot (bullets 4500, lasers 1800, rockets 1200, launcher 2200, thrown
/// 500, satchel 50) and <see cref="Lift"/> (+0x1c) the speed added straight up (392 for the
/// launcher and the thrown items). The muzzle is from the eye in the view's frame: x right,
/// y down, z ahead (DOS adds 20 to z).
/// </summary>
public sealed record WeaponRecord(int Index, string Name, int AmmoType, int Speed, int Lift, int Rate, int MuzzleX, int MuzzleY,
    int MuzzleZ, int FireSound, int Pool, int Cost, int SelectSound, int DrySound, int Flags, int ViewX)
{
    /// <summary>+0x5c bit 1: the shot flies at the point under the crosshair (else along the view).</summary>
    public const int FlagAimed = 0x02;

    public bool Aimed => (Flags & FlagAimed) != 0;
    /// <summary>Seconds between shots: DOS's cooldown is 0x10000 / rate in 16.16 seconds.</summary>
    public float Cooldown => Rate > 0 ? 1f / Rate : 1f;
}

/// <summary>An ammo pool <c>{current, initial, max}</c> (0x440ec, 13 × 12 bytes).</summary>
public readonly record struct PoolRecord(int Initial, int Max);

/// <summary>
/// The weapon, ammo and pool tables and the campaign start list: SKYNET.EXE v1.01's, as shipped in
/// data/tables (weapons, ammo_types, ammo_pools, start_weapons; see <see cref="GameTables"/>).
/// </summary>
public sealed class WeaponTable
{
    public const int WeaponStride = 0x60, WeaponCount = 26, AmmoStride = 0x32, AmmoCount = 25, PoolCount = 13;
    /// <summary>The thrown items (second register, F1-F5): records 14-19, 17 never owned.</summary>
    public const int FirstThrown = 14, LastThrown = 19;
    /// <summary>Records 20-25 are the vehicle guns; 13 the multiplayer motion detector.</summary>
    public const int FirstVehicle = 20, MotionDetector = 13, SuperUzi = 12, Pipe = 0;

    /// <summary>The on-foot names (STRINGS.PRS + the CFA art, as the Godot port keys its slots).</summary>
    public static readonly IReadOnlyList<string> Names =
    [
        "PIPE", "UZI", "ASSAULT RIFLE", "MACHINE GUN", "SHOTGUN", "GRENADE LAUNCHER", "ROCKET LAUNCHER",
        "LASER RIFLE", "LASER CANNON", "PLASMA PISTOL", "PLASMA RIFLE", "PLASMA CANNON", "SUPER UZI",
        "MOTION DETECTOR", "PIPE BOMB", "MOLOTOV", "GRENADE", "THROWN 17", "CANISTER BOMB", "SATCHEL",
        "JEEP PLASMA", "JEEP PLASMA", "JEEP ROCKETS", "JEEP ROCKETS", "HK LASER", "HK ROCKETS",
    ];

    public ImmutableArray<WeaponRecord> Weapons { get; }
    public ImmutableArray<AmmoRecord> Ammo { get; }
    public ImmutableArray<PoolRecord> Pools { get; }
    public ImmutableArray<int> StartList { get; }
    /// <summary>Where the weapon table came from: "embedded", or a mod's file.</summary>
    public string Source { get; }

    public WeaponTable(IEnumerable<WeaponRecord> weapons, IEnumerable<AmmoRecord> ammo, IEnumerable<PoolRecord> pools,
        IEnumerable<int> start, string source)
    {
        Weapons = [.. weapons];
        Ammo = [.. ammo];
        Pools = [.. pools];
        StartList = [.. start];
        Source = source;
    }

    public AmmoRecord? AmmoOf(WeaponRecord w) => w.AmmoType >= 0 && w.AmmoType < Ammo.Length ? Ammo[w.AmmoType] : null;

    /// <summary>The fire sound: the record's +0x48, or its ammo type's +0x1c when that is -1.</summary>
    public int FireSoundOf(WeaponRecord w) => w.FireSound >= 0 ? w.FireSound : AmmoOf(w)?.FireSound ?? -1;

    /// <summary>The same tables with another campaign start list (Future Shock's).</summary>
    public WeaponTable WithStartList(IEnumerable<int> start) => new(Weapons, Ammo, Pools, start, Source);

    /// <summary>The shipped tables with <paramref name="game"/>'s start list (Future Shock: the pipe
    /// alone on foot, then the thrown items and vehicle guns).</summary>
    public static WeaponTable For(GameKind game)
    {
        var weapons = GameTables.Weapons.Select(w => new WeaponRecord(w.Index, w.Index < Names.Count ? Names[w.Index] : $"WEAPON {w.Index}",
            w.Ammo, w.Speed, w.Lift, w.Rate, w.MuzzleX, w.MuzzleY, w.MuzzleZ, w.FireSound, w.Pool, w.Cost, w.SelectSound, w.DrySound,
            w.Flags, w.ViewX));
        var ammo = GameTables.Ammo.Select(a => new AmmoRecord(a.Index,
            a.Family switch { "gravity" => AmmoFamily.Gravity, "bolt" => AmmoFamily.Bolt, _ => AmmoFamily.Hitscan },
            a.Model, a.Look is > 0x10000 and < 0x40000 ? -1 : a.Look, a.Effect > 0 ? a.Effect >> 7 : -1, a.Damage, a.Size, a.Life, a.FireSound,
            a.ImpactSound, a.Reach, a.Flags, a.Pieces, a.PieceType));
        var pools = GameTables.Pools.Select(p => new PoolRecord(p.Initial, p.Max));
        var start = game == GameKind.FutureShock ? GameTables.StartLists.FutureShock : GameTables.StartLists.SkyNet;
        return new WeaponTable(weapons, ammo, pools, start, GameTables.SourceOf(TableNames.Weapons));
    }

    /// <summary>The shipped tables with SkyNET's start list.</summary>
    public static WeaponTable Shipped => For(GameKind.SkyNet);

    /// <summary>The shipped tables with the data's game's start list.</summary>
    public static WeaponTable Load(GameData data) => For(data.Game);
}
