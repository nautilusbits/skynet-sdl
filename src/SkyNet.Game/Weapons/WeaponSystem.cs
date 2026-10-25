using System.Numerics;
using RetroEng.Diagnostics;
using RetroEng.Physics;
using SkyNet.Game.Audio;
using SkyNet.Game.DevTools;

namespace SkyNet.Game.Weapons;

/// <summary>The weapon keys of one tick: the triggers held, RUN (the rocket eye), and the
/// selections pressed (-1 none; <see cref="Cycle"/> ±1 for <c>'</c>/<c>;</c>).</summary>
public readonly record struct WeaponInput(bool Fire, bool Throw, bool Run, int Select = -1, int SelectThrown = -1, int Cycle = 0);

/// <summary>Where the soldier shoots from: the eye, the view and his velocity (the muzzle leads by
/// the distance he covers in the tick).</summary>
public readonly record struct Shooter(Vector3 Eye, float Yaw, float Pitch, Vector3 Velocity = default);

/// <summary>
/// A vehicle's gun mount (SkyNet.Game.Vehicles.VehicleGuns): while one is set, FIRE fires its gun,
/// THROW its rocket pod (DOS's secondary register in a vehicle), and the weapon keys do nothing.
/// </summary>
public interface IWeaponMount
{
    /// <summary>Once a tick before the triggers (the energy regen).</summary>
    void Tick(Inventory inventory, float dt);

    /// <summary>The record the gun (<paramref name="secondary"/> false) or the pod fires now and
    /// whether its muzzle's x is mirrored (the other barrel); null: held back (overheated).</summary>
    (int Record, bool Mirror)? Trigger(bool secondary, Inventory inventory);

    /// <summary>What the pull did (the barrel flips, an empty energy pool overheats).</summary>
    void Fired(bool secondary, FireResult result);

    /// <summary>The shooter the mount fires as (the jeep's muzzle takes no lead).</summary>
    Shooter Aim(in Shooter shooter);
}

/// <summary>What one shot or blast did (the scripted fire test prints these).</summary>
public readonly record struct ShotReport(int Weapon, string What, Vector3 Point, int Owner, IDamageable? Target, float Points);

/// <summary>
/// The soldier's weapons in the world: the <see cref="Inventory"/>, firing (hitscan bullets, the
/// pipe, bolts and rockets, lobbed shells and thrown items), the shots in flight against the
/// <see cref="CollisionWorld"/> and the <see cref="DamageTargets"/>, impacts with their effect
/// sprites and sounds, and DOS's radial blast. Ported from the Godot port's fly_camera.gd
/// (_shoot, _throw_secondary, _melee_hit), projectile.gd, grenade.gd and explosion.gd, with the
/// numbers from SKYNET.EXE's tables (<see cref="WeaponTable"/>). Run <see cref="Tick"/> once per
/// fixed tick. World axes: Y up, −Z north.
/// </summary>
public sealed class WeaponSystem
{
    /// <summary>DOS adds 20 to every muzzle's z (FUN_00125caf).</summary>
    public const float MuzzleAhead = 20f;
    /// <summary>The hitscan ray's reach, and how far the aim point is looked for.</summary>
    public const float HitscanReach = 60000f;
    public const float AimReach = 20000f;
    /// <summary>The pipe's swing starts this far ahead of the eye (Godot's _melee_hit).</summary>
    public const float MeleeStart = 40f;
    /// <summary>The bullet impact puff's radius (Godot's hitscan Explosion.spawn).</summary>
    public const float PuffRadius = 40f;
    /// <summary>Thrown and lobbed bodies burst at this radius (grenade.gd).</summary>
    public const float LobbedBurstRadius = 280f;
    /// <summary>The enhanced look's generated explosion of a lobbed grenade, as a share of
    /// <see cref="LobbedBurstRadius"/> (the DOS sprite's size reads far too big as a fireball).</summary>
    public const float LobbedCloudScale = 0.45f;
    /// <summary>The same for a blasting projectile (rockets).</summary>
    public const float RocketCloudScale = 0.7f;
    /// <summary>An effect nearer the eye than this is skipped, and none is drawn bigger than this
    /// share of its distance (explosion.gd NEAR_SKIP, NEAR_SIZE_RATIO).</summary>
    public const float NearSkip = 70f;
    public const float NearSizeRatio = 0.4f;
    /// <summary>The Godot port's muzzle flare: TEXTURE.219 record 0 for 0.08 s, 18 units (26 for
    /// the shotgun), 70 units down the shot; at the gun art for the guns that fire from the eye.</summary>
    public const int FlashBank = 219;
    public const float FlashLife = 0.08f;
    public static readonly Vector3 FlashAtGun = new(15f, 12f, 40f);
    /// <summary>Each bounce of a thrown item plays this (jmpmet.raw).</summary>
    public const int BounceSound = SoundIds.JumpMetal;
    /// <summary>The difficulty's damage to enemies (0x5c210 column 2): LOW 1.5, MED 1.25, HIGH 1.0.</summary>
    public static readonly float[] DamageToEnemiesByDifficulty = [1.5f, 1.25f, 1f];

    /// <summary>A robot's shot strikes the soldier inside this sphere about his centre (the Godot
    /// port's player capsule is 2 × 38 units tall).</summary>
    public const float PlayerHitRadius = 32f;
    /// <summary>The record a robot's shots carry (index -1: not one of the soldier's weapons).</summary>
    public static readonly WeaponRecord RobotWeapon = new(-1, "ROBOT", -1, 0, 0, 0, 0, 0, 0, -1, -1, 0, -1, -1, 0, 0);

    private readonly CollisionWorld _world;
    private readonly List<Projectile> _projectiles = [];
    private readonly List<Effect> _effects = [];
    private readonly List<ImpactFlash> _impacts = [];
    private readonly List<BulletPuff> _puffs = [];
    private readonly List<BlastCloud> _blasts = [];
    private int _puffSeed;
    private Vector3 _eye;

    public WeaponSystem(WeaponTable table, CollisionWorld world, DamageTargets? targets = null, SoundBank? sounds = null)
    {
        Inventory = new Inventory(table);
        _world = world;
        Targets = targets ?? new DamageTargets();
        Sounds = sounds;
    }

    public Inventory Inventory { get; }
    public WeaponTable Table => Inventory.Table;
    public DamageTargets Targets { get; }
    public SoundBank? Sounds { get; set; }
    /// <summary>The soldier, hurt by blasts (unscaled) when set.</summary>
    public IDamageable? Player { get; set; }
    /// <summary>Multiplies the direct hits on targets (default MED).</summary>
    public float DamageToEnemies { get; set; } = DamageToEnemiesByDifficulty[1];
    /// <summary>Multiplies the direct hits of the robots' shots on the soldier (the difficulty's
    /// damage to the player: LOW 0.75, MED and HIGH 1).</summary>
    public float DamageToPlayer { get; set; } = 1f;
    /// <summary>The vehicle's guns while riding; null on foot.</summary>
    public IWeaponMount? Mount { get; set; }
    /// <summary>Frames of an effect bank's record 0 (the renderer knows); null: 8.</summary>
    public Func<int, int>? EffectFrames { get; set; }

    public IReadOnlyList<Projectile> Projectiles => _projectiles;
    public IReadOnlyList<Effect> Effects => _effects;
    /// <summary>Where bolts struck lately (the enhanced look's impact flash; no simulation reads it).</summary>
    public IReadOnlyList<ImpactFlash> Impacts => _impacts;
    /// <summary>Where bullets struck walls, floors and props lately (the enhanced look's dust and sparks; no simulation reads it).</summary>
    public IReadOnlyList<BulletPuff> Puffs => _puffs;
    /// <summary>Explosions lately (the enhanced look's fireball and smoke; no simulation reads it).</summary>
    public IReadOnlyList<BlastCloud> Blasts => _blasts;
    /// <summary>The map's surface is metal (its footsteps', <see cref="Audio.PlayerSounds.Metal"/>): bullets throw sparks there.</summary>
    public bool MetalWorld { get; set; }
    /// <summary>The sparks and fragments thrown where shots strike (ammo +0x2a/+0x2e).</summary>
    public ImpactPieces Pieces { get; } = new();
    /// <summary>Every impact and blast as it happens.</summary>
    public event Action<ShotReport>? Reported;
    /// <summary>The night moon a trigger pull may hit (null: none).</summary>
    public NightMoon? Moon { get; set; }
    /// <summary>A shot left (or the pipe swung): the record, and whether it was thrown (the view-model's cue).</summary>
    public event Action<int, bool>? Fired;
    /// <summary>A shot left: the record, where it left from and along what (a network game sends it on,
    /// and the other machines draw it with <see cref="RemoteFire"/>).</summary>
    public event Action<int, Vector3, Vector3>? Shot;

    /// <summary>The rocket eye (docs rocket-eye.md): 120 behind and 10 above the followed rocket,
    /// looking along its flight; null when no rocket is followed.</summary>
    public (Vector3 Eye, Vector3 Forward)? RocketEye => RocketEyeAt(1f);

    /// <summary>The rocket eye with the rocket where it is drawn, <paramref name="alpha"/> of the way
    /// from the previous tick (<see cref="Projectile.DrawnAt"/>).</summary>
    public (Vector3 Eye, Vector3 Forward)? RocketEyeAt(float alpha)
    {
        foreach (var p in _projectiles)
        {
            if (p.Followed && !p.Done && p.Velocity.LengthSquared() > 1e-6f)
            {
                var f = Vector3.Normalize(p.Velocity);
                return (p.DrawnAt(alpha) - f * 120f + Vector3.UnitY * 10f, f);
            }
        }

        return null;
    }

    /// <summary>A tick's start: the shots' and the impact pieces' positions become the ones the
    /// frames glide from (render interpolation, <see cref="WeaponRenderer.Capture"/>).</summary>
    public void KeepPoses()
    {
        foreach (var p in _projectiles)
        {
            p.Previous = p.Position;
        }

        Pieces.KeepPoses();
    }

    /// <summary>One fixed tick: selections, triggers, the shots in flight and the effects.</summary>
    public void Tick(in WeaponInput input, in Shooter shooter, float dt)
    {
        _eye = shooter.Eye;
        Inventory.Tick(dt);
        if (Mount is { } mount)
        {
            TickMounted(mount, input, shooter, dt);
        }
        else
        {
            TickOnFoot(input, shooter, dt);
        }

        using (Prof.Zone(GameZones.Projectiles))
        {
            StepProjectiles(dt);
            Pieces.Step(dt, _world);
        }

        for (var i = _effects.Count - 1; i >= 0; i--)
        {
            _effects[i].Age += dt;
            if (_effects[i].Done)
            {
                _effects.RemoveAt(i);
            }
        }

        for (var i = _puffs.Count - 1; i >= 0; i--)
        {
            _puffs[i].Age += dt;
            if (_puffs[i].Age >= BulletPuff.MaxLife)
            {
                _puffs.RemoveAt(i);
            }
        }

        for (var i = _blasts.Count - 1; i >= 0; i--)
        {
            _blasts[i].Age += dt;
            if (_blasts[i].Age >= BlastCloud.MaxLife)
            {
                _blasts.RemoveAt(i);
            }
        }

        for (var i = _impacts.Count - 1; i >= 0; i--)
        {
            _impacts[i].Age += dt;
            if (_impacts[i].Age >= ImpactFlash.MaxLife)
            {
                _impacts.RemoveAt(i);
            }
        }
    }

    /// <summary>In a vehicle: no selections; FIRE is the gun, THROW the rocket pod, both through the
    /// gun's path (and its shared cooldown), the rockets followed with RUN held.</summary>
    private void TickMounted(IWeaponMount mount, in WeaponInput input, in Shooter shooter, float dt)
    {
        mount.Tick(Inventory, dt);
        var aim = mount.Aim(shooter);
        if (!input.Run)
        {
            foreach (var p in _projectiles)
            {
                p.Followed = false;
            }
        }

        if (input.Fire && mount.Trigger(false, Inventory) is { } gun)
        {
            var fired = FireRecord(gun.Record, aim, input.Run, dt, gun.Mirror);
            mount.Fired(false, fired);
            MoonShot(fired, aim);
        }

        if (input.Throw && mount.Trigger(true, Inventory) is { } pod)
        {
            mount.Fired(true, FireRecord(pod.Record, aim, input.Run, dt, pod.Mirror));
        }
    }

    private void TickOnFoot(in WeaponInput input, in Shooter shooter, float dt)
    {
        if (input.Select >= 0 && Inventory.Select(input.Select))
        {
            Sounds?.Play2D(Table.Weapons[Inventory.Primary].SelectSound, -8f);
        }

        if (input.Cycle != 0 && Inventory.Cycle(input.Cycle))
        {
            Sounds?.Play2D(Table.Weapons[Inventory.Primary].SelectSound, -8f);
        }

        if (input.SelectThrown >= 0 && Inventory.SelectThrown(input.SelectThrown))
        {
            Sounds?.Play2D(9, -8f);
        }

        if (!input.Run)
        {
            foreach (var p in _projectiles)
            {
                p.Followed = false;
            }
        }

        if (input.Fire)
        {
            MoonShot(Fire(shooter, input.Run, dt), shooter);
        }

        if (input.Throw)
        {
            Throw(shooter, dt);
        }
    }

    /// <summary>A trigger pull with nothing under the crosshair may hit the moon (FUN_001261b1: not
    /// the thrown items nor a vehicle's rocket pod, which go through FUN_001263cc).</summary>
    private void MoonShot(FireResult result, in Shooter s)
    {
        if (result != FireResult.Fired || Moon is not { Shown: true } moon)
        {
            return;
        }

        var (f, r, u) = Basis(s.Yaw, s.Pitch);
        if (Trace(s.Eye, f, AimReach, out _, out _, out _, out _))
        {
            return;
        }

        moon.Shoot(f, r, u);
    }

    /// <summary>The view's basis: forward, right, up.</summary>
    public static (Vector3 Forward, Vector3 Right, Vector3 Up) Basis(float yaw, float pitch)
    {
        var f = SkyNet.Game.Player.PlayerController.Direction(yaw, pitch);
        var r = new Vector3(MathF.Cos(yaw), 0f, -MathF.Sin(yaw));
        return (f, r, Vector3.Cross(r, f));
    }

    /// <summary>A muzzle offset (x right, y down, z ahead) from the eye, plus DOS's 20 and the lead.</summary>
    public static Vector3 MuzzlePoint(in Shooter s, Vector3 offset, float dt)
    {
        var (f, r, u) = Basis(s.Yaw, s.Pitch);
        var lead = MathF.Max(Vector3.Dot(s.Velocity, f), 0f) * dt;
        return s.Eye + r * offset.X - u * offset.Y + f * (MuzzleAhead + offset.Z + lead);
    }

    /// <summary>Pulls the gun's trigger once (held: DOS auto-fire, paced by the cooldown).</summary>
    public FireResult Fire(in Shooter s, bool run = false, float dt = 0f) => FireRecord(Inventory.Primary, s, run, dt);

    /// <summary>Pulls the trigger of record <paramref name="record"/> (owned, through its pool and
    /// the gun's cooldown); <paramref name="mirror"/> negates its muzzle's x (a vehicle's other barrel).</summary>
    public FireResult FireRecord(int record, in Shooter s, bool run = false, float dt = 0f, bool mirror = false)
    {
        var w = Table.Weapons[record];
        var ammo = Table.AmmoOf(w);
        if (ammo == null)
        {
            return FireResult.NotOwned;
        }

        var result = Inventory.TryFire(record);
        if (result == FireResult.Dry)
        {
            Sounds?.Play2D(w.DrySound, -6f);
        }

        if (result != FireResult.Fired)
        {
            return result;
        }

        Sounds?.Play2D(Table.FireSoundOf(w), -5f);
        Fired?.Invoke(record, false);
        var (fwd, _, _) = Basis(s.Yaw, s.Pitch);
        if (ammo.IsMelee || record == WeaponTable.Pipe)
        {
            Shot?.Invoke(record, s.Eye, fwd);
            Melee(s, w, ammo, fwd);
            return result;
        }

        var offset = new Vector3(mirror ? -w.MuzzleX : w.MuzzleX, w.MuzzleY, w.MuzzleZ);
        var muzzle = MuzzlePoint(s, offset, dt);
        var shot = offset == Vector3.Zero ? fwd : ShotDirection(s.Eye, muzzle, fwd, w.Aimed);
        Shot?.Invoke(record, ammo.Family == AmmoFamily.Hitscan ? s.Eye : muzzle, ammo.Family == AmmoFamily.Hitscan ? fwd : shot);
        var flashAt = offset == Vector3.Zero ? MuzzlePoint(s, FlashAtGun, dt) : muzzle;
        Flash(flashAt + shot * 70f, record == 4 ? 26f : 18f, ammo, EffectKind.OwnFlash);

        if (ammo.Family == AmmoFamily.Hitscan)
        {
            Hitscan(s.Eye, fwd, w, ammo);
        }
        else
        {
            Launch(w, ammo, muzzle, shot * w.Speed + Vector3.UnitY * w.Lift, run && ammo.FollowCamera);
        }

        return result;
    }

    /// <summary>One press of THROW: the selected item leaves the left hand along the view at the
    /// record's speed plus its lift straight up.</summary>
    public FireResult Throw(in Shooter s, float dt = 0f)
    {
        var result = Inventory.TryThrow();
        if (result != FireResult.Fired)
        {
            return result;
        }

        var w = Table.Weapons[Inventory.Thrown];
        if (Table.AmmoOf(w) is not { } ammo)
        {
            return FireResult.NotOwned;
        }

        Fired?.Invoke(Inventory.Thrown, true);

        var (fwd, _, _) = Basis(s.Yaw, s.Pitch);
        var muzzle = MuzzlePoint(s, new Vector3(w.MuzzleX, w.MuzzleY, w.MuzzleZ), dt);
        Sounds?.Play2D(Table.FireSoundOf(w), -5f);
        Shot?.Invoke(Inventory.Thrown, muzzle, fwd);
        Launch(w, ammo, muzzle, fwd * w.Speed + Vector3.UnitY * w.Lift, false);
        return result;
    }

    /// <summary>
    /// Somebody else's shot in a network game, drawn and heard here but hurting nobody (the Godot
    /// port's dm_game.gd _on_fired: damage is the shooter's business): record
    /// <paramref name="record"/> from <paramref name="from"/> along <paramref name="dir"/>, as
    /// <see cref="Shot"/> gave them on the shooter's machine. The fire sound and the muzzle flash; a
    /// bullet's ray to its puff (none on the soldier); a bolt, rocket, shell or thrown item flying as its
    /// own and going off where it lands. <paramref name="shooter"/> (its body) is passed through.
    /// False when the record is not one of the table's.
    /// </summary>
    public bool RemoteFire(int record, Vector3 from, Vector3 dir, IDamageable? shooter = null)
    {
        if (record < 0 || record >= Table.Weapons.Length || Table.AmmoOf(Table.Weapons[record]) is not { } ammo
            || !float.IsFinite(dir.X) || !float.IsFinite(dir.Y) || !float.IsFinite(dir.Z) || dir.LengthSquared() < 1e-6f)
        {
            return false;
        }

        var w = Table.Weapons[record];
        dir = Vector3.Normalize(dir);
        Sounds?.Play3D(Table.FireSoundOf(w), from, -6f);
        if (ammo.IsMelee || record == WeaponTable.Pipe)
        {
            return true;
        }

        if (ammo.Family == AmmoFamily.Hitscan)
        {
            Flash(from + dir * (MuzzleAhead + FlashAtGun.Z + 70f), record == 4 ? 26f : 18f, ammo);
            if (Trace(from, dir, HitscanReach, out var at, out var normal, out _, out var target, shooter, hostile: true) && target != Player)
            {
                AddEffect(ammo.ImpactBank, at + normal, PuffRadius, kind: EffectKind.Impact);
                Puff(at, normal, dir, ammo);
                SpawnPieces(ammo, at + normal, dir, normal);
            }

            return true;
        }

        Flash(from + dir * 70f, 18f, ammo);
        _projectiles.Add(new Projectile
        {
            Weapon = w, Ammo = ammo, Position = from, Velocity = dir * w.Speed + Vector3.UnitY * w.Lift, Source = shooter, Harmless = true,
        });
        return true;
    }

    /// <summary>A shot with the aim bit flies from its muzzle at the point under the crosshair;
    /// with nothing there (or the point behind the muzzle), along the view.</summary>
    private Vector3 ShotDirection(Vector3 eye, Vector3 muzzle, Vector3 fwd, bool aimed)
    {
        if (!aimed || !_world.Raycast(eye, fwd, AimReach, out var hit))
        {
            return fwd;
        }

        var to = hit.Point - muzzle;
        return Vector3.Dot(to, fwd) < 1f ? fwd : Vector3.Normalize(to);
    }

    /// <summary>The nearest thing along a ray: a sphere target, a registered collision owner, or
    /// plain geometry (target null).</summary>
    private bool Trace(Vector3 origin, Vector3 dir, float reach, out Vector3 point, out Vector3 normal, out int owner,
        out IDamageable? target, IDamageable? ignore = null, bool hostile = false)
    {
        (point, normal, owner, target) = (origin + dir * reach, -dir, -1, null);
        var found = false;
        if (_world.Raycast(origin, dir, reach, out var hit))
        {
            (point, normal, owner, reach, found) = (hit.Point, hit.Normal, hit.Owner, hit.Time, true);
            target = owner >= 0 ? Targets.ByOwner(owner) : null;
            if (target == ignore)
            {
                target = null;
            }
        }

        if (Targets.RaySpheres(origin, dir, reach, out var d, ignore) is { } sphere)
        {
            (point, normal, owner, target, found, reach) = (origin + dir * d, -dir, -1, sphere, true, d);
        }

        if (hostile && Player is { Alive: true } player
            && DamageTargets.RaySphere(origin, dir, player.Centre, MathF.Max(player.Radius, PlayerHitRadius), out var pd) && pd <= reach)
        {
            (point, normal, owner, target, found) = (origin + dir * pd, -dir, -1, player, true);
        }

        return found;
    }

    private void Hitscan(Vector3 eye, Vector3 fwd, WeaponRecord w, AmmoRecord ammo)
    {
        if (!Trace(eye, fwd, HitscanReach, out var at, out var normal, out var owner, out var target))
        {
            return;
        }

        if (target != null)
        {
            Strike(target, ammo.Strength, DamageKind.Direct, at, w, owner);
        }
        else
        {
            Report(new ShotReport(w.Index, "wall", at, owner, null, 0f));
        }

        // The puff on a machine too (DOS FUN_0012311f: on anything but the soldier).
        AddEffect(ammo.ImpactBank, at + normal, PuffRadius, kind: EffectKind.Impact);
        Puff(at, normal, fwd, ammo);
        SpawnPieces(ammo, at + normal, fwd, normal);
        if (ammo.Blasts)
        {
            BlastAt(at + normal * 2f, ammo.Strength, target, w.Index);
        }
    }

    /// <summary>The pipe: one ray from just ahead of the eye up to the ammo's reach (120); the blow
    /// sounds (ammo +0x20, PIPEHIT1) wherever it lands, prop or machine.</summary>
    private void Melee(in Shooter s, WeaponRecord w, AmmoRecord ammo, Vector3 fwd)
    {
        var reach = ammo.Reach > 0 ? ammo.Reach : 120;
        if (!Trace(s.Eye + fwd * MeleeStart, fwd, reach, out var at, out _, out var owner, out var target))
        {
            Report(new ShotReport(w.Index, "swing", s.Eye + fwd * (MeleeStart + reach), -1, null, 0f));
            return;
        }

        Sounds?.Play3D(ammo.ImpactSound, at, -3f);
        if (target != null)
        {
            Strike(target, ammo.Strength, DamageKind.Melee, at, w, owner);
        }
        else
        {
            Report(new ShotReport(w.Index, "clang", at, owner, null, 0f));
        }
    }

    /// <summary>
    /// A robot's shot (the Godot port's enemy.gd _shoot): ammo type <paramref name="ammo"/> from
    /// <paramref name="muzzle"/> along <paramref name="dir"/>. A hitscan type strikes at once
    /// (the soldier, a machine, or a puff on the wall); the others fly at <paramref name="speed"/>
    /// as projectiles that pass through <paramref name="source"/> and strike the soldier.
    /// </summary>
    public void EnemyFire(IDamageable source, AmmoRecord ammo, Vector3 muzzle, Vector3 dir, float speed)
    {
        Sounds?.Play3D(ammo.FireSound, muzzle, -7f);
        Flash(muzzle, 30f, ammo);
        if (ammo.Family == AmmoFamily.Hitscan)
        {
            if (!Trace(muzzle, dir, HitscanReach, out var at, out var normal, out var owner, out var target, source, true))
            {
                return;
            }

            if (target != null)
            {
                Strike(target, ammo.Strength, DamageKind.Direct, at, RobotWeapon, owner);
            }
            else
            {
                Report(new ShotReport(-1, "wall", at, owner, null, 0f));
            }

            // The puff on anything but the soldier (DOS FUN_0012311f).
            if (target != Player)
            {
                AddEffect(ammo.ImpactBank, at + normal, PuffRadius, kind: EffectKind.Impact);
                Puff(at, normal, dir, ammo);
            }

            SpawnPieces(ammo, at + normal, dir, normal);
            return;
        }

        _projectiles.Add(new Projectile { Weapon = RobotWeapon, Ammo = ammo, Position = muzzle, Velocity = dir * speed, Source = source });
    }

    private void Launch(WeaponRecord w, AmmoRecord ammo, Vector3 from, Vector3 velocity, bool follow) =>
        _projectiles.Add(new Projectile { Weapon = w, Ammo = ammo, Position = from, Velocity = velocity, Followed = follow });

    private void StepProjectiles(float dt)
    {
        // By index, the count read once: a hit's consequences (a target that fires back, an act
        // that clears the level's shots) may add to or empty the list while it is walked. A shot
        // added here flies from the next tick.
        var count = _projectiles.Count;
        for (var i = 0; i < count && i < _projectiles.Count; i++)
        {
            var p = _projectiles[i];
            if (p.Done)
            {
                continue;
            }

            p.Age += dt;
            if (p.Age >= p.Ammo.Life)
            {
                // A bolt runs out quietly; a lobbed body goes off where it is.
                if (p.Lobbed)
                {
                    Detonate(p, p.Position, Vector3.UnitY, null, -1);
                }

                p.Done = true;
                continue;
            }

            if (p.Lobbed)
            {
                p.Velocity -= new Vector3(0f, Projectile.Gravity * dt, 0f);
            }

            var motion = p.Velocity * dt;
            var length = motion.Length();
            if (length < 1e-6f)
            {
                continue;
            }

            var dir = motion / length;
            if (!Trace(p.Position, dir, length, out var at, out var normal, out var owner, out var target, p.Source ?? Player, p.Source != null || p.Harmless))
            {
                p.Position += motion;
                continue;
            }

            if (target == null && p.Lobbed && p.Ammo.Bounces)
            {
                if (Projectile.Bounce(p.Velocity, normal, out var bounced) == Projectile.BounceOutcome.Bounce)
                {
                    p.Velocity = bounced;
                    p.Position = at + normal * 2f;
                    p.Bounces++;
                    Sounds?.Play3D(BounceSound, p.Position);
                    Report(new ShotReport(p.Weapon.Index, "bounce", at, owner, null, 0f));
                    continue;
                }
            }

            Detonate(p, at, normal, target, owner);
            p.Done = true;
        }

        _projectiles.RemoveAll(p => p.Done);
    }

    /// <summary>A shot's end on something: the direct hit, the blast, the effect and the sound.</summary>
    private void Detonate(Projectile p, Vector3 at, Vector3 normal, IDamageable? target, int owner)
    {
        var ammo = p.Ammo;
        if (p.Harmless)
        {
            // Somebody else's shot (RemoteFire): its bang and its fire, the damage was theirs to report.
        }
        else if (target != null)
        {
            Strike(target, ammo.Strength, DamageKind.Direct, at, p.Weapon, owner);
        }
        else
        {
            Report(new ShotReport(p.Weapon.Index, p.Lobbed ? "burst" : "impact", at, owner, null, 0f));
        }

        if (ammo.Blasts && !p.Harmless)
        {
            BlastAt(at + normal * 2f, ammo.Strength, target, p.Weapon.Index);
        }

        Sounds?.Play3D(ammo.ImpactSound, at, p.Lobbed ? -1f : -2f);
        var radius = p.Lobbed ? LobbedBurstRadius : MathF.Max((ammo.Blasts ? ammo.EffectSize : 0) * 0.6f, 45f);
        var burst = p.Lobbed || ammo.Blasts;
        AddEffect(ammo.ImpactBank, at + normal * 2f, radius, ammo, burst ? EffectKind.Explosion : EffectKind.Impact,
            p.Lobbed ? LobbedCloudScale : ammo.Blasts ? RocketCloudScale : 1f);
        if (!burst && target == null)
        {
            // A laser's or a robot's bolt on a wall: the enhanced look's dust there, as a bullet's.
            Puff(at, normal, p.Velocity.LengthSquared() > 1e-6f ? Vector3.Normalize(p.Velocity) : -normal, ammo);
        }

        if (!p.Lobbed && ammo.Family == AmmoFamily.Bolt && _impacts.Count < ImpactFlash.MaxKept)
        {
            _impacts.Add(new ImpactFlash { At = at + normal * ImpactFlash.Lift, Ammo = ammo, Robot = p.Source != null });
        }

        SpawnPieces(ammo, at + normal * 2f, p.Velocity, normal);
    }

    /// <summary>A bullet's dust where it struck a surface (<see cref="Puffs"/>; the oldest goes
    /// when <see cref="BulletPuff.MaxKept"/> are kept), sized by the shot's strength.</summary>
    private void Puff(Vector3 at, Vector3 normal, Vector3 incoming, AmmoRecord ammo)
    {
        if (_puffs.Count >= BulletPuff.MaxKept)
        {
            _puffs.RemoveAt(0);
        }

        var n = normal.LengthSquared() > 1e-12f ? Vector3.Normalize(normal) : -incoming;
        _puffs.Add(new BulletPuff
        {
            At = at + n * 2f, Normal = n, Incoming = incoming, Metal = MetalWorld, Seed = _puffSeed++,
            Scale = Math.Clamp(MathF.Sqrt(MathF.Max(ammo.Strength, 1f) / 20f), 0.8f, 2.2f),
        });
    }

    /// <summary>The ammo row's pieces (+0x2a count, +0x2e type) where a shot struck: along its
    /// flight bounced in the surface (FUN_0012311f → FUN_0011f761).</summary>
    private void SpawnPieces(AmmoRecord ammo, Vector3 at, Vector3 incoming, Vector3 normal)
    {
        if (ammo.Pieces > 0)
        {
            Pieces.Spawn(ammo.Pieces, ammo.PieceType, at, incoming, normal);
        }
    }

    private void Strike(IDamageable target, float points, DamageKind kind, Vector3 at, WeaponRecord w, int owner)
    {
        var dealt = points * (target == Player ? DamageToPlayer : DamageToEnemies);
        target.TakeDamage(new Damage(dealt, kind, at, true));
        Report(new ShotReport(w.Index, kind == DamageKind.Melee ? "melee" : "hit", at, owner, target, dealt));
    }

    /// <summary>DOS's radial blast at <paramref name="at"/> over every live target (but the one the
    /// shot struck, which took the direct hit) and the soldier, each behind a clear line of sight;
    /// <paramref name="spare"/> leaves out the targets it says (a network game's level blasts).</summary>
    public void BlastAt(Vector3 at, float strength, IDamageable? except = null, int weapon = -1, Func<IDamageable, bool>? spare = null)
    {
        var reach = Blast.Reach(strength);
        // A snapshot: a target the blast kills can leave the list (a crate breaks, a robot's death
        // spawns or retires others) while the blast is still walking it.
        foreach (var t in Targets.All.Append(Player).ToArray())
        {
            if (t == null || t == except || !t.Alive || spare?.Invoke(t) == true)
            {
                continue;
            }

            var d = Vector3.Distance(t.Centre, at);
            if (d > reach)
            {
                continue;
            }

            var points = Blast.Damage(strength, d);
            if (points <= 0f || !LineClear(at, t))
            {
                continue;
            }

            t.TakeDamage(new Damage(points, DamageKind.Blast, at, false));
            Report(new ShotReport(weapon, t == Player ? "blast-self" : "blast", t.Centre, Targets.OwnerOf(t), t, points));
        }
    }

    /// <summary>No level geometry between the blast and the target (its own triangles excepted).</summary>
    private bool LineClear(Vector3 at, IDamageable target)
    {
        var to = target.Centre - at;
        var d = to.Length();
        if (d < 1f || !_world.Raycast(at, to / d, d - 1f, out var hit))
        {
            return true;
        }

        return hit.Owner >= 0 && hit.Owner == Targets.OwnerOf(target);
    }

    private void Flash(Vector3 at, float size, AmmoRecord ammo, EffectKind kind = EffectKind.Flash)
    {
        if (Vector3.Distance(at, _eye) >= 1f)
        {
            _effects.Add(new Effect { Bank = FlashBank, Position = at, Size = size, Life = FlashLife, Ammo = ammo, Kind = kind });
        }
    }

    /// <summary>An effect animation (a robot's death: bank 358) of about <paramref name="radius"/>.</summary>
    public void Explosion(Vector3 at, int bank, float radius) => AddEffect(bank, at, radius, kind: EffectKind.Explosion);

    /// <summary>An animation of an effect bank's record 0, sized as explosion.gd sizes it; a shot's
    /// impact keeps its <paramref name="ammo"/> (the enhanced look's light colours it).</summary>
    private void AddEffect(int bank, Vector3 at, float radius, AmmoRecord? ammo = null, EffectKind kind = EffectKind.Impact,
        float cloudScale = 1f)
    {
        if (bank <= 0)
        {
            return;
        }

        if (kind == EffectKind.Explosion)
        {
            if (_blasts.Count >= BlastCloud.MaxKept)
            {
                _blasts.RemoveAt(0);
            }

            _blasts.Add(new BlastCloud { At = at, Radius = MathF.Max(radius * cloudScale, 40f), Seed = _puffSeed++ });
        }

        var dist = Vector3.Distance(_eye, at);
        if (dist < NearSkip)
        {
            return;
        }

        radius = MathF.Min(MathF.Max(radius, 60f), dist * NearSizeRatio);
        var frames = Math.Max(1, EffectFrames?.Invoke(bank) ?? 8);
        _effects.Add(new Effect { Bank = bank, Position = at, Size = radius * 2f, Life = frames / Effect.Fps, Animated = true, Ammo = ammo, Kind = kind });
    }

    private void Report(in ShotReport r) => Reported?.Invoke(r);

    /// <summary>Drops every shot and effect (a level ends).</summary>
    public void Clear()
    {
        _projectiles.Clear();
        _effects.Clear();
        _impacts.Clear();
        _puffs.Clear();
        _blasts.Clear();
        Pieces.Clear();
    }
}
