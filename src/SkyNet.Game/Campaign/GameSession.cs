using System.Numerics;
using RetroEng.Diagnostics;
using RetroEng.Loop;
using RetroEng.Physics;
using RetroEng.World;
using SkyNet.Data;
using SkyNet.Formats;
using SkyNet.Game.Audio;
using SkyNet.Game.Behaviour;
using SkyNet.Game.Collision;
using SkyNet.Game.DevTools;
using SkyNet.Game.Enemies;
using SkyNet.Game.Hud;
using SkyNet.Game.Player;
using SkyNet.Game.Water;
using SkyNet.Game.Weapons;

namespace SkyNet.Game.Campaign;

/// <summary>Where the session is in its mission.</summary>
public enum SessionPhase { Playing, Complete, Failed, CampaignOver }

/// <summary>What the session asks its presenter (the app) to show. Every method has an empty default.</summary>
public interface ISessionListener
{
    /// <summary>A map was entered: build its picture (<see cref="GameSession.Map"/>, <see cref="GameSession.Behaviour"/>).</summary>
    void MapEntered(GameSession session) { }
    /// <summary>A mission (re)starts on its first map: its briefing, when it has one.</summary>
    void MissionStarted(Mission mission, Briefing? briefing, bool restart) { }
    void Hint(string text) { }
    /// <summary>An objective's line (DOS prints it only when longer than 2 characters; sound 0x51 either way).</summary>
    void Objective(string text) { }
    /// <summary>The top-left status line (pickups, MISSION COMPLETE …).</summary>
    void Status(string text) { }
    /// <summary>A status line in palette index <paramref name="colour"/> (the moon's "OW!" is 8).</summary>
    void Status(string text, byte colour) => Status(text);
    void MissionComplete(Mission mission) { }
    void MissionFailed(Mission mission, bool died) { }
    /// <summary>The MISSION FAILED banner has stood its <see cref="GameSession.FailedHold"/> and
    /// <see cref="GameSession.AutoRestart"/> is off: the session holds at
    /// <see cref="SessionPhase.Failed"/> until <see cref="GameSession.RestartMission"/> (the app asks
    /// RESTART MISSION?).</summary>
    void RestartOffered(Mission? mission) { }
    void CampaignComplete() { }
}

/// <summary>
/// One player's campaign, headless: the mission (table row, objective counter, the kit it began
/// with), the map being played (collision, level behaviour, pickups) and the per-map state of the
/// mission's other maps, the soldier and his weapons. The app draws it; tests and
/// <c>--play-script</c> drive it with the same calls.
/// <para>A mission is played from its mission file (<see cref="Campaign.MissionFile"/>,
/// docs/formats/mission.md): the map instances and the links between them. An 0xF0 exit follows
/// its link (back = the previous instance; an exit the file does not list goes to its record's
/// target map, as the Godot port's per-map runtime does: main.gd _on_teleport_requested,
/// _change_level) and places the player at marker set N facing marker N + 1. Each instance's
/// state — the live <see cref="LevelBehaviour"/> itself (states, movers, wrecks, spent cues), the
/// pickups taken and the robots killed — is kept for the rest of the mission and put back on
/// re-entry (DOS's Mst overlay). A restart or a new mission drops it all.</para>
/// <para>Mission end (SKYNET.EXE v1.01 FUN_00121fe3): the counter reaching 0 lets the game run
/// <see cref="CompleteDelay"/> more, then freezes it under WELLDONE.IMG for <see cref="CompleteHold"/>
/// and starts the next mission; act 0x2B or death shows FAILED.IMG for <see cref="FailedHold"/> and restarts the mission
/// from its first map with the kit it began with (DOS RESTART MISSION); with
/// <see cref="AutoRestart"/> off it holds there and offers the restart to the app
/// (<see cref="ISessionListener.RestartOffered"/>).</para>
/// </summary>
public sealed class GameSession
{
    public const int TicksPerSecond = 60;
    public const float Dt = 1f / TicksPerSecond;
    /// <summary>DOS's game clock (DAT_00038c00): 70 ticks a second (FUN_00124c63 adds 0x460000 / frame rate).</summary>
    public const float GameClockHz = 70f;
    /// <summary>The PC's BIOS tick (0x46C), 1 193 182 / 65 536 a second: FUN_0011b80f waits on it.</summary>
    public const float BiosTicksPerSecond = 1193182f / 65536f;
    /// <summary>The game plays on this long after the counter reaches 0 (FUN_00121fe3 stamps the clock
    /// and waits until it has run more than 0x68 ticks): the last line is read before the banner.</summary>
    public const float CompleteDelay = 0x68 / GameClockHz;
    /// <summary>A wreck's stage blast and a destruction's blasts: their size, how high over the
    /// record's origin they go off, and how far a destruction's extra effects scatter.</summary>
    public const float StageBlastRadius = 120f, DestroyBlastRadius = 190f, BlastLift = 60f;
    public const int BlastScatter = 120;
    /// <summary>The banner over the frozen frame (FUN_0011b80f(0xAF): 0xAF >> 2 = 43 BIOS ticks, about
    /// 2.36 s), for WELL DONE and FAILED alike; then the screen goes black and the next step comes.</summary>
    public const float BannerHold = (0xAF >> 2) / BiosTicksPerSecond;
    /// <summary>WELLDONE.IMG stands <see cref="BannerHold"/>, then the next mission's briefing.</summary>
    public const float CompleteHold = BannerHold;
    /// <summary>FAILED.IMG stands <see cref="BannerHold"/>, then RESTART MISSION?.</summary>
    public const float FailedHold = BannerHold;
    /// <summary>The banner art FUN_00121fe3 loads from MDMDIMGS.BSA (MDMDHRES.BSA in the hi-res set).</summary>
    public const string CompleteImage = "WELLDONE.IMG";
    public const string FailedImage = "FAILED.IMG";
    /// <summary>Death plays sound 108 (FUN_00122b52).</summary>
    public const int DeathSound = 108;
    /// <summary>An objective's sound (0x137fd0).</summary>
    public const int ObjectiveSound = 0x51;

    private sealed class MapState(LevelBehaviour behaviour, WaterLevel? water)
    {
        public readonly LevelBehaviour Behaviour = behaviour;
        /// <summary>The map's water surface (marker 103/104), moved by acts 0xD6-0xDA; null: dry.</summary>
        public readonly WaterLevel? Water = water;
        public readonly HashSet<int> Taken = [];
        public readonly HashSet<int> Dead = [];
        /// <summary>The ambient loops running when the map was left, to start again on re-entry.</summary>
        public readonly Dictionary<int, (int Id, Vector3 At)> Loops = [];
        /// <summary>What destroyed records left here and is still lying (ids −1, −2, …).</summary>
        public readonly List<DroppedSprite> Drops = [];
        public int NextDrop = -1;
    }

    private readonly record struct Pickup(int Offset, Vector3 Base, PickupItem Item);

    /// <summary>The current map's colliders behind one source the player and the weapons keep.</summary>
    private sealed class MapSource : ITriangleSource
    {
        public CollisionWorld? World;

        public Box Bounds
        {
            get
            {
                var b = Box.Empty;
                foreach (var s in World?.Sources ?? [])
                {
                    b = b.Union(s.Bounds);
                }

                return b;
            }
        }

        public void Gather(in Box box, List<Triangle> into) => World?.Gather(box, into);
    }

    /// <summary>A breakable record as a weapon target, struck through its collision owner.</summary>
    private sealed class RecordTarget(LevelBehaviour behaviour, int offset, Vector3 centre) : IDamageable
    {
        public Vector3 Centre => centre;
        public float Radius => 0f;
        public bool Alive => behaviour.IsDamageable(offset);
        public void TakeDamage(in Damage damage) => behaviour.Hit(offset, damage.Points);
    }

    /// <summary>The behaviour's effects, routed to whatever map is current.</summary>
    private sealed class Bridge(GameSession s) : IBehaviourListener
    {
        public void Sound(int source, int soundId, Vector3 at)
        {
            if (soundId >= 0)
            {
                // The 0xDB-0xEC nodes (doors, buttons, levers, lifts): FUN_0012f018's 3D rules.
                s.Sounds?.PlayDos3D(soundId, at);
            }
        }

        public void Voice(int source, int voiceId, Vector3 at)
        {
            if (s._voices?.Text(string.Create(System.Globalization.CultureInfo.InvariantCulture, $"no.{voiceId}")) is { Length: > 0 } file)
            {
                s.Sounds?.Play2D(file.ToUpperInvariant());
            }
        }

        public void LoopStart(int source, int soundId, Vector3 at)
        {
            if (soundId < 0 || s._state == null)
            {
                return;
            }

            s._state.Loops[source] = (soundId, at);
            s.StartLoop(source, soundId, at);
        }

        public void LoopStop(int source)
        {
            s._state?.Loops.Remove(source);
            if (s._loops.Remove(source, out var h))
            {
                h.Stop();
            }
        }

        public void Hint(int source, int index, string text)
        {
            if (text.Length > 0)
            {
                s.Listener?.Hint(text);
            }
        }

        public void Objective(int source, int index, int left, string text)
        {
            if (text.Length > 2)
            {
                s.Listener?.Objective(text);
            }
            else
            {
                s.Sounds?.Play2D(ObjectiveSound);
            }

            s.Log($"objective {index + 1} done, {left} left");
        }

        public void MissionComplete() => s._completeAt ??= s._clock + CompleteDelay;

        public void MissionFailed(int source) => s.Fail(died: false);

        public void Exit(in ExitRequest request) => s._pendingExit ??= request;

        /// <summary>A TRANSFRM.PRS wreck stepped a stage. Handler 0x120833 (v1.01): while the record
        /// still has hit points, a blast of bank 358 at it and one of explosions 33..36; a wreck
        /// a chain breaks (no hit points: MAP.248's wall) only changes its mesh.</summary>
        public void Stage(int source, int stage, int stages)
        {
            if (s._state?.Behaviour is not { } b || b.Hp(source) <= 0f || s.Map == null || !s.Map.TryGetEntityAt(source, out var e))
            {
                return;
            }

            var at = BehaviourModel.Position(e) + new Vector3(0f, BlastLift, 0f);
            s.Weapons.Explosion(at, DestructionTable.TypeZeroEffect >> 7, StageBlastRadius);
            var sample = SoundIds.Explosion1 + s.Cosmetic.Next(4);
            s.Sounds?.Play3D(sample, at);
        }

        /// <summary>The hit points ran out (FUN_00124793, v1.01): the destruction type's blasts and
        /// sound, the object's own radial blast (the link record's i16, which is how a row of cars
        /// goes up one after another), then its drop.</summary>
        public void Destroyed(in DestroyEvent e)
        {
            var at = e.Position + new Vector3(0f, BlastLift, 0f);
            var (effects, sound) = s._destruction.BlastOf(e.DestroyType);
            var first = true;
            foreach (var fx in effects)
            {
                if (fx <= 0)
                {
                    continue;
                }

                var off = first ? Vector3.Zero : new Vector3(s.Cosmetic.Next(-BlastScatter, BlastScatter + 1), 0f, s.Cosmetic.Next(-BlastScatter, BlastScatter + 1));
                s.Weapons.Explosion(at + off, fx >> 7, DestroyBlastRadius);
                first = false;
            }

            if (sound == -2)
            {
                var sample = SoundIds.Explosion1 + s.Cosmetic.Next(4);
                s.Sounds?.Play3D(sample, at);
            }
            else if (sound >= 0)
            {
                s.Sounds?.Play3D(sound, at);
            }

            if (e.Removed)
            {
                s.Weapons.Targets.Unregister(e.Source);
            }

            if (Math.Abs(e.DestroyParam) is var strength and > 0)
            {
                if (s.LevelBlast is { } blast)
                {
                    blast(e.Source, at, strength);
                }
                else
                {
                    s.Weapons.BlastAt(at, strength);
                }
            }

            s.Drop(e);
        }

        public void Spawn(int source, int param) => s.Robots?.Spawn(source);

        public void Water(int source, float target, bool absolute) => s._state?.Water?.Move(target, absolute);
    }

    private readonly Bridge _bridge;
    private readonly MapSource _source = new();
    private readonly Dictionary<string, MapState> _maps = new(StringComparer.Ordinal);
    private readonly Dictionary<int, MissionFile> _generated = [];
    private readonly Dictionary<int, SoundBank.LoopHandle> _loops = [];
    private readonly List<BodySound> _bodySounds = [];
    private readonly List<Pickup> _pickups = [];
    private readonly List<int> _targets = [];
    private readonly Dictionary<int, float> _waterSoundsUntil = [];
    private readonly DestructionTable _destruction;
    private readonly PrsFile? _strings, _voices;
    private readonly AisData _ais;
    private readonly EnemyModels _models;
    private Difficulty _difficulty = Difficulty.Medium;
    private MapState? _state;
    private MissionFile? _file;
    private WorldInstance? _instance, _previous;
    private ExitRequest? _pendingExit;
    private float _clock;
    private float? _completeAt, _endAt;
    private InventoryState? _startKit;
    private (float Health, float Armour) _startBody;

    public GameSession(GameData data, SoundBank? sounds = null)
    {
        Data = data;
        Sounds = sounds;
        Transfrm = BehaviourModel.LoadTransfrm(data);
        World = new CollisionWorld().Add(_source);
        Player = new PlayerController(World);
        Soldier = new Soldier(Player);
        Soldier.Pained = () =>
        {
            var grunt = PlayerSounds.PainGrunt(Cosmetic.Next(16));
            Sounds?.PlayDos2D(grunt, SoundBank.DosOverlap.Skip);
        };
        Weapons = new WeaponSystem(WeaponTable.Load(data), World, new DamageTargets(), sounds) { Player = Soldier };
        Weapons.DamageToEnemies = _difficulty.DamageToEnemy;
        Weapons.DamageToPlayer = _difficulty.DamageToPlayer;
        _bridge = new Bridge(this);
        Breath = new WaterBreath(Random);
        Weapons.Pieces.Random = Cosmetic;
        Stats = new MissionStats(data.Game);
        Weapons.Fired += (_, _) => Stats.Shot();
        Weapons.Reported += CountHit;
        Weapons.Moon = Moon;
        Moon.Said += (text, colour) => Listener?.Status(text, colour);
        _ais = GameTables.Ais;
        _destruction = DestructionTable.Load();
        Locations = LocationNames.Load();
        _models = new EnemyModels(data);
        if (data.Exists("MDMDBRIF.BSA"))
        {
            using var brif = data.OpenArchive("MDMDBRIF.BSA");
            _strings = brif.Read("STRINGS.PRS") is { } s ? PrsFile.Parse(s) : null;
            _voices = brif.Read("VOICE.PRS") is { } v ? PrsFile.Parse(v) : null;
        }
    }

    public GameData Data { get; }
    public SoundBank? Sounds { get; }
    /// <summary>The missions this game plays: the table's and the mounted mods' (<see cref="MissionTable.Playable"/>).</summary>
    public MissionTable Table => MissionTable.Playable(Data.Game);
    public TransfrmPrs Transfrm { get; }
    /// <summary>The world the player and the weapons collide with; its content follows the map.</summary>
    public CollisionWorld World { get; }
    public PlayerController Player { get; }
    public Soldier Soldier { get; }
    /// <summary>The soldier's steps and landings (cosmetic).</summary>
    public PlayerSounds Body { get; } = new();
    public WeaponSystem Weapons { get; }
    public ISessionListener? Listener { get; set; }
    /// <summary>The STATISTICS counters: this mission's row and the career (<see cref="MissionStats"/>).</summary>
    public MissionStats Stats { get; }
    /// <summary>The STATISTICS page for the briefing <see cref="ISessionListener.MissionStarted"/>
    /// opens: the mission just finished and the career with it, frozen before the new mission's
    /// row begins; no row (STATS ARE NOT YET AVAILABLE) on a new game or a restart, as in DOS.</summary>
    public StatSheet BriefingStats { get; private set; }
    /// <summary>STATS ARE NOT YET AVAILABLE, or the page's header, for <paramref name="sheet"/>.</summary>
    public string StatsHeader(StatSheet sheet) => MissionStats.Header(sheet, _strings, Pilot);
    /// <summary>The player's name and call sign from the new-game dialog (per game, saved with it).</summary>
    public Pilot Pilot { get; set; } = Pilot.None;
    /// <summary>STRINGS.PRS's text for <paramref name="key"/> (its first, [english], definition);
    /// <paramref name="fallback"/> without the file or the key.</summary>
    public string Text(string key, string fallback) => _strings?.Text(key, fallback) ?? fallback;
    /// <summary>What an empty field of the dialog takes: STRINGS.PRS DefaultName / DefaultSign.</summary>
    public Pilot DefaultPilot => Pilot.Defaults(_strings);
    /// <summary>The page's four rows for <paramref name="sheet"/> (labels from STRINGS.PRS).</summary>
    public IReadOnlyList<(string Label, string Value)> StatsRows(StatSheet sheet) => MissionStats.Rows(sheet, _strings);
    /// <summary>The current map's robots (null: no SKYNET.EXE in the data, or no map yet).</summary>
    public Robots? Robots { get; private set; }
    /// <summary>Mission 5's torpedo ride while it runs (MAP.250 entered from MAP.253): the keys are
    /// locked and the view is the ride's; null otherwise.</summary>
    public TorpedoRide? Torpedo { get; private set; }
    /// <summary>The current map's water surface (null: a dry map).</summary>
    public WaterLevel? Water => _state?.Water;
    /// <summary>The soldier's held breath under water.</summary>
    public WaterBreath Breath { get; }
    /// <summary>The night moon: its hits and its fall on this map (<see cref="NightMoon"/>).</summary>
    public NightMoon Moon { get; } = new();

    /// <summary>The automap's seen records on the current map (wiped on every map entry).</summary>
    public Automap.AutomapSeen Seen { get; } = new();

    /// <summary>The seed a new session's <see cref="Random"/> starts from.</summary>
    public const ulong DefaultSeed = 1;

    /// <summary>The simulation's one generator (docs/demos.md): the level behaviour of every map, the
    /// robots and their brains and wreckage, the drops, the breath. Its state is saved with a game
    /// and hashed every tick; draw from it only inside a tick and only for what changes the game.</summary>
    public SimRandom Random { get; } = new(DefaultSeed);

    /// <summary>Draws for sound and looks alone (which explosion sample, where a blast's extra flashes
    /// stand, the impact sparks, where the bubbles sound): never <see cref="Random"/>, so audio and
    /// effects can never shift the simulation. Not saved, not hashed.</summary>
    public SimRandom Cosmetic { get; } = new(0xC05E71C);

    /// <summary>DIFFICULTY's four multipliers: the robots' fire rate and the weapons' damage both ways.</summary>
    public Difficulty Difficulty
    {
        get => _difficulty;
        set
        {
            _difficulty = value;
            // A deathmatch has no DIFFICULTY: every shot deals the table's points.
            Weapons.DamageToEnemies = Deathmatch ? 1f : value.DamageToEnemy;
            Weapons.DamageToPlayer = Deathmatch ? 1f : value.DamageToPlayer;
            if (Robots != null)
            {
                Robots.Difficulty = value;
            }
        }
    }

    /// <summary>ROBOT AI: DOS's movement (CLASSIC, the default here; the settings' default is IMPROVED)
    /// or the IMPROVED one, for the robots of this map and the next.</summary>
    public RobotAi RobotAi
    {
        get => _robotAi;
        set
        {
            _robotAi = value;
            if (Robots != null)
            {
                Robots.Ai = value;
                BakeRobotNav();
            }
        }
    }

    private RobotAi _robotAi = RobotAi.Classic;

    /// <summary>Console <c>navmesh on</c>: the walkers' nav meshes and paths drawn, on this map and the next.</summary>
    internal bool ShowRobotNav
    {
        get => _showRobotNav;
        set
        {
            _showRobotNav = value;
            if (Robots != null)
            {
                Robots.ShowNav = value;
            }
        }
    }

    private bool _showRobotNav;

    /// <summary>A network deathmatch arena (<see cref="StartDeathmatch"/>): no robots, no mission,
    /// none of the map's own pickups (the server's items come instead), no DIFFICULTY, and death
    /// is the server's business (no MISSION FAILED); the keys are dead while the soldier is.</summary>
    public bool Deathmatch { get; private set; }

    /// <summary>A deathmatch host's crate drops (an item sprite and where it lies), handed to the
    /// server's item list; null elsewhere (a client drops no item of its own).</summary>
    public Action<int, Vector3>? DeathmatchDrop { get; set; }

    /// <summary>A destroyed record's radial blast (its source record, where, the strength) in a
    /// network game, which owns it: the server's blast strikes the records and every body and is
    /// credited to whoever broke the record, a client's strikes nothing. Null: the weapons' blast
    /// over every target and the soldier, as in the campaign.</summary>
    public Action<int, Vector3, float>? LevelBlast { get; set; }

    /// <summary>The ambient loops the current map runs (source record → sound and place).</summary>
    public IReadOnlyDictionary<int, (int Id, Vector3 At)> Loops => _state?.Loops ?? new Dictionary<int, (int Id, Vector3 At)>();

    /// <summary>
    /// A network game's NEW ROUND: the current map's level back to <paramref name="start"/> (doors,
    /// switches, breakables as the arena started), its breakables targets again, its ambient loops
    /// those of <paramref name="loops"/> (the ones running at the start). The robots, the pickups
    /// and the soldier are left alone (an arena has none of the first two; the server respawns him).
    /// </summary>
    public void ResetLevel(BehaviourSnapshot start, IReadOnlyDictionary<int, (int Id, Vector3 At)> loops)
    {
        if (_state is not { } state || Map is not { } map)
        {
            return;
        }

        foreach (var h in _loops.Values)
        {
            h.Stop();
        }

        _loops.Clear();
        state.Loops.Clear();
        foreach (var off in _targets)
        {
            Weapons.Targets.Unregister(off);
        }

        _targets.Clear();
        Weapons.Clear();
        state.Behaviour.ResetTo(start);
        RegisterTargets(map, state.Behaviour);
        foreach (var (source, (id, at)) in loops)
        {
            state.Loops[source] = (id, at);
            StartLoop(source, id, at);
        }

        StartSpriteLoops(map);

        Log("level reset to its start");
    }

    /// <summary>A deathmatch's other bodies, after the soldier's move each tick: (where he stood,
    /// where the move put him) → where he may stand (<see cref="Net.BodyBlock"/>); null elsewhere.</summary>
    public Func<Vector3, Vector3, Vector3>? DeathmatchBlock { get; set; }

    /// <summary>A deathmatch's use key before the level's (step 9: boarding or leaving a vehicle):
    /// true when it took the press.</summary>
    public Func<bool>? DeathmatchUse { get; set; }

    /// <summary>After MISSION FAILED, restart by itself (scripts, tests); false: hold at
    /// <see cref="SessionPhase.Failed"/> and ask the listener (<see cref="ISessionListener.RestartOffered"/>).</summary>
    public bool AutoRestart { get; set; } = true;

    /// <summary>Lines for the console (map changes, objectives); null: silent.</summary>
    public Action<string>? Logger { get; set; }

    /// <summary>A map's baked block (<see cref="Baking.BlockStore.Load"/>), when there is one: its
    /// collision triangles are used instead of placing the models again.</summary>
    public Func<int, Baking.MapBlock?>? Blocks { get; set; }

    /// <summary>A mission's file through the mods (<see cref="Baking.BlockStore.LoadMission"/>); null
    /// or a null result: generated from the data in memory (<see cref="MissionGenerator"/>).</summary>
    public Func<Mission, MissionFile?>? Missions { get; set; }

    public Mission? Mission { get; private set; }
    /// <summary>The mission file being played (null: a loose map).</summary>
    public MissionFile? MissionFile => _file;
    /// <summary>The map instance being played (null before the first map).</summary>
    public WorldInstance? Instance => _instance;
    /// <summary>The instance the player came from (null: none yet).</summary>
    public WorldInstance? PreviousInstance => _previous;
    /// <summary>The map whose block the current instance draws (today always <see cref="MapNumber"/>).</summary>
    public int BlockMap => _instance is { } i ? Campaign.MissionFile.BlockMap(i) : MapNumber;
    /// <summary>The current instance's song (null: none named; the map's maptype decides).</summary>
    public string? InstanceMusic => _instance is { } i ? Campaign.MissionFile.Music(i) : null;
    /// <summary>The current instance's vehicle (null: none named; the mission table decides).</summary>
    public PlayerMode? InstanceVehicle => _instance is { } i ? Campaign.MissionFile.Vehicle(i) : null;
    public Briefing? Briefing { get; private set; }
    public MissionProgress Progress { get; private set; } = new(0);
    public SessionPhase Phase { get; private set; } = SessionPhase.Playing;

    /// <summary>The banner DOS draws over the frozen frame at the mission's end (FUN_00121fe3:
    /// "welldone.img", or "failed.img" when the failed bit 0x1000 is set), centred on the view
    /// centre; null while playing and after the campaign.</summary>
    public string? BannerImage => Phase switch
    {
        SessionPhase.Complete => CompleteImage,
        SessionPhase.Failed => FailedImage,
        _ => null,
    };
    public int MapNumber { get; private set; }
    public int PreviousMap { get; private set; }
    public MapFile? Map { get; private set; }
    public WldTerrain? Wld { get; private set; }
    /// <summary>The map's static collision: <see cref="World"/> without the movers and breakables (a nav mesh's source).</summary>
    public CollisionWorld? StaticWorld { get; private set; }
    public LevelBehaviour? Behaviour => _state?.Behaviour;
    public LevelCollision.Stats CollisionStats { get; private set; }
    /// <summary>The current map's pickups taken (the level hides these sprites).</summary>
    public IReadOnlySet<int> TakenPickups => _state?.Taken ?? (IReadOnlySet<int>)new HashSet<int>();
    /// <summary>The current map's pickups still lying there: their record and where they stand.</summary>
    public IEnumerable<(int Offset, Vector3 At)> PickupsLeft => _pickups.Select(p => (p.Offset, p.Base));
    /// <summary>What destroyed records left on the current map (pickups and burning wrecks), for
    /// the renderer: the sprite index and its foot.</summary>
    public IReadOnlyList<DroppedSprite> Drops => _state?.Drops ?? (IReadOnlyList<DroppedSprite>)[];
    /// <summary>The robots killed on the current map, by marker offset (the enemy layer fills it).</summary>
    public ISet<int> Dead => _state?.Dead ?? new HashSet<int>();
    /// <summary>Maps entered since the session began (a counter for presenters and tests).</summary>
    public int MapsEntered { get; private set; }
    /// <summary>The location names of SKYNET.EXE's table 0x53d00 (<see cref="LocationNames"/>).</summary>
    public LocationNames Locations { get; }
    /// <summary>The "YOU ENTERED &lt;location&gt;" line of the map just entered (FUN_00136768, on every
    /// entry), for the presenter to queue in colour 0xB3 after the level start empties the status
    /// lines; null when the map has no name.</summary>
    public string? EnteredLine { get; private set; }
    public float Clock => _clock;

    private void Log(string line) => Logger?.Invoke(line);

    // ---------------------------------------------------------------- missions

    /// <summary>Starts mission <paramref name="mission"/> on its first map (or on
    /// <paramref name="map"/>, a side area of it, for <c>--map</c>): the counter from its briefing,
    /// every per-map state dropped, the kit and body taken as the restart snapshot.</summary>
    public void StartMission(Mission mission, int? map = null, bool restart = false)
    {
        Deathmatch = false;
        Difficulty = _difficulty;
        Mission = mission;
        Phase = SessionPhase.Playing;
        _completeAt = _endAt = null;
        ResetClock();
        _file = FileFor(mission);
        Briefing = LoadBriefing(_file.Briefing);
        Progress = Briefing != null ? MissionProgress.From(Briefing) : new MissionProgress(0);
        _maps.Clear();
        _state = null;
        PreviousMap = 0;
        _previous = null;
        var row = Table.IndexOf(mission);
        if (row == 0 && !restart)
        {
            Stats.NewGame(); // the campaign's first mission: a new game (FUN_00134b00)
        }

        BriefingStats = restart ? new StatSheet(null, Stats.CareerAtStart) : Stats.Sheet;
        Stats.Begin(row, restart);
        if (restart && _startKit != null)
        {
            Weapons.Inventory.Restore(_startKit);
            (Soldier.Health, Soldier.Armour) = _startBody;
        }
        else
        {
            // A new mission: full health; the kit carries from the last one.
            Soldier.Reset();
            _startKit = Weapons.Inventory.Save();
            _startBody = (Soldier.Health, Soldier.Armour);
        }

        var start = map is { } side ? InstanceFor(side) : _file.World.Instance(_file.World.Start.Instance)!;
        Log($"mission {mission.Number}: {Describe(start)}, {Progress.Total} objectives{(restart ? " (restart)" : "")}");
        Listener?.MissionStarted(mission, Briefing, restart);
        Enter(start, map != null ? -1 : _file.World.Start.Arrival, null);
    }

    /// <summary>The mission's file: the provider's (a mod's or the system ZIP's), else generated
    /// from the data (kept for the session). One whose start map is not in the data is not used.</summary>
    private MissionFile FileFor(Mission mission)
    {
        if ((Missions?.Invoke(mission) ?? Mods.ModMissions.File(Data.Game, mission.Number)) is { } file && file.World.Instance(file.World.Start.Instance) is { } start
            && Data.LoadMap(Campaign.MissionFile.LayerMap(start)) != null)
        {
            return file;
        }

        if (!_generated.TryGetValue(mission.Number, out var made))
        {
            _generated[mission.Number] = made = MissionGenerator.Build(Data, mission);
        }

        return made;
    }

    /// <summary>MAP.<paramref name="map"/>'s instance in the mission file, else a loose one (id = the number).</summary>
    private WorldInstance InstanceFor(int map) =>
        _file?.InstanceOf(map) ?? Loose(Campaign.MissionFile.DefaultId(map), map);

    private static WorldInstance Loose(string id, int map) =>
        new(id, Campaign.MissionFile.MapName(map), Campaign.MissionFile.MapName(map), null, new Dictionary<string, string>());

    private static string Describe(WorldInstance i)
    {
        var map = Campaign.MissionFile.LayerMap(i);
        return i.Id == Campaign.MissionFile.DefaultId(map) ? $"MAP.{map:D3}" : $"MAP.{map:D3} (instance {i.Id})";
    }

    /// <summary>Starts the game on MAP.<paramref name="map"/>: the mission starting there, else the
    /// one whose decade holds it (entered on that map), else a loose map with no counter.</summary>
    public void StartAt(int map)
    {
        Deathmatch = false;
        if (Table.ForMap(map) is { } m)
        {
            StartMission(m, m.StartMap == map ? null : map);
            return;
        }

        Mission = null;
        _file = null;
        Briefing = null;
        Progress = new MissionProgress(0);
        Stats.Leave();
        _maps.Clear();
        EnterMap(map, -1);
    }

    /// <summary>A network deathmatch on MAP.<paramref name="map"/> (docs/multiplayer.md): a loose
    /// map with <see cref="Deathmatch"/> on, full health, the kit as it is. The soldier stands at
    /// the map's start until the server's spawn moves him. False when the map is not in the data.</summary>
    public bool StartDeathmatch(int map)
    {
        Deathmatch = true;
        Difficulty = _difficulty;
        Mission = null;
        _file = null;
        Briefing = null;
        Progress = new MissionProgress(0);
        Phase = SessionPhase.Playing;
        _completeAt = _endAt = null;
        Stats.Leave();
        Soldier.Reset();
        _maps.Clear();
        _state = null;
        return EnterMap(map, -1);
    }

    /// <summary>Leaves deathmatch mode (the network game ended): the session's DIFFICULTY applies
    /// again, so whatever starts or loads next is a campaign game. The map stays as it is.</summary>
    public void EndDeathmatch()
    {
        Deathmatch = false;
        Difficulty = _difficulty;
    }

    /// <summary>RESTART MISSION: back to the first map with the kit the mission began with.</summary>
    public void RestartMission()
    {
        if (Mission is { } m)
        {
            StartMission(m, restart: true);
        }
        else if (MapNumber != 0)
        {
            // A loose map (no mission): entered afresh with full health.
            Phase = SessionPhase.Playing;
            _completeAt = _endAt = null;
            Soldier.Reset();
            _maps.Clear();
            _state = null;
            Enter(_instance ?? InstanceFor(MapNumber), -1, null);
        }
    }

    /// <summary>The briefing <paramref name="name"/>: MDMDBRIF.BSA's, else a mod's (<see cref="Mods.ModMissions.Briefing"/>).</summary>
    private Briefing? LoadBriefing(string? name)
    {
        if (name == null)
        {
            return null;
        }

        byte[]? bytes = null;
        if (Data.Exists("MDMDBRIF.BSA"))
        {
            using var brif = Data.OpenArchive("MDMDBRIF.BSA");
            bytes = brif.Read(name);
        }

        bytes ??= Mods.ModMissions.Briefing(name);
        return bytes != null ? Briefing.Parse(bytes) : null;
    }

    private void Fail(bool died)
    {
        if (Phase != SessionPhase.Playing)
        {
            return;
        }

        Phase = SessionPhase.Failed;
        _endAt = _clock + FailedHold;
        if (died)
        {
            Sounds?.Play2D(DeathSound);
        }

        // DOS prints no status line here: FAILED.IMG says it (BannerImage).
        Log(died ? "the soldier is dead: MISSION FAILED" : "MISSION FAILED");
        if (Mission is { } m)
        {
            Listener?.MissionFailed(m, died);
        }
    }

    // ---------------------------------------------------------------- maps

    /// <summary>Leaves the current map (its state stays with the mission) and enters
    /// MAP.<paramref name="map"/> at marker set <paramref name="markerSet"/> (−1: its start).
    /// False when the map is not in the data (the exit is refused).</summary>
    public bool EnterMap(int map, int markerSet) => Enter(InstanceFor(map), markerSet, null);

    /// <summary>Enters the mission file's instance <paramref name="id"/> at marker set
    /// <paramref name="markerSet"/>; false when the file has no such instance or its map is not in the data.</summary>
    public bool EnterInstance(string id, int markerSet) =>
        _file?.World.Instance(id) is { } i && Enter(i, markerSet, null);

    /// <summary>The play-test's hot reload (SkyNetEdit, <c>--editor-link</c>): the current map entered afresh
    /// from its files as they are now (the mods mounted again first), the soldier left where he stands; the
    /// map he came from stays the one he came from. False when no map is in play or it no longer loads.</summary>
    public bool ReloadMap()
    {
        if (_instance is not { } instance || Deathmatch)
        {
            return false;
        }

        var (previous, previousInstance) = (PreviousMap, _previous);
        var place = new Placement(Player.Position, Player.Yaw, Player.Pitch, Player.Noclip);
        var kept = _maps.Remove(instance.Id, out var old);
        if (!Enter(instance, -1, place))
        {
            if (kept)
            {
                _maps[instance.Id] = old!;
            }

            return false;
        }

        (PreviousMap, _previous) = (previous, previousInstance);
        return true;
    }

    /// <summary>Where a loaded game puts the soldier instead of a marker set.</summary>
    private readonly record struct Placement(Vector3 Feet, float Yaw, float Pitch, bool Noclip);

    private bool Enter(WorldInstance instance, int markerSet, Placement? place)
    {
        var map = Campaign.MissionFile.LayerMap(instance);
        using var zone = Prof.Zone(GameZones.MapEnter);
        MapFile? file;
        using (Prof.Zone(GameZones.MapParse))
        {
            // Every reader of the map from here (level, block, collision, robots) sees this layer.
            Data.UseLayer(map, instance.Layer);
            file = Data.LoadMap(map);
        }

        if (file == null)
        {
            Log($"MAP.{map:D3} is not in the data: the exit is refused");
            return false;
        }

        LeaveMap();
        if (MapNumber != 0)
        {
            PreviousMap = MapNumber;
            _previous = _instance;
        }

        MapNumber = map;
        _instance = instance;
        Map = file;
        using (Prof.Zone(GameZones.Terrain))
        {
            Wld = file.IsOutdoor ? Data.LoadWld(map) : null;
        }

        var fresh = !_maps.TryGetValue(instance.Id, out var state);
        if (state == null)
        {
            using var build = Prof.Zone(GameZones.BehaviourBuild);
            state = new MapState(new LevelBehaviour(file, Transfrm, Progress, _bridge, random: Random), WaterLevel.Find(file));
            _maps[instance.Id] = state;
        }

        _state = state;
        var behaviour = state.Behaviour;
        using (Prof.Zone(GameZones.CollisionBuild))
        {
            var (world, stats) = Blocks?.Invoke(BlockMap) is { } block
                ? LevelCollision.FromBlock(block, file, Wld, behaviour.IsDynamic)
                : LevelCollision.Build(Data, file, Wld, behaviour.IsDynamic);
            StaticWorld = world.Sources.Aggregate(new CollisionWorld(), (w, s) => w.Add(s));
            world.Add(behaviour.BuildColliders(Data));
            _source.World = world;
            CollisionStats = stats;
        }

        using (Prof.Zone(GameZones.RobotsPlace))
        {
            PlaceRobots(file, state);
        }

        Torpedo = TorpedoDue() ? new TorpedoRide() : null;

        if (place is { } spot)
        {
            Player.Spawn(spot.Feet, spot.Yaw);
            Player.Pitch = spot.Pitch;
            Player.Noclip = spot.Noclip;
        }
        else
        {
            var (feet, yaw, _) = PlayerStart.Find(file, Wld, markerSet);
            feet = PlayerStart.Clear(World, Player.Body, feet);
            Player.Spawn(feet, yaw);
            Player.Noclip = false;
        }

        Player.FallDamage = 0f;
        Player.WaterSurface = state.Water?.Surface;
        Breath.Reset();
        Moon.Reset(NightMoon.SkyOf(map, file));
        using (Prof.Zone(GameZones.BehaviourStart))
        {
            StartBehaviour(state, behaviour, fresh);
            StartSpriteLoops(file);
        }

        Body.Enter(MusicTracks.MapTypeOf(file));
        Weapons.MetalWorld = Body.Surface == PlayerSounds.Metal;

        using (Prof.Zone(GameZones.PickupsPlace))
        {
            RegisterTargets(file, behaviour);
            CollectPickups(file, state);
        }

        MapsEntered++;
        EnteredLine = Locations.EnteredLine(map);
        Log($"enter {Describe(instance)}{(markerSet >= 0 ? $" at marker set {markerSet}" : "")}{(fresh ? "" : " (state kept)")}; " +
            $"{_targets.Count} breakables, {_pickups.Count} pickups");
        Listener?.MapEntered(this);
        return true;
    }

    /// <summary>A fresh map's behaviour starts; one visited before is armed again where it was left.</summary>
    private void StartBehaviour(MapState state, LevelBehaviour behaviour, bool fresh)
    {
        if (fresh)
        {
            behaviour.Start(Player.Position, Player.EyePosition);
        }
        else
        {
            behaviour.ExitRefused();               // the latch of the exit taken out of it
            behaviour.Arm(Player.Position, Player.EyePosition);
            foreach (var (source, (id, at)) in state.Loops)
            {
                StartLoop(source, id, at);
            }
        }
    }

    /// <summary>The map's robots, less the ones killed on an earlier visit in this mission; a
    /// death raises its marker's act in the behaviour and is kept with the map.</summary>
    private void PlaceRobots(MapFile file, MapState state)
    {
        Robots?.Detach();
        Robots = null;
        if (Deathmatch)
        {
            return; // the arena's only opponents are the other players
        }

        var robots = new Robots(_ais, _models, World, Weapons, random: Random)
        {
            Sounds = Sounds, Difficulty = _difficulty, Paths = state.Behaviour.Paths, Cosmetic = Cosmetic, Ai = _robotAi,
            ShowNav = _showRobotNav,
        };
        robots.Populate(file);
        robots.Retire(state.Dead);
        var behaviour = state.Behaviour;
        robots.DeathAct += e => behaviour.RobotAct(e.Act, e.MarkerOffset);
        robots.Died += r =>
        {
            state.Dead.Add(r.MarkerOffset >= 0 ? r.MarkerOffset : r.SpawnSource);
            Stats.Kill();
        };
        Robots = robots;
        BakeRobotNav();
    }

    /// <summary>The walkers' nav meshes, once, when IMPROVED plays (CLASSIC never builds them).</summary>
    private void BakeRobotNav()
    {
        if (_robotAi != RobotAi.Improved || Robots is not { Nav: null } robots)
        {
            return;
        }

        robots.Nav = RobotNav.For(this);
    }

    private void LeaveMap()
    {
        // DOS forgets the automap with the map.
        Seen.Clear();
        foreach (var h in _loops.Values)
        {
            h.Stop();
        }

        _loops.Clear();
        foreach (var off in _targets)
        {
            Weapons.Targets.Unregister(off);
        }

        _targets.Clear();
        _pickups.Clear();
        Weapons.Clear();
        _pendingExit = null;
    }

    /// <summary>An ambient loop (a 0xEE node, a burning sprite) by DOS's 3D rules (handler 0x12a97a).</summary>
    private void StartLoop(int source, int id, Vector3 at)
    {
        if (Sounds?.PlayDosLoop3D(id, at) is { } h)
        {
            _loops[source] = h;
        }
    }

    /// <summary>The burning sprites' loops: at a map's start FUN_0012a900 (v1.01) turns every sprite
    /// with no act whose picture is in the table at 0x4ce00 into a 0xEE node playing that table's
    /// sound (fire.raw 63 or its quieter copy 83). They are the map's own, so they are not saved.</summary>
    private void StartSpriteLoops(MapFile file)
    {
        foreach (var e in file.Entities)
        {
            if (e.Variant == MapEntityVariant.Sprite && e.ActType == 0 && !e.IsMarker
                && SpriteSounds.TryGetValue(e.SpriteIndex, out var id) && !_loops.ContainsKey(e.FileOffset))
            {
                StartLoop(e.FileOffset, id, BehaviourModel.Position(e));
            }
        }
    }

    /// <summary>v1.01 0x4ce00: a sprite picture (bank × 128 + record) → its looping sound.</summary>
    public static readonly IReadOnlyDictionary<int, int> SpriteSounds = new Dictionary<int, int>
    {
        [206 * 128 + 1] = 63, [208 * 128 + 2] = 63, [208 * 128 + 3] = 63,
        [216 * 128 + 5] = 83, [216 * 128 + 11] = 83, [216 * 128 + 12] = 83,
        [218 * 128 + 0] = 83, [218 * 128 + 1] = 83, [218 * 128 + 2] = 63, [218 * 128 + 3] = 63,
    };

    /// <summary>The soldier's own sounds this tick: steps, a landing (DOS's FUN_0012b18c). Cosmetic:
    /// the stride runs with or without a sound bank and changes nothing in the game.</summary>
    private void BodySounds()
    {
        var p = Player;
        if (p.Mover != null || p.Noclip)
        {
            return;
        }

        var ahead = new Vector3(-MathF.Sin(p.Yaw), 0f, -MathF.Cos(p.Yaw));
        var forward = Vector3.Dot(p.Velocity with { Y = 0f }, ahead);
        _bodySounds.Clear();
        Body.Tick(Dt, forward, p.OnFloor, p.OnFloor ? 0f : p.Velocity.Y, p.LandedSpeed, p.Wading && !p.Submerged, p.Submerged, _bodySounds);
        foreach (var b in _bodySounds)
        {
            Sounds?.PlayDos2D(b.Id, b.Overlap);
        }
    }

    private void RegisterTargets(MapFile map, LevelBehaviour behaviour)
    {
        foreach (var e in map.Entities)
        {
            if (e.Variant == MapEntityVariant.Mesh && behaviour.IsDamageable(e.FileOffset))
            {
                Weapons.Targets.Register(e.FileOffset, new RecordTarget(behaviour, e.FileOffset, BehaviourModel.Position(e)));
                _targets.Add(e.FileOffset);
            }
        }
    }

    private void CollectPickups(MapFile map, MapState state)
    {
        foreach (var e in map.Entities)
        {
            if (e.Variant != MapEntityVariant.Sprite || e.IsMarker || e.MarkerType != -1 || state.Taken.Contains(e.FileOffset)
                || !PickupTable.Items.TryGetValue(e.SpriteIndex, out var item))
            {
                continue;
            }

            if (Deathmatch)
            {
                // The server's items (NETLEVEL.PRS) instead: the map's own are hidden, as the Godot
                // port leaves them out of an arena.
                state.Taken.Add(e.FileOffset);
                continue;
            }

            // Where Level draws its foot: the ground outdoors, 16 under the record indoors.
            var y = Wld != null ? (float)Wld.HeightAt(e.X, e.Z) : -e.Y - Level.IndoorSpriteLift;
            _pickups.Add(new Pickup(e.FileOffset, new Vector3(e.X, y, -e.Z), item));
        }

        foreach (var d in state.Drops)
        {
            if (PickupTable.Items.TryGetValue(d.Sprite, out var item))
            {
                _pickups.Add(new Pickup(d.Id, d.At, item));
            }
        }
    }

    /// <summary>A destroyed record's drop (FUN_00124619): one sprite of its destruction type's list
    /// on the ground under it, a pickup when the item table lists it.</summary>
    private void Drop(in DestroyEvent e)
    {
        var sprite = _state != null ? _destruction.PickDrop(e.DestroyType, Random) : -1;
        if (_state == null || sprite < 0)
        {
            return;
        }

        var y = Wld != null ? (float)Wld.HeightAt(e.Position.X, -e.Position.Z) : e.Position.Y;
        if (Deathmatch && PickupTable.Items.ContainsKey(sprite))
        {
            // An arena's items are the server's: the host's drop becomes one of them, a client's none.
            DeathmatchDrop?.Invoke(sprite, new Vector3(e.Position.X, y, e.Position.Z));
            return;
        }

        var drop = new DroppedSprite(_state.NextDrop--, sprite, new Vector3(e.Position.X, y, e.Position.Z));
        _state.Drops.Add(drop);
        if (PickupTable.Items.TryGetValue(sprite, out var item))
        {
            _pickups.Add(new Pickup(drop.Id, drop.At, item));
        }

        Log($"drop: sprite {sprite >> 7}/{sprite & 0x7F} at {drop.At.X:0},{drop.At.Y:0},{drop.At.Z:0}");
    }

    /// <summary>A hit for the statistics: a shot of the soldier's (or his vehicle's) whose impact
    /// damaged something other than him (DOS FUN_0012311f counts the impact once, not its blast).</summary>
    private void CountHit(ShotReport r)
    {
        if (r.Weapon >= 0 && r.What is "hit" or "melee" && r.Target != null && r.Target != Weapons.Player)
        {
            Stats.Hit();
        }
    }

    // ---------------------------------------------------------------- save and load

    /// <summary>The vehicle layer (the app's VehicleRig): a save keeps its ride, a load puts it back
    /// after the map is entered. Null: no vehicles, every save on foot.</summary>
    public IVehicleSaver? Ride { get; set; }

    /// <summary>Runs right after the soldier's move each tick with where he stood before it (the
    /// vehicle rig's map border puts a move that left the fence back).</summary>
    public Action<Vector3>? Moved { get; set; }

    /// <summary>A save can be taken: a map is being played, no doorway is under way, the torpedo
    /// ride is not running, the mission has not ended and it is not a deathmatch.</summary>
    public bool CanSave => Phase == SessionPhase.Playing && _state != null && Map != null && _pendingExit == null && !Deathmatch
        && Torpedo is not { Riding: true } && Soldier.Alive;

    /// <summary>The game as it stands, for a save (<see cref="CanSave"/> first): the mission and its
    /// counter, the map and where the soldier stands, his body and kit (and the kit the mission
    /// began with, for RESTART MISSION), every visited map of the mission and the current map's robots.</summary>
    public SaveGame Capture(DateTime when)
    {
        using var zone = Prof.Zone(GameZones.Capture);
        if (!CanSave || _state == null)
        {
            throw new InvalidOperationException("the game cannot be saved now");
        }

        var maps = _maps.Select(p => (Instance: p.Key, Map: MapOfInstance(p.Key), p.Value))
            .OrderBy(p => p.Map).ThenBy(p => p.Instance, StringComparer.Ordinal)
            .Select(p => new SavedMap(p.Map, p.Value.Behaviour.Snapshot(),
            [.. p.Value.Taken.Order()], [.. p.Value.Dead.Order()],
            [.. p.Value.Loops.OrderBy(l => l.Key).Select(l => new SavedLoop(l.Key, l.Value.Id, l.Value.At))])
        {
            Drops = [.. p.Value.Drops],
            NextDrop = p.Value.NextDrop,
            Instance = p.Instance,
        }).ToArray();
        var startKit = _startKit ?? Weapons.Inventory.Save();
        var startBody = _startKit != null ? _startBody : (Soldier.Health, Soldier.Armour);
        var ride = Ride?.Save();
        return new SaveGame(Data.Game, Mission?.Number ?? 0, MapNumber, PreviousMap, Player.Position, Player.Yaw, Player.Pitch,
            Player.Noclip, ride?.Mode ?? PlayerMode.Foot, Soldier.Health, Soldier.Armour, Weapons.Inventory.Save(), startKit, startBody.Item1, startBody.Item2,
            Progress.Snapshot(), _completeAt is { } due ? MathF.Max(0f, due - _clock) : null, maps, Robots?.Snapshot() ?? [], when, ride)
        {
            Stats = Stats.Save(),
            Pilot = Pilot,
            Instance = _instance?.Id,
            PreviousInstance = _previous?.Id,
            RandomState = Random.State,
            Seen = Seen.Save(),
        };
    }

    /// <summary>The map an instance id of the current mission (or a loose instance, id = its number) runs.</summary>
    private int MapOfInstance(string id) =>
        _file?.World.Instance(id) is { } i ? Campaign.MissionFile.LayerMap(i)
        : _instance is { } cur && cur.Id == id ? Campaign.MissionFile.LayerMap(cur)
        : _previous is { } prev && prev.Id == id ? Campaign.MissionFile.LayerMap(prev)
        : int.TryParse(id, System.Globalization.NumberStyles.None, System.Globalization.CultureInfo.InvariantCulture, out var n) ? n : 0;

    /// <summary>A saved map's instance: the mission file's instance of the id it was saved under
    /// (format 6) when that instance runs the same map, else the instance the file gives its map
    /// number (an older save, or a mission file changed since).</summary>
    private WorldInstance SavedInstance(string? id, int map) =>
        id != null && _file?.World.Instance(id) is { } i && Campaign.MissionFile.LayerMap(i) == map ? i
        : id != null && _file == null ? Loose(id, map)
        : InstanceFor(map);

    /// <summary>
    /// Puts a <see cref="Capture"/>d game back and resumes play on its map: the mission and its
    /// counter, every visited map's state, the soldier where he stood with his body and kit, the
    /// current map's robots. No briefing is shown. Throws <see cref="SaveFormatException"/> when the
    /// save is from the other game or names a mission or map this data does not have; the session is
    /// left as it was then.
    /// </summary>
    public void Load(SaveGame save)
    {
        using var zone = Prof.Zone(GameZones.Restore);
        if (save.Game != Data.Game)
        {
            throw new SaveFormatException($"the save is from {save.Game}, the data is {Data.Game}");
        }

        Mission? mission = null;
        if (save.Mission > 0)
        {
            mission = Table.ByNumber(save.Mission) ?? throw new SaveFormatException($"no mission {save.Mission} in the table");
        }

        var files = new Dictionary<int, MapFile>();
        foreach (var m in save.Maps)
        {
            files[m.Map] = Data.LoadMap(m.Map) ?? throw new SaveFormatException($"MAP.{m.Map:D3} is not in the data");
        }

        if (!files.ContainsKey(save.Map))
        {
            throw new SaveFormatException($"the save holds no state for its own map MAP.{save.Map:D3}");
        }

        var file = mission is { } fm ? FileFor(fm) : null;

        LeaveMap();
        Robots?.Detach();
        Robots = null;
        // A save is always a campaign game (CanSave is false in a deathmatch): a load after one
        // leaves deathmatch mode and takes DIFFICULTY back.
        Deathmatch = false;
        Difficulty = _difficulty;
        Mission = mission;
        _file = file;
        Briefing = LoadBriefing(file?.Briefing);
        Progress = Briefing != null ? MissionProgress.From(Briefing) : new MissionProgress(0);
        Progress.Restore(save.Progress);
        Phase = SessionPhase.Playing;
        ResetClock();
        _completeAt = save.CompleteIn is { } wait ? _clock + wait : null;
        _endAt = null;
        _maps.Clear();
        _state = null;
        foreach (var m in save.Maps)
        {
            var at = SavedInstance(m.Instance, m.Map);
            var behaviour = new LevelBehaviour(files[m.Map], Transfrm, Progress, _bridge, random: Random);
            behaviour.Restore(m.Behaviour);
            var state = new MapState(behaviour, WaterLevel.Find(files[m.Map]));
            state.Taken.UnionWith(m.Taken);
            state.Dead.UnionWith(m.Dead);
            foreach (var l in m.Loops)
            {
                state.Loops[l.Source] = (l.Sound, l.At);
            }

            state.Drops.AddRange(m.Drops);
            state.NextDrop = Math.Min(m.NextDrop, m.Drops.Length > 0 ? m.Drops.Min(d => d.Id) - 1 : -1);

            _maps[at.Id] = state;
        }

        Weapons.Inventory.Restore(save.Kit);
        (Soldier.Health, Soldier.Armour) = (save.Health, save.Armour);
        _startKit = save.StartKit;
        _startBody = (save.StartHealth, save.StartArmour);
        if (save.Pilot is { } pilot)
        {
            Pilot = pilot;
        }

        if (save.Stats is { } stats)
        {
            Stats.Restore(stats);
        }
        else
        {
            // An older save: a fresh row for its mission; the career is unknown.
            Stats.NewGame();
            Stats.Begin(mission is { } row ? Table.IndexOf(row) : -1, restart: false);
        }

        MapNumber = 0;
        _instance = _previous = null;
        Log($"load: mission {save.Mission}, MAP.{save.Map:D3}, {save.Maps.Length} maps, {Progress.Left} objectives left");
        Enter(SavedInstance(save.Instance, save.Map), -1, new Placement(save.Feet, save.Yaw, save.Pitch, save.Noclip));
        PreviousMap = save.PreviousMap;
        _previous = save.PreviousMap > 0 ? SavedInstance(save.PreviousInstance, save.PreviousMap) : null;
        Torpedo = null;
        if (save.Ride is { } ride)
        {
            // The map's entry gave a fresh ride from the mission table (its seat levelled the view);
            // the saved one replaces it.
            Player.Pitch = save.Pitch;
            Ride?.Restore(ride);
        }

        Seen.Restore(save.Seen);
        if (Robots != null && !Robots.Restore(save.Robots))
        {
            Log("load: the robots do not match the map; they start afresh");
        }

        // Last: the generator stands where the saved game's stood, whatever the entry drew.
        if (save.RandomState is { } random)
        {
            Random.State = random;
        }
    }

    /// <summary>The session's clock from 0 (a mission's start, a load): what is timed on it starts
    /// afresh, so a run from here does not depend on how long the session ran before.</summary>
    private void ResetClock()
    {
        _clock = 0f;
        _waterSoundsUntil.Clear();
    }

    // ---------------------------------------------------------------- the tick

    /// <summary>One fixed tick: the soldier moves, the guns fire, the level behaves (use key,
    /// floor, crosshair), lifts carry him, pickups are taken; then a doorway taken changes the map,
    /// and the mission's end is timed.</summary>
    public void Tick(in TickInput tick, in PlayerActions actions, in WeaponInput weapons, bool use)
    {
        KeepPoses();
        _clock += Dt;
        var playing = Phase == SessionPhase.Playing;
        var keys = playing && Torpedo is not { Riding: true } && (!Deathmatch || Soldier.Alive);
        var moved = Player.Position;
        using (Prof.Zone(GameZones.Player))
        {
            WaterTick();
            Player.Step(keys ? tick : default, Dt, actions);
            BodySounds();
            Moved?.Invoke(moved);
            if (DeathmatchBlock is { } block)
            {
                Player.Position = block(moved, Player.Position);
            }
        }

        using (Prof.Zone(GameZones.Weapons))
        {
            Weapons.Tick(keys ? weapons : new WeaponInput(false, false, false), new Shooter(Player.EyePosition, Player.Yaw, Player.Pitch, Player.Velocity), Dt);
        }

        Moon.Tick(Dt);

        using (Prof.Zone(GameZones.Robots))
        {
            Robots?.Tick(Dt, playing ? Player.Position : null);
        }

        if (Player.FallDamage > 0f)
        {
            Soldier.Hurt(Player.FallDamage);
            Player.FallDamage = 0f;
        }

        if (_state is { } state)
        {
            use &= playing;
            if (use && Deathmatch && DeathmatchUse?.Invoke() == true)
            {
                use = false;
            }

            var aimed = use ? Aimed() : -1;
            var probe = new PlayerProbe(Player.Position, Player.EyePosition, Player.OnFloor ? Player.FloorOwner : -1, use, aimed);
            Vector3 carry;
            using (Prof.Zone(GameZones.Behaviour))
            {
                carry = state.Behaviour.Tick(Dt, probe);
            }

            if (carry != Vector3.Zero)
            {
                Player.Position += carry;
            }

            if (playing)
            {
                using var pickups = Prof.Zone(GameZones.Pickups);
                TakePickups(state);
            }
        }

        if (playing && !Soldier.Alive && !Deathmatch)
        {
            Fail(died: true);
        }

        if (_pendingExit is { } exit && playing)
        {
            _pendingExit = null;
            using var exitZone = Prof.Zone(GameZones.Exit);
            TakeExit(exit);
        }
        else if (playing && Torpedo is { Riding: true } ride)
        {
            var arrived = ride.Step(Dt, Player.Position, Player.EyePosition, Player.Yaw, out var sound);
            if (sound)
            {
                Sounds?.Play2D(TorpedoRide.Sound);
            }

            if (arrived)
            {
                if (_file?.Torpedo is { } t && _file.World.Instance(t.To) is { } to)
                {
                    Log($"the torpedo ride ends: {Describe(to)}");
                    Enter(to, t.Arrival, null);
                }
                else
                {
                    Log($"the torpedo ride ends: MAP.{TorpedoRide.ToMap:D3}");
                    EnterMap(TorpedoRide.ToMap, TorpedoRide.ToSet);
                }
            }
        }

        TimeTheEnd();
    }

    /// <summary>A tick's start: what moves keeps its pose as the one the frames glide from to the
    /// tick's (render interpolation, <see cref="Interpolation"/>). Presentation only: nothing the
    /// simulation reads, hashes, saves or sends.</summary>
    public void KeepPoses()
    {
        Robots?.KeepPoses();
        Weapons.KeepPoses();
        Torpedo?.KeepPose();
        if (_state is { } state)
        {
            state.Behaviour.KeepPoses();
            state.Water?.KeepPose();
        }
    }

    /// <summary>A dedicated server's tick (no player of its own): the level behaves (movers travel,
    /// chains and cues run, walk-in triggers are the server's sweep of the other bodies) and the
    /// water glides; the session's own soldier is not stepped and stands nowhere near anything.</summary>
    public void TickServer()
    {
        _clock += Dt;
        if (_state is not { } state)
        {
            return;
        }

        state.Water?.Tick(Dt);
        var nowhere = new Vector3(0f, -1e7f, 0f);
        state.Behaviour.Tick(Dt, new PlayerProbe(nowhere, nowhere));
    }

    /// <summary>The water's frame before the soldier moves (FUN_00121083 then FUN_0012ea6b): the
    /// surface glides, the breath runs, and the sounds play.</summary>
    private void WaterTick()
    {
        if (_state?.Water is not { } water)
        {
            Player.WaterSurface = null;
            return;
        }

        water.Tick(Dt);
        Player.WaterSurface = water.Surface;
        var f = Breath.Tick(Dt, Player.Submerged && Phase == SessionPhase.Playing);
        if (f.WentUnder)
        {
            Player.Velocity = Player.Velocity with { Y = Player.Velocity.Y / WaterBreath.UnderSpeedDivisor };
            WaterSound(WaterBreath.SplashSound);
        }

        if (f.CameUp)
        {
            WaterSound(WaterBreath.SplashSound);
            if (f.GetAir)
            {
                WaterSound(WaterBreath.GetAirSound);
            }
        }

        if (f.Bubbles)
        {
            // FUN_0012ec34: 3D, up to 128 units off the head on each axis.
            var off = new Vector3(Cosmetic.Next(256) - 128, Cosmetic.Next(256) - 128, Cosmetic.Next(256) - 128);
            WaterSound(WaterBreath.BubblesSound, Player.EyePosition + off);
        }

        if (f.Drown)
        {
            WaterSound(WaterBreath.DrownSound);
        }

        if (f.Damage > 0f)
        {
            Soldier.Hurt(f.Damage, silent: true);
        }
    }

    /// <summary>A water sound unless the same one still plays (FUN_0012eee2's test).</summary>
    private void WaterSound(int id, Vector3? at = null)
    {
        if (Sounds == null || _waterSoundsUntil.TryGetValue(id, out var until) && _clock < until)
        {
            return;
        }

        var sound = Sounds.Get(id);
        _waterSoundsUntil[id] = _clock + (sound is { SampleRate: > 0 } ? (float)sound.Frames / sound.SampleRate : 0f);
        if (at is { } p)
        {
            Sounds.Play3D(id, p);
        }
        else
        {
            Sounds.Play2D(id);
        }
    }

    /// <summary>The record under the crosshair within the use ray (−1 none).</summary>
    public int Aimed()
    {
        var dir = PlayerController.Direction(Player.Yaw, Player.Pitch);
        return World.Raycast(Player.EyePosition, dir, ActRules.UseRay, out var hit) ? hit.Owner : -1;
    }

    /// <summary>The torpedo ride is due on entry: the mission file's (its instance entered from the
    /// named one), or DOS's own test on map numbers for a loose map.</summary>
    private bool TorpedoDue() => _file != null
        ? _file.Torpedo is { } t && _instance?.Id == t.At && _previous?.Id == t.After
        : TorpedoRide.Due(MapNumber, PreviousMap);

    private void TakeExit(ExitRequest exit)
    {
        if (_instance != null && _file?.World.Link(_instance.Id, exit.Source) is { } link)
        {
            var to = link.Back ? _previous : link.To != null ? _file.World.Instance(link.To) : null;
            Log($"exit {Describe(_instance)} -> {(to != null ? Describe(to) : "?")} (marker set {link.Arrival})");
            if (to == null || !Enter(to, link.Arrival, null))
            {
                Behaviour?.ExitRefused();
            }

            return;
        }

        var target = exit.Back ? PreviousMap : exit.TargetMap;
        Log($"exit MAP.{MapNumber:D3} -> {(target > 0 ? $"MAP.{target:D3}" : "?")} (marker set {exit.MarkerSet})");
        if (target <= 0 || !EnterMap(target, exit.MarkerSet))
        {
            Behaviour?.ExitRefused();
        }
    }

    private void TakePickups(MapState state)
    {
        for (var i = _pickups.Count - 1; i >= 0; i--)
        {
            var p = _pickups[i];
            var d = Player.Position - p.Base;
            if (new Vector2(d.X, d.Z).Length() > PickupTable.GrabAcross || MathF.Abs(d.Y) > PickupTable.GrabVertical)
            {
                continue;
            }

            _pickups.RemoveAt(i);
            if (p.Offset < 0)
            {
                state.Drops.RemoveAll(d => d.Id == p.Offset);
            }
            else
            {
                state.Taken.Add(p.Offset);
            }

            Apply(p.Item);
        }
    }

    /// <summary>An item's effect (handler 0x11d670): the pool topped up, health, armour, the weapon
    /// owned and selected, the "PICKED UP" line and sound 179; always consumed.</summary>
    public void Apply(in PickupItem item)
    {
        var inv = Weapons.Inventory;
        if (item.Pool >= 0 && item.Amount > 0)
        {
            inv.AddAmmo(item.Pool, item.Amount);
        }

        if (item.HealPercent > 0)
        {
            Soldier.HealPercent(item.HealPercent);
        }

        if (item.Armour > 0)
        {
            Soldier.AddArmour(item.Armour / 65536f);
        }

        if (item.Weapon >= 0)
        {
            inv.Grant(item.Weapon);
            inv.Select(item.Weapon);
        }

        var text = _strings?.Text(item.MessageKey, item.MessageKey) ?? item.MessageKey;
        Sounds?.Play2D(PickupTable.Sound, -4f);
        Log($"pickup: {text}");
        Listener?.Status(text);
    }

    private void TimeTheEnd()
    {
        if (Phase == SessionPhase.Playing && _completeAt is { } at && _clock >= at)
        {
            _completeAt = null;
            Phase = SessionPhase.Complete;
            _endAt = _clock + CompleteHold;
            // DOS prints no status line here: WELLDONE.IMG says it (BannerImage).
            Log("MISSION COMPLETE");
            if (Mission is { } done)
            {
                Listener?.MissionComplete(done);
            }

            return;
        }

        if (_endAt is not { } end || _clock < end)
        {
            return;
        }

        _endAt = null;
        if (Phase == SessionPhase.Failed)
        {
            if (AutoRestart)
            {
                RestartMission();
            }
            else
            {
                Log("RESTART MISSION?");
                Listener?.RestartOffered(Mission);
            }
        }
        else if (Phase == SessionPhase.Complete)
        {
            var after = Mission is not { } m ? null
                : _file != null ? (_file.Next is { } n ? Table.ByNumber(n) : null)
                : Table.After(m);
            if (after is { } next)
            {
                StartMission(next);
            }
            else
            {
                Phase = SessionPhase.CampaignOver;
                Log("the campaign is over");
                Listener?.CampaignComplete();
            }
        }
    }

    /// <summary>The mission counted as done now (the console's <c>win</c>, DOS's "win" cheat): MISSION
    /// COMPLETE after the usual delay, as the last objective would. False when not playing a map.</summary>
    public bool CompleteMission()
    {
        if (Phase != SessionPhase.Playing || _state == null)
        {
            return false;
        }

        _completeAt ??= _clock + CompleteDelay;
        return true;
    }

    /// <summary>Skips the end screen's wait (a key on WELL DONE / FAILED).</summary>
    public void SkipEndScreen()
    {
        if (_endAt != null)
        {
            _endAt = _clock;
            TimeTheEnd();
        }
    }
}

/// <summary>A sprite a destroyed record left (<see cref="GameSession.Drops"/>): its id (negative),
/// sprite index (bank &lt;&lt; 7 | record) and foot in world units.</summary>
public readonly record struct DroppedSprite(int Id, int Sprite, Vector3 At);
