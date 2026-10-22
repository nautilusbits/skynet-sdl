using System.Numerics;
using RetroEng.Loop;
using RetroEng.Physics;
using SkyNet.Game.Campaign;
using SkyNet.Game.Navigation;
using SkyNet.Game.Player;
using SkyNet.Game.Vehicles;
using SkyNet.Game.Weapons;

namespace SkyNet.Game.Net;

/// <summary>What a bot does to the game: the server side of the wire (<see cref="DmBots"/> sends
/// it through <see cref="DmHost"/>) or a test's recorder.</summary>
public interface IBotOutput
{
    void Pose(int id, Vector3 feet, float yaw, float pitch, int flags);
    /// <summary>A driver's pose: <paramref name="carYaw"/> is the vehicle's heading (the jeep's view is its turret).</summary>
    internal void Pose(int id, Vector3 feet, float yaw, float pitch, int flags, float carYaw) => Pose(id, feet, yaw, pitch, flags);
    void Fire(int id, int weapon, Vector3 from, Vector3 dir);
    /// <summary>The bot's shot struck <paramref name="victim"/> (reported as a human's hit is).</summary>
    void Hit(int id, int victim, float points, int weapon);
    /// <summary>The bot's own hurt (a fall): a self-hit.</summary>
    void SelfHit(int id, float points, int weapon);
    void Pickup(int id, int key);
}

/// <summary>
/// One deathmatch bot on the server (the Godot port's bot_brain.gd, its constants and its rules):
/// hunts the nearest player it can see (a ray through the level's collision from eye to eye),
/// keeps a weapon-dependent distance while strafing, fires bursts with a skill-dependent spread,
/// roams item spots (health when hurt) and spawns when nobody is in sight, jumps what blocks it at
/// the knee, and grabs the items it walks over. It moves with the soldier's own
/// <see cref="PlayerController"/> (DOS walk/run numbers, gravity, jump, step-up, fall damage and the
/// campaign's collision), fed as if its keys were held: forward and sidestep with RUN, JUMP when
/// blocked; the other bodies block it (<see cref="BodyBlock"/>).
/// <para>Its shots are the DOS weapon's (the owner's rules): the weapon and ammo tables' points as a
/// human player's weapon deals them (no skill scale); a bullet is a ray, the shotgun's (and the super
/// UZI's) also DOS's radial blast where it lands (ammo damage negative: a blast of |damage|), a laser,
/// plasma or rocket a bolt flying at the record's speed for the ammo's life, a launcher's shell a
/// lobbed body that bounces; whatever blasts strikes every body in reach with a clear line, the bot
/// itself too. A weapon item it takes is put in hand.</para>
/// No clock of its own: <see cref="Step"/> moves it on, so tests drive it by hand.
/// </summary>
public sealed class BotBrain
{
    /// <summary>bot_brain.gd SEE_RANGE: nobody farther is a target.</summary>
    public const float SeeRange = 7000f;
    public const float ThinkInterval = 0.2f;
    public const float PoseInterval = 0.05f;
    /// <summary>bot_brain.gd GRAB_RANGE_H / GRAB_RANGE_V + 60 (the DOS 80 / 160 reach, a little
    /// taller for a body standing on a step).</summary>
    public const float GrabAcross = PickupTable.GrabAcross, GrabVertical = PickupTable.GrabVertical + 60f;
    public const float PickupInterval = 0.4f;
    /// <summary>A target out of sight this long is given up.</summary>
    public const float LoseAfter = 3f;
    /// <summary>The shot leaves this far ahead of the eye (bot_brain.gd).</summary>
    public const float MuzzleAhead = 40f;
    /// <summary>The firing flag stays in the pose this long after a shot (as <see cref="DmGame.FiringHold"/>).</summary>
    public const float FiringHold = 0.25f;
    /// <summary>Shots only leave when facing the target within this (radians, 12°).</summary>
    public static readonly float FireCone = 12f * MathF.PI / 180f;
    /// <summary>Facing turns towards the aim at this rate (lerp weight per second).</summary>
    public const float TurnRate = 8f;
    /// <summary>The distance band about the preferred range.</summary>
    public const float RangeBand = 250f;
    public const float KneeHeight = 30f, KneeProbe = 110f;
    public const float GoalReached = 120f;
    public const float StuckAfter = 0.6f, EvadeFor = 0.8f;
    /// <summary>A player's eye above the feet (dm_avatar.gd EYE_HEIGHT; the soldier's too).</summary>
    public const float Eye = PlayerController.EyeHeight;
    /// <summary>A health bar under this share of the class's full bar sends it after health
    /// (bot_brain.gd: 45 of the Godot port's 100).</summary>
    public const float HurtShare = 0.45f;

    /// <summary>The weapon records a bot starts a life with: UZI, ASSAULT RIFLE, MACHINE GUN, SHOTGUN,
    /// LASER RIFLE (bot_brain.gd WEAPON_CHOICES).</summary>
    public static readonly int[] WeaponChoices = [1, 2, 3, 4, 7];

    /// <summary>The preferred engagement distance per weapon record (bot_brain.gd RANGE_FOR).</summary>
    public static float RangeFor(int weapon) => weapon switch
    {
        1 => 700f,
        2 => 1100f,
        3 => 1000f,
        4 => 350f,
        5 => 900f,
        6 => 1500f,
        7 or 8 => 1400f,
        9 => 1000f,
        10 or 11 => 1200f,
        12 => 700f,
        _ => 800f,
    };

    /// <summary>Per skill 0-2: aim spread (degrees), reaction (s), burst pause (s) (bot_brain.gd
    /// SKILL; its damage scale is gone: a bot's hit is the DOS table's, as a human's).</summary>
    public static readonly (float Spread, float Reaction, float BurstPause)[] Skills =
    [
        (9.0f, 0.9f, 1.2f),
        (4.5f, 0.45f, 0.7f),
        (1.8f, 0.15f, 0.35f),
    ];

    /// <summary>A weapon record a bot can carry and fire: the on-foot guns 1-12 (no pipe, no motion
    /// detector, no thrown items).</summary>
    public static bool Carries(int weapon) => weapon is >= 1 and <= WeaponTable.SuperUzi;

    /// <summary>A bot's shot in flight (a bolt or a lobbed shell), stepped as the weapons step theirs.</summary>
    private sealed class Shot(int weapon, AmmoRecord ammo, Vector3 at, Vector3 velocity)
    {
        public readonly int Weapon = weapon;
        public readonly AmmoRecord Ammo = ammo;
        public Vector3 Position = at, Velocity = velocity;
        public float Age;
    }

    private readonly List<Shot> _inFlight = [];

    /// <summary>The keys the bot "holds": RUN and JUMP; the walk comes as the analogue stick.</summary>
    private static readonly PlayerActions Keys = new(Forward: -1, Back: -1, Left: -1, Right: -1, Run: 0, Jump: 1, Crouch: -1);

    private readonly CollisionWorld _world;
    private readonly WeaponTable _table;
    private readonly Random _rng;
    private float _thinkT, _poseT, _pickupT, _firing;
    private float _fireCd, _burstPause;
    private int _burstLeft;
    private float _strafeDir = 1f, _strafeT;
    private float _goalT;
    private float _stuckT, _evadeT, _evadeDir = 1f;
    private float _aimYaw, _aimPitch;

    // The nav path (with a mesh): corners from the bot to _pathTo, _corner the one walked to.
    //   bot ─► c1 ─► c2 ─► goal      repath: goal moved, RepathEvery passed, off the leg, stuck
    /// <summary>A corner this near (horizontally) is passed.</summary>
    private const float CornerReached = 40f;
    /// <summary>A goal moved this far, or the bot this far off its leg, asks for a new path.</summary>
    private const float RepathMoved = 100f, OffPath = 120f;
    private const float RepathEvery = 1.5f;

    private readonly NavQuery? _nav;
    private readonly List<Vector3> _corners = [];
    private Vector3? _pathTo;
    private int _corner;
    private float _repathT;

    /// <param name="nav">The arena's mesh for the soldier; null: straight at the goal (the Godot port's way).</param>
    public BotBrain(int id, int skill, CollisionWorld world, WeaponTable table, Random rng, NavMesh? nav = null)
        : this(id, skill, world, table, rng, nav, null)
    {
    }

    /// <inheritdoc cref="BotBrain(int, int, CollisionWorld, WeaponTable, Random, NavMesh?)"/>
    /// <param name="rides">The jeep's mesh and the HK's ceiling; null: none (straight at the goal, no ceiling).</param>
    internal BotBrain(int id, int skill, CollisionWorld world, WeaponTable table, Random rng, NavMesh? nav, BotRides? rides)
    {
        Id = id;
        Skill = Math.Clamp(skill, 0, Skills.Length - 1);
        _world = world;
        _table = table;
        _rng = rng;
        _nav = nav != null ? new NavQuery(nav) : null;
        Body = new PlayerController(world);
        _ride = new BotRide(Body, world, table, rides ?? BotRides.None, TurretRates[Skill]);
        _ride.Ram = new Ramming(world, _ramTargets);
        PickWeapon();
    }

    public int Id { get; }
    public int Skill { get; }
    public PlayerController Body { get; }
    /// <summary>The weapon record in hand.</summary>
    public int Weapon { get; set; }
    /// <summary>The player hunted, if any.</summary>
    public int? Target { get; private set; }
    /// <summary>Where the target was last seen (feet).</summary>
    public Vector3 LastSeen { get; private set; }
    /// <summary>The target is in sight at the last think.</summary>
    public bool TargetVisible { get; private set; }
    /// <summary>Negative while reacting to a new target; the shots wait for 0.</summary>
    public float TargetSeen { get; private set; }
    /// <summary>The roaming goal, if any.</summary>
    public Vector3? Goal { get; private set; }
    /// <summary>The last tick's horizontal intent (unit or zero).</summary>
    public Vector3 Want { get; private set; }
    /// <summary>JUMP was held on the last tick (blocked at the knee).</summary>
    public bool Jumped { get; private set; }
    public int Shots { get; private set; }
    public int Hits { get; private set; }
    /// <summary>Its bolts and shells still flying.</summary>
    public int InFlight => _inFlight.Count;
    /// <summary>Weapon items taken and put in hand.</summary>
    public int WeaponsTaken { get; private set; }
    /// <summary>Distance walked (horizontal units).</summary>
    public float Walked { get; private set; }
    /// <summary>Times it got stuck and veered.</summary>
    public int Stuck { get; private set; }
    /// <summary>The nav path's corners still ahead (empty: none, or no mesh); driving, the jeep's.</summary>
    public IEnumerable<Vector3> Path => _ride.Riding ? _ride.Path : _corners.Skip(_corner);
    /// <summary>What it drives (<see cref="VehicleMode.Foot"/> on foot).</summary>
    internal VehicleMode Vehicle => _ride.Mode;
    /// <summary>Times it boarded a vehicle; seconds driven; stuck spells, jeep bounces and HK crashes.</summary>
    internal int Boarded { get; private set; }
    internal float RideSeconds => _ride.Seconds;
    internal int RideStuck => _ride.Stuck;
    internal int Bounces => _ride.Bounces;
    internal int Crashes => _ride.Crashes;

    /// <summary>A new life: at the spawn, on foot, a fresh weapon, no target, no goal.</summary>
    public void Spawn(Vector3 feet, float yaw)
    {
        _ride.Leave();
        _rideKey = null;
        _landing = false;
        Body.Spawn(feet, yaw);
        _aimYaw = yaw;
        _aimPitch = 0f;
        Target = null;
        TargetVisible = false;
        Goal = null;
        _burstLeft = 0;
        _burstPause = 0f;
        _fireCd = 0.5f;
        _stuckT = _evadeT = 0f;
        _corners.Clear();
        _pathTo = null;
        PickWeapon();
    }

    private void PickWeapon() => Weapon = WeaponChoices[_rng.Next(WeaponChoices.Length)];

    /// <summary>The server gave it item <paramref name="sprite"/>: a weapon it can carry goes in hand
    /// (as the soldier's pickup selects the weapon it grants).</summary>
    public bool Took(int sprite)
    {
        if (!PickupTable.Items.TryGetValue(sprite, out var item) || !Carries(item.Weapon) || _table.AmmoOf(_table.Weapons[item.Weapon]) == null)
        {
            return false;
        }

        Weapon = item.Weapon;
        WeaponsTaken++;
        return true;
    }

    /// <summary>One tick of <paramref name="dt"/> seconds while alive and the round runs.</summary>
    public void Step(float dt, DmServer server, IBotOutput output)
    {
        _server = server;
        _output = output;
        if (_ride.Riding && server.VehicleOf(Id) == null)
        {
            GetOut(server, Exit.PutOut); // a wreck: the server put it out
        }

        _thinkT -= dt;
        if (_thinkT <= 0f)
        {
            _thinkT = ThinkInterval;
            Think(server, output);
        }

        _decideT -= dt;
        _reboardT -= dt;
        if (_decideT <= 0f)
        {
            _decideT = DecideEvery;
            Decide(server);
        }

        if (_ride.Riding)
        {
            Drive(dt, server);
        }
        else
        {
            Move(dt, server, output);
        }

        AimAndFire(dt, server, output);
        _firing = MathF.Max(0f, _firing - dt);
        _poseT += dt;
        if (_poseT >= PoseInterval)
        {
            _poseT = 0f;
            var flags = new Vector2(Body.Velocity.X, Body.Velocity.Z).Length() > 1f ? DmProtocol.FlagMoving : 0;
            if (_firing > 0f)
            {
                flags |= DmProtocol.FlagFiring;
            }

            output.Pose(Id, Body.Position, Body.Yaw, Body.Pitch, flags, Heading);
        }
    }

    // ---- perception ----

    /// <summary>The nearest living player in sight within <see cref="SeeRange"/>; else the old
    /// target is chased to where it was last seen, and given up after <see cref="LoseAfter"/> s
    /// out of sight or when it dies.</summary>
    public void Think(DmServer server, IBotOutput output)
    {
        int? best = null;
        var bestD = SeeRange;
        var me = Body.Position;
        var eye = EyeOf(me);
        foreach (var p in server.Players.Values)
        {
            if (p.Id == Id || !p.Alive)
            {
                continue;
            }

            var d = Vector3.Distance(p.Position, me);
            if (d < bestD && CanSee(eye, EyeOf(p.Position)))
            {
                best = p.Id;
                bestD = d;
            }
        }

        if (best is { } b)
        {
            // A new face costs the reaction time before the first shot. (bot_brain.gd clamps the
            // same target's clock up to 0 on every think, which cut any reaction to one think.)
            if (b != Target)
            {
                TargetSeen = -Skills[Skill].Reaction;
            }

            Target = b;
            TargetVisible = true;
            LastSeen = server.Players[b].Position;
            _lostT = 0f;
        }
        else if (Target is { } t)
        {
            TargetVisible = false;
            _lostT += ThinkInterval;
            if (_lostT > LoseAfter || !server.IsAlive(t))
            {
                Target = null;
            }
        }

        CheckPickups(server, output);
    }

    private float _lostT;

    public static Vector3 EyeOf(Vector3 feet) => feet + new Vector3(0f, Eye, 0f);

    /// <summary>Nothing of the level between the two points.</summary>
    public bool CanSee(Vector3 from, Vector3 to)
    {
        var d = to - from;
        var len = d.Length();
        return len < 1f || !_world.Raycast(from, d, len - 1f, out _);
    }

    private void CheckPickups(DmServer server, IBotOutput output)
    {
        _pickupT += ThinkInterval;
        if (_pickupT < PickupInterval)
        {
            return;
        }

        _pickupT = 0f;
        var p = Body.Position;
        foreach (var item in server.Pickups.Values)
        {
            var d = item.Position - p;
            if (!item.Taken && new Vector2(d.X, d.Z).Length() <= GrabAcross && MathF.Abs(d.Y) <= GrabVertical)
            {
                output.Pickup(Id, item.Key);
            }
        }
    }

    // ---- movement ----

    private void Move(float dt, DmServer server, IBotOutput output)
    {
        var want = Vector3.Zero;
        var p = Body.Position;
        if (_rideKey != null)
        {
            // Off to a vehicle (Decide drops it when an enemy comes close).
            want = ToVehicle(server, dt);
        }
        else if (Target != null && TargetVisible)
        {
            // In sight: hold the weapon's range, strafing.
            var to = server.Players.TryGetValue(Target.Value, out var tp) ? tp.Position - p : LastSeen - p;
            want = KeepDistance(to, dt);
        }
        else if (Target != null)
        {
            // Out of sight: to where it was last seen.
            var to = LastSeen - p;
            to.Y = 0f;
            if (to.Length() > GoalReached)
            {
                want = Toward(LastSeen, dt);
            }
            else
            {
                Target = null;
            }
        }
        else
        {
            _goalT -= dt;
            if (Goal is not { } g || _goalT <= 0f || new Vector2(g.X - p.X, g.Z - p.Z).Length() < GoalReached)
            {
                PickGoal(server);
            }

            if (Goal is { } goal)
            {
                want = Toward(goal, dt);
            }
        }

        // Parked vehicles (not on the mesh): pushed off the ones about to be walked into.
        if (want.LengthSquared() > 0.01f)
        {
            want = Vector3.Normalize(want) + AwayFromParked(server, p);
        }

        // Obstacles: a knee-height ray ahead; blocked means jump, and a stuck bot veers.
        var jump = false;
        if (want.LengthSquared() > 0.01f)
        {
            want = Vector3.Normalize(want);
            jump = Body.OnFloor && _world.Raycast(p + new Vector3(0f, KneeHeight, 0f), want, KneeProbe, out _);
            if (_evadeT > 0f)
            {
                _evadeT -= dt;
                var side = new Vector3(-want.Z, 0f, want.X);
                want = Vector3.Normalize(want * 0.3f + side * _evadeDir);
            }
        }

        // Facing: the target's eye when in sight, else the way it walks.
        if (Target is { } id && TargetVisible && server.Players.TryGetValue(id, out var tgt))
        {
            var d = EyeOf(tgt.Position) - EyeOf(p);
            _aimYaw = MathF.Atan2(-d.X, -d.Z);
            _aimPitch = MathF.Atan2(d.Y, new Vector2(d.X, d.Z).Length());
        }
        else if (want.LengthSquared() > 0.01f)
        {
            _aimYaw = MathF.Atan2(-want.X, -want.Z);
            _aimPitch = 0f;
        }

        var w = Math.Clamp(dt * TurnRate, 0f, 1f);
        Body.Yaw += WrapAngle(_aimYaw - Body.Yaw) * w;
        Body.Pitch = float.Lerp(Body.Pitch, _aimPitch, w);

        // The soldier's legs: forward / sidestep relative to the facing, RUN held.
        var ahead = new Vector3(-MathF.Sin(Body.Yaw), 0f, -MathF.Cos(Body.Yaw));
        var right = new Vector3(MathF.Cos(Body.Yaw), 0f, -MathF.Sin(Body.Yaw));
        var fwd = Vector3.Dot(want, ahead);
        var str = Vector3.Dot(want, right);
        var down = TickInput.Bit(Keys.Run) | (jump ? TickInput.Bit(Keys.Jump) : 0UL);
        var before = Body.Position;
        Body.Step(new TickInput(down, 0, 0, 0f, 0f, 0f, str, -fwd), dt, Keys);
        Body.Position = BodyBlock.Separate(before, Body.Position, Others(server), _world, Body.Body);
        Want = want;
        Jumped = jump;
        var moved = new Vector2(Body.Position.X - before.X, Body.Position.Z - before.Z).Length();
        Walked += moved;
        if (Body.FallDamage > 0f)
        {
            output.SelfHit(Id, Body.FallDamage, Weapon);
            Body.FallDamage = 0f;
        }

        // Stuck: pushing without progress for a while veers off for a moment.
        if (want.LengthSquared() > 0.01f)
        {
            _stuckT = moved < 0.5f * dt * 60f ? _stuckT + dt : 0f;
            if (_stuckT > StuckAfter)
            {
                _stuckT = 0f;
                _repathT = 0f;
                Stuck++;
                _evadeT = EvadeFor;
                _evadeDir = _rng.NextDouble() < 0.5 ? -1f : 1f;

                // Stuck on the way to a vehicle again and again: it is out of reach.
                if (_rideKey != null && ++_walkStuck >= WalkStuckMax)
                {
                    _rideKey = null;
                    _reboardT = ReboardWait;
                }

                if (Target == null)
                {
                    PickGoal(server);
                }
            }
        }
    }

    /// <summary>A push off every parked vehicle within <see cref="ParkedAvoid"/> of
    /// <paramref name="p"/> (stronger the nearer), the one walked to excepted.</summary>
    private Vector3 AwayFromParked(DmServer server, Vector3 p)
    {
        var push = Vector3.Zero;
        foreach (var v in server.Vehicles.Values)
        {
            if (v.Driver != 0 || v.Wrecked || v.Key == _rideKey)
            {
                continue;
            }

            var d = p - v.Position;
            d.Y = 0f;
            var len = d.Length();
            if (len < 1f || len > ParkedAvoid)
            {
                continue;
            }

            push += d / len * (1f - len / ParkedAvoid) * AvoidPush;
        }

        return push;
    }

    /// <summary>The horizontal way to <paramref name="dest"/>: at the nav path's next corner with a
    /// mesh, else (or past a partial path's end) straight; zero when there.</summary>
    private Vector3 Toward(Vector3 dest, float dt)
    {
        var p = Body.Position;
        var aim = _nav != null ? NextCorner(dest, dt) : dest;
        var to = aim - p;
        to.Y = 0f;
        return to.Length() > 1f ? Vector3.Normalize(to) : Vector3.Zero;
    }

    /// <summary>The corner to walk to, planning again when the goal moved, the bot strayed off the
    /// leg, it got stuck or <see cref="RepathEvery"/> passed.</summary>
    private Vector3 NextCorner(Vector3 dest, float dt)
    {
        var p = Body.Position;
        _repathT -= dt;
        if (_pathTo is not { } old || Flat(old - dest) > RepathMoved || _repathT <= 0f || OffLeg(p))
        {
            _nav!.FindPath(p, dest, _corners);
            _pathTo = dest;
            _corner = 0;
            _repathT = RepathEvery;
        }

        while (_corner < _corners.Count && Flat(_corners[_corner] - p) < CornerReached)
        {
            _corner++;
        }

        return _corner < _corners.Count ? _corners[_corner] : dest;
    }

    /// <summary>Farther than <see cref="OffPath"/> from the leg being walked.</summary>
    private bool OffLeg(Vector3 p)
    {
        if (_corner == 0 || _corner >= _corners.Count)
        {
            return false;
        }

        var a = _corners[_corner - 1];
        var b = _corners[_corner];
        var ab = new Vector2(b.X - a.X, b.Z - a.Z);
        var ap = new Vector2(p.X - a.X, p.Z - a.Z);
        var t = ab.LengthSquared() > 1f ? Math.Clamp(Vector2.Dot(ap, ab) / ab.LengthSquared(), 0f, 1f) : 0f;
        return (ap - ab * t).Length() > OffPath;
    }

    private static float Flat(Vector3 v) => new Vector2(v.X, v.Z).Length();

    /// <summary>Every other living body: its feet and half width (a driver's vehicle's sphere).</summary>
    private IEnumerable<(Vector3, float)> Others(DmServer server)
    {
        foreach (var p in server.Players.Values)
        {
            if (p.Id != Id && p.Alive)
            {
                yield return (p.Position, server.VehicleOf(p.Id) is { } v ? DmAvatar.VehicleRadius(v.Kind) : BodyBlock.Radius);
            }
        }
    }

    /// <summary>The horizontal intent towards <paramref name="to"/> (from the bot to its target):
    /// closer when beyond the weapon's range, back when inside it, sideways within the band,
    /// always with some strafe that flips every 0.8-2.2 s.</summary>
    public Vector3 KeepDistance(Vector3 to, float dt)
    {
        to.Y = 0f;
        var dist = to.Length();
        var fwd = dist > 1f ? to / dist : -Vector3.UnitZ;
        var side = new Vector3(-fwd.Z, 0f, fwd.X);
        var pref = RangeFor(Weapon);
        _strafeT -= dt;
        if (_strafeT <= 0f)
        {
            _strafeT = Range(0.8f, 2.2f);
            _strafeDir = _rng.NextDouble() < 0.5 ? -1f : 1f;
        }

        if (dist > pref + RangeBand)
        {
            return fwd + side * _strafeDir * 0.4f;
        }

        return dist < pref - RangeBand ? -fwd + side * _strafeDir * 0.6f : side * _strafeDir;
    }

    /// <summary>A roaming goal: a health item when hurt, else any item standing (nearer ones
    /// likelier), else a spawn, else a point 1500 away; one of the best three.</summary>
    public void PickGoal(DmServer server)
    {
        _goalT = Range(6f, 12f);
        Goal = null;
        var p = Body.Position;
        var me = server.Players.GetValueOrDefault(Id);
        var hurt = me != null && me.Health < HurtShare * DmProtocol.DosHealth;
        var options = new List<(float Weight, Vector3 Pos)>();
        foreach (var item in server.Pickups.Values)
        {
            if (item.Taken)
            {
                continue;
            }

            var heal = PickupTable.Items.TryGetValue(item.Sprite, out var fx) && fx.HealPercent > 0;
            if (hurt && !heal)
            {
                continue;
            }

            var d = Vector3.Distance(item.Position, p);
            if (d is > 150f and < 9000f)
            {
                options.Add((hurt ? d : d * Range(0.5f, 1.5f), item.Position));
            }
        }

        if (options.Count == 0)
        {
            foreach (var sp in server.Spawns)
            {
                var d = Vector3.Distance(sp.Feet, p);
                if (d > 300f)
                {
                    options.Add((d * Range(0.5f, 1.5f), sp.Feet));
                }
            }
        }

        if (options.Count == 0)
        {
            var a = (float)(_rng.NextDouble() * Math.Tau);
            Goal = p + new Vector3(MathF.Cos(a), 0f, MathF.Sin(a)) * 1500f;
            return;
        }

        options.Sort((x, y) => x.Weight.CompareTo(y.Weight));
        Goal = options[Math.Min(_rng.Next(3), options.Count - 1)].Pos;
    }

    // ---- vehicles ----
    //
    //   on foot ─ Decide (1 s): free whole vehicle in reach, nobody close, health ok, open sky,
    //   │         utility ≥ MinUtility, the skill's chance ─► walk to its side ─► EnterVehicle
    //   driving ─ Drive: jeep path / HK flight to the orbit point, the last seen place or a roam goal
    //   └ Decide: hull low, stuck for long, target where the vehicle cannot go ─► out (an HK lands first)
    //     a wreck: the server puts it out ─► on foot

    /// <summary>How often the ride is weighed (s).</summary>
    private const float DecideEvery = 1f;
    /// <summary>Vehicles farther than this are not walked to.</summary>
    private const float BoardSearch = 2500f;
    /// <summary>An enemy in sight this near keeps it on foot (the fight is now).</summary>
    private const float CloseEnemy = 1200f;
    /// <summary>The utility a vehicle needs: nearness (1 at the bot, 0 at <see cref="BoardSearch"/>)
    /// plus <see cref="HuntBonus"/> when the target is out of sight or far.</summary>
    private const float MinUtility = 0.4f, HuntBonus = 0.3f;
    /// <summary>The bot walks to this far off the vehicle's middle, on its side.</summary>
    private const float BoardSide = 85f;
    /// <summary>Something over the head this near: indoors, no ride.</summary>
    private const float IndoorsAbove = 600f;
    /// <summary>A parked vehicle's roof is looked for this far over its own hull (its collider).</summary>
    private const float RoofClear = 10f;
    /// <summary>Out of the seat below this share of the hull; none boarded below the second.</summary>
    private const float LeaveHull = 0.2f, BoardHull = 0.4f;
    /// <summary>The target cut off from the vehicle this long gets the driver out (s).</summary>
    private const float CutOffFor = 3f;
    /// <summary>After getting out no boarding for this long (s).</summary>
    private const float ReboardWait = 8f;
    /// <summary>An HK that cannot land in this long lets its driver out anyway (s).</summary>
    private const float LandFor = 6f;
    /// <summary>Climbing out: this far to the right of the hull's middle (past its side: the jeep 57, the
    /// HK 50) and up.</summary>
    private const float StepAside = 90f, StepUp = 40f;
    /// <summary>Orbit radius about a target in sight (jeep, HK), its step (rad) and how long a sense lasts.</summary>
    private const float JeepOrbit = 700f, HkOrbit = 1000f, OrbitStep = 0.6f;
    /// <summary>On foot a parked vehicle this near pushes the bot off it this hard (a share of its walk).</summary>
    private const float ParkedAvoid = 220f, AvoidPush = 1.5f;
    /// <summary>A vehicle not reached in this long is given up (s).</summary>
    private const float WalkFor = 20f;
    /// <summary>Stuck this often on the way to a vehicle: given up.</summary>
    private const int WalkStuckMax = 3;
    /// <summary>A parked jeep's and HK's reach about its marker (their hulls' half lengths, 0x51F54 / 0x51FFE).</summary>
    private const float ParkedJeep = 75f, ParkedHk = 115f;
    /// <summary>A target on foot this near is rammed (if the skill's chance says so).</summary>
    private const float RamRange = 900f;
    /// <summary>Rockets (the pod) every 3-6 s at a target within this.</summary>
    private const float RocketRange = 2500f;
    /// <summary>A jeep goal this far off the jeep's mesh is out of its reach.</summary>
    private static readonly Vector3 MeshReach = new(160f, 200f, 160f);

    /// <summary>Per skill 0-2: chance a second to go for a vehicle, chance to ram, turret swing (rad/s).</summary>
    private static readonly float[] RideChance = [0.15f, 0.3f, 0.5f];
    private static readonly float[] RamChance = [0f, 0.4f, 0.8f];
    private static readonly float[] TurretRates = [2f, 3.5f, 5f];

    /// <summary>How the seat was left.</summary>
    private enum Exit
    {
        /// <summary>The bot climbed out (the server is told).</summary>
        Asked,
        /// <summary>The server put it out (a wreck).</summary>
        PutOut,
    }

    /// <summary>Another player as the jeep's ram sees it: its body sphere, live from the server; a
    /// ram's points go out as the bot's hit with the jeep's gun (as a human's ram is reported).</summary>
    private sealed class RamTarget(BotBrain bot, int id) : IDamageable
    {
        public Vector3 Centre => bot._server?.Players.GetValueOrDefault(id) is { } p ? CentreOf(bot._server, p) : default;
        public float Radius => bot._server is { } s && s.IsAlive(id)
            ? s.VehicleOf(id) is { } v ? DmAvatar.VehicleRadius(v.Kind) : DmAvatar.HitRadius
            : 0f;
        public bool Alive => bot._server?.IsAlive(id) ?? false;
        public void TakeDamage(in Damage damage) => bot._output?.Hit(bot.Id, id, damage.Points, VehicleGuns.GunOf(VehicleMode.Jeep));
    }

    private readonly BotRide _ride;
    private readonly DamageTargets _ramTargets = new();
    private readonly Dictionary<int, RamTarget> _ramProxies = [];
    private DmServer? _server;
    private IBotOutput? _output;
    private int? _rideKey;
    private int _walkStuck;
    private bool _landing, _ramming;
    private float _decideT, _reboardT, _landT, _orbitT, _orbitDir = 1f, _rocketT, _cutOffT, _walkT;

    /// <summary>The vehicle's heading on the wire: the jeep's car, the HK's view; on foot the view.</summary>
    private float Heading => _ride.Mode == VehicleMode.Jeep ? _ride.Controller.CarYaw : Body.Yaw;

    /// <summary>Weighs the ride: on foot whether to go for a vehicle, driving whether to get out.</summary>
    private void Decide(DmServer server)
    {
        if (_ride.Riding)
        {
            DecideOut(server);
            return;
        }

        if (_rideKey is { } key && (!server.Vehicles.TryGetValue(key, out var v) || v.Driver != 0 || v.Wrecked))
        {
            _rideKey = null;
        }

        // A vehicle not reached in WalkFor is out of reach (on a roof, behind a fence): given up.
        _walkT -= DecideEvery;
        if (_rideKey != null && _walkT <= 0f)
        {
            _rideKey = null;
            _reboardT = ReboardWait;
        }

        if (_reboardT > 0f || server.Vehicles.Count == 0)
        {
            return;
        }

        var me = server.Players.GetValueOrDefault(Id);
        var close = Target is { } t && TargetVisible && server.Players.TryGetValue(t, out var tp) && Flat(tp.Position - Body.Position) < CloseEnemy;
        if (close || me == null || me.Health < HurtShare * DmProtocol.DosHealth)
        {
            _rideKey = null;
            return;
        }

        if (_rideKey != null || _rng.NextDouble() >= RideChance[Skill] || Indoors(Body.Position))
        {
            return;
        }

        // The best free vehicle by utility: near, more so when the hunt goes far.
        var hunting = Target != null && (!TargetVisible || Flat(LastSeen - Body.Position) > BoardSearch) ? HuntBonus : 0f;
        var best = MinUtility;
        foreach (var c in server.Vehicles.Values)
        {
            var d = Flat(c.Position - Body.Position);
            var worn = c.Hull < BoardHull * Hull.PointsFor(c.Kind);
            if (c.Driver != 0 || c.Wrecked || worn || d > BoardSearch || Roofed(c.Position + new Vector3(0f, 2f * DmAvatar.VehicleRadius(c.Kind) + RoofClear, 0f)))
            {
                continue;
            }

            var utility = 1f - d / BoardSearch + hunting;
            if (utility > best)
            {
                best = utility;
                _rideKey = c.Key;
                _walkT = WalkFor;
                _walkStuck = 0;
            }
        }
    }

    /// <summary>Out when the hull is low, the vehicle stuck for long, or the target out of sight
    /// where the vehicle cannot follow (indoors; for the jeep, off its mesh). An HK lands first.</summary>
    private void DecideOut(DmServer server)
    {
        if (_landing || server.VehicleOf(Id) is not { } v)
        {
            return;
        }

        _ramming = _rng.NextDouble() < RamChance[Skill];
        var low = v.Hull < LeaveHull * Hull.PointsFor(v.Kind);
        // The target out of sight where the vehicle cannot go, for a few decisions running.
        _cutOffT = Target != null && !TargetVisible && !Reaches(v.Kind, LastSeen) ? _cutOffT + DecideEvery : 0f;
        var cutOff = _cutOffT >= CutOffFor;
        if (!low && !_ride.StuckLong && !cutOff)
        {
            return;
        }

        if (v.Kind == VehicleMode.Hk)
        {
            _landing = true;
            _landT = LandFor;
            return;
        }

        GetOut(server, Exit.Asked);
    }

    /// <summary>The vehicle can get to <paramref name="at"/>: not indoors, and for the jeep on its mesh.</summary>
    private bool Reaches(VehicleMode kind, Vector3 at)
    {
        if (Indoors(at))
        {
            return false;
        }

        return kind != VehicleMode.Jeep || _ride.OnMesh(at, MeshReach);
    }

    /// <summary>Something over <paramref name="feet"/> within <see cref="IndoorsAbove"/>.</summary>
    private bool Indoors(Vector3 feet) => Roofed(EyeOf(feet));

    /// <summary>Something over <paramref name="at"/> within <see cref="IndoorsAbove"/>.</summary>
    private bool Roofed(Vector3 at) => _world.Raycast(at, Vector3.UnitY, IndoorsAbove, out _);

    /// <summary>On foot to the chosen vehicle's side; in reach, the seat (the server's rules).</summary>
    private Vector3 ToVehicle(DmServer server, float dt)
    {
        if (_rideKey is not { } key || !server.Vehicles.TryGetValue(key, out var v))
        {
            return Vector3.Zero;
        }

        var p = Body.Position;
        if (Vector3.Distance(p, v.Position) <= DmProtocol.VehicleBoardReach)
        {
            if (server.EnterVehicle(Id, key))
            {
                GetIn(v);
            }

            _rideKey = null;
            return Vector3.Zero;
        }

        var side = new Vector3(MathF.Cos(v.Yaw), 0f, -MathF.Sin(v.Yaw)) * BoardSide;
        var spot = Vector3.DistanceSquared(p, v.Position + side) < Vector3.DistanceSquared(p, v.Position - side) ? v.Position + side : v.Position - side;
        return Toward(spot, dt);
    }

    private void GetIn(DmVehicle v)
    {
        Body.Position = v.Position;
        Body.Velocity = Vector3.Zero;
        _ride.Enter(v.Kind, v.Yaw);
        Boarded++;
        _landing = false;
        _ramming = false;
        _cutOffT = 0f;
        Goal = null;
    }

    /// <summary>Out of the seat beside the vehicle, on foot. DOS steps out 20 across and 40 up
    /// (0x130e36): here inside the parked hull's collider, so the bot steps out past its side
    /// (then the clear-spawn nudge).</summary>
    private void GetOut(DmServer server, Exit how)
    {
        var at = Body.Position;
        var right = new Vector3(MathF.Cos(Heading), 0f, -MathF.Sin(Heading));
        if (how == Exit.Asked)
        {
            server.LeaveVehicle(Id, at, Heading);
        }

        _ride.Leave();
        Body.Position = PlayerStart.Clear(_world, Body.Body, at + right * StepAside + new Vector3(0f, StepUp, 0f));
        Body.Velocity = Vector3.Zero;
        _landing = false;
        _rideKey = null;
        _reboardT = ReboardWait;
        _pathTo = null;
        _corners.Clear();
    }

    /// <summary>One tick in the vehicle: after the target (round it, or rams it), its last seen
    /// place or a roam goal; an HK about to be left lands.</summary>
    private void Drive(float dt, DmServer server)
    {
        SyncRamTargets(server);
        _rocketT -= dt;
        if (_landing)
        {
            _landT -= dt;
            _ride.Drive(dt, null, null, RideStyle.Land);
            if (_ride.Landed || _landT <= 0f)
            {
                GetOut(server, Exit.Asked);
            }

            return;
        }

        Vector3? goal;
        Vector3? aim = null;
        var style = RideStyle.Arrive;
        var p = Body.Position;
        if (Target is { } id && TargetVisible && server.Players.TryGetValue(id, out var tgt))
        {
            aim = EyeOf(tgt.Position);
            var onFoot = server.VehicleOf(id) == null;
            if (_ride.Mode == VehicleMode.Jeep && _ramming && onFoot && Flat(tgt.Position - p) < RamRange)
            {
                goal = tgt.Position;
                style = RideStyle.Ram;
            }
            else
            {
                goal = Orbit(tgt.Position, _ride.Mode == VehicleMode.Jeep ? JeepOrbit : HkOrbit, dt);
            }
        }
        else if (Target != null)
        {
            goal = LastSeen;
            if (Flat(LastSeen - p) < GoalReached * 2f)
            {
                Target = null;
            }
        }
        else
        {
            _goalT -= dt;
            if (Goal == null || _goalT <= 0f || _ride.AtGoal)
            {
                PickGoal(server);
            }

            goal = Goal;
        }

        Obstacles(server, style == RideStyle.Ram ? Target : null);
        _ride.Drive(dt, goal, aim, style);
    }

    /// <summary>The jeep's feelers keep clear of the parked vehicles (their hulls' half lengths) and
    /// of every other body but the one it rams.</summary>
    private void Obstacles(DmServer server, int? rammed)
    {
        var list = _ride.Obstacles;
        list.Clear();
        if (_ride.Mode != VehicleMode.Jeep)
        {
            return;
        }

        foreach (var v in server.Vehicles.Values)
        {
            if (v.Driver == 0 && !v.Wrecked)
            {
                list.Add((v.Position, v.Kind == VehicleMode.Hk ? ParkedHk : ParkedJeep));
            }
        }

        foreach (var p in server.Players.Values)
        {
            if (p.Id != Id && p.Alive && p.Id != rammed)
            {
                list.Add((p.Position, server.VehicleOf(p.Id) is { } v ? DmAvatar.VehicleRadius(v.Kind) : BodyBlock.Radius));
            }
        }
    }

    /// <summary>A point on the circle of <paramref name="radius"/> about <paramref name="centre"/>,
    /// <see cref="OrbitStep"/> round from the bot; the sense flips every 3-6 s.</summary>
    private Vector3 Orbit(Vector3 centre, float radius, float dt)
    {
        _orbitT -= dt;
        if (_orbitT <= 0f)
        {
            _orbitT = Range(3f, 6f);
            _orbitDir = _rng.NextDouble() < 0.5 ? -1f : 1f;
        }

        var from = Body.Position - centre;
        var a = MathF.Atan2(from.Z, from.X) + _orbitDir * OrbitStep;
        return centre + new Vector3(MathF.Cos(a), 0f, MathF.Sin(a)) * radius;
    }

    /// <summary>Every other player is a ram target (proxies kept for the ram's re-arm waits).</summary>
    private void SyncRamTargets(DmServer server)
    {
        foreach (var id in server.Players.Keys)
        {
            if (id != Id && !_ramProxies.ContainsKey(id))
            {
                var t = new RamTarget(this, id);
                _ramProxies[id] = t;
                _ramTargets.Add(t);
            }
        }
    }

    /// <summary>The vehicle's gun to fire: its rockets every 3-6 s at a target in their range, else the gun.</summary>
    private int RideGun(float distance)
    {
        if (_rocketT <= 0f && distance < RocketRange)
        {
            _rocketT = Range(3f, 6f);
            return VehicleGuns.PodOf(_ride.Mode);
        }

        return VehicleGuns.GunOf(_ride.Mode);
    }

    // ---- combat ----

    private void AimAndFire(float dt, DmServer server, IBotOutput output)
    {
        _fireCd -= dt;
        if (Target is not { } id || !TargetVisible || !server.Players.TryGetValue(id, out var tgt) || !tgt.Alive)
        {
            _burstLeft = 0;
            return;
        }

        TargetSeen += dt;
        if (TargetSeen < 0f)
        {
            return; // still reacting
        }

        if (_burstPause > 0f)
        {
            _burstPause -= dt;
            return;
        }

        if (_fireCd > 0f)
        {
            return;
        }

        var eye = Body.EyePosition;
        var d = EyeOf(tgt.Position) - eye;
        if (MathF.Abs(WrapAngle(MathF.Atan2(-d.X, -d.Z) - Body.Yaw)) > FireCone || !CanSee(eye, EyeOf(tgt.Position)))
        {
            return;
        }

        // Driving: the vehicle's gun, now and then its rockets.
        var weapon = _ride.Riding ? RideGun(d.Length()) : Weapon;
        var record = _table.Weapons[weapon];
        if (_ride.Riding && !_ride.Spend(record))
        {
            return; // overheated
        }

        _fireCd = record.Cooldown;
        if (_burstLeft <= 0)
        {
            _burstLeft = 3 + _rng.Next(4);
        }

        _burstLeft--;
        if (_burstLeft <= 0)
        {
            _burstPause = Skills[Skill].BurstPause * Range(0.7f, 1.4f);
        }

        var dir = Spread(Vector3.Normalize(d), Skills[Skill].Spread * MathF.PI / 180f * Range(0.3f, 1f));
        var from = eye + dir * MuzzleAhead;
        Shots++;
        _firing = FiringHold;
        output.Fire(Id, weapon, from, dir);
        if (_table.AmmoOf(record) is not { } ammo)
        {
            return;
        }

        if (ammo.Family != AmmoFamily.Hitscan)
        {
            // A bolt or a shell: it flies (WeaponSystem.FireRecord's launch: the record's speed along
            // the shot plus its lift straight up) and strikes when it gets there.
            _inFlight.Add(new Shot(weapon, ammo, from, dir * record.Speed + Vector3.UnitY * record.Lift));
            return;
        }

        var victim = Strike(server, from, dir, WeaponSystem.HitscanReach, out var t);
        if (victim is { } v)
        {
            Hits++;
            output.Hit(Id, v, ammo.Strength, weapon);
        }

        if (ammo.Blasts && t < WeaponSystem.HitscanReach)
        {
            // The shotgun: DOS's radial blast where the shot landed, over everybody but the one it struck.
            BlastAt(server, output, from + dir * (t - 2f), ammo.Strength, victim, weapon);
        }
    }

    /// <summary>The bot's bolts and shells (WeaponSystem.StepProjectiles' rules): each flies its
    /// ammo's life (a shell under gravity, bouncing as DOS's 0x1233ea bounces); a body it reaches takes
    /// the ammo's points, and a blasting ammo goes off where it struck. Stepped while the bot is dead
    /// too: a shot in the air still counts (the server's dead-shot grace).</summary>
    public void StepShots(float dt, DmServer server, IBotOutput output)
    {
        for (var i = _inFlight.Count - 1; i >= 0; i--)
        {
            var s = _inFlight[i];
            s.Age += dt;
            var lobbed = s.Ammo.Family == AmmoFamily.Gravity;
            if (s.Age >= s.Ammo.Life)
            {
                if (lobbed && s.Ammo.Blasts)
                {
                    BlastAt(server, output, s.Position, s.Ammo.Strength, null, s.Weapon);
                }

                _inFlight.RemoveAt(i);
                continue;
            }

            if (lobbed)
            {
                s.Velocity -= new Vector3(0f, Projectile.Gravity * dt, 0f);
            }

            var motion = s.Velocity * dt;
            var len = motion.Length();
            if (len < 1e-6f)
            {
                continue;
            }

            var dir = motion / len;
            var victim = Strike(server, s.Position, dir, len, out var t);
            if (victim == null && t >= len)
            {
                s.Position += motion;
                continue;
            }

            var at = s.Position + dir * t;
            if (victim == null && lobbed && s.Ammo.Bounces && _world.Raycast(s.Position, dir, len, out var wall)
                && Projectile.Bounce(s.Velocity, wall.Normal, out var bounced) == Projectile.BounceOutcome.Bounce)
            {
                s.Velocity = bounced;
                s.Position = wall.Point + wall.Normal * 2f;
                continue;
            }

            if (victim is { } v)
            {
                Hits++;
                output.Hit(Id, v, s.Ammo.Strength, s.Weapon);
            }

            if (s.Ammo.Blasts)
            {
                BlastAt(server, output, at - dir * 2f, s.Ammo.Strength, victim, s.Weapon);
            }

            _inFlight.RemoveAt(i);
        }
    }

    /// <summary>DOS's radial blast (<see cref="Blast"/>, FUN_00124386) of a bot's shot: every living
    /// body but <paramref name="except"/> (the one the shot struck) within reach and in a clear line
    /// takes the falloff's points; the bot's own is a self-hit, as the soldier's own blast is.</summary>
    private void BlastAt(DmServer server, IBotOutput output, Vector3 at, float strength, int? except, int weapon)
    {
        var reach = Blast.Reach(strength);
        foreach (var p in server.Players.Values.ToList())
        {
            if (!p.Alive || p.Id == except)
            {
                continue;
            }

            var centre = p.Id == Id ? Body.Position + new Vector3(0f, DmAvatar.Height / 2f, 0f) : CentreOf(server, p);
            var d = Vector3.Distance(centre, at);
            var points = d <= reach ? Blast.Damage(strength, d) : 0f;
            if (points <= 0f || !CanSee(at, centre))
            {
                continue;
            }

            if (p.Id == Id)
            {
                output.SelfHit(Id, points, weapon);
            }
            else
            {
                output.Hit(Id, p.Id, points, weapon);
            }
        }
    }

    private static Vector3 CentreOf(DmServer server, DmPlayer p) => server.VehicleOf(p.Id) is { } v
        ? p.Position + new Vector3(0f, DmAvatar.VehicleRadius(v.Kind), 0f)
        : p.Position + new Vector3(0f, DmAvatar.Height / 2f, 0f);

    /// <summary><paramref name="dir"/> turned off by <paramref name="spread"/> radians in a random
    /// direction about itself.</summary>
    private Vector3 Spread(Vector3 dir, float spread)
    {
        var up = MathF.Abs(dir.Y) < 0.99f ? Vector3.UnitY : Vector3.UnitX;
        var right = Vector3.Normalize(Vector3.Cross(dir, up));
        var up2 = Vector3.Cross(right, dir);
        var a = (float)(_rng.NextDouble() * Math.Tau);
        return Vector3.Normalize(dir + right * (MathF.Cos(a) * spread) + up2 * (MathF.Sin(a) * spread));
    }

    /// <summary>The player the hitscan ray strikes first (the body sphere of a <see cref="DmAvatar"/>:
    /// radius 40 about the middle of 88 units, a driver's vehicle's sphere), short of the level; null for a miss.</summary>
    public int? Strike(DmServer server, Vector3 from, Vector3 dir) => Strike(server, from, dir, WeaponSystem.HitscanReach, out _);

    /// <summary>As <see cref="Strike(DmServer, Vector3, Vector3)"/> within <paramref name="reach"/>;
    /// <paramref name="t"/> is where the ray stopped (a body, the level, or the reach).</summary>
    public int? Strike(DmServer server, Vector3 from, Vector3 dir, float reach, out float t)
    {
        var wall = _world.Raycast(from, dir, reach, out var hit) ? hit.Time : reach;
        int? best = null;
        var bestT = wall;
        foreach (var p in server.Players.Values)
        {
            if (p.Id == Id || !p.Alive)
            {
                continue;
            }

            // A driver is struck as the vehicle (step 9), as the avatars are on every machine.
            var r = server.VehicleOf(p.Id) is { } v ? DmAvatar.VehicleRadius(v.Kind) : DmAvatar.HitRadius;
            var c = p.Position + new Vector3(0f, server.VehicleOf(p.Id) != null ? r : DmAvatar.Height / 2f, 0f);
            if (RaySphere(from, dir, c, r) is { } d && d < bestT)
            {
                bestT = d;
                best = p.Id;
            }
        }

        t = bestT;
        return best;
    }

    private static float? RaySphere(Vector3 o, Vector3 d, Vector3 c, float r)
    {
        var oc = o - c;
        var b = Vector3.Dot(oc, d);
        var q = oc.LengthSquared() - r * r;
        var disc = b * b - q;
        if (disc < 0f)
        {
            return null;
        }

        var t = -b - MathF.Sqrt(disc);
        if (t < 0f)
        {
            t = -b + MathF.Sqrt(disc);
        }

        return t >= 0f ? t : null;
    }

    private float Range(float a, float b) => a + (float)_rng.NextDouble() * (b - a);

    private static float WrapAngle(float a)
    {
        a %= MathF.Tau;
        return a > MathF.PI ? a - MathF.Tau : a < -MathF.PI ? a + MathF.Tau : a;
    }
}
