using System.Numerics;
using RetroEng.Render;
using SkyNet.Game.Weapons;

namespace SkyNet.Game.Enhanced;

/// <summary>The game a frame shows: a network game's weapon lights cast no shadows (GitHub #17).</summary>
public enum PlayKind { Campaign, Network }

/// <summary>
/// The shots' own light in the enhanced look, made each frame from the weapons' effects and the
/// bolts in flight (the Godot port's ENHANCED muzzle_flash.gd, explosion.gd and projectile.gd,
/// removed in git 3dfe3b6). Intensities are at 150 u (<see cref="RetroEng.Render.Lights.LightFalloff.EnergyAtRef"/>);
/// Godot's colours are sRGB, linear here.
/// <code>
///   muzzle flash   1.2 → 0 over its 0.08 s, 600 u, the shot's colour
///   explosion      3.0 → 0 down its frames (every blast, the robots' deaths too),
///                  max(radius · 5, 600) u, (1, 0.7, 0.4)
///   bullet hit     0.5 → 0 over 0.08 s, 140 u, the explosion's colour (a hand laser's hit)
///   bolt in flight 0.5, dark at the muzzle and full 260 u on (it never lights its shooter),
///                  max(splash, 420) u, the bolt's colour
///   laser / plasma by its class and colour (<see cref="ShotLightTable"/>, a mod's lights/shots.json),
///                  ramped as above, and a short coloured flash where it strikes (instead of the
///                  explosion's warm light) — every shooter's: the soldier's, the robots', the vehicles',
///                  a network game's other players and bots (their shots fly here too)
/// </code>
/// </summary>
public static class GameLights
{
    /// <summary>Explosions shadowed at once, the newest (brightest) first (GitHub #17). The engine
    /// shadows at most 2 lights a frame, each about as dear as the other; one blast leaves the
    /// other slot to the soldier's flash.</summary>
    public const int ShadowedBlasts = 1;

    public const float FlashIntensity = 1.2f, FlashRange = 600f;
    public const float ExplosionIntensity = 3f, ExplosionRangeScale = 5f, ExplosionRangeMin = 600f;
    /// <summary>A bullet's hit: a hand laser's (it lit like a grenade, an uzi's burst flooded rooms).</summary>
    public const float HitIntensity = 0.5f, HitRange = 140f, HitLife = 0.08f;
    /// <summary>projectile.gd LIGHT_ENERGY and LIGHT_RAMP.</summary>
    public const float BoltIntensity = 0.5f, BoltRamp = 260f, BoltRangeMin = 420f;

    /// <summary>explosion.gd's light colour (sRGB).</summary>
    public static readonly Vector3 ExplosionSrgb = new(1f, 0.7f, 0.4f);
    /// <summary>fly_camera.gd _KIND_COLOR / enemy.gd _ammo_color / projectile.gd (sRGB).</summary>
    public static readonly Vector3 BulletSrgb = new(1f, 0.92f, 0.55f), LaserSrgb = new(1f, 0.32f, 0.22f),
        PlasmaSrgb = new(0.45f, 0.7f, 1f), RocketSrgb = new(1f, 0.75f, 0.4f), RobotBoltSrgb = new(1f, 0.45f, 0.22f);

    private static readonly Vector3 Explosion = LevelLights.Linear(ExplosionSrgb), Bullet = LevelLights.Linear(BulletSrgb),
        Laser = LevelLights.Linear(LaserSrgb), Plasma = LevelLights.Linear(PlasmaSrgb), Rocket = LevelLights.Linear(RocketSrgb),
        RobotBolt = LevelLights.Linear(RobotBoltSrgb);

    /// <summary>A shot's light colour (linear) by its ammo: a hitscan shot warm white; a bolt by its
    /// model (LASER1 red, LASER2 blue, ROCKET orange), else the soldier's default orange or the
    /// robots' red-orange.</summary>
    public static Vector3 ColourOf(AmmoRecord? ammo, bool robot = false)
    {
        if (ammo == null || ammo.Family == AmmoFamily.Hitscan)
        {
            return Bullet;
        }

        var model = ammo.Model;
        if (model.StartsWith("LASER1", StringComparison.OrdinalIgnoreCase)) return Laser;
        if (model.StartsWith("LASER2", StringComparison.OrdinalIgnoreCase)) return Plasma;
        if (model.StartsWith("ROCKET", StringComparison.OrdinalIgnoreCase)) return Rocket;
        return robot ? RobotBolt : ammo.Family == AmmoFamily.Bolt ? Rocket : Bullet;
    }

    /// <summary>A muzzle flash's light: 1.2 fading to 0 over its life.</summary>
    public static PointLight FlashLight(Effect e) =>
        new(e.Position, ColourOf(e.Ammo), FlashRange,
            RetroEng.Render.Lights.LightFalloff.EnergyAtRef(FlashIntensity * MathF.Max(0f, 1f - e.Age / MathF.Max(e.Life, 1e-4f))));

    /// <summary>An explosion's light: 3.0 stepping down its frames, (1 − frame / frames).</summary>
    public static PointLight ExplosionLight(Effect e)
    {
        var frames = MathF.Max(1f, MathF.Round(e.Life * Effect.Fps));
        var k = MathF.Max(0f, 1f - e.Frame / frames);
        return new PointLight(e.Position, Explosion, MathF.Max(e.Size * 0.5f * ExplosionRangeScale, ExplosionRangeMin),
            RetroEng.Render.Lights.LightFalloff.EnergyAtRef(ExplosionIntensity * k));
    }

    /// <summary>A bullet's hit: <see cref="HitIntensity"/> fading to 0 over <see cref="HitLife"/>.</summary>
    public static PointLight HitLight(Effect e) =>
        new(e.Position, Explosion, HitRange,
            RetroEng.Render.Lights.LightFalloff.EnergyAtRef(HitIntensity * MathF.Max(0f, 1f - e.Age / HitLife)));

    /// <summary>A bolt's light (null for a lobbed body or a hitscan record), ramped over the first
    /// <see cref="BoltRamp"/> units of its flight: a laser's (and any bolt a mod names) from
    /// <paramref name="table"/> (default <see cref="ShotLightTable.Current"/>), else 0.5 over
    /// max(splash, 420) u.</summary>
    public static PointLight? BoltLight(Projectile p, ShotLightTable? table = null)
    {
        if (p.Done || p.Lobbed || p.Ammo.Family != AmmoFamily.Bolt)
        {
            return null;
        }

        var travelled = p.Age * p.Velocity.Length();
        var ramp = Math.Clamp(travelled / BoltRamp, 0f, 1f);
        if ((table ?? ShotLightTable.Current).For(p.Ammo, p.Source != null) is { } shot)
        {
            return new PointLight(p.Position, shot.Colour, shot.Range, shot.Energy * ramp);
        }

        var range = MathF.Max(p.Ammo.Blasts ? Weapons.Blast.Radius(p.Ammo.Strength) : 0f, BoltRangeMin);
        return new PointLight(p.Position, ColourOf(p.Ammo, p.Source != null), range,
            RetroEng.Render.Lights.LightFalloff.EnergyAtRef(BoltIntensity * ramp));
    }

    /// <summary>A bolt's impact flash: its table light fading linearly over its life (null: none).</summary>
    public static PointLight? ImpactLight(ImpactFlash f, ShotLightTable? table = null)
    {
        if ((table ?? ShotLightTable.Current).For(f.Ammo, f.Robot) is not { ImpactLife: > 0f } shot || f.Age >= shot.ImpactLife)
        {
            return null;
        }

        return new PointLight(f.At, shot.Colour, shot.ImpactRange, shot.ImpactEnergy * (1f - f.Age / shot.ImpactLife));
    }

    /// <summary>Adds the weapons' lights that shine now to <paramref name="into"/>. A shot whose
    /// table light has an impact flash lights its impact with that, not the explosion's. Shadows at
    /// <paramref name="shadows"/> High only (GitHub #5: a phone's Low can't spare the ~3 ms) and in a
    /// <see cref="PlayKind.Campaign"/> only (GitHub #17): the soldier's own muzzle flash and the newest
    /// <see cref="ShadowedBlasts"/> explosions; others' flashes, impacts and bolts light shadowless.</summary>
    public static void Append(List<PointLight> into, WeaponSystem weapons, ShotLightTable? table = null, ShadowQuality shadows = ShadowQuality.Off,
        PlayKind play = PlayKind.Campaign)
    {
        table ??= ShotLightTable.Current;
        var casts = shadows == ShadowQuality.High && play == PlayKind.Campaign;

        // Explosions before the newest ShadowedBlasts light shadowless.
        var blasts = 0;
        foreach (var e in weapons.Effects)
        {
            blasts += e.Kind == EffectKind.Explosion && EffectLight(e, table).Energy > 0f ? 1 : 0;
        }

        var unshadowed = blasts - ShadowedBlasts;
        foreach (var e in weapons.Effects)
        {
            var light = EffectLight(e, table);
            if (light.Energy <= 0f)
            {
                continue;
            }

            var shadowed = e.Kind == EffectKind.OwnFlash || (e.Kind == EffectKind.Explosion && unshadowed-- <= 0);
            into.Add(light with { CastsShadow = casts && shadowed });
        }

        foreach (var p in weapons.Projectiles)
        {
            if (BoltLight(p, table) is { Energy: > 0f } light)
            {
                into.Add(light);
            }
        }

        foreach (var f in weapons.Impacts)
        {
            if (ImpactLight(f, table) is { Energy: > 0f } light)
            {
                into.Add(light);
            }
        }
    }

    /// <summary>An effect's light: an explosion's or a muzzle flash's (zero energy: none, or its
    /// shot's table impact flash lights it instead).</summary>
    private static PointLight EffectLight(Effect e, ShotLightTable table)
    {
        if (e.Done || (e.Animated && e.Ammo != null && table.For(e.Ammo) is { ImpactLife: > 0f }))
        {
            return default;
        }

        if (e.Animated && e.Kind == EffectKind.Impact)
        {
            return HitLight(e);
        }

        return e.Animated ? ExplosionLight(e) : e.Bank == WeaponSystem.FlashBank ? FlashLight(e) : default;
    }
}
