using System.Numerics;
using SkyNet.Game.Campaign;
using SkyNet.Game.Weapons;

namespace SkyNet.Game.Vehicles;

/// <summary>
/// A vehicle's hull, the blast and shot target while the player rides (DOS 0x130c4a): the soldier
/// is not hurt at all, the hull takes every hit 1:1 from its own pool (0x130caa: 1000 for the jeep,
/// 1500 for the HK), the ARMOR bar shows it (0x13231d), and at zero the ride is over: the soldier
/// dies with it (the Godot port's take_damage). No armour, no difficulty factor of its own (the
/// shots' factor is <see cref="WeaponSystem.DamageToPlayer"/>, applied before the hit lands).
/// </summary>
public sealed class Hull(VehicleController vehicle, Soldier? soldier) : IDamageable
{
    public const float JeepPoints = 1000f;
    public const float HkPoints = 1500f;
    /// <summary>Where DOS measures a distance to the jeep (a death blast, 0x1359e1): 70 over the wheels.</summary>
    public const float JeepCentreHeight = 70f;

    public static float PointsFor(VehicleMode mode) => mode == VehicleMode.Hk ? HkPoints : JeepPoints;

    public float Points { get; set; } = PointsFor(vehicle.Mode);
    public float Max => PointsFor(vehicle.Mode);
    /// <summary>0..1 for the ARMOR bar.</summary>
    public float Fraction => Math.Clamp(Points / Max, 0f, 1f);
    public bool God { get; set; }

    /// <summary>A network deathmatch: the server owns the hull, so every hit it takes here (the
    /// driver's own blast, a level blast, a ram) goes to it as points, and the hull comes back in the
    /// server's answer (as <see cref="Soldier.Remote"/>). Null: the campaign.</summary>
    public Action<float>? Remote { get; set; }

    public Vector3 Centre => vehicle.Mode == VehicleMode.Jeep
        ? vehicle.Body.Position + new Vector3(0f, JeepCentreHeight, 0f)
        : vehicle.Body.EyePosition;
    public float Radius => vehicle.Mode == VehicleMode.Hk ? VehicleController.HkRadius : VehicleController.JeepRadius;
    public bool Alive => Points > 0f && (soldier?.Alive ?? true);

    /// <summary>Raised on every hit that lands (the app plays HIT2, shakes the view).</summary>
    public event Action<float>? Hurt;
    /// <summary>Raised once when the hull reaches zero.</summary>
    public event Action? Destroyed;

    public void TakeDamage(in Damage damage) => Hit(damage.Points);

    public void Hit(float points)
    {
        // God mode is the soldier's: the cheat sets only Soldier.God, and an emptied hull kills him.
        if (points <= 0f || God || soldier is { God: true } || !Alive)
        {
            return;
        }

        if (Remote is { } remote)
        {
            remote(points);
            return;
        }

        Points = MathF.Max(0f, Points - points);
        Hurt?.Invoke(points);
        if (Points <= 0f)
        {
            if (soldier != null)
            {
                soldier.Health = 0f;
            }

            Destroyed?.Invoke();
        }
    }

    /// <summary>A fresh ride: the whole hull.</summary>
    public void Reset() => Points = Max;
}
