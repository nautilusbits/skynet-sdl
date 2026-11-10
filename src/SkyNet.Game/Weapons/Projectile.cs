using System.Numerics;

namespace SkyNet.Game.Weapons;

/// <summary>A shot in flight: a straight bolt or rocket (<see cref="AmmoFamily.Bolt"/>) or a lobbed
/// body under gravity (<see cref="AmmoFamily.Gravity"/>: the launcher's shell and the thrown items).</summary>
public sealed class Projectile
{
    /// <summary>The fall of a robot's lobbed body (the Godot port's grenade.gd GRAVITY). The soldier's
    /// shots fall at their record's +0x1c instead (<see cref="Fall"/>).</summary>
    public const float Gravity = 1400f;
    /// <summary>A steered rocket's turn (DOS 0x123fdc): at most 0x300 of 2048 a second (135°), and
    /// the look-ahead of half its time to the target, 0x20 / 0x10000 s to 2 s.</summary>
    public const float SteerRate = 0x300 / 2048f * MathF.Tau;
    public const float SteerLookMin = 0x20 / 65536f, SteerLookMax = 2f;
    /// <summary>A bounce keeps 0xAAAB/0x10000 of the speed (FUN_0011c19a → FUN_0011c100).</summary>
    public const float BounceKeep = 2f / 3f;
    /// <summary>Under this bounced speed it goes off.</summary>
    public const float BounceDeadSpeed = 50f;
    /// <summary>On a floor (normal up ≥ 252/256) it goes off at this bounced speed or less.</summary>
    public const float BounceRestSpeed = 100f;
    public const float BounceFloorUp = 252f / 256f;

    public required WeaponRecord Weapon { get; init; }
    public required AmmoRecord Ammo { get; init; }
    public Vector3 Position { get; set; }
    public Vector3 Velocity { get; set; }
    public float Age { get; set; }
    public bool Done { get; set; }
    /// <summary>The rocket eye follows it (ammo flag 0x08 and RUN held at launch).</summary>
    public bool Followed { get; set; }
    public int Bounces { get; set; }
    /// <summary>A lobbed body's fall (units/s²): DOS 0x1261b1/0x1263cc put the record's +0x1c in the
    /// shot's acceleration (392, the soldier's own gravity).</summary>
    public float Fall { get; init; } = Gravity;
    /// <summary>What a steered rocket follows (DOS's tracker entry, 0x12543b); null: flies straight.</summary>
    public IDamageable? Target { get; set; }
    /// <summary>Seconds until a planted satchel goes off; null while it flies (or for any other shot).</summary>
    public float? Fuse { get; set; }
    /// <summary>Where a planted satchel's blast sits (<see cref="Blast.Placement"/>); null for any other shot.</summary>
    public Vector3? Burst { get; set; }
    /// <summary>The robot that fired it (<see cref="WeaponSystem.EnemyFire"/>): the shot passes
    /// through its shooter and strikes the soldier; null for the soldier's own shots.</summary>
    public IDamageable? Source { get; init; }
    /// <summary>Somebody else's shot in a network game (<see cref="WeaponSystem.RemoteFire"/>): it
    /// flies, bounces and goes off as its own, but strikes and blasts nothing.</summary>
    public bool Harmless { get; init; }

    public bool Lobbed => Ammo.Family == AmmoFamily.Gravity;

    /// <summary>Where it was at the previous tick's end (render interpolation; never simulated); null
    /// until a tick's start has kept it (fired this tick).</summary>
    public Vector3? Previous { get; set; }

    /// <summary>Where it is drawn between the last two ticks (<see cref="Interpolation"/>): a shot
    /// never jumps, so only a flight past <see cref="DrawSnap"/> in a tick is drawn where it ends.</summary>
    public Vector3 DrawnAt(float alpha) => Previous is { } p ? Interpolation.Lerp(p, Position, alpha, DrawSnap) : Position;

    public const float DrawSnap = 4096f;

    /// <summary>
    /// DOS's steering of a locked rocket (0x123fdc): it aims at the target less its own flight over
    /// half the time to it, and turns toward that by the angle over that half time a second (at most
    /// <see cref="SteerRate"/>), keeping its speed.
    /// </summary>
    public static Vector3 Steer(Vector3 at, Vector3 velocity, Vector3 target, float dt)
    {
        var speed = velocity.Length();
        if (speed < 1e-3f)
        {
            return velocity;
        }

        // Half the time to the target, and where the rocket will be by then.
        var half = Math.Clamp(Vector3.Distance(target, at) / speed * 0.5f, SteerLookMin, SteerLookMax);
        var want = target - (at + velocity * half);
        var heading = velocity / speed;
        var axis = Vector3.Cross(heading, want);
        if (axis.LengthSquared() < 1e-6f)
        {
            return velocity;
        }

        var angle = MathF.Acos(Math.Clamp(Vector3.Dot(heading, Vector3.Normalize(want)), -1f, 1f));
        var turn = MathF.Min(angle / half, SteerRate) * dt;
        return Vector3.Transform(heading, Quaternion.CreateFromAxisAngle(Vector3.Normalize(axis), turn)) * speed;
    }

    /// <summary>What a lobbed body does on touching a surface.</summary>
    public enum BounceOutcome { Detonate, Bounce }

    /// <summary>
    /// DOS's bounce (routine 0x1233ea): the velocity mirrored in the surface and scaled by 2/3;
    /// it detonates instead when the bounced speed is under 50 u/s, or at most 100 u/s on a floor.
    /// </summary>
    public static BounceOutcome Bounce(Vector3 velocity, Vector3 normal, out Vector3 bounced)
    {
        bounced = Vector3.Reflect(velocity, normal) * BounceKeep;
        var speed = bounced.Length();
        if (speed < BounceDeadSpeed || (speed <= BounceRestSpeed && normal.Y >= BounceFloorUp))
        {
            return BounceOutcome.Detonate;
        }

        return BounceOutcome.Bounce;
    }
}

/// <summary>A short-lived sprite: an impact or explosion animation (every frame of record 0 of an
/// effect bank at <see cref="Effect.Fps"/>) or a muzzle flash (one frame for <see cref="Effect.Life"/>).</summary>
public sealed class Effect
{
    /// <summary>The Godot port's explosion.gd rate.</summary>
    public const float Fps = 24f;

    public int Bank { get; init; }
    public int Record { get; init; }
    public Vector3 Position { get; init; }
    /// <summary>The sprite's largest side in world units.</summary>
    public float Size { get; init; }
    /// <summary>The ORIGINAL look's size: texels per world unit of the sprite (DOS FUN_0012420f's
    /// +0x1a / 256); 0 draws <see cref="Size"/> as the ENHANCED look does.</summary>
    public float DosScale { get; init; }
    public float Age { get; set; }
    /// <summary>Seconds it shows; for an animation the frames' time.</summary>
    public float Life { get; init; }
    public bool Animated { get; init; }
    /// <summary>A muzzle flash's shot, or the shot whose impact this is (its light's colour in the
    /// enhanced look); null otherwise.</summary>
    public AmmoRecord? Ammo { get; init; }
    /// <summary>What it shows: the enhanced look (with PARTICLES on) draws a shot's impact as
    /// <see cref="BulletPuff"/> dust and an explosion as a <see cref="BlastCloud"/>, not the sprite.</summary>
    public EffectKind Kind { get; init; }
    /// <summary>A muzzle flash's shot way (unit; zero: unknown) and its seed (the enhanced look's
    /// burst turns and spreads by it: no two shots alike).</summary>
    public Vector3 Direction { get; init; }
    public int Seed { get; init; }

    public int Frame => Animated ? (int)(Age * Fps) : 0;
    public bool Done => Age >= Life;
}

/// <summary>An <see cref="Effect"/>'s part: a muzzle flash, a shot's impact on a surface, an explosion
/// (a rocket's or a lobbed body's burst, a robot's, a vehicle's or a breakable's death).</summary>
/// <summary>What an effect is; <see cref="Warp"/>: a robot teleporting in (its sprite in every look).</summary>
public enum EffectKind { Flash, Impact, Explosion, OwnFlash, Warp }

/// <summary>Whether the DOS flash of the player's own shot shows: hidden while a 3D gun draws its own.</summary>
public enum OwnFlashes { Shown, Hidden }

/// <summary>An explosion (<see cref="WeaponSystem.Blasts"/>): the enhanced look's fireball, flash
/// ring of dust, sparks and lingering smoke (<see cref="WeaponRenderer"/>), sized by its
/// <see cref="Radius"/>. Cosmetic: no simulation reads it.</summary>
public sealed class BlastCloud
{
    /// <summary>Seconds one is kept (the smoke's life) and how many at most (the oldest go first).</summary>
    public const float MaxLife = 4.5f;
    public const int MaxKept = 32;

    public Vector3 At { get; init; }
    /// <summary>The blast's size (units, the radius the DOS sprite is drawn at).</summary>
    public float Radius { get; init; }
    public int Seed { get; init; }
    public float Age { get; set; }
}

/// <summary>Where a bullet (a hitscan shot: the player's, a robot's, a remote player's or bot's)
/// struck a wall, a floor or a prop (<see cref="WeaponSystem.Puffs"/>): the enhanced look's dust
/// cloud there, with sparks on metal (<see cref="WeaponRenderer"/>). Cosmetic: no simulation reads it.</summary>
public sealed class BulletPuff
{
    /// <summary>Seconds one is kept (the dust's life) and how many at most (the oldest go first).</summary>
    public const float MaxLife = 1.6f;
    public const int MaxKept = 96;

    public Vector3 At { get; init; }
    /// <summary>The struck surface's normal (unit) and the shot's way (unit).</summary>
    public Vector3 Normal { get; init; }
    public Vector3 Incoming { get; init; }
    /// <summary>Metal: sparks besides the dust (a metal map, <see cref="WeaponSystem.MetalWorld"/>).</summary>
    public bool Metal { get; init; }
    /// <summary>The cloud's size factor (the shot's strength: a shotgun's is bigger).</summary>
    public float Scale { get; init; } = 1f;
    public int Seed { get; init; }
    public float Age { get; set; }
}

/// <summary>A shot's wisp of smoke at the muzzle (<see cref="WeaponSystem.Smokes"/>): the enhanced
/// look's (<see cref="Enhanced.MuzzleFlashes"/>). Cosmetic: no simulation reads it.</summary>
public sealed class MuzzleSmoke
{
    /// <summary>Seconds one is kept and how many at most (the oldest go first).</summary>
    public const float MaxLife = 0.7f;
    public const int MaxKept = 32;

    public Vector3 At { get; init; }
    /// <summary>The shot's way (unit; zero: unknown).</summary>
    public Vector3 Direction { get; init; }
    /// <summary>The flash's size (world units): the wisp's scale.</summary>
    public float Size { get; init; }
    public int Seed { get; init; }
    public float Age { get; set; }
}

/// <summary>Where a bolt struck (<see cref="WeaponSystem.Impacts"/>): the enhanced look's short
/// impact flash (<see cref="Enhanced.GameLights"/>). Cosmetic: no simulation reads it.</summary>
public sealed class ImpactFlash
{
    /// <summary>Seconds one is kept (the longest flash), how many at most, and how far off the surface it sits.</summary>
    public const float MaxLife = 0.5f, Lift = 8f;
    public const int MaxKept = 64;

    public Vector3 At { get; init; }
    public required AmmoRecord Ammo { get; init; }
    /// <summary>A robot's shot (it takes the robots' class, <see cref="Enhanced.ShotLightTable.RobotClass"/>).</summary>
    public bool Robot { get; init; }
    public float Age { get; set; }
}
