using System.Numerics;
using RetroEng.Loop;
using RetroEng.Physics;
using SkyNet.Game.Campaign;
using SkyNet.Game.Enemies;
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
    /// <summary>The bot's shot blew up at <paramref name="at"/> (the other bots hear it).</summary>
    internal void Blast(int id, Vector3 at)
    {
    }
}

/// <summary>What a bot is about (Quake III's AI nodes):
/// <code>
///   SeekItem ── enemy in sight ──► Fight ── hurt / outgunned ──► Retreat ── healed, timed out ──► SeekItem
///      ▲  │                         │  ▲                           (fires back while it runs)
///      │  └ heard a shot / hit ──►  ▼  │ seen again
///      └──── searched, gave up ── Chase (last seen / heard place, 5-10 s)
///   SeekItem ── reached its vehicle goal ──► Ride ── wrecked, stuck, hull low, cut off ──► SeekItem
/// </code></summary>
internal enum BotState
{
    /// <summary>To the best item or vehicle by value over travel time; walks when nothing presses.</summary>
    SeekItem,
    /// <summary>An enemy in sight: keeps the weapon's range, strafes, shoots.</summary>
    Fight,
    /// <summary>The enemy lost (or a noise, a hit from nowhere): to where it was last known.</summary>
    Chase,
    /// <summary>Hurt or outgunned: away to health or armour, shooting back.</summary>
    Retreat,
    /// <summary>In a jeep or an HK.</summary>
    Ride,
}

/// <summary>
/// One deathmatch bot on the server (the Godot port's bot_brain.gd, its constants and its rules,
/// with Quake III's nodes, goals and aim as ideas): a <see cref="BotState"/> machine; it acquires
/// players in its view cone (a ray through the level's collision from eye to eye), turns to a hit
/// and to a shot it hears, keeps a weapon-dependent distance while strafing, aims with
/// <see cref="BotAim"/>, fires bursts with a skill- and trait-dependent spread, seeks items and
/// free vehicles by value over travel time, jumps what blocks it at the knee, and grabs the items
/// it walks over. Its character is <see cref="BotTraits"/>. It moves with the soldier's own
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
    /// <summary>A target out of sight is chased at most this long (5-10 s by aggression), then given up.</summary>
    public const float LoseAfter = ChaseMax;
    private const float ChaseMin = 5f, ChaseMax = 10f;
    /// <summary>Think ticks are spread over this many 60 Hz ticks (<see cref="ThinkInterval"/>): bot
    /// n thinks on tick (n × <see cref="PhaseStride"/>) mod it, so a dozen never think on one tick.</summary>
    private const int ThinkTicks = 12, PhaseStride = 5;
    /// <summary>A new face is seen within this half angle of the view (75°), or this near all round;
    /// the target it hunts by line of sight alone; all round for <see cref="HurtWide"/> s after a hit.</summary>
    private static readonly float ViewHalf = 75f * MathF.PI / 180f;
    private const float FeelRange = 300f, HurtWide = 1.5f;
    /// <summary>A gun is heard this far, a blast this far; a noise is looked into for this long (s).</summary>
    private const float HearShot = 2500f, HearBlast = 4000f, InvestigateFor = 6f;
    /// <summary>Stuck this often in a chase: given up.</summary>
    private const int ChaseStuckMax = 3;
    private int _chaseStuck;
    /// <summary>A look turned to (a hit, a noise) counts as done within this (rad).</summary>
    private const float LookedAt = 0.15f;
    /// <summary>The shot leaves this far ahead of the eye (bot_brain.gd).</summary>
    public const float MuzzleAhead = 40f;
    /// <summary>The firing flag stays in the pose this long after a shot (as <see cref="DmGame.FiringHold"/>).</summary>
    public const float FiringHold = 0.25f;
    /// <summary>Shots only leave when facing the target within this (radians, 12°).</summary>
    public static readonly float FireCone = 12f * MathF.PI / 180f;
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

    /// <summary>Per skill 0-2, the aim (<see cref="BotAim"/>): error settle rate (1/s), trail behind a
    /// crossing target (s), the view's top turn (degrees a second).</summary>
    private static readonly (float Settle, float Lag, float TurnDeg)[] AimSkills =
    [
        (1.2f, 0.30f, 200f),
        (2.2f, 0.18f, 320f),
        (3.5f, 0.08f, 480f),
    ];

    /// <summary>Slow shots are led from this aim score on (skill / 4 + aim skill / 2).</summary>
    private const float LeadAt = 0.45f;

    /// <summary>The weapon records from worst to best: a bot never swaps down. (UZI, SHOTGUN,
    /// ASSAULT RIFLE, MACHINE GUN, GRENADE LAUNCHER, LASER RIFLE, small PLASMA, LASER CANNON,
    /// medium PLASMA, ROCKET LAUNCHER, PLASMA CANNON, SUPER UZI.)</summary>
    private static readonly int[] Preference = [1, 4, 2, 3, 5, 7, 9, 8, 10, 6, 11, WeaponTable.SuperUzi];

    /// <summary>Where <paramref name="weapon"/> stands in <see cref="Preference"/> (−1 none); a vehicle's gun tops it.</summary>
    internal static int Rank(int weapon) => weapon >= WeaponTable.FirstVehicle ? Preference.Length : Array.IndexOf(Preference, weapon);

    /// <summary>A weapon record a bot can carry and fire: the on-foot guns 1-12 (no pipe, no motion
    /// detector, no thrown items).</summary>
    public static bool Carries(int weapon) => weapon is >= 1 and <= WeaponTable.SuperUzi;

    /// <summary>A bot's shot in flight (a bolt or a lobbed shell), stepped as the weapons step theirs
    /// (a struct: a shot fired allocates nothing).</summary>
    private struct Shot(int weapon, AmmoRecord ammo, Vector3 at, Vector3 velocity)
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
    private Random _rng;
    private readonly BotTraits _traits;
    private readonly BotAim _aim;
    private readonly BotClaims _claims;
    private readonly int _phase;
    private int _tick;
    private float _poseT, _pickupT, _firing;
    private float _fireCd, _burstPause;
    private int _burstLeft;
    private float _strafeDir = 1f, _strafeT;
    private bool _hop;
    private float _goalT;
    private float _stuckT, _evadeT, _evadeDir = 1f;

    // Alerts: a hit or a noise turns the view to _lookAt once _lookDelay (the reaction) has passed.
    private Vector3? _lookAt;
    private float _lookDelay, _hurtT, _noiseT, _deafT, _chaseFor = ChaseMax, _retreatT, _campT;
    private Vector3 _noiseAt;
    private readonly List<(Vector3 Feet, float Radius)> _others = [];
    private readonly List<DmPlayer> _blastees = [];

    // The nav path (with a mesh): corners from the bot to _pathTo, _corner the one walked to.
    //   bot ─► c1 ─► c2 ─► goal      repath: goal moved, RepathEvery passed, off the leg, stuck
    /// <summary>A corner this near (horizontally) is passed.</summary>
    private const float CornerReached = 40f;
    /// <summary>...and no higher over the feet than this: a spiral stair's next turn lies right over
    /// the last one, and a turn skipped walks the bot off the flight's side (one below is passed:
    /// the bot falls to it, or stands on a prop over it).</summary>
    private const float CornerRise = PlayerController.StepHeight / 2f;
    /// <summary>A landing farther than this from its ledge is jumped to (a walk off falls short);
    /// a jump's rise (DOS 177² / 2·392) must still land unhurt.</summary>
    private const float WalkOffReach = 100f;
    private static readonly float JumpRise = PlayerController.JumpSpeed * PlayerController.JumpSpeed / (2f * PlayerController.Gravity);
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
    /// <param name="traits">Its character; null: <see cref="BotTraits.Average"/>.</param>
    /// <param name="claims">The vehicles the server's bots head for; null: its own (no other bots).</param>
    internal BotBrain(int id, int skill, CollisionWorld world, WeaponTable table, Random rng, NavMesh? nav, BotRides? rides,
        BotTraits? traits = null, BotClaims? claims = null)
    {
        Id = id;
        Skill = Math.Clamp(skill, 0, Skills.Length - 1);
        _world = world;
        _table = table;
        _rng = rng;
        _traits = traits ?? BotTraits.Average;
        _claims = claims ?? new BotClaims();
        _phase = (int)((uint)((DmServer.FirstBotId - id) * PhaseStride) % ThinkTicks);
        var a = AimSkills[Skill];
        var aimSkill = _traits.AimSkill;
        _aim = new BotAim(a.Settle * BotTraits.Lerp(0.6f, 1.4f, aimSkill), a.Lag * BotTraits.Lerp(1.4f, 0.6f, aimSkill),
            a.TurnDeg * BotTraits.Lerp(0.75f, 1.25f, aimSkill) * MathF.PI / 180f,
            Skill / 4f + aimSkill / 2f >= LeadAt ? AimLead.Slow : AimLead.None);
        _nav = nav != null ? new NavQuery(nav) : null;
        Body = new PlayerController(world);
        _ride = new BotRide(Body, world, table, rides ?? BotRides.None, TurretRates[Skill]);
        _ride.Ram = new Ramming(world, _ramTargets);
        PickWeapon();
    }

    public int Id { get; }
    public int Skill { get; }
    /// <summary>Its node now (set at each think).</summary>
    internal BotState State { get; private set; }
    internal BotTraits Traits => _traits;
    internal BotAim Aim => _aim;
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
        DropRide(0f);
        _landing = false;
        Body.Spawn(feet, yaw);
        Target = null;
        TargetVisible = false;
        Goal = null;
        _goalKind = GoalKind.None;
        State = BotState.SeekItem;
        _lookAt = null;
        _hurtT = _noiseT = _campT = _retreatT = 0f;
        _burstLeft = 0;
        _burstPause = 0f;
        _fireCd = 0.5f;
        _stuckT = _evadeT = 0f;
        _corners.Clear();
        _pathTo = null;
        PickWeapon();
    }

    /// <summary>A new stream (the server's next round).</summary>
    internal void Reseed(Random rng) => _rng = rng;

    private void PickWeapon() => Weapon = WeaponChoices[_rng.Next(WeaponChoices.Length)];

    /// <summary>The server gave it item <paramref name="sprite"/>: a weapon it can carry goes in hand
    /// (as the soldier's pickup selects the weapon it grants) unless it is worse than the one held.</summary>
    public bool Took(int sprite)
    {
        if (!PickupTable.Items.TryGetValue(sprite, out var item) || !Carries(item.Weapon) || _table.AmmoOf(_table.Weapons[item.Weapon]) == null)
        {
            return false;
        }

        if (Rank(item.Weapon) < Rank(Weapon))
        {
            return false; // never a swap down: the ammo is all it takes
        }

        Weapon = item.Weapon;
        WeaponsTaken++;
        return true;
    }

    /// <summary>The server gave it <paramref name="item"/> at <paramref name="now"/>: not sought
    /// again until it is back (<see cref="DmProtocol.PickupRespawn"/>).</summary>
    internal void Took(DmPickup item, double now)
    {
        _avoid[item.Key] = now + DmProtocol.PickupRespawn;
        Took(item.Sprite);
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

        // A fixed rotation: this bot's tick of every ThinkTicks (no clock, so a seed replays).
        if ((_tick++ + _phase) % ThinkTicks == 0)
        {
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
            flags |= DmProtocol.WeaponBits(Weapon);
            if (_firing > 0f)
            {
                flags |= DmProtocol.FlagFiring;
            }

            output.Pose(Id, Body.Position, Body.Yaw, Body.Pitch, flags, Heading);
        }
    }

    // ---- perception ----

    /// <summary>The nearest living player in sight within <see cref="SeeRange"/> (a new one in the
    /// view cone); else the old target is chased to where it was last seen, and given up after its
    /// chase time (at most <see cref="LoseAfter"/> s) out of sight or when it dies. Then the node.</summary>
    public void Think(DmServer server, IBotOutput output)
    {
        _server = server;
        _hurtT = MathF.Max(0f, _hurtT - ThinkInterval);
        _deafT = MathF.Max(0f, _deafT - ThinkInterval);
        if (_noiseT > 0f && (_noiseT -= ThinkInterval) <= 0f)
        {
            GiveUp(); // looked into long enough
        }
        Perceive(server);
        CheckPickups(server, output);
        Transition(server);
    }

    private float _lostT;

    private void Perceive(DmServer server)
    {
        int? best = null;
        var bestD = SeeRange;
        var me = Body.Position;
        var eye = EyeOf(me);
        var allRound = _hurtT > 0f;
        foreach (var p in server.AllPlayers)
        {
            if (p.Id == Id || !p.Alive)
            {
                continue;
            }

            var d = Vector3.Distance(p.Position, me);
            if (d >= bestD)
            {
                continue;
            }

            // A new face must be in the view cone (or near, or the bot just hit); the hunted one is watched.
            if (p.Id != Target && !allRound && d > FeelRange && !InView(p.Position))
            {
                continue;
            }

            if (CanSee(eye, EyeOf(p.Position)))
            {
                best = p.Id;
                bestD = d;
            }
        }

        if (best is { } b)
        {
            // A new face costs the reaction time before the first shot and a fresh aim error; one
            // seen again only half the error. (bot_brain.gd clamps the same target's clock up to 0
            // on every think, which cut any reaction to one think.)
            if (b != Target)
            {
                TargetSeen = -ReactionTime;
                _aim.Acquire(_rng, SpreadRad, 1f);
                _chaseFor = BotTraits.Lerp(ChaseMin, ChaseMax, _traits.Aggression);
            }
            else if (!TargetVisible)
            {
                _aim.Acquire(_rng, SpreadRad, ResightShare);
            }

            Target = b;
            TargetVisible = true;
            LastSeen = server.Players[b].Position;
            _lostT = 0f;
            _lookAt = null;
            return;
        }

        if (Target is not { } t)
        {
            return;
        }

        TargetVisible = false;
        _lostT += ThinkInterval;
        if (_lostT > _chaseFor || !server.IsAlive(t))
        {
            Target = null;
        }
    }

    /// <summary>A re-sighted target's aim error: this share of a new one's.</summary>
    private const float ResightShare = 0.5f;

    /// <summary>Within <see cref="ViewHalf"/> of where the bot looks.</summary>
    private bool InView(Vector3 at)
    {
        var d = at - Body.Position;
        return MathF.Abs(WrapAngle(MathF.Atan2(-d.X, -d.Z) - Body.Yaw)) <= ViewHalf;
    }

    /// <summary>Seconds before a new face is shot at (the skill's, by the reaction trait).</summary>
    private float ReactionTime => Skills[Skill].Reaction * BotTraits.Lerp(1.3f, 0.7f, _traits.Reaction);

    /// <summary>A shot's spread (radians; the skill's, by the accuracy trait).</summary>
    private float SpreadRad => Skills[Skill].Spread * BotTraits.Lerp(1.4f, 0.6f, _traits.Accuracy) * MathF.PI / 180f;

    // ---- alerts ----

    /// <summary>A hit from <paramref name="attacker"/>: it looks round for a moment, and an attacker
    /// out of sight (or nearer than the one it fights) becomes the target, chased from where he
    /// stands now; the view turns there after the reaction time.</summary>
    internal void Hurt(int attacker, DmServer server)
    {
        if (attacker == Id || !server.Players.TryGetValue(attacker, out var a))
        {
            return;
        }

        _hurtT = HurtWide;
        if (Target == attacker && TargetVisible)
        {
            return;
        }

        var me = Body.Position;
        if (Target != null && TargetVisible && Flat(a.Position - me) >= Flat(LastSeen - me))
        {
            return; // the nearer threat in sight stays the target
        }

        if (Target != attacker)
        {
            TargetSeen = -ReactionTime;
            _aim.Acquire(_rng, SpreadRad, 1f);
            _chaseFor = BotTraits.Lerp(ChaseMin, ChaseMax, _traits.Aggression);
        }

        Target = attacker;
        TargetVisible = false;
        LastSeen = a.Position;
        _lostT = 0f;
        LookAt(a.Position);
    }

    /// <summary>A gun or a blast at <paramref name="at"/> (<paramref name="source"/> its player):
    /// heard within its radius when nobody is in sight; the lost target heard again is placed, else
    /// the noise is looked into.</summary>
    internal void Hear(int source, Vector3 at, NoiseKind kind)
    {
        if (source == Id || TargetVisible)
        {
            return;
        }

        var radius = kind == NoiseKind.Blast ? HearBlast : HearShot;
        if (Vector3.DistanceSquared(at, Body.Position) > radius * radius)
        {
            return;
        }

        if (Target == source)
        {
            LastSeen = at;
            _lostT = 0f;
            return;
        }

        // One noise at a time, and a rest after a search that found nobody (a battle elsewhere
        // does not pull it round the map).
        if (Target != null || _ride.Riding || _noiseT > 0f || _deafT > 0f)
        {
            return;
        }

        _noiseAt = at;
        _noiseT = InvestigateFor;
        LookAt(at);
    }

    /// <summary>The view turns to <paramref name="at"/> once the reaction time has passed.</summary>
    private void LookAt(Vector3 at)
    {
        _lookAt = at;
        _lookDelay = ReactionTime;
    }

    // ---- nodes ----

    /// <summary>The node for now (the class's <see cref="BotState"/> drawing).</summary>
    private void Transition(DmServer server)
    {
        if (_ride.Riding)
        {
            State = BotState.Ride;
            return;
        }

        var share = server.Players.TryGetValue(Id, out var me) ? me.Health / DmProtocol.DosHealth : 1f;
        if (State == BotState.Retreat)
        {
            _retreatT += ThinkInterval;
            if (share < RecoverShare && _retreatT < RetreatMax)
            {
                return;
            }

            Goal = null; // healed, or long enough
        }

        if (Target is { } t && TargetVisible)
        {
            var enemy = server.Players.GetValueOrDefault(t);
            if (enemy != null && Outmatched(share, enemy))
            {
                State = BotState.Retreat;
                _retreatT = 0f;
                PickRetreat(server, enemy.Position);
                return;
            }

            State = BotState.Fight;
            return;
        }

        if (Target != null || _noiseT > 0f)
        {
            State = BotState.Chase;
            return;
        }

        if (State != BotState.SeekItem)
        {
            State = BotState.SeekItem;
            Goal = null;
        }

        // Seeking: weighed again every so often (the goal in hand counts a little more).
        _goalT -= ThinkInterval;
        if (_goalT <= 0f && _rideKey == null)
        {
            PickGoal(server);
        }
    }

    /// <summary>Hurt below the caution's share, or outgunned (its weapon well under the enemy's,
    /// a vehicle's gun against it on foot) and not well.</summary>
    private bool Outmatched(float share, DmPlayer enemy)
    {
        if (share < BotTraits.Lerp(RetreatLow, RetreatHigh, _traits.Caution))
        {
            return true;
        }

        var weak = Rank(Weapon) + WeakGap <= Rank(enemy.Weapon);
        return weak && share < BotTraits.Lerp(WeakLow, WeakHigh, _traits.Caution);
    }

    /// <summary>Retreat below a health share of RetreatLow (reckless) to RetreatHigh (careful);
    /// outgunned by WeakGap ranks below WeakLow to WeakHigh; back once over RecoverShare or after
    /// RetreatMax s.</summary>
    private const float RetreatLow = 0.15f, RetreatHigh = 0.45f, WeakLow = 0.3f, WeakHigh = 0.9f, RetreatMax = 12f;
    private const int WeakGap = 4;
    private float RecoverShare => BotTraits.Lerp(0.5f, 0.8f, _traits.Caution);

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
        foreach (var item in server.AllPickups)
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
        var p = Body.Position;
        var want = State switch
        {
            BotState.Fight => FightMove(server, dt),
            BotState.Chase => ChaseMove(dt),
            BotState.Retreat => RetreatMove(server, dt),
            _ => SeekMove(server, dt),
        };

        // Parked vehicles (not on the mesh): pushed off the ones about to be walked into.
        if (want.LengthSquared() > 0.01f)
        {
            want = Vector3.Normalize(want) + AwayFromParked(server, p);
        }

        // Obstacles: a knee-height ray ahead; blocked means jump, and a stuck bot veers. A fighter
        // hops now and then (its jump trait).
        var jump = false;
        if (want.LengthSquared() > 0.01f)
        {
            want = Vector3.Normalize(want);
            var knee = _world.Raycast(p + new Vector3(0f, KneeHeight, 0f), want, KneeProbe, out _);
            jump = Body.OnFloor && ((knee && !Climbing()) || JumpOff());
            if (_evadeT > 0f)
            {
                _evadeT -= dt;
                var side = new Vector3(-want.Z, 0f, want.X);
                want = Vector3.Normalize(want * 0.3f + side * _evadeDir);
            }
        }

        if (_hop && State == BotState.Fight && Body.OnFloor)
        {
            jump = true;
            _hop = false;
        }

        Face(server, want, dt);

        // The soldier's legs: forward / sidestep relative to the facing; RUN held unless it strolls.
        var ahead = new Vector3(-MathF.Sin(Body.Yaw), 0f, -MathF.Cos(Body.Yaw));
        var right = new Vector3(MathF.Cos(Body.Yaw), 0f, -MathF.Sin(Body.Yaw));
        var fwd = Vector3.Dot(want, ahead);
        var str = Vector3.Dot(want, right);
        var run = Hurried ? TickInput.Bit(Keys.Run) : 0UL;
        var down = run | (jump ? TickInput.Bit(Keys.Jump) : 0UL);
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
                _evadeDir = FreerSide(p, want);

                // Stuck on the way to a vehicle again and again: it is out of reach.
                if (_rideKey != null && ++_walkStuck >= WalkStuckMax)
                {
                    GiveUpRide();
                }

                // Stuck again and again on the way to an item (a prop the mesh does not know, a
                // leg it cannot climb): out of reach for a while, longer each time.
                if (_goalKind == GoalKind.Item && ++_goalStuck >= GoalStuckMax)
                {
                    var strikes = _strikes[_goalKey] = _strikes.GetValueOrDefault(_goalKey) + 1;
                    Avoid(AvoidStuck * MathF.Min(strikes, MaxStrikes));
                    Goal = null;
                }

                if (State == BotState.SeekItem && _rideKey == null)
                {
                    PickGoal(server);
                }

                if (State == BotState.Retreat && Goal == null)
                {
                    PickRetreat(server, LastSeen);
                }

                // A chase that sticks is given up (the last seen place lies past a wall).
                if (State == BotState.Chase && ++_chaseStuck >= ChaseStuckMax)
                {
                    GiveUp();
                }
            }
        }
    }

    /// <summary>The way to veer when stuck walking <paramref name="want"/>: the side with more room
    /// at the knee (a pole or a fence end the mesh hugs), a coin toss when both are as open.</summary>
    private float FreerSide(Vector3 p, Vector3 want)
    {
        var knee = p + new Vector3(0f, KneeHeight, 0f);
        float Room(float side)
        {
            var dir = Vector3.Normalize(want * 0.5f + new Vector3(-want.Z, 0f, want.X) * side);
            return _world.Raycast(knee, dir, SideProbe, out var hit) ? hit.Time : SideProbe;
        }

        var left = Room(1f);
        var right = Room(-1f);
        if (MathF.Abs(left - right) < 1f)
        {
            return _rng.NextDouble() < 0.5 ? -1f : 1f;
        }

        return left > right ? 1f : -1f;
    }

    /// <summary>How far the stuck bot looks to each side for room.</summary>
    private const float SideProbe = 200f;

    /// <summary>A push off every parked vehicle within <see cref="ParkedAvoid"/> of
    /// <paramref name="p"/> (stronger the nearer), the one walked to excepted.</summary>
    private Vector3 AwayFromParked(DmServer server, Vector3 p)
    {
        var push = Vector3.Zero;
        foreach (var v in server.AllVehicles)
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
        // Not in the air: a drop is followed to its landing before a new plan.
        var plan = _pathTo is not { } old || Flat(old - dest) > RepathMoved || _repathT <= 0f || OffLeg(p);
        if (plan && (Body.OnFloor || _pathTo == null))
        {
            _nav!.FindPath(p, dest, _corners, _blocked, WalkNodes);
            _pathTo = dest;
            _corner = 0;
            _repathT = RepathEvery;
        }

        while (_corner < _corners.Count && Flat(_corners[_corner] - p) < CornerReached && !Overhead(p))
        {
            _corner++;
        }

        return _corner < _corners.Count ? _corners[_corner] : dest;
    }

    /// <summary>The next corner lies over <paramref name="p"/> by more than <see cref="CornerRise"/>
    /// while the last one does not: the bot is on the flight, a turn below the next. Passed: the
    /// path's start (snapped maybe onto a flight over the bot), a climb link's top (walking on
    /// steps the body up onto it), a corner the bot is under (off the flight: on its way back).</summary>
    private bool Overhead(Vector3 p)
    {
        if (_corner == 0 || _corners[_corner].Y - p.Y < CornerRise)
        {
            return false;
        }

        var last = _corners[_corner - 1];
        return last.Y - p.Y < CornerRise && !_nav!.IsLink(last, _corners[_corner]);
    }

    /// <summary>On a climb link's leg (up onto a ledge within the step-up): walked, never jumped,
    /// for in the air the body does not step up.</summary>
    private bool Climbing()
    {
        if (_nav == null || _corner < 1 || _corner >= _corners.Count)
        {
            return false;
        }

        var from = _corners[_corner - 1];
        var to = _corners[_corner];
        return to.Y - from.Y > KneeHeight && _nav.IsLink(from, to);
    }
    /// <summary>Just past a ledge (a drop link's start: the leg falls more than a step), headed for
    /// a landing farther than a walk off carries the body: time to jump.</summary>
    private bool JumpOff()
    {
        if (_corner < 1 || _corner >= _corners.Count)
        {
            return false;
        }

        var edge = _corners[_corner - 1];
        var land = _corners[_corner];
        if (edge.Y - land.Y <= PlayerController.StepHeight)
        {
            return false; // no drop: walked
        }

        var high = edge.Y - land.Y + JumpRise <= NavAgents.SafeDrop;
        return high && Flat(land - edge) > WalkOffReach && Flat(edge - Body.Position) < CornerReached * 1.5f;
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
    private List<(Vector3 Feet, float Radius)> Others(DmServer server)
    {
        _others.Clear();
        foreach (var p in server.AllPlayers)
        {
            if (p.Id != Id && p.Alive)
            {
                _others.Add((p.Position, server.VehicleOf(p.Id) is { } v ? DmAvatar.VehicleRadius(v.Kind) : BodyBlock.Radius));
            }
        }

        return _others;
    }

    // ---- the nodes' legs ----

    /// <summary>It runs unless it only strolls to a wander goal (or waits at a reached one).</summary>
    private bool Hurried => State != BotState.SeekItem || _goalKind is GoalKind.Item or GoalKind.Vehicle;

    /// <summary>Fight: the weapon's range, strafing; still off to its vehicle unless the enemy is close and nearer.</summary>
    private Vector3 FightMove(DmServer server, float dt)
    {
        var p = Body.Position;
        var to = Target is { } t && server.Players.TryGetValue(t, out var tp) ? tp.Position - p : LastSeen - p;
        if (_rideKey is { } key && server.Vehicles.TryGetValue(key, out var v) && !EnemyFirst(server, v.Position))
        {
            return ToVehicle(server, dt);
        }

        return KeepDistance(to, dt);
    }

    /// <summary>Chase: to where the target was last seen (or the noise was); there, given up.</summary>
    private Vector3 ChaseMove(float dt)
    {
        // A vehicle on the way is taken first: the chase goes on from its seat.
        if (_rideKey != null && _server != null)
        {
            return ToVehicle(_server, dt);
        }

        var at = Target != null ? LastSeen : _noiseAt;
        if (Flat(at - Body.Position) > GoalReached)
        {
            return Toward(at, dt);
        }

        GiveUp();
        return Vector3.Zero;
    }

    /// <summary>The search found nobody: back to seeking, deaf to noises for a while.</summary>
    private void GiveUp()
    {
        Target = null;
        _noiseT = 0f;
        _deafT = InvestigateFor;
        _chaseStuck = 0;
    }

    /// <summary>Retreat: to the health or armour picked (or away), a new pick once there.</summary>
    private Vector3 RetreatMove(DmServer server, float dt)
    {
        if (_rideKey != null)
        {
            return ToVehicle(server, dt);
        }

        if (Goal is not { } g || Flat(g - Body.Position) < GoalReached)
        {
            PickRetreat(server, LastSeen);
        }

        return Goal is { } goal ? Toward(goal, dt) : Vector3.Zero;
    }

    /// <summary>SeekItem: to its vehicle, or to its goal (a new one when reached or overdue; a
    /// camper waits a moment at a reached one).</summary>
    private Vector3 SeekMove(DmServer server, float dt)
    {
        if (_rideKey != null)
        {
            return ToVehicle(server, dt);
        }

        if (_campT > 0f)
        {
            _campT -= dt;
            return Vector3.Zero;
        }

        var p = Body.Position;
        if (Goal is { } g && Flat(g - p) < GoalReached)
        {
            Reached();
            if (_campT > 0f)
            {
                return Vector3.Zero;
            }
        }

        if (Goal == null || server.Now > _goalDue)
        {
            if (Goal != null)
            {
                Avoid(AvoidUnreached); // overdue: out of reach for now
            }

            PickGoal(server);
        }

        return Goal is { } goal ? Toward(goal, dt) : Vector3.Zero;
    }

    /// <summary>A goal reached: not sought again for a while (an item not grabbed is out of
    /// reach); a camper waits there up to <see cref="CampMax"/> s.</summary>
    private void Reached()
    {
        Avoid(AvoidReached);
        Goal = null;
        _campT = _rng.NextDouble() < _traits.Camp ? Range(0.5f, CampMax) * _traits.Camp : 0f;
    }

    private void Avoid(float seconds)
    {
        if (_goalKind == GoalKind.Item && _server != null)
        {
            _avoid[_goalKey] = Math.Max(_avoid.GetValueOrDefault(_goalKey), _server.Now + seconds);
        }
    }

    /// <summary>The view: on the target in sight (<see cref="BotAim"/>), else to a hit's or noise's
    /// place once the reaction passed, else the way it walks; turned at the aim's rate.</summary>
    private void Face(DmServer server, Vector3 want, float dt)
    {
        float yaw = Body.Yaw, pitch = 0f;
        if (Target is { } id && TargetVisible && server.Players.TryGetValue(id, out var tgt))
        {
            var record = _table.Weapons[Weapon];
            var ammo = _table.AmmoOf(record);
            var speed = ammo == null || ammo.Family == AmmoFamily.Hitscan ? 0f : record.Speed;
            var drop = ammo?.Family == AmmoFamily.Gravity ? record.Lift : 0f;
            (yaw, pitch) = _aim.Track(Body.EyePosition, EyeOf(tgt.Position), speed, drop, dt);
        }
        else if (_lookAt is { } look && (_lookDelay -= dt) <= 0f)
        {
            var d = look - Body.Position;
            yaw = MathF.Atan2(-d.X, -d.Z);
            if (MathF.Abs(WrapAngle(yaw - Body.Yaw)) < LookedAt)
            {
                _lookAt = null; // looked: now the way it walks
            }
        }
        else if (_lookAt == null && want.LengthSquared() > 0.01f)
        {
            yaw = MathF.Atan2(-want.X, -want.Z);
        }

        var step = _aim.TurnRate * dt;
        Body.Yaw = WrapAngle(BotAim.Toward(Body.Yaw, yaw, step));
        Body.Pitch = Math.Clamp(Body.Pitch + Math.Clamp(pitch - Body.Pitch, -step, step), -PlayerController.PitchMax, PlayerController.PitchMax);
    }

    /// <summary>The horizontal intent towards <paramref name="to"/> (from the bot to its target):
    /// closer when beyond the weapon's range, back when inside it, sideways within the band,
    /// always with some strafe (harder with the strafe trait) that flips every 0.4-0.6 s; an
    /// aggressive bot holds a shorter range.</summary>
    public Vector3 KeepDistance(Vector3 to, float dt)
    {
        to.Y = 0f;
        var dist = to.Length();
        var fwd = dist > 1f ? to / dist : -Vector3.UnitZ;
        var side = new Vector3(-fwd.Z, 0f, fwd.X);
        var pref = RangeFor(Weapon) * BotTraits.Lerp(1.2f, 0.8f, _traits.Aggression);
        var strafe = BotTraits.Lerp(0.5f, 1.5f, _traits.Strafe);
        _strafeT -= dt;
        if (_strafeT <= 0f)
        {
            _strafeT = Range(StrafeMin, StrafeMax);
            _strafeDir = _rng.NextDouble() < 0.5 ? -1f : 1f;
            _hop = _rng.NextDouble() < _traits.Jump * HopShare;
        }

        if (dist > pref + RangeBand)
        {
            return fwd + side * _strafeDir * 0.4f * strafe;
        }

        return dist < pref - RangeBand ? -fwd + side * _strafeDir * 0.6f * strafe : side * _strafeDir;
    }

    /// <summary>The strafe's flip time (s); the share of flips a full jumper hops on.</summary>
    private const float StrafeMin = 0.4f, StrafeMax = 0.6f, HopShare = 0.3f;

    // ---- goals (Quake III's goal weights over travel time, ideas only) ----
    //
    //   value: health ∝ the bar missing, armour ∝ the armour missing, a better weapon ∝ the ranks
    //          gained, ammo a little, a free vehicle (more when enemies are far, the arena large),
    //          a spawn to wander to the least
    //   score = value / (travel time + TimeBias); the goal in hand × KeepGoal
    //   cheapest first: the straight-line hope bounds the nav path's score, at most MaxWeighed paths

    /// <summary>What a roaming goal is.</summary>
    private enum GoalKind
    {
        None,
        Item,
        Vehicle,
        Wander,
    }

    /// <summary>What a goal pick is for.</summary>
    private enum GoalUse
    {
        /// <summary>Anything of value.</summary>
        Roam,
        /// <summary>Health and armour away from the enemy, else a spawn far from it.</summary>
        Retreat,
    }

    private struct Option
    {
        public GoalKind Kind;
        public int Key;
        public Vector3 At;
        public float Value, Hope, Time;
        public bool Weighed;
    }

    private const float HealthWeight = 3f, ArmourWeight = 2f, WeaponWeight = 2.5f, AmmoValue = 0.3f, VehicleWeight = 3f, WanderValue = 0.1f;
    /// <summary>Health and armour count by the bar they fill: a quarter of it is worth the weight.</summary>
    private const float FillScale = 4f;
    /// <summary>Seconds added to every travel time (a goal at the feet is not infinitely good).</summary>
    private const float TimeBias = 1f;
    /// <summary>The goal in hand counts this much more (no flip-flop between two near equals).</summary>
    private const float KeepGoal = 1.3f;
    /// <summary>Nav paths weighed a pick at most; the goal is weighed again every RethinkEvery s.</summary>
    private const int MaxWeighed = 6;
    private const float RethinkEvery = 2f;
    /// <summary>A vehicle's worth grows by FarBonus with the enemies far (FarEnemy) and by OpenBonus
    /// on an arena LargeArena across.</summary>
    private const float FarBonus = 1f, FarEnemy = 4000f, OpenBonus = 0.5f, LargeArena = 12000f;
    /// <summary>An HK is worth this much more than a jeep (it flies over the fight, rockets ready).</summary>
    private const float HkBonus = 1.25f;
    /// <summary>Items nearer than this are being walked over; farther than ItemFar not looked at.</summary>
    private const float ItemNear = 150f, ItemFar = 9000f, SpawnNear = 300f;
    /// <summary>Overdue goals are out of reach this long (s); reached ones are not sought for this long; campers wait up to CampMax.</summary>
    private const float AvoidUnreached = 15f, AvoidReached = 4f, CampMax = 3f;
    /// <summary>Stuck this often on the way to an item: avoided AvoidStuck s × its strikes (at most MaxStrikes).</summary>
    private const int GoalStuckMax = 2, MaxStrikes = 4;
    private const float AvoidStuck = 30f;
    private int _goalStuck;
    private readonly Dictionary<int, int> _strikes = [];
    /// <summary>A goal is overdue after twice its travel time and this.</summary>
    private const float GoalSlack = 4f;
    /// <summary>A partial path ending this far from its goal: unreachable.</summary>
    private const float PartialSlack = 150f;

    private readonly List<Option> _options = [];
    private readonly List<Vector3> _probe = [];
    private readonly Dictionary<int, double> _avoid = [];
    private readonly Dictionary<int, double> _avoidRide = [];
    private GoalKind _goalKind;
    private int _goalKey;
    private double _goalDue;
    private float _arenaOpen = -1f;

    /// <summary>A roaming goal: the best of the items standing, the free vehicles and the spawns by
    /// value over travel time; with none, a point 1500 away.</summary>
    public void PickGoal(DmServer server) => Pick(server, GoalUse.Roam, Body.Position);

    /// <summary>A retreat goal: health or armour not towards <paramref name="enemy"/>, else the spawn farthest from it.</summary>
    private void PickRetreat(DmServer server, Vector3 enemy) => Pick(server, GoalUse.Retreat, enemy);

    private void Pick(DmServer server, GoalUse use, Vector3 enemy)
    {
        // The tick's picks are spent (a round's start: every bot at once): this one waits a tick.
        if (!_claims.TakePick())
        {
            return;
        }

        _server = server;
        _goalT = RethinkEvery;
        _pickNodes = 0;
        _fromReached = _nav != null && _claims.Reach is { } reach && reach.Contains(_nav.EndPoly(Body.Position));
        Picks++;
        var p = Body.Position;
        var me = server.Players.GetValueOrDefault(Id);
        _options.Clear();
        foreach (var item in server.AllPickups)
        {
            var d = Flat(item.Position - p);
            if (item.Taken || d < ItemNear || d > ItemFar || _avoid.GetValueOrDefault(item.Key) > server.Now)
            {
                continue;
            }

            var value = ItemValue(item.Sprite, me, use);
            if (use == GoalUse.Retreat && Flat(item.Position - enemy) < Flat(p - enemy))
            {
                value *= TowardsEnemy;
            }

            Offer(GoalKind.Item, item.Key, item.Position, value);
        }

        // A seat is also the way out of a losing fight on foot (the hull takes the hits).
        if (!_ride.Riding && _reboardT <= 0f)
        {
            OfferVehicles(server, use, enemy);
        }

        for (var i = 0; i < server.Spawns.Count; i++)
        {
            var sp = server.Spawns[i].Feet;
            if (Flat(sp - p) > SpawnNear)
            {
                var far = use == GoalUse.Retreat ? Flat(sp - enemy) / FarEnemy : Range(0.5f, 1.5f);
                Offer(GoalKind.Wander, i, sp, WanderValue * far);
            }
        }

        if (Best() is not { } best)
        {
            var a = (float)(_rng.NextDouble() * Math.Tau);
            SetGoal(GoalKind.Wander, -1, p + new Vector3(MathF.Cos(a), 0f, MathF.Sin(a)) * 1500f, 1500f / PlayerController.RunSpeed);
            return;
        }

        var o = _options[best];
        SetGoal(o.Kind, o.Key, o.At, o.Weighed ? o.Time : Flat(o.At - p) / PlayerController.RunSpeed);
    }

    /// <summary>An item retreated to that lies towards the enemy is worth this share.</summary>
    private const float TowardsEnemy = 0.3f;

    /// <summary>Every free whole vehicle under open sky that no other bot heads for and this one has
    /// not given up on.</summary>
    private void OfferVehicles(DmServer server, GoalUse use, Vector3 enemy)
    {
        var like = BotTraits.Lerp(0.4f, 1.6f, _traits.VehicleLike);
        var far = Math.Clamp((NearestEnemy(server) - CloseEnemy) / (FarEnemy - CloseEnemy), 0f, 1f);
        var worth = VehicleWeight * like * (1f + FarBonus * far + OpenBonus * ArenaOpen(server));
        foreach (var v in server.AllVehicles)
        {
            if (v.Driver != 0 || v.Wrecked || v.Hull < BoardHull * Hull.PointsFor(v.Kind) || _claims.TakenByOther(v.Key, Id)
                || _avoidRide.GetValueOrDefault(v.Key) > server.Now
                || Roofed(v.Position + new Vector3(0f, 2f * DmAvatar.VehicleRadius(v.Kind) + RoofClear, 0f)))
            {
                continue;
            }

            var kind = v.Kind == VehicleMode.Hk ? HkBonus : 1f;
            if (use == GoalUse.Retreat && Flat(v.Position - enemy) < Flat(Body.Position - enemy))
            {
                kind *= TowardsEnemy;
            }
            Offer(GoalKind.Vehicle, v.Key, v.Position, worth * kind * v.Hull / Hull.PointsFor(v.Kind));
        }
    }

    /// <summary>What item <paramref name="sprite"/> is worth to the bot now.</summary>
    private float ItemValue(int sprite, DmPlayer? me, GoalUse use)
    {
        if (!PickupTable.Items.TryGetValue(sprite, out var fx))
        {
            return 0f;
        }

        var focus = BotTraits.Lerp(0.6f, 1.4f, _traits.ItemFocus);
        var health = me != null ? me.Health / DmProtocol.DosHealth : 1f;
        if (fx.HealPercent > 0)
        {
            var careful = BotTraits.Lerp(0.7f, 1.3f, _traits.Caution);
            return HealthWeight * MathF.Min(fx.HealPercent / 100f, 1f - health) * FillScale * careful * focus;
        }

        if (fx.Armour > 0)
        {
            var armour = me?.Armour ?? 0f;
            return ArmourWeight * MathF.Min(fx.Armour / 65536f, 1f - armour) * FillScale * focus;
        }

        if (use == GoalUse.Retreat)
        {
            return 0f;
        }

        var gain = Carries(fx.Weapon) ? Rank(fx.Weapon) - Rank(Weapon) : 0;
        return gain > 0 ? WeaponWeight * (0.5f + (float)gain / Preference.Length) * focus : AmmoValue * focus;
    }

    /// <summary>One more option; its hope is its score over the straight line (the best it can be).</summary>
    private void Offer(GoalKind kind, int key, Vector3 at, float value)
    {
        if (value <= 0f)
        {
            return;
        }

        var keep = kind == _goalKind && key == _goalKey && Goal != null ? KeepGoal : 1f;
        value *= keep;
        var hope = value / (Flat(at - Body.Position) / PlayerController.RunSpeed + TimeBias);
        _options.Add(new Option { Kind = kind, Key = key, At = at, Value = value, Hope = hope });
    }

    /// <summary>The option with the best score: weighed (a nav path) in order of hope until no hope
    /// beats the best score or <see cref="MaxWeighed"/> were weighed; null when none.</summary>
    private int? Best()
    {
        int? best = null;
        var bestScore = 0f;
        for (var n = 0; n < MaxWeighed; n++)
        {
            var next = -1;
            for (var i = 0; i < _options.Count; i++)
            {
                if (!_options[i].Weighed && (next < 0 || _options[i].Hope > _options[next].Hope))
                {
                    next = i;
                }
            }

            if (next < 0 || _options[next].Hope <= bestScore)
            {
                break;
            }

            var o = _options[next];
            var reach = Reach.Cut;
            var t = float.MaxValue;
            if (!CutOff(o))
            {
                t = Travel(o.At, out reach);
                if (reach == Reach.Cut)
                {
                    Unreachable(o.Kind, o.Key, Found.Searched);
                }
            }

            o.Weighed = true;
            o.Time = t;
            _options[next] = o;

            // The pick's search budget is spent: the best weighed so far.
            if (reach == Reach.Unknown)
            {
                break;
            }

            if (reach == Reach.Cut)
            {
                continue;
            }

            var score = o.Value / (t + TimeBias);
            if (score > bestScore)
            {
                (best, bestScore) = (next, score);
            }
        }

        return best;
    }

    /// <summary>What a goal's path search says of it: reached, cut off, or nothing (the pick's
    /// search budget ran out first).</summary>
    private enum Reach
    {
        Yes,
        Cut,
        Unknown,
    }

    /// <summary>How a goal was found cut off: by this bot's search, or another bot's lately.</summary>
    private enum Found
    {
        Searched,
        Shared,
    }

    /// <summary>Polygons a pick's path searches expand at most, all its goals together: a search
    /// for a cut-off goal expands every polygon it reaches (7 000 on MAP.601, 15-30 ms), and the
    /// budget keeps a pick's cost fixed, the same in every replay.</summary>
    internal const int PickNodes = 6000;

    /// <summary>Polygons a walk's path search expands at most (a chase after a target on a roof).</summary>
    internal const int WalkNodes = 3000;

    private int _pickNodes;
    /// <summary>The bot stands on the mesh the spawns reach (<see cref="BotClaims.Reach"/>).</summary>
    private bool _fromReached;

    /// <summary>Picks made; path searches of the picks; those that found the goal cut off; the most
    /// polygons a pick's searches expanded.</summary>
    internal int Picks { get; private set; }
    internal int PickSearches { get; private set; }
    internal int CutSearches { get; private set; }
    internal int MostPickNodes { get; private set; }

    /// <summary>Goal <paramref name="o"/> is known cut off without a search: off the mesh the spawns
    /// reach (from a spot on it), or found so by another bot lately.</summary>
    private bool CutOff(Option o)
    {
        if (_fromReached && _claims.Reach is { } reach && !reach.Contains(_nav!.EndPoly(o.At)))
        {
            return true;
        }

        if (!_claims.Unreached((int)o.Kind, o.Key, _server!.Now))
        {
            return false;
        }

        Unreachable(o.Kind, o.Key, Found.Shared);
        return true;
    }

    /// <summary>The other bots skip a goal found cut off this long (s).</summary>
    private const double SharedUnreached = 10.0;

    /// <summary>A goal no path reaches is not weighed again for <see cref="AvoidUnreached"/> s, nor
    /// by the other bots for <see cref="SharedUnreached"/> s: a search that fails floods the whole
    /// mesh (15-30 ms on MAP.601, whose HKs stand on the roofs).</summary>
    private void Unreachable(GoalKind kind, int key, Found how)
    {
        var until = _server!.Now + AvoidUnreached;
        if (how == Found.Searched)
        {
            _claims.MarkUnreached((int)kind, key, _server.Now + SharedUnreached);
        }

        if (kind == GoalKind.Item)
        {
            _avoid[key] = Math.Max(_avoid.GetValueOrDefault(key), until);
            return;
        }

        if (kind == GoalKind.Vehicle)
        {
            _avoidRide[key] = Math.Max(_avoidRide.GetValueOrDefault(key), until);
        }
    }

    /// <summary>Seconds running to <paramref name="to"/>: along the nav path (none: straight; a
    /// vehicle's driver: straight); unreachable when the path stops well short.</summary>
    private float Travel(Vector3 to, out Reach reach)
    {
        var p = Body.Position;
        reach = Reach.Yes;
        if (_nav == null || _ride.Riding)
        {
            return Flat(to - p) / PlayerController.RunSpeed;
        }

        // A leg it cannot climb blocks the polygon it crosses for good, and the path is asked again
        // (the way round: a ramp, the roofs and a drop).
        for (var tries = 0; tries <= MaxDetours; tries++)
        {
            if (_pickNodes >= PickNodes)
            {
                reach = Reach.Unknown;
                return float.MaxValue;
            }

            var found = _nav.FindPath(p, to, _probe, _blocked, PickNodes - _pickNodes);
            _pickNodes += _nav.Nodes;
            MostPickNodes = Math.Max(MostPickNodes, _pickNodes);
            PickSearches++;
            if (_nav.OutOfNodes)
            {
                reach = Reach.Unknown;
                return float.MaxValue;
            }

            if (found == PathResult.None || _probe.Count == 0 || (found == PathResult.Partial && Flat(_probe[^1] - to) > PartialSlack))
            {
                break;
            }

            var bad = BadLeg();
            if (bad < 0)
            {
                var length = Flat(_probe[0] - p);
                for (var i = 1; i < _probe.Count; i++)
                {
                    length += Flat(_probe[i] - _probe[i - 1]);
                }

                return length / PlayerController.RunSpeed;
            }

            // The polygon the leg crosses, not the floor it starts or ends on.
            var poly = _nav.PolyAt(Vector3.Lerp(_probe[bad - 1], _probe[bad], 0.5f), out _);
            var ends = poly == _nav.PolyAt(_probe[bad - 1], out _) || poly == _nav.PolyAt(_probe[bad], out _);
            if (ends)
            {
                break;
            }

            if (poly == 0 || _blocked.Count >= MaxBlocked || _blocked.Contains(poly))
            {
                break;
            }

            _blocked.Add(poly);
        }

        CutSearches++;
        reach = Reach.Cut;
        return float.MaxValue;
    }

    /// <summary>The probe path's first leg it cannot climb (its end's index), −1 none.</summary>
    private int BadLeg()
    {
        for (var i = 1; i < _probe.Count; i++)
        {
            if (!Climbable(_probe[i - 1], _probe[i]))
            {
                return i;
            }
        }

        return -1;
    }

    /// <summary>New paths asked a goal at most; polygons a bot blocks at most (they are the map's).</summary>
    private const int MaxDetours = 3, MaxBlocked = 32;
    private readonly List<long> _blocked = new(MaxBlocked);

    /// <summary>A leg that rises more than a step has floor under it all the way (a ramp, stairs):
    /// on MAP.605 the mesh joins the street to the overpass the HKs park on with legs that run
    /// 90-170 over the street, no way up.</summary>
    private bool Climbable(Vector3 a, Vector3 b)
    {
        // Down more than a step: only off a baked drop link (it lands unhurt on the mesh). Up a step: walked.
        if (b.Y - a.Y <= PlayerController.StepHeight)
        {
            return a.Y - b.Y <= PlayerController.StepHeight || (_nav?.IsLink(a, b) ?? false);
        }

        // Up past a step: a climb link, or floor under the line all the way (a ramp, stairs).
        if (_nav?.IsLink(a, b) ?? false)
        {
            return true;
        }


        for (var k = 1; k < LegSamples; k++)
        {
            var q = Vector3.Lerp(a, b, (float)k / LegSamples);
            // From just over the line down: a deck over the street is above the probe, not under it.
            var reach = KneeHeight + PlayerController.StepHeight;
            if (!_world.Raycast(q + new Vector3(0f, KneeHeight, 0f), -Vector3.UnitY, reach, out var floor)
                || q.Y - floor.Point.Y > RiseGap)
            {
                return false;
            }
        }

        return true;
    }

    /// <summary>Floor probes along a climbing leg.</summary>
    private const int LegSamples = 10;
    /// <summary>A rising leg's line may run this far over the floor (stairs, a ramp's kink).</summary>
    private const float RiseGap = 65f;

    private void SetGoal(GoalKind kind, int key, Vector3 at, float travel)
    {
        if (kind != _goalKind || key != _goalKey)
        {
            _goalStuck = 0;
        }

        Goal = at;
        _goalKind = kind;
        _goalKey = key;
        _goalDue = (_server?.Now ?? 0.0) + travel * 2f + GoalSlack;
        if (kind != GoalKind.Vehicle)
        {
            DropRide(0f);
            return;
        }

        _claims.Claim(key, Id);
        _rideKey = key;
        _walkT = MathF.Max(WalkFor, travel * 2f);
        _walkStuck = 0;
    }

    /// <summary>The vehicle walked to is out of reach (not reached in time, stuck on the way):
    /// not sought for AvoidStuck s × its strikes (a seat on a deck the mesh only seems to reach).</summary>
    private void GiveUpRide()
    {
        if (_rideKey is not { } key)
        {
            return;
        }

        var strikes = _rideStrikes[key] = _rideStrikes.GetValueOrDefault(key) + 1;
        DropRide(AvoidStuck * MathF.Min(strikes, MaxStrikes));
    }

    private readonly Dictionary<int, int> _rideStrikes = [];

    /// <summary>No vehicle in view any more; that one not sought for <paramref name="avoid"/> s.</summary>
    private void DropRide(float avoid)
    {
        if (_rideKey is { } key && avoid > 0f && _server != null)
        {
            _avoidRide[key] = _server.Now + avoid;
        }

        if (_rideKey != null && _goalKind == GoalKind.Vehicle)
        {
            Goal = null;
            _goalKind = GoalKind.None;
        }

        _rideKey = null;
        _claims.Release(Id);
    }

    /// <summary>The nearest other living player (∞ none).</summary>
    private float NearestEnemy(DmServer server)
    {
        var best = float.PositiveInfinity;
        foreach (var p in server.AllPlayers)
        {
            if (p.Id != Id && p.Alive)
            {
                best = MathF.Min(best, Flat(p.Position - Body.Position));
            }
        }

        return best;
    }

    /// <summary>0..1: how large the arena is (its spawns' and items' spread over <see cref="LargeArena"/>), once.</summary>
    private float ArenaOpen(DmServer server)
    {
        if (_arenaOpen >= 0f)
        {
            return _arenaOpen;
        }

        Vector2 lo = new(float.MaxValue), hi = new(float.MinValue);
        foreach (var sp in server.Spawns)
        {
            lo = Vector2.Min(lo, new Vector2(sp.Feet.X, sp.Feet.Z));
            hi = Vector2.Max(hi, new Vector2(sp.Feet.X, sp.Feet.Z));
        }

        foreach (var item in server.AllPickups)
        {
            lo = Vector2.Min(lo, new Vector2(item.Position.X, item.Position.Z));
            hi = Vector2.Max(hi, new Vector2(item.Position.X, item.Position.Z));
        }

        _arenaOpen = hi.X >= lo.X ? Math.Clamp(MathF.Max(hi.X - lo.X, hi.Y - lo.Y) / LargeArena, 0f, 1f) : 0f;
        return _arenaOpen;
    }

    // ---- vehicles ----
    //
    //   on foot ─ a goal (PickGoal): a free whole vehicle under open sky, unclaimed, its value over
    //   │         the walk ─► claim, walk to its side ─► EnterVehicle (Decide: gone, enemy close, overdue ─► drop)
    //   driving ─ Drive: jeep path / HK flight to the orbit point, the last seen place or a roam goal
    //   └ Decide: hull low, stuck for long, target where the vehicle cannot go ─► out (an HK lands first)
    //     a wreck: the server puts it out ─► on foot

    /// <summary>How often the ride is weighed (s).</summary>
    private const float DecideEvery = 1f;
    /// <summary>An enemy in sight this near keeps it on foot (the fight is now).</summary>
    private const float CloseEnemy = 1200f;
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
    /// <summary>A vehicle not reached in this long, or twice its path's run when longer (the far
    /// overpass HK: stairs, a roof, a drop), is given up (s).</summary>
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

    /// <summary>Per skill 0-2: chance to ram, turret swing (rad/s).</summary>
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
    private int _walkStuck, _seatKey;
    /// <summary>A vehicle left stuck is not sought for this long (s).</summary>
    private const float AvoidStuckRide = 30f;
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

        if (_rideKey is not { } key)
        {
            return;
        }

        // The vehicle went (taken, wrecked), was not reached in WalkFor (on a roof, behind a
        // fence), or an enemy in sight came close (the fight is now): another goal.
        _walkT -= DecideEvery;
        var gone = !server.Vehicles.TryGetValue(key, out var v) || v.Driver != 0 || v.Wrecked;
        if (gone || EnemyFirst(server, v!.Position))
        {
            DropRide(0f);
            return;
        }

        if (_walkT <= 0f)
        {
            GiveUpRide();
        }
    }

    /// <summary>An enemy in sight within <see cref="CloseEnemy"/> and nearer than the vehicle at
    /// <paramref name="vehicle"/>: the fight comes first (a nearer vehicle is fought from).</summary>
    private bool EnemyFirst(DmServer server, Vector3 vehicle)
    {
        if (Target is not { } t || !TargetVisible || !server.Players.TryGetValue(t, out var tp))
        {
            return false;
        }

        // On foot against a vehicle: the seat first, whatever the distance.
        if (server.VehicleOf(t) != null)
        {
            return false;
        }

        var enemy = Flat(tp.Position - Body.Position);
        return enemy < CloseEnemy && enemy < Flat(vehicle - Body.Position);
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
            var seated = server.EnterVehicle(Id, key);
            DropRide(0f);
            if (seated)
            {
                GetIn(v);
            }

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
        State = BotState.Ride;
        _seatKey = v.Key;
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

        // A vehicle that kept sticking is not taken again soon.
        if (_ride.StuckLong)
        {
            _avoidRide[_seatKey] = server.Now + AvoidStuckRide;
        }

        _ride.Leave();
        Body.Position = PlayerStart.Clear(_world, Body.Body, at + right * StepAside + new Vector3(0f, StepUp, 0f));
        Body.Velocity = Vector3.Zero;
        _landing = false;
        _rideKey = null;
        _reboardT = ReboardWait;
        State = BotState.SeekItem;
        Goal = null;
        _goalKind = GoalKind.None;
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
            if (Goal != null && _ride.AtGoal)
            {
                Reached(); // as near as the vehicle goes: somewhere else next
                _campT = 0f;
            }

            if (Goal == null || _goalT <= 0f)
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

        foreach (var v in server.AllVehicles)
        {
            if (v.Driver == 0 && !v.Wrecked)
            {
                list.Add((v.Position, v.Kind == VehicleMode.Hk ? ParkedHk : ParkedJeep));
            }
        }

        foreach (var p in server.AllPlayers)
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
        foreach (var pl in server.AllPlayers)
        {
            var id = pl.Id;
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

        // The view (turned by the aim, error and all) must be near the target.
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
            return; // short of energy
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

        // On foot along the view (where the aim put it); a driver's turret straight at the target.
        var along = _ride.Riding ? Vector3.Normalize(d) : PlayerController.Direction(Body.Yaw, Body.Pitch);
        var dir = Spread(along, SpreadRad * Range(0.3f, 1f));
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
            // the shot, falling at its +0x1c) and strikes when it gets there.
            _inFlight.Add(new Shot(weapon, ammo, from, dir * record.Speed));
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
                s.Velocity -= new Vector3(0f, _table.Weapons[s.Weapon].Lift * dt, 0f);
            }

            var motion = s.Velocity * dt;
            var len = motion.Length();
            if (len < 1e-6f)
            {
                _inFlight[i] = s;
                continue;
            }

            var dir = motion / len;
            var victim = Strike(server, s.Position, dir, len, out var t);
            if (victim == null && t >= len)
            {
                s.Position += motion;
                _inFlight[i] = s;
                continue;
            }

            var at = s.Position + dir * t;
            if (victim == null && lobbed && s.Ammo.Bounces && _world.Raycast(s.Position, dir, len, out var wall)
                && Projectile.Bounce(s.Velocity, wall.Normal, out var bounced) == Projectile.BounceOutcome.Bounce)
            {
                s.Velocity = bounced;
                s.Position = wall.Point + wall.Normal * 2f;
                _inFlight[i] = s;
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
        output.Blast(Id, at);
        var reach = Blast.Reach(strength);
        _blastees.Clear();
        _blastees.AddRange(server.AllPlayers); // a copy: a kill may change the roster
        foreach (var p in _blastees)
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
        foreach (var p in server.AllPlayers)
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
