using SkyNet.Game.Weapons;

namespace SkyNet.Game.Vehicles;

/// <summary>
/// A vehicle's two guns on the <see cref="WeaponSystem"/> (the Godot port's VEHICLE_WEAPONS,
/// _throw_secondary and _regen_vehicle_energy):
/// <list type="bullet">
/// <item>FIRE: the jeep's plasma (record 20; 21 is the other barrel), the HK's laser (24);
/// THROW: the rocket pod (22/23 jeep, 25 HK; DOS's secondary default 0x4449a = {15, 25, 22}).</item>
/// <item>The barrels take turns: DOS negates the record's muzzle x after every shot in a vehicle
/// (0x125e5e).</item>
/// <item>Pool 10 (vehicle energy, 1000 of 2000, 100 a bolt) never runs dry for good: it recharges
/// 450 a second once the trigger has rested 0.35 s, and a gun emptied overheats until the pool is
/// back to 400 (a port addition, docs/game-logic/damage-hud.md). Pool 11 holds the rockets.</item>
/// </list>
/// </summary>
public sealed class VehicleGuns(VehicleMode mode) : IWeaponMount
{
    public const int EnergyPool = 10;
    public const int RocketPool = 11;
    public const float RegenPerSecond = 450f;
    public const float RegenDelay = 0.35f;
    public const int CoolLevel = 400;

    private bool _gunMirror, _podMirror;
    private float _regenCarry;

    public VehicleMode Mode { get; } = mode;
    /// <summary>The record on FIRE: jeep plasma 20, HK laser 24.</summary>
    public int Gun => GunOf(Mode);
    /// <summary>The record on THROW: jeep rockets 22, HK rockets 25.</summary>
    public int Pod => PodOf(Mode);
    /// <summary>The gun is cooling down to <see cref="CoolLevel"/> (a load sets it back).</summary>
    public bool Overheated { get; internal set; }
    /// <summary>Seconds since the gun last drew on the energy pool.</summary>
    public float SinceShot { get; private set; } = 10f;

    public static int GunOf(VehicleMode mode) => mode switch { VehicleMode.Jeep => 20, VehicleMode.Hk => 24, _ => -1 };

    public static int PodOf(VehicleMode mode) => mode switch { VehicleMode.Jeep => 22, VehicleMode.Hk => 25, _ => -1 };

    /// <summary>The records a vehicle mounts (the even barrel of each jeep pair).</summary>
    public static int[] Records(VehicleMode mode) => mode == VehicleMode.Foot ? [] : [GunOf(mode), PodOf(mode)];

    public void Tick(Inventory inventory, float dt)
    {
        SinceShot += dt;
        if (SinceShot < RegenDelay)
        {
            return;
        }

        var max = inventory.Table.Pools[EnergyPool].Max;
        if (inventory.Pool(EnergyPool) >= max)
        {
            _regenCarry = 0f;
            return;
        }

        _regenCarry += RegenPerSecond * dt;
        var whole = (int)_regenCarry;
        _regenCarry -= whole;
        inventory.AddAmmo(EnergyPool, whole);
        if (Overheated && inventory.Pool(EnergyPool) >= CoolLevel)
        {
            Overheated = false;
        }
    }

    public (int Record, bool Mirror)? Trigger(bool secondary, Inventory inventory)
    {
        var record = secondary ? Pod : Gun;
        if (record < 0)
        {
            return null;
        }

        var w = inventory.Table.Weapons[record];
        if (w.Pool == EnergyPool && inventory.FireCooldown <= 0f && inventory.Pool(EnergyPool) < w.Cost)
        {
            Overheated = true;               // a cold start needs CoolLevel
        }

        if (w.Pool == EnergyPool && Overheated)
        {
            return null;
        }

        return (record, secondary ? _podMirror : _gunMirror);
    }

    public void Fired(bool secondary, FireResult result)
    {
        if (result != FireResult.Fired)
        {
            return;
        }

        if (secondary)
        {
            _podMirror = !_podMirror;
        }
        else
        {
            _gunMirror = !_gunMirror;
            SinceShot = 0f;
        }
    }

    /// <summary>The jeep's muzzle takes no lead (fly_camera.gd _muzzle_point: the car keeps its
    /// speed elsewhere); the HK's does.</summary>
    public Shooter Aim(in Shooter shooter) => Mode == VehicleMode.Jeep ? shooter with { Velocity = default } : shooter;
}
