using SkyNet.Game.Weapons;

namespace SkyNet.Game.Vehicles;

/// <summary>
/// A vehicle's two guns on the <see cref="WeaponSystem"/> (the Godot port's VEHICLE_WEAPONS,
/// _throw_secondary and _regen_vehicle_energy):
/// <list type="bullet">
/// <item>FIRE: the jeep's laser (record 24), the HK's plasma (21; 20 is the other barrel);
/// THROW: the rocket pod (25 jeep, 22/23 HK). DOS 0x1257a4 picks both by mode (4 jeep, 8 HK):
/// FIRE 0x44476 = {0, 24, 21}, THROW 0x4449a = {15, 25, 22}.</item>
/// <item>The barrels take turns: DOS negates the record's muzzle x after every shot in a vehicle
/// (0x125e5e).</item>
/// <item>Pool 10 (vehicle energy, 1000 of 2000, 100 a bolt) recharges 300 a second, firing or not
/// (DOS 0x13202b); short of a bolt's cost the gun clicks dry. Pool 11 holds the rockets.</item>
/// </list>
/// </summary>
public sealed class VehicleGuns(VehicleMode mode) : IWeaponMount
{
    public const int EnergyPool = 10;
    public const int RocketPool = 11;
    public const float RegenPerSecond = 300f;

    private bool _gunMirror, _podMirror;
    private float _regenCarry;

    public VehicleMode Mode { get; } = mode;
    /// <summary>The record on FIRE: jeep laser 24, HK plasma 21.</summary>
    public int Gun => GunOf(Mode);
    /// <summary>The record on THROW: jeep rockets 25, HK rockets 22.</summary>
    public int Pod => PodOf(Mode);

    public static int GunOf(VehicleMode mode) => mode switch { VehicleMode.Jeep => 24, VehicleMode.Hk => 21, _ => -1 };

    public static int PodOf(VehicleMode mode) => mode switch { VehicleMode.Jeep => 25, VehicleMode.Hk => 22, _ => -1 };

    /// <summary>The records a vehicle mounts.</summary>
    public static int[] Records(VehicleMode mode) => mode == VehicleMode.Foot ? [] : [GunOf(mode), PodOf(mode)];

    public void Tick(Inventory inventory, float dt)
    {
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
    }

    public (int Record, bool Mirror)? Trigger(bool secondary, Inventory inventory)
    {
        var record = secondary ? Pod : Gun;
        if (record < 0)
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
        }
    }

    /// <summary>The jeep's muzzle takes no lead (fly_camera.gd _muzzle_point: the car keeps its
    /// speed elsewhere); the HK's does.</summary>
    public Shooter Aim(in Shooter shooter) => Mode == VehicleMode.Jeep ? shooter with { Velocity = default } : shooter;
}
