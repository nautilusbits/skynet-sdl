using RetroEng.Loop;
using System.Numerics;
using RetroEng.Physics;
using SkyNet.Formats;
using SkyNet.Game.Audio;
using SkyNet.Game.Weapons;

namespace SkyNet.Game.Enemies;

/// <summary>What a save keeps of one robot (<see cref="Robots.Snapshot"/>): which one it is (its
/// marker or spawn sprite), where it stands, its hit points and its life.</summary>
public readonly record struct RobotSnapshot(int MarkerOffset, int SpawnSource, Vector3 Position, float Yaw, float Hp,
    bool Dead, bool Hidden, float DormantDistance, int DeathAct, bool InitDone, bool Alert, int SenseCount = 0, bool Perceived = false)
{
    /// <summary>Every segment's hit points, root first in the segments' depth-first order (issue #58);
    /// null: every segment whole (and so a save from before).</summary>
    public float[]? Parts { get; init; }
    /// <summary>The shot-off segments, a bit each in that order.</summary>
    public ulong Gone { get; init; }
    /// <summary>Every segment's own look in that order, the root's 0 (its are the fields above):
    /// counter 0..3, bit 2 the last answer, bit 3 a pending alert. Null: none kept.</summary>
    public byte[]? Senses { get; init; }
    /// <summary>A hover's point on the soldier's trail (0 none) and the stamp it read there.</summary>
    public int Node { get; init; }
    public int NodeStamp { get; init; }
}

/// <summary>A robot's act on death: the marker's act byte, its file offset, and the robot.</summary>
public readonly record struct RobotActEvent(int Act, int MarkerOffset, Enemy Robot);

/// <summary>
/// The robots of one level: built from the map's enemy-start markers (type 2) and its 0xF3 spawn
/// sprites (hidden until their chain fires), ticked against the soldier, struck through the
/// <see cref="WeaponSystem"/>'s targets and shooting through its <see cref="WeaponSystem.EnemyFire"/>.
/// Deaths fling the type's wreck parts (<see cref="Wreckage"/>), explode the segments whose death
/// spec says so (<see cref="ExplodesOnDeath"/>: the effect plus the death blast 75, 0.375 s later, at
/// the soldier and the other robots), and raise
/// <see cref="DeathAct"/> for the behaviour (<c>LevelBehaviour.RobotAct</c>). Ported from the Godot
/// port's level_loader.gd (enemy placement) and enemy.gd.
/// </summary>
public sealed class Robots
{
    /// <summary>DOS places a start marker's robot at Y + 0x10 (FUN_00129f39); a spawn sprite's at its own Y.</summary>
    public const int MarkerLift = 0x10;
    /// <summary>SpawnEnemiesInit builds at most 50 spawn-sprite robots.</summary>
    public const int MaxSpawns = 50;
    /// <summary>FUN_0012a52c stands a new robot on the floor only when the floor is within this of its Y.</summary>
    public const int StandWindow = 0x50;
    /// <summary>The ammo type a robot fires when its own is out of the table.</summary>
    private const int FallbackAmmo = 15;

    private readonly List<Enemy> _all = [];
    private readonly List<(float Left, Vector3 At)> _blasts = [];
    private Difficulty _difficulty = Difficulty.Medium;
    private Vector3? _centre;
    private readonly Action<Vector3, WreckPart> _partGone;
    private bool _spawnRunDone;
    private readonly List<Triangle> _surfaceTris = [];
    private MapFile? _map;

    public Robots(AisData ais, EnemyModels models, CollisionWorld world, WeaponSystem? weapons = null, int seed = 1, SimRandom? random = null)
    {
        Ais = ais;
        Models = models;
        World = world;
        Weapons = weapons;
        Rng = random ?? new SimRandom((ulong)seed);
        Difficulty = Difficulty.Medium;
        _partGone = PartExploded;
    }

    public AisData Ais { get; }
    public EnemyModels Models { get; }
    public CollisionWorld World { get; }
    public WeaponSystem? Weapons { get; }
    public SoundBank? Sounds { get; set; }
    /// <summary>The simulation's generator (the session's: every robot, its brain and its wreckage draw from it).</summary>
    public SimRandom Rng { get; }
    /// <summary>Draws for sound alone (which explosion sample plays): never the simulation's.</summary>
    public SimRandom Cosmetic { get; set; } = new(0x5EED);
    public IReadOnlyList<Enemy> All => _all;
    public IEnumerable<Enemy> Active => _all.Where(e => e.Alive);
    /// <summary>The wreck parts the destroyed robots threw (DOS state-1 actors), flying until they go off.</summary>
    public Wreckage Wreckage { get; } = new();
    /// <summary>Death blasts thrown and not yet gone off (each 0.375 s after its explosion).</summary>
    public int PendingBlasts => _blasts.Count;
    /// <summary>No robot fires (verifier runs).</summary>
    public bool HoldFire { get; set; }

    /// <summary>The robots' own clock (seconds run), counting DOS frames for the fire roll.</summary>
    internal double Clock { get; set; }

    /// <summary>A DOS frame began this tick: robots roll their fire once a frame (0x13c17a). DOS's
    /// rate is the machine's (5..50, 0x124c38); the port counts <see cref="DosFps"/>.</summary>
    internal bool FrameDue { get; private set; }

    public const double DosFps = 35.0;

    /// <summary>Where the soldier went: the hovers follow it out of sight (0x137545).</summary>
    internal PlayerTrail Trail { get; } = new();

    /// <summary>The map is outdoors (MAP +0x2344 = 1, [0x24c96]): a hover keeps under its ceiling.</summary>
    internal bool Outdoor => _map?.IsOutdoor == true;

    /// <summary>The trail and the clock for a save; null before the soldier was seen.</summary>
    public SavedTrail? SaveTrail() => Trail.Ready ? Trail.Save(Clock) : null;

    /// <summary>A saved trail and clock put back (none: filled again where he stands).</summary>
    public void RestoreTrail(SavedTrail? saved)
    {
        Trail.Clear();
        if (saved == null)
        {
            return;
        }

        Clock = saved.Clock;
        Trail.Restore(saved);
    }
    /// <summary>The level's path vehicles (<c>LevelBehaviour.Paths</c>): a robot on a path stands
    /// where its machine has driven. Null: the path robots stay at their markers.</summary>
    public PathVehicles? Paths { get; set; }

    /// <summary>ROBOT AI: DOS's movement (CLASSIC, the default here) or the IMPROVED one.</summary>
    public RobotAi Ai { get; set; } = RobotAi.Classic;

    /// <summary>The walkers' nav meshes (IMPROVED only; null: they head straight for their goals).</summary>
    internal RobotNav? Nav { get; set; }

    /// <summary>Console <c>navmesh on</c>: the walkers' meshes and paths are drawn (<see cref="EnemyRenderer"/>).</summary>
    internal bool ShowNav { get; set; }

    /// <summary>The four multipliers; also sets the weapons' damage to machines and to the soldier.</summary>
    public Difficulty Difficulty
    {
        get => _difficulty;
        set
        {
            _difficulty = value;
            if (Weapons != null)
            {
                Weapons.DamageToEnemies = value.DamageToEnemy;
                Weapons.DamageToPlayer = value.DamageToPlayer;
            }
        }
    }

    /// <summary>A robot whose marker carries an act with state bit 2 was destroyed.</summary>
    public event Action<RobotActEvent>? DeathAct;
    /// <summary>A robot was destroyed (after its act).</summary>
    public event Action<Enemy>? Died;
    /// <summary>A segment was shot off a robot that lives on.</summary>
    public event Action<Enemy>? PartLost;

    /// <summary>The id a map keeps a robot's fate under: its marker, else its spawn sprite.</summary>
    public static int Id(Enemy r) => r.MarkerOffset >= 0 ? r.MarkerOffset : r.SpawnSource;

    /// <summary>A brain's generator: the shared one (one stream for the whole simulation).</summary>
    internal SimRandom NewRng() => Rng;

    /// <summary>Places the robots of <paramref name="map"/>: start markers, then spawn sprites.</summary>
    public void Populate(MapFile map)
    {
        _map = map;
        Trail.Clear();
        var spawns = 0;
        foreach (var e in map.Entities)
        {
            if (e.Variant != MapEntityVariant.Sprite)
            {
                continue;
            }

            if (e.MarkerType == 2 && e.EnemyType >= 0)
            {
                var robot = Add(e.EnemyType, new Vector3(e.X, -(e.Y + MarkerLift), -e.Z), 0f, e.FileOffset);
                if (e.Param8 > 0 && (e.StateByte & 4) != 0)
                {
                    robot.DeathAct = e.Param8;
                    robot.DeathState = e.StateByte;
                }

                if (robot.State == 11 && e.LinkNext > 0)
                {
                    robot.OnPath = true;
                    Paths?.Get(e.FileOffset)?.StandAt(robot.Position);
                    robot.Indestructible = (robot.Type?.HitPoints ?? 0) == 0;
                }

                if (e.Param16 > 0)
                {
                    robot.MakeDormant(e.Param16);
                }
            }
            else if (!e.IsMarker && e.ActType == 0xF3 && spawns < MaxSpawns)
            {
                spawns++;
                // 0x12a4f0: the sprite's own X/Y/Z, facing +Z, no act.
                var robot = Add(e.Param16 & 0xFFFF, new Vector3(e.X, -e.Y, -e.Z), 0f, e.FileOffset, e.FileOffset);
                robot.HideUntilSpawned();
            }
        }
    }

    private int _sensePhase;

    /// <summary>0x12a57e: each new actor's perception counter, dealt round-robin 1, 2, 3, 0, ….</summary>
    internal int NextSensePhase() => _sensePhase = (_sensePhase + 1) & 3;

    /// <summary>One robot of <paramref name="type"/> (registered as a weapons target).</summary>
    public Enemy Add(int type, Vector3 position, float yaw = 0f, int markerOffset = -1, int spawnSource = -1)
    {
        position = StandOnFloor(type, position);
        var robot = new Enemy(this, type, position, yaw, markerOffset) { SpawnSource = spawnSource };
        _all.Add(robot);
        AddTargets(robot);
        return robot;
    }

    /// <summary>Each segment a target of its own (issue #58: each is a map record, 0x129a9d).</summary>
    private void AddTargets(Enemy robot)
    {
        foreach (var p in robot.Segments)
        {
            if (!p.Gone)
            {
                Weapons?.Targets.Add(p);
            }
        }
    }

    /// <summary>A restored robot's targets: its whole segments while it lives, none once dead.</summary>
    private void SyncTargets(Enemy robot)
    {
        foreach (var p in robot.Segments)
        {
            if (robot.Dead || p.Gone)
            {
                Weapons?.Targets.Remove(p);
            }
            else
            {
                Weapons?.Targets.Add(p);
            }
        }
    }

    private void RemoveTargets(Enemy robot)
    {
        foreach (var p in robot.Segments)
        {
            Weapons?.Targets.Remove(p);
        }
    }

    /// <summary>
    /// Where DOS stands a new robot (FUN_0012a52c, called by FUN_0012a4f0 for start markers, path
    /// machines and spawn sprites alike): with a stand height h (the type record's last dword, +0x18)
    /// and the surface <see cref="StandSurface"/> finds no more than <see cref="StandWindow"/> above or
    /// below its Y, the origin goes h + 2·sign h from that surface (DOS: Y = s − h − 2·sign h, Y down).
    /// A positive h is how far the model reaches below its origin (guntwr1 72, guntwr3 160, hvytrrt 36,
    /// walkers 44-108, hvytnk 20): the level author marked the turret's base, which would otherwise
    /// stay in the floor. A negative h (smltrrt −8, hvytrrt types 22-24 −36, hvytrrt2 type 21 −28) puts
    /// the origin |h| + 2 UNDER the same surface: the query has no ceiling case, so a hung gun goes
    /// under the top of the slab it hangs from (or of the floor, with no slab in reach).
    /// Unchanged with h = 0 or no surface in the window.
    /// </summary>
    public Vector3 StandOnFloor(int type, Vector3 position)
    {
        var h = type >= 0 && type < Ais.Types.Length && Ais.Types[type].Record is { Length: > 6 } r ? (int)r[6] : 0;
        if (h == 0)
        {
            return position;
        }

        var s = StandSurface(position);
        if (MathF.Abs(s - position.Y) > StandWindow)
        {
            return position;
        }

        return position with { Y = s + h + 2 * Math.Sign(h) };
    }

    /// <summary>Half the height of the column <see cref="StandSurface"/> gathers (the ground is read at
    /// any height).</summary>
    private const float SurfaceGather = 1e5f;

    /// <summary>
    /// The surface FUN_00138d00 returns for a vertical segment from <see cref="StandWindow"/> above
    /// <paramref name="position"/> to as far below it (SKYNET.EXE v1.01): the highest face that the
    /// segment crosses and whose world normal points up (0x138ebf: hit +0x14 &lt; 0, Y down), of the
    /// variant-1 meshes without the 0x40 bit linked in the four map cells (1024 u) round
    /// (x − 512, z − 512) (0x136cb7 per cell); outdoors the WLD ground straight under or over the
    /// point wins when it is higher or nothing was crossed (0x14575d, not limited to the segment);
    /// indoors with nothing crossed DOS returns Y 0xE10 (here −3600, far out of the window).
    /// Level axes (Y up).
    /// </summary>
    public float StandSurface(Vector3 position)
    {
        var x = (int)MathF.Round(position.X);
        var z = (int)MathF.Round(-position.Z);
        int cx0 = Math.Max(x - 0x200, 0) >> 10, cz0 = Math.Max(z - 0x200, 0) >> 10;
        var top = position.Y + StandWindow;
        var bottom = position.Y - StandWindow;
        var mesh = float.NegativeInfinity;
        float? ground = null;
        var from = new Vector3(position.X, SurfaceGather, position.Z);
        _surfaceTris.Clear();
        World.Gather(new Box(new Vector3(position.X - 0.5f, -SurfaceGather, position.Z - 0.5f),
            new Vector3(position.X + 0.5f, SurfaceGather, position.Z + 0.5f)), _surfaceTris);
        foreach (var tri in _surfaceTris)
        {
            if (!Intersect.Ray(from, -Vector3.UnitY, tri, out var d))
            {
                continue;
            }

            var y = SurfaceGather - d;
            if (tri.Owner < 0)
            {
                ground = ground is { } g ? MathF.Max(g, y) : y;   // the WLD heightfield
                continue;
            }

            if (y < bottom || y > top || tri.Cross.Y <= 0f || y <= mesh)
            {
                continue;
            }

            if (_map != null)
            {
                if (!_map.TryGetEntityAt(tri.Owner, out var e) || e.Variant != MapEntityVariant.Mesh || e.IsActor
                    || (e.CellX - cx0) is not (0 or 1) || (e.CellZ - cz0) is not (0 or 1))
                {
                    continue;
                }
            }

            mesh = y;
        }

        var floor = ground ?? -0xE10;
        return float.IsNegativeInfinity(mesh) || floor > mesh ? floor : mesh;
    }

    /// <summary>Robots destroyed on an earlier visit (by marker or spawn-sprite offset) stay gone:
    /// dead without a blast, an act or a target.</summary>
    public void Retire(IEnumerable<int> offsets)
    {
        var set = offsets.ToHashSet();
        foreach (var r in _all)
        {
            if (set.Contains(Id(r)) && r.Retire())
            {
                RemoveTargets(r);
            }
        }
    }

    /// <summary>Segments shot off on an earlier visit stay off, quietly: DOS replays the map's logged
    /// EnemyKill on that segment's record (0x129f30 logs kind 3; 0x12e7f0 replays it).</summary>
    public void ShootOff(IReadOnlyDictionary<int, ulong> parts)
    {
        foreach (var r in _all)
        {
            if (r.Dead || !parts.TryGetValue(Id(r), out var bits))
            {
                continue;
            }

            r.ShootOff(bits);
            SyncTargets(r);
        }
    }

    /// <summary>Takes every robot off the weapons' targets (the map is left).</summary>
    public void Detach()
    {
        foreach (var r in _all)
        {
            RemoveTargets(r);
        }

        _blasts.Clear();
        Wreckage.Clear();
    }

    /// <summary>Every robot as it stands, in the order <see cref="Populate"/> built them (a save).</summary>
    public RobotSnapshot[] Snapshot() => [.. _all.Select(r => r.Snapshot())];

    /// <summary>Puts a <see cref="Snapshot"/> back onto the robots <see cref="Populate"/> built from
    /// the same map: matched by position in the list and checked by marker; a robot that died is
    /// taken off the targets. The death blasts in flight are dropped. False when the lists differ.</summary>
    public bool Restore(IReadOnlyList<RobotSnapshot> robots)
    {
        if (robots.Count != _all.Count)
        {
            return false;
        }

        for (var i = 0; i < robots.Count; i++)
        {
            if (robots[i].MarkerOffset != _all[i].MarkerOffset || robots[i].SpawnSource != _all[i].SpawnSource)
            {
                return false;
            }
        }

        _blasts.Clear();
        Wreckage.Clear();
        _spawnRunDone = true;                         // a saved map was run long ago
        for (var i = 0; i < robots.Count; i++)
        {
            var r = _all[i];
            var died = r.Restore(robots[i]);
            SyncTargets(r);
            if (died)
            {
                if (r.OnPath)
                {
                    Paths?.Kill(r.MarkerOffset);
                }
            }
        }

        return true;
    }

    /// <summary>The 0xF3 sprite at <paramref name="source"/> fired (the behaviour's Spawn): its robot comes out.</summary>
    public bool Spawn(int source)
    {
        var any = false;
        foreach (var r in _all)
        {
            if (r.SpawnSource == source && r.SpawnIn())
            {
                any = true;
            }
        }

        return any;
    }

    /// <summary>A tick's start: every robot's and wreck part's pose becomes the one the frames glide
    /// from (render interpolation, <see cref="EnemyRenderer.Capture(Robots, RobotFrame, float)"/>).</summary>
    public void KeepPoses()
    {
        foreach (var r in _all)
        {
            r.KeepPose();
        }

        Wreckage.KeepPoses();
    }

    /// <summary>The robots run by the last <see cref="Tick"/> (the activity window's catch).</summary>
    public int Ticked { get; private set; }

    /// <summary>The centre of the activity window: the soldier's feet as last seen (null: never).</summary>
    public Vector3? WindowCentre => _centre;

    /// <summary>One tick: the robots in DOS's activity window (<see cref="TickWindow"/>: the 5×5 map
    /// cells round the soldier) run against his feet (null: none, the window stays where he was last
    /// seen); the rest stand frozen as they are. The very first tick runs every robot once, as DOS
    /// gives each start-marker robot one run while it builds the map (0x12a439). Then the death
    /// blasts.</summary>
    public void Tick(float dt, Vector3? player)
    {
        if (player is { } seen)
        {
            _centre = seen;
        }

        var frames = Math.Floor(Clock * DosFps);
        Clock += dt;
        FrameDue = Math.Floor(Clock * DosFps) > frames;
        if (player is { } feet)
        {
            Trail.Record(feet, (int)(Clock * PlayerTrail.StampRate));   // the soldier moves before the robots (0x12251b)
        }

        var all = !_spawnRunDone || _centre == null;
        _spawnRunDone = true;
        var cx = 0;
        var cz = 0;
        if (_centre is { } c)
        {
            cx = TickWindow.CellX(c);
            cz = TickWindow.CellZ(c);
        }

        var ticked = 0;
        for (var i = 0; i < _all.Count; i++)
        {
            var r = _all[i];
            if (r.OnPath && r.Dead)
            {
                Paths?.Kill(r.MarkerOffset);          // retired on an earlier visit
            }
            else if (r.OnPath && r.Alive && Paths != null && Paths.TryGetPose(r.MarkerOffset, out var at, out var yaw))
            {
                r.Position = at;                      // the machine keeps its own window (PathVehicle)
                r.Yaw = yaw;
                r.UpdateTransforms();
            }

            if (r.Dead || r.Hidden || r.OnPath)
            {
                continue;                             // a dormant robot ticks: state 12 watches for the soldier
            }

            if (!all && Ai == RobotAi.Improved)
            {
                if (!TickWindow.ContainsImproved(r.Position, _centre!.Value))
                {
                    continue;
                }
            }
            else if (!all && (Math.Abs(TickWindow.CellX(r.Position) - cx) > TickWindow.Reach
                         || Math.Abs(TickWindow.CellZ(r.Position) - cz) > TickWindow.Reach))
            {
                continue;
            }

            r.Tick(dt, player);
            ticked++;
        }

        Ticked = ticked;
        Wreckage.Tick(dt, World, _partGone);

        for (var i = _blasts.Count - 1; i >= 0; i--)
        {
            var (left, at) = _blasts[i];
            left -= dt;
            if (left > 0f)
            {
                _blasts[i] = (left, at);
                continue;
            }

            _blasts.RemoveAt(i);
            DeathBlastAt(at);
        }
    }

    /// <summary>EnemyKill's blast (75) at <paramref name="at"/>: 115 − d to the soldier and to every
    /// robot and breakable map record in reach with a clear line, unscaled by the difficulty.</summary>
    public void DeathBlastAt(Vector3 at)
    {
        // Map records (cars, pumps) through the weapons' record path: FUN_00124886 strikes them too.
        if (Weapons is { } w)
        {
            w.BlastAt(at, Enemy.DeathBlast, spare: t => w.Targets.OwnerOf(t) < 0,
                falloff: (d, pad) => Enemy.DeathBlastDamage(d, pad));
        }

        if (Weapons?.Player is { Alive: true } player)
        {
            var dmg = Enemy.DeathBlastDamage(Vector3.Distance(at, player.Centre));
            if (dmg > 0 && Clear(at, player.Centre))
            {
                player.TakeDamage(new Damage(dmg, DamageKind.Blast, at, false));
            }
        }

        // Each segment by its own origin and model radius (FUN_00124886 walks the records).
        foreach (var r in _all.ToArray())
        {
            foreach (var p in r.Segments)
            {
                if (!p.Alive)
                {
                    continue;
                }

                var ed = Enemy.DeathBlastDamage(Vector3.Distance(at, p.BlastOrigin), p.BlastPad);
                if (ed > 0 && Clear(at, p.BlastOrigin))
                {
                    p.TakeDamage(new Damage(ed, DamageKind.Blast, at, false));
                }
            }
        }
    }

    private bool Clear(Vector3 from, Vector3 to)
    {
        var d = to - from;
        var len = d.Length();
        return len < 1f || !World.Raycast(from, d / len, len - 1f, out _);
    }

    // ---------------------------------------------------------------- what the robots ask for

    internal void Sound(int id, Vector3 at)
    {
        if (id >= 0)
        {
            // DOS plays the AIS frame sounds at the id's own volume and pitch (FUN_0012f018).
            Sounds?.PlayDos3D(id, at);
        }
    }

    internal void HitSound(Vector3 at) => Sounds?.Play3D("HIT2.RAW", at, -4f);

    internal void HurtPlayer(float points, DamageKind kind, Vector3 at) =>
        Weapons?.Player?.TakeDamage(new Damage(points, kind, at, false));

    internal void Shoot(Enemy robot, int ammoType, Vector3 muzzle, Vector3 dir, float speed)
    {
        if (Weapons == null)
        {
            return;
        }

        Weapons.EnemyFire(robot, AmmoOf(Weapons, ammoType), muzzle, dir, speed);
    }

    /// <summary>The ammo record a robot's ammo type fires (an unknown type: <see cref="FallbackAmmo"/>).</summary>
    private static AmmoRecord AmmoOf(WeaponSystem weapons, int ammoType)
    {
        var table = weapons.Table.Ammo;
        return ammoType >= 0 && ammoType < table.Length ? table[ammoType] : table[FallbackAmmo];
    }

    internal void RaiseDeathAct(int act, int marker, Enemy robot) => DeathAct?.Invoke(new RobotActEvent(act, marker, robot));

    /// <summary>A wreck part touched the world: its own EnemyKill (0x124793) — the explosion, one of
    /// the four explosion sounds (0x21 + rand &amp; 3) and the death blast 75.</summary>
    private void PartExploded(Vector3 at, WreckPart part)
    {
        Sounds?.Play3D(SoundIds.Explosion1 + Cosmetic.Next(4), at, -4f);
        var size = part.Model is { } m ? (m.Bounds.Max - m.Bounds.Min).Length() : 60f;
        Weapons?.Explosion(at, Enemy.DeathEffectBank, size * 0.55f);
        _blasts.Add((Enemy.DeathBlastDelay, at));
    }

    /// <summary>
    /// Whether EnemyKill (v1.01 0x129f30) runs the actor's own explosion (0x124793: the effect, the
    /// sound and the death blast 75) after its parts. The death spec (table +0x10) decides:
    /// <list type="bullet">
    /// <item>0 — nothing at all, no part and no explosion (<c>je 0x129fff</c>);</item>
    /// <item>1 — the explosion alone;</item>
    /// <item>a spec of kind 0 — its one part (the arg type, every turret segment's own model) and
    /// <b>no</b> explosion (<c>call 0x12a059; jmp 0x129fff</c>): the part goes off when it lands;</item>
    /// <item>kind 1 — every part of the list, then the explosion;</item>
    /// <item>any other kind — nothing.</item>
    /// </list>
    /// A kind-0 spec always names its part, so there is no kind-0 death without one; a spec with no
    /// parts is 0 or 1. A table without <see cref="EnemyType.DeathKind"/> (an older mod file) reads a
    /// single part at the origin as kind 0.
    /// </summary>
    public static bool ExplodesOnDeath(EnemyType? type)
    {
        if (type == null)
        {
            return false;
        }

        var spec = type.Record.Length > 4 ? type.Record[4] : (type.Death.IsEmpty ? 1u : 2u);
        if (spec <= 1)
        {
            return spec == 1;
        }

        var kind = type.DeathKind
                   ?? (type.Death is [{ X: 0, Y: 0, Z: 0 }] ? 0 : 1);
        return kind == 1;
    }

    /// <summary>State 12's wake (0x14305e): TEXTURE.367 half the model's radius toward the soldier,
    /// sized 20 per two radius units (0x143034), sound 0x26 there and at the robot.</summary>
    internal void Woke(Enemy robot, Vector3 player)
    {
        var half = (((int)(robot.BlastPad * 256f) + 0x80) >> 8) >> 1;   // 0x13c2d1: radius, rounded
        var to = player - robot.Position;
        var at = robot.Position + (to.LengthSquared() > 0f ? Vector3.Normalize(to) * half : Vector3.Zero);
        Weapons?.Warp(at, Enemy.TrapEffectBank, robot.BodySize * 0.55f, half * WarpSize / WeaponSystem.DosScaleUnit);
        Sound(Enemy.TrapSound, at);
        Sound(Enemy.TrapSound, robot.Position);
    }

    private const float WarpSize = 20f;

    internal void Destroyed(Enemy robot, bool trap)
    {
        var centre = robot.Position + new Vector3(0f, robot.BodyHeight * 0.5f, 0f);
        if (trap)
        {
            Sounds?.Play3D(Enemy.TrapSound, centre, -2f);
        }

        // EnemyKill recurses into the segments first; each one throws its parts and explodes or not
        // by its own spec. A turret (kind 0 throughout) only throws: its head flies off and goes off
        // where it lands.
        Wreckage.Throw(robot, this);
        foreach (var seg in robot.Root.Walk())
        {
            if (seg.Gone || !ExplodesOnDeath(seg.Type))
            {
                continue;
            }

            if (seg == robot.Root)
            {
                Sounds?.Play3D("EXPLO1.RAW", centre, -2f);
                Weapons?.Explosion(centre, Enemy.DeathEffectBank, robot.BodySize * 0.55f);
                _blasts.Add((Enemy.DeathBlastDelay, robot.Position));
            }
            else
            {
                Explode(seg);
            }
        }

        if (robot.OnPath)
        {
            Paths?.Kill(robot.MarkerOffset);
        }

        RemoveTargets(robot);
        Died?.Invoke(robot);
    }

    /// <summary>A segment's own explosion (0x124793): the effect, one of four sounds, the blast 75.</summary>
    private void Explode(EnemyPart seg)
    {
        var at = seg.World.Translation;
        Sounds?.Play3D(SoundIds.Explosion1 + Cosmetic.Next(4), at, -4f);
        var size = seg.Model is { } m ? (m.Bounds.Max - m.Bounds.Min).Length() : 60f;
        Weapons?.Explosion(at, Enemy.DeathEffectBank, size * 0.55f);
        _blasts.Add((Enemy.DeathBlastDelay, at));
    }

    /// <summary>EnemyKill (0x129f30) on one segment, not the root: its parts thrown and its
    /// children's, each exploding by its own spec; it leaves its parent's list (0x12a00b) and is not
    /// drawn, run or struck again. No act, no kill: the robot fights on.</summary>
    internal void PartDestroyed(Enemy robot, EnemyPart part)
    {
        robot.UpdateTransforms();
        Wreckage.Throw(part, robot, this);
        foreach (var seg in part.Walk())
        {
            if (seg.Gone)
            {
                continue;
            }

            if (ExplodesOnDeath(seg.Type))
            {
                Explode(seg);
            }

            seg.Gone = true;
            Weapons?.Targets.Remove(seg);
        }

        PartLost?.Invoke(robot);
    }
}
