using System.Numerics;
using SkyNet.Game.Vehicles;
using SkyNet.Game.Weapons;

namespace SkyNet.Game.Net;

/// <summary>One clip of the avatar bodies: first and last frame, frames a second, looping.</summary>
public readonly record struct AvatarClip(int First, int Last, float Fps, bool Loops)
{
    public int Frames => Last - First + 1;
}

/// <summary>
/// Another player as this machine sees them (the Godot port's dm_avatar.gd, replicated mode): the
/// body glides towards the last pose its owner sent, walks while the pose says it moves, plays the
/// firing pose on a shot and the death clip on a death, then leaves the arena until the respawn.
/// It is a sphere target of the local weapons: what they strike here is reported to the server
/// (<see cref="Hit"/>), which owns the health.
/// </summary>
public sealed class DmAvatar : IDamageable
{
    /// <summary>The Godot port's capsule: 26 across, 88 tall; struck as a sphere about its middle.</summary>
    public const float Height = 88f;
    public const float HitRadius = 40f;
    /// <summary>How fast the body closes on the reported pose (dm_avatar.gd LERP_RATE), and how far
    /// off it snaps instead (a respawn, a long lag).</summary>
    public const float LerpRate = 14f;
    public const float SnapDistance = 1500f;
    /// <summary>The body lies this long after a death, then leaves the arena.</summary>
    public const float HideAfterDeath = 2.5f;
    /// <summary>A pose this long without movement turns the walk back to the stance.</summary>
    public const float MovingHold = 0.15f;

    /// <summary>The Godot port's first body for everyone; here only when the data lacks the avatars.</summary>
    public const string FallbackBody = "T800RFL.3D";

    /// <summary>The avatar bodies' clips, measured off their vertex data (enemy_anim.gd AVATAR).</summary>
    public static readonly AvatarClip Idle = new(1, 6, 6f, true);
    public static readonly AvatarClip Walk = new(7, 14, 10f, true);
    public static readonly AvatarClip Run = new(15, 24, 14f, true);
    public static readonly AvatarClip Stagger = new(25, 28, 12f, false);
    public static readonly AvatarClip Death = new(29, 34, 10f, false);
    public static readonly AvatarClip Attack = new(35, 41, 12f, false);

    /// <summary>T800RFL's clips (enemy_anim.gd T800_FAMILY), for the fallback body.</summary>
    public static readonly AvatarClip[] T800Clips = [new(1, 1, 10f, true), new(6, 20, 10f, true), new(31, 37, 5f, false), new(46, 60, 10f, false)];

    private Vector3 _target;
    private float _targetYaw;
    private float _targetCarYaw;
    private float _targetPitch;
    private bool _hasTarget;
    private float _clipTime;
    private float _moving;

    public DmAvatar(int id, string name, DmClass cls)
    {
        Id = id;
        Name = name;
        Class = cls;
    }

    public int Id { get; }
    public string Name { get; set; }
    public DmClass Class { get; set; }
    /// <summary>Which of DOS's thirteen avatars the body wears (<see cref="DmAvatarTable"/>, 1..13;
    /// the server's word, one of <see cref="Class"/>).</summary>
    public int Avatar { get; set; } = 1;
    /// <summary>The feet.</summary>
    public Vector3 Position { get; private set; }
    /// <summary>Radians about +Y, 0 looking −Z (the player's convention).</summary>
    public float Yaw { get; private set; }
    public float Pitch { get; private set; }
    public int Flags { get; private set; }
    /// <summary>The vehicle they drive (the pose flags' bits 3-4, the server's word; step 9): the body
    /// is drawn as the vehicle and struck as its hull.</summary>
    public VehicleMode Vehicle => DmProtocol.VehicleOf(Flags);
    /// <summary>The vehicle's heading (the jeep's turret is <see cref="Yaw"/>, the view).</summary>
    public float CarYaw { get; private set; }
    /// <summary>The server's word: alive between a respawn and a death.</summary>
    public bool Alive { get; private set; }
    /// <summary>Seconds since the death.</summary>
    public float DeadFor { get; private set; }
    /// <summary>Drawn: alive, or lying for <see cref="HideAfterDeath"/> after the death.</summary>
    public bool Visible => _hasTarget && (Alive || DeadFor < HideAfterDeath);
    public AvatarClip Clip { get; private set; } = Idle;
    /// <summary>The frame of the clip on show (0 = its first).</summary>
    public int ClipFrame { get; private set; }
    /// <summary>The model frame on show.</summary>
    public int Frame => Clip.First + ClipFrame;
    /// <summary>Called with the points the local weapons dealt (the presenter reports it to the server).</summary>
    public Action<DmAvatar, float>? Hit { get; set; }

    /// <summary>The body: DOS's avatar table (SKYNET.EXE v1.01, file offset 0x84dd0): a headless body
    /// and a head on the same frames, in the avatar's skin, as the host screen's preview shows it.</summary>
    public DmAvatarLook Look => DmAvatarTable.Get(DmAvatarTable.Resolve(Avatar, Class));

    public Vector3 Centre => Position + new Vector3(0f, Vehicle == VehicleMode.Foot ? Height / 2f : VehicleRadius(Vehicle), 0f);
    public float Radius => !Visible || !Alive ? 0f : Vehicle == VehicleMode.Foot ? HitRadius : VehicleRadius(Vehicle);

    /// <summary>A vehicle as a sphere target resting on its position (the campaign's bodies:
    /// <see cref="VehicleController.JeepRadius"/>, <see cref="VehicleController.HkRadius"/>).</summary>
    public static float VehicleRadius(VehicleMode mode) => mode == VehicleMode.Hk ? VehicleController.HkRadius : VehicleController.JeepRadius;

    /// <summary>A pose from the owner (dm_avatar.gd apply_pose); the first one places the body.</summary>
    public void ApplyPose(Vector3 pos, float yaw, float pitch, int flags, float carYaw = float.NaN)
    {
        if (!float.IsFinite(pos.X) || !float.IsFinite(pos.Y) || !float.IsFinite(pos.Z) || !float.IsFinite(yaw) || !float.IsFinite(pitch))
        {
            return;
        }

        if (!float.IsFinite(carYaw))
        {
            carYaw = yaw;
        }

        // Into or out of a vehicle: the heading is the car's from the first pose, no swing.
        var boarded = DmProtocol.VehicleOf(flags) != Vehicle;
        (_target, _targetYaw, _targetPitch, _targetCarYaw, Flags) = (pos, yaw, pitch, carYaw, flags);
        if (!_hasTarget)
        {
            _hasTarget = true;
            (Position, Yaw, Pitch, CarYaw) = (pos, yaw, pitch, carYaw);
        }

        if (boarded)
        {
            CarYaw = carYaw;
        }

        if ((flags & DmProtocol.FlagFiring) != 0 && Alive)
        {
            Play(Attack, restart: Clip != Attack);
        }
    }

    /// <summary>A shot of theirs: the firing pose.</summary>
    public void Fired()
    {
        if (Alive)
        {
            Play(Attack, restart: true);
        }
    }

    /// <summary>The server put them back in the arena (dm_avatar.gd spawn_at).</summary>
    public void SpawnAt(Vector3 feet, float yaw)
    {
        Alive = true;
        DeadFor = 0f;
        _hasTarget = true;
        (Position, _target, Yaw, _targetYaw, Flags, _moving) = (feet, feet, yaw, yaw, 0, 0f);
        (CarYaw, _targetCarYaw) = (yaw, yaw);
        Play(Idle, restart: true);
    }

    /// <summary>In the roster but not in the arena yet, or alive at <paramref name="feet"/> (a joiner's view).</summary>
    public void SetAlive(bool alive, Vector3 feet, float yaw)
    {
        if (alive)
        {
            SpawnAt(feet, yaw);
        }
        else
        {
            Alive = false;
            DeadFor = HideAfterDeath;
        }
    }

    /// <summary>The death clip, then the body goes (dm_avatar.gd die).</summary>
    public bool Die()
    {
        if (!Alive)
        {
            return false;
        }

        Alive = false;
        DeadFor = 0f;
        Play(Death, restart: true);
        return true;
    }

    /// <summary>One frame: the glide to the pose, the walk or stance, the clip's frames.</summary>
    public void Step(float dt)
    {
        if (!Alive)
        {
            DeadFor += dt;
            StepClip(dt);
            return;
        }

        if (_hasTarget)
        {
            var k = Math.Clamp(dt * LerpRate, 0f, 1f);
            var before = Position;
            Position = Vector3.Lerp(Position, _target, k);
            if (Vector3.Distance(Position, _target) > SnapDistance)
            {
                Position = _target;
            }

            Yaw = LerpAngle(Yaw, _targetYaw, k);
            CarYaw = LerpAngle(CarYaw, _targetCarYaw, k);
            Pitch += (_targetPitch - Pitch) * k;
            _moving = (Flags & DmProtocol.FlagMoving) != 0 || Vector3.Distance(Position, before) > 0.5f ? MovingHold : MathF.Max(_moving - dt, 0f);
        }

        if (Clip != Attack && Clip != Death)
        {
            Play(_moving > 0f ? Walk : Idle);
        }

        StepClip(dt);
    }

    public void TakeDamage(in Damage damage)
    {
        if (Alive && damage.Points > 0f)
        {
            Hit?.Invoke(this, damage.Points);
        }
    }

    private void Play(AvatarClip clip, bool restart = false)
    {
        if (Clip == clip && !restart)
        {
            return;
        }

        Clip = clip;
        ClipFrame = 0;
        _clipTime = 0f;
    }

    private void StepClip(float dt)
    {
        if (Clip.Frames <= 1)
        {
            return;
        }

        _clipTime += dt;
        var step = 1f / (Clip.Fps > 0f ? Clip.Fps : 10f);
        while (_clipTime >= step)
        {
            _clipTime -= step;
            if (++ClipFrame < Clip.Frames)
            {
                continue;
            }

            if (Clip == Attack)
            {
                Play(Idle);
                return;
            }

            // The death holds its last frame; the rest loop.
            ClipFrame = Clip == Death ? Clip.Frames - 1 : 0;
        }
    }

    /// <summary>The model frame for the fallback T800RFL body (its own clips, same states).</summary>
    public int T800Frame()
    {
        var c = Clip == Death ? T800Clips[3] : Clip == Attack ? T800Clips[2] : Clip == Walk || Clip == Run ? T800Clips[1] : T800Clips[0];
        return c.First + (c.Frames <= 1 ? 0 : Math.Min(ClipFrame * c.Frames / Math.Max(Clip.Frames, 1), c.Frames - 1));
    }

    private static float LerpAngle(float from, float to, float k)
    {
        var d = (to - from) % MathF.Tau;
        if (d > MathF.PI)
        {
            d -= MathF.Tau;
        }
        else if (d < -MathF.PI)
        {
            d += MathF.Tau;
        }

        return from + d * k;
    }
}
