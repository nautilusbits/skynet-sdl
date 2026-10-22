using System.Numerics;
using RetroEng.Net;
using SkyNet.Formats;
using SkyNet.Game.Campaign;
using SkyNet.Game.Vehicles;

namespace SkyNet.Game.Net;

/// <summary>
/// The deathmatch rules, owned by the server (the Godot port's net_game.gd, server half): the
/// roster, health and armour, frags and points, deaths and respawns, the arena's items and their
/// return, the time and frag limits. No network here — <see cref="DmHost"/> turns the events into
/// messages — and no clock of its own: <see cref="Update"/> moves time on, so tests drive it by
/// hand. Bots are players with negative ids (later step); the world is id 0.
/// </summary>
public sealed class DmServer
{
    /// <summary>The first bot id; the next ones go down from it.</summary>
    public const int FirstBotId = -1;

    /// <summary>The host's own player.</summary>
    public const int HostId = 1;

    /// <summary>The Godot port's bot names.</summary>
    public static readonly string[] BotNames =
        ["T-800", "T-600", "HK-UNIT", "SKYNET", "CYBERDYNE", "ENDO", "T-1000", "SERIES 800", "DYSON", "REESE", "CONNOR", "MARCUS"];

    private readonly Dictionary<int, DmPlayer> _players = [];
    private readonly Dictionary<int, DmPickup> _pickups = [];
    private readonly Dictionary<int, DmVehicle> _vehicles = [];
    private readonly Dictionary<int, double> _respawnAt = [];
    private readonly Dictionary<int, double> _pickupRespawnAt = [];
    private readonly Dictionary<int, double> _diedAt = [];
    private readonly HashSet<int> _levelReady = [];
    private readonly Dictionary<int, (DmClass Class, int Avatar)> _pendingClass = [];
    private readonly Dictionary<int, TokenBucket[]> _buckets = [];
    private readonly List<int> _due = [];
    private readonly Random _rng;
    private List<DmSpawn> _spawns = [];
    private int _nextPickupKey = 1;
    private int _botSeq;
    private int _timeSent = -1;

    public DmServer(DmSettings settings, Random? rng = null)
    {
        Settings = settings;
        _rng = rng ?? new Random();
        TimeLeft = settings.RoundSeconds;
    }

    public DmSettings Settings { get; }

    /// <summary>Seconds since the server started (the clock <see cref="Update"/> advances).</summary>
    public double Now { get; private set; }

    /// <summary>Seconds left of a timed round (0 without a time limit).</summary>
    public double TimeLeft { get; private set; }

    public bool MatchRunning { get; private set; } = true;

    public IReadOnlyDictionary<int, DmPlayer> Players => _players;

    public IReadOnlyDictionary<int, DmPickup> Pickups => _pickups;

    public IReadOnlyList<DmSpawn> Spawns => _spawns;

    /// <summary>The arena's jeeps and HKs (step 9), by key (1-based, DOS's vehicle index).</summary>
    public IReadOnlyDictionary<int, DmVehicle> Vehicles => _vehicles;

    // ---- events (DmHost sends them; a test watches them) ----

    public event Action<DmPlayer>? PlayerAdded;
    public event Action<int>? PlayerRemoved;
    /// <summary>A player's health or armour changed: (player, attacker; 0 = a pickup or the world).</summary>
    public event Action<int, int>? HealthChanged;
    /// <summary>(victim, killer, weapon).</summary>
    public event Action<int, int, int>? Died;
    /// <summary>(player, spawn).</summary>
    public event Action<int, DmSpawn>? Respawned;
    /// <summary>(key, taker).</summary>
    public event Action<int, int>? PickupTaken;
    public event Action<int>? PickupSpawned;
    public event Action<DmPickup>? PickupAdded;
    /// <summary>The whole seconds left changed (timed rounds only).</summary>
    public event Action<int>? TimeChanged;
    public event Action<string>? MatchOver;
    public event Action? Restarted;
    public event Action<int, DmClass>? ClassChanged;
    /// <summary>A vehicle's state changed (a seat, its place, its hull, a wreck, a new round).</summary>
    public event Action<DmVehicle, DmVehicleEvent>? VehicleChanged;

    public bool IsBot(int id) => id < 0;

    public bool IsAlive(int id) => _players.TryGetValue(id, out var p) && p.Alive;

    public float MaxHealthOf(int id) => _players.ContainsKey(id) ? DmProtocol.DosHealth : Soldier.MaxHealth;

    public int Humans => _players.Values.Count(p => !p.Bot);

    public int BotCount => _players.Values.Count(p => p.Bot);

    // ---- roster ----

    /// <summary>Adds a human with <paramref name="id"/> (the host 1, clients from 2), under a name
    /// no one else has (a number appended); null when the id is taken or the server is full.</summary>
    /// <param name="avatar">The avatar asked for (1..13); one not of <paramref name="cls"/> is the class's default.</param>
    public DmPlayer? AddHuman(int id, string name, DmClass cls, int avatar = 0)
    {
        if (id <= 0 || _players.ContainsKey(id))
        {
            return null;
        }

        var cap = Settings.MaxPlayers > 0 ? Math.Min(Settings.MaxPlayers, DmProtocol.MaxPeers) : DmProtocol.MaxPeers;
        if (Humans >= cap)
        {
            return null;
        }

        return Add(id, UniqueName(DmProtocol.CleanText(name, DmProtocol.MaxNameLength), "PLAYER"), cls, false, avatar);
    }

    /// <summary>A bot (negative id), spawned at once when the arena is up, in one of its class's avatars.</summary>
    public DmPlayer AddBot(DmClass? cls = null)
    {
        var id = FirstBotId - _botSeq;
        _botSeq++;
        var name = BotNames[(FirstBotId - id) % BotNames.Length];
        if (NameTaken(name))
        {
            name = $"{name}-{FirstBotId - id + 1}";
        }

        var c = cls ?? (DmClass)_rng.Next(2);
        var looks = DmAvatarTable.OfClass(c);
        var p = Add(id, name, c, true, looks[_rng.Next(looks.Count)]);
        if (_spawns.Count > 0)
        {
            Respawn(id);
        }

        return p;
    }

    public void RemovePlayer(int id)
    {
        if (!_players.Remove(id))
        {
            return;
        }

        _respawnAt.Remove(id);
        _diedAt.Remove(id);
        DriverGone(id);
        _levelReady.Remove(id);
        _pendingClass.Remove(id);
        _buckets.Remove(id);
        _travel.Remove(id);
        PlayerRemoved?.Invoke(id);
    }

    private DmPlayer Add(int id, string name, DmClass cls, bool bot, int avatar)
    {
        var p = new DmPlayer { Id = id, Name = name, Class = cls, Avatar = DmAvatarTable.Resolve(avatar, cls), Bot = bot, Health = DmProtocol.DosHealth };
        _players[id] = p;
        PlayerAdded?.Invoke(p);
        return p;
    }

    private string UniqueName(string name, string fallback)
    {
        var b = name.Length > 0 ? name : fallback;
        var n = b;
        for (var i = 2; NameTaken(n); i++)
        {
            n = $"{b}{i}";
        }

        return n;
    }

    private bool NameTaken(string name) => _players.Values.Any(p => string.Equals(p.Name, name, StringComparison.OrdinalIgnoreCase));

    // ---- the arena ----

    /// <summary>The map is up on the server: its spawns, its vehicles (<see cref="PlaceVehicles"/>),
    /// and its items on the marker-100 spots as SKYNET.EXE lays them out (<see cref="DmItemLayout"/>:
    /// one item a spot, no random choice, the NETLEVEL counts per category; what does not fit is
    /// left out).</summary>
    public void SetArena(DmArena arena)
    {
        _spawns = [.. arena.Spawns];
        PlaceVehicles(arena.JeepSpots, arena.HkSpots);
        _pickups.Clear();
        _pickupRespawnAt.Clear();
        _nextPickupKey = 1;
        foreach (var (spot, sprite) in DmItemLayout.Lay(arena.ItemSpots.Count, c => Settings.Items.GetValueOrDefault(c)))
        {
            var key = _nextPickupKey++;
            _pickups[key] = new DmPickup { Key = key, Sprite = sprite, Position = arena.ItemSpots[spot] };
        }
    }

    /// <summary><paramref name="id"/> has the arena loaded: its first spawn, once per player.</summary>
    public void LevelReady(int id)
    {
        if (_players.ContainsKey(id) && _levelReady.Add(id))
        {
            Respawn(id);
        }
    }

    // ---- poses ----

    /// <summary>A pose from its owner (or a bot); the flags as the server knows them come back
    /// (moving / firing are the owner's word, dead is the server's).</summary>
    public int Pose(int id, Vector3 pos, float yaw, float pitch, int flags, float carYaw = float.NaN)
    {
        if (!_players.TryGetValue(id, out var p))
        {
            return flags;
        }

        p.Position = pos;
        p.Yaw = yaw;
        p.Pitch = pitch;
        if (VehicleOf(id) is { } v)
        {
            // DOS has no vehicle pose: the record follows its driver (0x390C7). The heading comes
            // with the pose (the jeep's turret is the view, the car turns on its own).
            v.Position = pos;
            v.Yaw = float.IsFinite(carYaw) ? carYaw : yaw;
        }

        return ServerFlags(id, flags);
    }

    public int ServerFlags(int id, int flags)
    {
        // The scanner only in a HUMAN's hand, on foot (a machine has none, 0x134a9c).
        var human = _players.TryGetValue(id, out var p) && p.Class == DmClass.Human;
        flags &= DmProtocol.FlagMoving | DmProtocol.FlagFiring | (human ? DmProtocol.FlagScanner : 0);
        if (VehicleOf(id) is { } v)
        {
            flags &= ~DmProtocol.FlagScanner;
            flags |= (int)v.Kind << DmProtocol.VehicleShift;
        }

        return IsAlive(id) ? flags : flags | DmProtocol.FlagDead;
    }

    /// <summary>A client's pose, checked: within its budget and a place in an arena.</summary>
    public bool ClientPose(int id, Vector3 pos, float yaw, float pitch, int flags, float carYaw = float.NaN) =>
        ClientPose(id, pos, yaw, pitch, flags, carYaw, out _);

    /// <summary>
    /// A client's pose, checked: within its message budget, a place in an arena, and no farther from
    /// the last accepted pose than its body can have gone (<see cref="TravelGuard"/>, the owner's rule:
    /// a forged pose must not put it next to an item or a button). A move beyond that is clamped to
    /// what the body could have done and logged; a far pose in the second after a respawn (one sent
    /// before the client saw it) is dropped. While dead the body stays where it fell.
    /// <paramref name="accepted"/> is the place the server keeps and relays.
    /// </summary>
    public bool ClientPose(int id, Vector3 pos, float yaw, float pitch, int flags, float carYaw, out Vector3 accepted)
    {
        accepted = pos;
        if (!Gate(id, DmProtocol.Limit.Pose) || !DmProtocol.SanePos(pos) || !float.IsFinite(yaw) || !float.IsFinite(pitch))
        {
            return false;
        }

        if (Travel(id, pos) is not { } place)
        {
            return false;
        }

        accepted = place;
        Pose(id, place, yaw, pitch, flags, carYaw);
        return true;
    }

    // ---- the travel guard ----

    /// <summary>A body's travel budget between accepted poses: horizontal and upward units (at the
    /// top speed times the lag slack) and downward units (a fall), refilled with the server's clock.</summary>
    private sealed class TravelGuard
    {
        public double At, Budget, Down, FreshUntil, LoggedAt = double.NegativeInfinity;
    }

    private readonly Dictionary<int, TravelGuard> _travel = [];

    /// <summary>Lag slack on the top speed (poses bunch up, the clocks differ).</summary>
    public const float TravelSlack = 1.5f;
    /// <summary>Seconds of travel a body may save up (a late burst of poses), plus a fixed margin
    /// (a step-out beside a vehicle, the spawn nudge).</summary>
    public const float TravelBurst = 0.5f, TravelMargin = 64f;
    /// <summary>The downward budget: a long fall (gravity 392 u/s^2, no terminal speed in DOS's figures).</summary>
    public const float FallRate = 3000f;
    /// <summary>After a respawn, a far pose this long is taken for a stale one (sent before the client saw it).</summary>
    public const double RespawnFresh = 1.0;

    /// <summary>The travel guard is on (tests that place bodies by hand turn it off).</summary>
    public bool LimitTravel { get; set; } = true;

    /// <summary>Clamped and dropped client poses so far (the log tells of them; tests count them).</summary>
    public int PosesClamped { get; private set; }
    public int PosesDropped { get; private set; }

    /// <summary>Lines for the host's log (a clamped pose); null: silent.</summary>
    public event Action<string>? Warned;

    /// <summary>The pace of <paramref name="id"/>'s class on foot under the game's CLASSES
    /// (<see cref="DmProtocol.SpeedFactor"/>: BALANCED's TERMINATOR 0.9, else 1).</summary>
    public float SpeedOf(int id) =>
        DmProtocol.SpeedFactor(_players.TryGetValue(id, out var p) ? p.Class : DmClass.Human, Settings.Classes);

    /// <summary>
    /// The top speed of <paramref name="id"/>'s body (units a second, horizontal): on foot DOS's run
    /// with the run sidestep (400 and 310, <see cref="Player.PlayerController"/>) times the class's
    /// pace (<see cref="SpeedOf"/>); a jeep its top speed (683.6), an HK its speed with the run sidestep (800 and 550).
    /// </summary>
    public float TopSpeed(int id)
    {
        static float Hyp(float a, float b) => MathF.Sqrt(a * a + b * b);
        return VehicleOf(id)?.Kind switch
        {
            VehicleMode.Jeep => VehicleController.JeepMaxSpeed,
            VehicleMode.Hk => Hyp(VehicleController.HkSpeed, VehicleController.HkStrafeRun),
            _ => Hyp(Player.PlayerController.RunSpeed, Player.PlayerController.StrafeRunSpeed)
                * SpeedOf(id),
        };
    }

    /// <summary>Where <paramref name="id"/>'s pose at <paramref name="pos"/> may put it: the pose itself
    /// within the budget, else as far towards it as the budget goes; null to drop it.</summary>
    private Vector3? Travel(int id, Vector3 pos)
    {
        var p = _players[id];
        if (!LimitTravel)
        {
            return pos;
        }

        var g = TravelOf(id);
        var rate = TopSpeed(id) * TravelSlack;
        g.Budget = Math.Min(g.Budget + (Now - g.At) * rate, rate * TravelBurst + TravelMargin);
        g.Down = Math.Min(g.Down + (Now - g.At) * FallRate, FallRate * TravelBurst + TravelMargin);
        g.At = Now;
        if (!p.Alive)
        {
            return p.Position; // the body lies where it fell until the server's respawn
        }

        var d = pos - p.Position;
        var across = new Vector2(d.X, d.Z).Length() + MathF.Max(0f, d.Y);
        var down = MathF.Max(0f, -d.Y);
        if (across <= g.Budget && down <= g.Down)
        {
            g.Budget -= across;
            g.Down -= down;
            return pos;
        }

        if (Now < g.FreshUntil)
        {
            PosesDropped++;
            return null;
        }

        var f = MathF.Min(across > 0f ? (float)(g.Budget / across) : 1f, down > 0f ? (float)(g.Down / down) : 1f);
        f = Math.Clamp(f, 0f, 1f);
        var place = p.Position + d * f;
        g.Budget = Math.Max(0.0, g.Budget - across * f);
        g.Down = Math.Max(0.0, g.Down - down * f);
        PosesClamped++;
        if (Now - g.LoggedAt >= 1.0)
        {
            g.LoggedAt = Now;
            Warned?.Invoke(string.Create(System.Globalization.CultureInfo.InvariantCulture,
                $"{p.Name}'s pose moved {d.Length():0} units at once (top speed {TopSpeed(id):0}/s): clamped to {(place - p.Position).Length():0}"));
        }

        return place;
    }

    private TravelGuard TravelOf(int id)
    {
        if (!_travel.TryGetValue(id, out var g))
        {
            g = new TravelGuard { At = Now };
            _travel[id] = g;
        }

        return g;
    }

    /// <summary>The server put the body somewhere new (a spawn): the budget starts afresh there, and
    /// far poses in the next moment are stale ones.</summary>
    private void Teleported(int id)
    {
        var g = TravelOf(id);
        g.At = Now;
        g.Budget = TravelMargin;
        g.Down = TravelMargin;
        g.FreshUntil = Now + RespawnFresh;
    }

    /// <summary>A client's shot, to be drawn by everyone else: within budget, from a real place
    /// along a real direction.</summary>
    public bool ClientFire(int id, int weapon, Vector3 from, Vector3 dir)
    {
        if (!Gate(id, DmProtocol.Limit.Fire) || !DmProtocol.SanePos(from) || !float.IsFinite(dir.X) || !float.IsFinite(dir.Y)
            || !float.IsFinite(dir.Z) || dir.LengthSquared() < 0.01f)
        {
            return false;
        }

        _players[id].Weapon = weapon;
        return true;
    }

    // ---- damage ----

    /// <summary>A hit a client says it scored (the shooter detects hits). Where a forged one stops:
    /// an admitted shooter alive (or killed a moment ago with a shot in the air), a real positive
    /// amount capped at <see cref="DmProtocol.MaxHitDamage"/>, a victim within ray range, and a
    /// damage budget a second.</summary>
    public bool ClientHit(int id, int victim, float damage, int weapon)
    {
        if (!Gate(id, victim == id ? DmProtocol.Limit.SelfHit : DmProtocol.Limit.Hit) || !_players.TryGetValue(victim, out var v)
            || !float.IsFinite(damage) || damage <= 0f)
        {
            return false;
        }

        if (!IsAlive(id) && !InShotGrace(id))
        {
            return false;
        }

        damage = MathF.Min(damage, DmProtocol.MaxHitDamage);
        if (victim != id)
        {
            if (Vector3.Distance(_players[id].Position, v.Position) > DmProtocol.MaxHitRange)
            {
                return false;
            }

            if (!Bucket(id, DmProtocol.Limit.Damage).Take(Now, MathF.Min(damage, MaxHealthOf(victim))))
            {
                return false;
            }
        }

        return Hit(victim, damage, id, weapon);
    }

    /// <summary>Killed a moment ago: a rocket still in the air when its shooter died counts.</summary>
    public bool InShotGrace(int id) => Now - _diedAt.GetValueOrDefault(id, double.NegativeInfinity) <= DmProtocol.DeadShotGrace;

    /// <summary>A hit the server trusts (its own player, a bot, the world): through the armour,
    /// a death at zero. False when it did nothing (no round, no such player, already dead).</summary>
    public bool Hit(int victim, float damage, int attacker, int weapon)
    {
        if (!MatchRunning || !_players.TryGetValue(victim, out var v) || !v.Alive || !float.IsFinite(damage) || damage <= 0f)
        {
            return false;
        }

        // DOS scores on the victim's machine (FUN_00121313): a hit that does not kill is worth
        // scoring_hit to the attacker, the killing blow scoring_kill and a kill instead (no hit).
        var scorer = attacker != victim && _players.TryGetValue(attacker, out var a) ? a : null;

        // In a vehicle the hull takes the whole hit and the driver nothing (DOS 0x122b52 hands it
        // to 0x13114a, no armour); a wreck throws the driver out alive.
        if (VehicleOf(victim) is { } ride)
        {
            WearHull(ride, damage);
            Score(scorer, Settings.ScoringHit);
            return true;
        }

        ApplyDamage(v, damage);
        HealthChanged?.Invoke(victim, attacker);
        if (v.Health <= 0f)
        {
            Kill(victim, attacker, weapon);
        }
        else
        {
            Score(scorer, Settings.ScoringHit);
        }

        return true;
    }

    private void Score(DmPlayer? p, int points)
    {
        if (p != null)
        {
            p.Points += points;
            CheckGoal();
        }
    }

    /// <summary>DOS armour (handler 0x122652, as <see cref="Soldier.Hurt"/>): it wears by 82/65536
    /// per point of the hit and lets (1 − armour) of it through.</summary>
    public static void ApplyDamage(DmPlayer p, float points)
    {
        p.Armour = MathF.Max(0f, p.Armour - Soldier.ArmourWear * points);
        p.Health = MathF.Max(0f, p.Health - (1f - p.Armour) * points);
    }

    private void Kill(int victim, int killer, int weapon)
    {
        var v = _players[victim];
        v.Alive = false;
        v.Health = 0f;
        // FUN_00122b52: every death is one more and costs scoring_death, whoever caused it.
        v.Deaths++;
        v.Points -= Settings.ScoringDeath;
        _diedAt[victim] = Now;
        DriverGone(victim);
        // Only yourself, the world (0) or someone already gone make it a suicide: no kill for anybody
        // and nothing more lost (DOS's killer 0). Bots are negative ids, real killers.
        if (killer != victim && killer != 0 && _players.TryGetValue(killer, out var k))
        {
            k.Frags++;
            k.Points += Settings.ScoringKill;
        }

        _respawnAt[victim] = Now + DmProtocol.RespawnDelay;
        Died?.Invoke(victim, killer, weapon);
        CheckGoal();
    }

    /// <summary>
    /// The host's goal check (SKYNET.EXE v1.01 FUN_00121370, every frame): POINTS — the leader's
    /// points, KILLS — the leader's kills, LIVES — the most deaths anybody has, against the goal's
    /// limit; at the limit the round is over for everybody (0x121504: game-over bit, net message
    /// 0xc). TIME runs out in <see cref="Update"/>. A limit of 0 never ends it.
    /// </summary>
    public void CheckGoal()
    {
        var limit = Settings.GoalLimit;
        if (!MatchRunning || limit <= 0 || Settings.Goal == DmGoal.Time || _players.Count == 0)
        {
            return;
        }

        var lead = Rank(_players.Values, Settings.Goal)[0];
        switch (Settings.Goal)
        {
            case DmGoal.Points when lead.Points >= limit:
                EndMatch($"{lead.Name} REACHED {limit} POINTS");
                break;
            case DmGoal.Kills when lead.Frags >= limit:
                EndMatch($"{lead.Name} REACHED {limit} KILLS");
                break;
            case DmGoal.Lives when _players.Values.MaxBy(p => p.Deaths) is { } worst && worst.Deaths >= limit:
                EndMatch($"{worst.Name} LOST {limit} LIVES");
                break;
        }
    }

    // ---- respawn ----

    /// <summary>Back into the arena at the best spawn, full health, no armour, wearing a class
    /// picked during the last life.</summary>
    public void Respawn(int id)
    {
        if (!_players.TryGetValue(id, out var p) || !MatchRunning)
        {
            return;
        }

        _respawnAt.Remove(id);
        if (_pendingClass.Remove(id, out var next) && (next.Class != p.Class || next.Avatar != p.Avatar))
        {
            (p.Class, p.Avatar) = next;
            ClassChanged?.Invoke(id, next.Class);
        }

        var sp = PickSpawn();
        p.Health = DmProtocol.DosHealth;
        p.Armour = 0f;
        p.Alive = true;
        p.Position = sp.Feet;
        p.Yaw = sp.Yaw;
        Teleported(id);
        Respawned?.Invoke(id, sp);
    }

    /// <summary>The spawn farthest from every living player, with up to 300 units of jitter so
    /// ties do not repeat (DOS picks at random among its pairs; distance keeps spawn kills rare).</summary>
    public DmSpawn PickSpawn()
    {
        if (_spawns.Count == 0)
        {
            return new DmSpawn(new Vector3(32768f, 800f, -32768f), 0f);
        }

        var best = _spawns[_rng.Next(_spawns.Count)];
        var bestD = -1.0;
        foreach (var sp in _spawns)
        {
            var d = 1e12;
            foreach (var p in _players.Values)
            {
                if (p.Alive)
                {
                    d = Math.Min(d, Vector3.Distance(p.Position, sp.Feet));
                }
            }

            d += _rng.NextDouble() * 300.0;
            if (d > bestD)
            {
                bestD = d;
                best = sp;
            }
        }

        return best;
    }

    /// <summary>A class and avatar for the next life (worn from the next spawn). An avatar not of the
    /// class (or 0) keeps the player's own when that is of the class, else the class's default.</summary>
    public bool ClientSetClass(int id, DmClass cls, int avatar = 0)
    {
        if (!Gate(id, DmProtocol.Limit.Class) || (byte)cls > 1)
        {
            return false;
        }

        var own = _players.TryGetValue(id, out var p) ? p.Avatar : 0;
        _pendingClass[id] = (cls, DmAvatarTable.Resolve(DmAvatarTable.Resolve(avatar, cls) == avatar ? avatar : own, cls));
        return true;
    }

    // ---- pickups ----

    /// <summary>A client says it walked over <paramref name="key"/>: granted only when the server's
    /// last pose of it stands within reach.</summary>
    public bool ClientPickup(int id, int key)
    {
        if (!Gate(id, DmProtocol.Limit.Pickup) || !_pickups.TryGetValue(key, out var item))
        {
            return false;
        }

        return Vector3.Distance(_players[id].Position, item.Position) <= DmProtocol.PickupReach && Pickup(key, id);
    }

    /// <summary><paramref name="by"/> takes <paramref name="key"/>: health and armour are server
    /// state (DOS percentages and 1/65536 armour), ammo and weapons the taker's own. A replenished
    /// arena puts it back after <see cref="DmProtocol.PickupRespawn"/> s; a drop never comes back.</summary>
    public bool Pickup(int key, int by)
    {
        if (!_pickups.TryGetValue(key, out var item) || item.Taken || !_players.TryGetValue(by, out var p) || !p.Alive)
        {
            return false;
        }

        item.Taken = true;
        if (PickupTable.Items.TryGetValue(item.Sprite, out var fx) && (fx.HealPercent > 0 || fx.Armour > 0))
        {
            var max = DmProtocol.DosHealth;
            if (fx.HealPercent > 0)
            {
                p.Health = MathF.Min(max, p.Health + max * fx.HealPercent / 100f);
            }

            if (fx.Armour > 0)
            {
                p.Armour = Math.Clamp(p.Armour + fx.Armour / 65536f, 0f, 1f);
            }

            HealthChanged?.Invoke(by, 0);
        }

        if (Settings.Replenish && !item.Drop)
        {
            _pickupRespawnAt[key] = Now + DmProtocol.PickupRespawn;
        }

        PickupTaken?.Invoke(key, by);
        return true;
    }

    /// <summary>A broken prop left item <paramref name="sprite"/>: one more server pickup, taken
    /// once, never back. Null for a sprite that is no item or a place that is none.</summary>
    public DmPickup? Drop(int sprite, Vector3 pos)
    {
        if (!PickupTable.Items.ContainsKey(sprite) || !DmProtocol.SanePos(pos))
        {
            return null;
        }

        var p = new DmPickup { Key = _nextPickupKey++, Sprite = sprite, Position = pos, Drop = true };
        _pickups[p.Key] = p;
        PickupAdded?.Invoke(p);
        return p;
    }

    // ---- vehicles (step 9) ----

    /// <summary>DOS's heading step between parked vehicles (0x130c5b: 0x80 of 2048 a vehicle).</summary>
    public const float VehicleYawStep = 0x80 / 2048f * MathF.Tau;

    /// <summary>Parks the arena's vehicles as SKYNET.EXE v1.01 0x130b00 does: the first
    /// min(<c>jeeps</c>, jeep spots) jeep spots (marker 101) and then the first min(<c>hks</c>, HK
    /// spots) HK spots (marker 102), in marker order, no random choice; the heading starts at 0
    /// and turns 0x80 more for each vehicle across the whole list.</summary>
    public void PlaceVehicles(IReadOnlyList<Vector3> jeepSpots, IReadOnlyList<Vector3> hkSpots)
    {
        _vehicles.Clear();
        var key = 1;
        foreach (var (spots, count, kind) in new[]
        {
            (jeepSpots, Settings.Items.GetValueOrDefault("jeeps"), VehicleMode.Jeep),
            (hkSpots, Settings.Items.GetValueOrDefault("hks"), VehicleMode.Hk),
        })
        {
            for (var i = 0; i < Math.Min(count, spots.Count); i++)
            {
                var yaw = WrapAngle((key - 1) * VehicleYawStep);
                var v = new DmVehicle { Key = key, Kind = kind, Home = spots[i], HomeYaw = yaw };
                Park(v, v.Home, yaw, whole: true);
                _vehicles[key++] = v;
            }
        }
    }

    /// <summary>The vehicle <paramref name="id"/> drives; null on foot.</summary>
    public DmVehicle? VehicleOf(int id)
    {
        if (id == 0)
        {
            return null;
        }

        foreach (var v in _vehicles.Values)
        {
            if (v.Driver == id)
            {
                return v;
            }
        }

        return null;
    }

    /// <summary>A client's use key on vehicle <paramref name="key"/>: within its budget and the
    /// server's last pose of it within <see cref="DmProtocol.VehicleReach"/> of the vehicle.</summary>
    public bool ClientEnterVehicle(int id, int key) =>
        Gate(id, DmProtocol.Limit.Vehicle) && _vehicles.TryGetValue(key, out var v)
        && Vector3.Distance(_players[id].Position, v.Position) <= DmProtocol.VehicleReach && EnterVehicle(id, key);

    /// <summary><paramref name="id"/> takes the seat of <paramref name="key"/>: one driver a vehicle,
    /// the first asker's (DOS's occupied bit, 0x130f01); alive, on foot, the vehicle whole.</summary>
    public bool EnterVehicle(int id, int key)
    {
        if (!MatchRunning || !IsAlive(id) || VehicleOf(id) != null || !_vehicles.TryGetValue(key, out var v) || v.Driver != 0 || v.Wrecked)
        {
            return false;
        }

        v.Driver = id;
        _players[id].Position = v.Position;
        VehicleChanged?.Invoke(v, DmVehicleEvent.Changed);
        return true;
    }

    /// <summary>A client climbs out at <paramref name="pos"/> (where the vehicle stays): a wild or
    /// far-off place is replaced by the server's last pose of it (net_game.gd _srv_client_vehicle_exit).</summary>
    public bool ClientLeaveVehicle(int id, Vector3 pos, float yaw)
    {
        if (!Gate(id, DmProtocol.Limit.Vehicle))
        {
            return false;
        }

        var known = _players[id].Position;
        if (!DmProtocol.SanePos(pos) || Vector3.Distance(pos, known) > DmProtocol.VehicleReach)
        {
            pos = known;
        }

        return LeaveVehicle(id, pos, float.IsFinite(yaw) ? yaw : 0f);
    }

    /// <summary><paramref name="id"/> leaves its seat; the vehicle stays parked at
    /// <paramref name="pos"/>, heading <paramref name="yaw"/>, for good (DOS 0x130e36: an abandoned
    /// vehicle never goes back to its marker).</summary>
    public bool LeaveVehicle(int id, Vector3 pos, float yaw)
    {
        if (VehicleOf(id) is not { } v)
        {
            return false;
        }

        Park(v, pos, yaw, whole: false);
        VehicleChanged?.Invoke(v, DmVehicleEvent.Changed);
        return true;
    }

    /// <summary>The hull wears 1:1 (0x13114a). At zero the vehicle blows up and stays a wreck for the
    /// round (DOS never clears the destroyed bit 8 and has no vehicle respawn); the driver is put out
    /// alive where it stood, as DOS forces the exit.</summary>
    private void WearHull(DmVehicle v, float damage)
    {
        v.Hull = MathF.Max(0f, v.Hull - damage);
        if (v.Hull <= 0f)
        {
            v.Wrecked = true;
            v.Driver = 0;
        }

        VehicleChanged?.Invoke(v, v.Wrecked ? DmVehicleEvent.Wrecked : DmVehicleEvent.Changed);
    }

    /// <summary>A driver died or left: the vehicle stays where it was, free for anybody.</summary>
    private void DriverGone(int id)
    {
        if (VehicleOf(id) is { } v)
        {
            v.Driver = 0;
            VehicleChanged?.Invoke(v, DmVehicleEvent.Changed);
        }
    }

    private static void Park(DmVehicle v, Vector3 at, float yaw, bool whole)
    {
        v.Driver = 0;
        v.Position = at;
        v.Yaw = yaw;
        if (whole)
        {
            v.Hull = Hull.PointsFor(v.Kind);
            v.Wrecked = false;
        }
    }

    private static float WrapAngle(float a)
    {
        a %= MathF.Tau;
        return a > MathF.PI ? a - MathF.Tau : a;
    }

    // ---- chat ----

    /// <summary>A client's chat line: within budget, cleaned; null to drop.</summary>
    public string? ClientChat(int id, string text)
    {
        if (!Gate(id, DmProtocol.Limit.Chat))
        {
            return null;
        }

        var t = DmProtocol.CleanText(text, DmProtocol.MaxChatLength);
        return t.Length > 0 ? t : null;
    }

    // ---- the round ----

    public void EndMatch(string reason)
    {
        if (!MatchRunning)
        {
            return;
        }

        MatchRunning = false;
        MatchOver?.Invoke(reason);
    }

    /// <summary>A new round on the same map: scores wiped, the drops gone, every item back,
    /// everybody respawned.</summary>
    public void Restart()
    {
        foreach (var p in _players.Values)
        {
            p.Frags = 0;
            p.Deaths = 0;
            p.Points = 0;
            p.Alive = false;
        }

        TimeLeft = Settings.RoundSeconds;
        _timeSent = -1;
        MatchRunning = true;
        foreach (var k in _pickups.Where(kv => kv.Value.Drop).Select(kv => kv.Key).ToList())
        {
            _pickups.Remove(k);
        }

        foreach (var p in _pickups.Values)
        {
            p.Taken = false;
        }

        _pickupRespawnAt.Clear();
        _respawnAt.Clear();
        Restarted?.Invoke();
        foreach (var v in _vehicles.Values)
        {
            Park(v, v.Home, v.HomeYaw, whole: true);
            VehicleChanged?.Invoke(v, DmVehicleEvent.Returned);
        }

        foreach (var id in _players.Keys.ToList())
        {
            Respawn(id);
        }
    }

    /// <summary>Moves the clock on by <paramref name="dt"/> seconds: respawns and item returns that
    /// are due, the round's time.</summary>
    public void Update(double dt)
    {
        Now += dt;
        if (_respawnAt.Count > 0)
        {
            _due.Clear();
            _due.AddRange(_respawnAt.Where(kv => Now >= kv.Value).Select(kv => kv.Key));
            foreach (var id in _due)
            {
                Respawn(id);
            }
        }

        if (_pickupRespawnAt.Count > 0)
        {
            _due.Clear();
            _due.AddRange(_pickupRespawnAt.Where(kv => Now >= kv.Value).Select(kv => kv.Key));
            foreach (var key in _due)
            {
                _pickupRespawnAt.Remove(key);
                if (_pickups.TryGetValue(key, out var p))
                {
                    p.Taken = false;
                    PickupSpawned?.Invoke(key);
                }
            }
        }

        if (MatchRunning && Settings.RoundSeconds > 0)
        {
            TimeLeft = Math.Max(TimeLeft - dt, 0.0);
            var sec = (int)Math.Ceiling(TimeLeft);
            if (sec != _timeSent)
            {
                _timeSent = sec;
                TimeChanged?.Invoke(sec);
            }

            if (TimeLeft <= 0.0)
            {
                EndMatch("TIME LIMIT REACHED");
            }
        }
    }

    /// <summary>The scoreboard's order for the round's goal (<see cref="Rank"/>).</summary>
    public List<DmPlayer> Scoreboard() => Rank(_players.Values, Settings.Goal);

    /// <summary>
    /// DOS's ranking (FUN_00121531 / FUN_0012159a: three keys, all best first, then the name): POINTS
    /// and TIME games by points, kills, fewest deaths; KILLS by kills, fewest deaths, points; LIVES by
    /// fewest deaths, kills, points. The id breaks a tie of names.
    /// </summary>
    public static List<DmPlayer> Rank(IEnumerable<DmPlayer> players, DmGoal goal)
    {
        var by = goal switch
        {
            DmGoal.Kills => players.OrderByDescending(p => p.Frags).ThenBy(p => p.Deaths).ThenByDescending(p => p.Points),
            DmGoal.Lives => players.OrderBy(p => p.Deaths).ThenByDescending(p => p.Frags).ThenByDescending(p => p.Points),
            _ => players.OrderByDescending(p => p.Points).ThenByDescending(p => p.Frags).ThenBy(p => p.Deaths),
        };
        return [.. by.ThenBy(p => p.Name, StringComparer.Ordinal).ThenBy(p => p.Id)];
    }

    // ---- input budgets ----

    /// <summary>May admitted player <paramref name="id"/> do <paramref name="kind"/> now?</summary>
    public bool Gate(int id, DmProtocol.Limit kind) => _players.ContainsKey(id) && Bucket(id, kind).Take(Now);

    private TokenBucket Bucket(int id, DmProtocol.Limit kind)
    {
        if (!_buckets.TryGetValue(id, out var list))
        {
            list = [.. DmProtocol.Limits.Select(l => new TokenBucket(l.Rate, l.Burst, Now))];
            _buckets[id] = list;
        }

        return list[(int)kind];
    }
}
