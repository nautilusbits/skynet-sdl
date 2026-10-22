using System.Numerics;
using SkyNet.Game.Hud;
using SkyNet.Game.Player;
using SkyNet.Game.Weapons;

namespace SkyNet.Game.Campaign;

/// <summary>
/// The soldier's health and armour (docs/game-logic/damage-hud.md): 1000 points; armour 0..1 wears
/// by 82/65536 per point of a hit and lets (1 − armour) of it through; no invulnerability frames.
/// A blast target through <see cref="WeaponSystem.Player"/>.
/// </summary>
public sealed class Soldier(PlayerController player) : IDamageable
{
    public const float MaxHealth = HudRecords.SoldierHealth;
    public const float ArmourWear = 82f / 65536f;

    public float Health { get; set; } = MaxHealth;
    /// <summary>0..1 of the bar.</summary>
    public float Armour { get; set; }
    public Vector3 Centre => player.Position + new Vector3(0f, PlayerController.BodyHeight / 2f, 0f);
    public float Radius => 0f;
    public bool Alive => Health > 0f;
    /// <summary>Takes no damage (a scripted run, a cheat).</summary>
    public bool God { get; set; }

    /// <summary>Called with the health a hit took (the gamepad's VIBRATION); null: nothing listens.</summary>
    public Action<float>? Wounded { get; set; }

    /// <summary>Called when a hit that is not silent leaves the soldier alive (DOS's pain grunt,
    /// FUN_00122b52); null: nothing listens.</summary>
    public Action? Pained { get; set; }

    /// <summary>A network deathmatch: the server owns the bar, so every hit the soldier takes here
    /// (his own blast, a fall, the water) goes to it as points before the armour, and the bar comes
    /// back in the server's answer (the Godot port's fly_camera.gd take_damage under Net.active).
    /// Null: the campaign, the hit is taken here.</summary>
    public Action<float>? Remote { get; set; }

    public void TakeDamage(in Damage damage) => Hurt(damage.Points);

    /// <summary>A hit of <paramref name="points"/> through the armour (handler 0x122652). DOS grunts
    /// on every hit the soldier lives through (<see cref="Pained"/>) except the silent ones: the
    /// water's and the radiation's.</summary>
    public void Hurt(float points, bool silent = false)
    {
        if (points <= 0f || God || !Alive)
        {
            return;
        }

        if (Remote is { } remote)
        {
            remote(points);
            return;
        }

        Armour = MathF.Max(0f, Armour - ArmourWear * points);
        var before = Health;
        Health = MathF.Max(0f, Health - (1f - Armour) * points);
        if (Health < before)
        {
            Wounded?.Invoke(before - Health);
        }

        if (!silent && Alive)
        {
            Pained?.Invoke();
        }
    }

    /// <summary>A health pickup: a percentage of the bar, capped at the bar.</summary>
    public void HealPercent(int percent) => Health = MathF.Min(MaxHealth, Health + MaxHealth * percent / 100f);

    public void AddArmour(float fraction) => Armour = Math.Clamp(Armour + fraction, 0f, 1f);

    /// <summary>Mission start: full health, no armour.</summary>
    public void Reset()
    {
        Health = MaxHealth;
        Armour = 0f;
    }
}
