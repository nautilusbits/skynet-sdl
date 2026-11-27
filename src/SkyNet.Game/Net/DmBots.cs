using System.Numerics;
using RetroEng.Physics;
using SkyNet.Game.Weapons;

namespace SkyNet.Game.Net;

/// <summary>
/// The server's bots (step 7 of docs/multiplayer.md; the Godot port's dm_game.gd gives each bot
/// avatar a bot_brain.gd on the server): one <see cref="BotBrain"/> per bot player of the
/// <see cref="DmHost"/>'s server, stepped at a fixed 60 Hz on the host's clock
/// (<see cref="DmHost.Update"/> calls <see cref="Step"/>). Their poses, shots, hits and pickups go
/// through the same host calls as the host's own player, so clients see them as any other body.
/// A bot's brain follows its player: made when the server adds it, put at the server's spawn on
/// every respawn, idle while dead, gone when removed.
/// </summary>
public sealed class DmBots : IBotOutput
{
    public const float Tick = 1f / 60f;

    /// <summary>Goal picks a tick for all bots: a round's start spreads its picks over ticks.</summary>
    private const int PicksPerTick = 2;

    /// <summary>Ticks a slow frame is caught up at most: more would make the next frame slow too.</summary>
    private const int MaxCatchUp = 4;

    private readonly DmHost _host;
    private readonly CollisionWorld _world;
    private readonly WeaponTable _table;
    private int _seed;
    private readonly Navigation.NavMesh? _nav;
    private readonly BotRides _rides;
    private readonly BotClaims _claims = new() { PicksPerTick = PicksPerTick };
    private readonly SortedDictionary<int, BotBrain> _brains = new(Comparer<int>.Create((a, b) => b.CompareTo(a)));
    /// <summary>The brains in <see cref="_brains"/>' order, walked every tick (a sorted dictionary's walk allocates).</summary>
    private readonly List<BotBrain> _order = [];
    private double _accum;

    /// <param name="seed">Seeds every brain (with its id); null = a random game.</param>
    /// <param name="nav">The arena's mesh the bots path on (<see cref="DmNav"/>); null: they steer straight.</param>
    public DmBots(DmHost host, CollisionWorld world, WeaponTable table, int skill, int? seed = null, Navigation.NavMesh? nav = null)
        : this(host, world, table, skill, seed, nav, null)
    {
    }

    /// <param name="rides">The jeep's mesh and the HK's ceiling (<see cref="DmNav.Rides"/>); null: none.</param>
    /// <inheritdoc cref="DmBots(DmHost, CollisionWorld, WeaponTable, int, int?, Navigation.NavMesh?)"/>
    internal DmBots(DmHost host, CollisionWorld world, WeaponTable table, int skill, int? seed, Navigation.NavMesh? nav,
        BotRides? rides)
    {
        _host = host;
        _nav = nav;
        _rides = rides ?? BotRides.None;
        _world = world;
        _table = table;
        Skill = Math.Clamp(skill, 0, BotBrain.Skills.Length - 1);
        _seed = seed ?? Random.Shared.Next();
        var server = host.Server;
        server.PlayerAdded += p =>
        {
            if (p.Bot)
            {
                Brain(p.Id);
            }
        };
        server.PlayerRemoved += id =>
        {
            _brains.Remove(id);
            _claims.Release(id);
            Reorder();
        };
        // A dead bot heads for no vehicle.
        server.Died += (victim, _, _) => _claims.Release(victim);
        // A hit turns the bot it struck round (the attacker's place, after its reaction).
        server.Hurt += (victim, attacker) =>
        {
            if (_brains.TryGetValue(victim, out var b))
            {
                b.Hurt(attacker, server);
            }
        };
        // A weapon item a bot takes goes in its hand.
        server.PickupTaken += (key, by) =>
        {
            if (server.IsBot(by) && _brains.TryGetValue(by, out var b) && server.Pickups.TryGetValue(key, out var item))
            {
                b.Took(item, server.Now);
            }
        };
        server.Respawned += (id, sp) =>
        {
            if (server.IsBot(id))
            {
                var b = Brain(id);
                b.Body.ClassSpeed = server.SpeedOf(id); // the class may have changed for this life
                b.Spawn(sp.Feet, sp.Yaw);
            }
        };
        foreach (var p in server.Players.Values.Where(p => p.Bot))
        {
            var b = Brain(p.Id);
            if (p.Alive)
            {
                b.Spawn(p.Position, p.Yaw);
            }
        }
    }

    public int Skill { get; }

    /// <summary>The seed every brain's stream derives from: the one given, else one drawn once and
    /// kept (written into a host's network demo, so the match can be played again).</summary>
    internal int Seed => _seed;

    /// <summary>A new seed for every brain's stream (a dedicated server's next round).</summary>
    internal void Reseed(int seed)
    {
        _seed = seed;
        foreach (var b in _order)
        {
            b.Reseed(Stream(b.Id));
        }
    }

    private Random Stream(int id) => new(unchecked(_seed * 7919 + id));

    private void Reorder()
    {
        _order.Clear();
        _order.AddRange(_brains.Values);
    }

    public IReadOnlyDictionary<int, BotBrain> Brains => _brains;

    /// <summary>The mesh the bots path on; null: they steer straight.</summary>
    public Navigation.NavMesh? Nav => _nav;

    /// <summary>The jeep's mesh and the HK's ceiling the bots drive with.</summary>
    internal BotRides Rides => _rides;

    /// <summary>Bots for <paramref name="host"/> on <paramref name="world"/>: the settings' count at
    /// the settings' skill, attached to the host (its <see cref="DmHost.Update"/> steps them).
    /// Call it once the arena is up (the bots spawn at once).</summary>
    public static DmBots Start(DmHost host, CollisionWorld world, WeaponTable table, int? seed = null, Navigation.NavMesh? nav = null) =>
        Start(host, world, table, seed, nav, null);

    /// <inheritdoc cref="Start(DmHost, CollisionWorld, WeaponTable, int?, Navigation.NavMesh?)"/>
    /// <param name="rides">The jeep's mesh and the HK's ceiling; null: none.</param>
    internal static DmBots Start(DmHost host, CollisionWorld world, WeaponTable table, int? seed, Navigation.NavMesh? nav,
        BotRides? rides)
    {
        var bots = new DmBots(host, world, table, host.Server.Settings.BotSkill, seed, nav, rides);
        host.Bots = bots;
        bots.Add(host.Server.Settings.Bots);
        return bots;
    }

    /// <summary><paramref name="arena"/> with its spawns given the campaign's clear-spawn nudge on
    /// <paramref name="session"/>'s level (<see cref="Player.PlayerStart.Clear"/>, as the windowed host).</summary>
    public static DmArena Cleared(DmArena arena, Campaign.GameSession session) => new()
    {
        Map = arena.Map,
        Spawns = [.. arena.Spawns.Select(sp => sp with { Feet = Player.PlayerStart.Clear(session.World, session.Player.Body, sp.Feet) })],
        ItemSpots = arena.ItemSpots, JeepSpots = arena.JeepSpots, HkSpots = arena.HkSpots,
    };

    /// <summary>Adds <paramref name="count"/> bots to the server (each spawns when the arena is up).</summary>
    public void Add(int count)
    {
        for (var i = 0; i < count; i++)
        {
            _host.Server.AddBot();
        }
    }

    /// <summary>The host's clock moved on by <paramref name="dt"/>: the brains' fixed ticks.</summary>
    public void Step(double dt)
    {
        _accum = Math.Min(_accum + dt, MaxCatchUp * Tick);
        var server = _host.Server;
        while (_accum >= Tick)
        {
            _accum -= Tick;
            if (!server.MatchRunning)
            {
                continue;
            }

            Flood(server);
            _claims.NewTick();

            foreach (var b in _order)
            {
                if (server.Players.TryGetValue(b.Id, out var p) && p.Alive)
                {
                    b.Step(Tick, server, this);
                    p.Position = b.Body.Position; // the others aim at where it is, not its last pose
                }

                b.StepShots(Tick, server, this);
            }
        }
    }

    /// <summary>Once the arena is up: the mesh its spawns reach, so no bot searches its way to a
    /// goal off it (a failed search floods the whole mesh).</summary>
    private void Flood(DmServer server)
    {
        if (_claims.Reach != null || _nav == null || server.Spawns.Count == 0)
        {
            return;
        }

        var spawns = new Vector3[server.Spawns.Count];
        for (var i = 0; i < spawns.Length; i++)
        {
            spawns[i] = server.Spawns[i].Feet;
        }

        _claims.Reach = new Navigation.NavQuery(_nav).Flood(spawns);
    }

    private BotBrain Brain(int id)
    {
        if (!_brains.TryGetValue(id, out var b))
        {
            var name = _host.Server.Players.TryGetValue(id, out var p) ? p.Name : "";
            b = new BotBrain(id, Skill, _world, _table, Stream(id), _nav, _rides, BotTraits.For(name), _claims);
            // The class's pace (BALANCED's TERMINATOR runs at 0.9), as a player's own body.
            b.Body.ClassSpeed = _host.Server.SpeedOf(id);
            _brains[id] = b;
            Reorder();
        }

        return b;
    }

    /// <summary>A gun fired or a blast went off at <paramref name="at"/> (<paramref name="source"/>
    /// the player): every brain within earshot hears it.</summary>
    internal void Noise(int source, Vector3 at, Enemies.NoiseKind kind)
    {
        foreach (var b in _order)
        {
            b.Hear(source, at, kind);
        }
    }

    // ---- IBotOutput: the host's calls, as its own player makes them ----

    void IBotOutput.Pose(int id, Vector3 feet, float yaw, float pitch, int flags) => _host.BotPose(id, feet, yaw, pitch, flags);

    void IBotOutput.Pose(int id, Vector3 feet, float yaw, float pitch, int flags, float carYaw) => _host.BotPose(id, feet, yaw, pitch, flags, carYaw);

    void IBotOutput.Fire(int id, int weapon, Vector3 from, Vector3 dir) => _host.BotFire(id, weapon, from, dir);

    void IBotOutput.Hit(int id, int victim, float points, int weapon) => _host.Hit(victim, points, weapon, id);

    void IBotOutput.SelfHit(int id, float points, int weapon) => _host.Hit(id, points, weapon, id);

    void IBotOutput.Pickup(int id, int key) => _host.RequestPickup(key, id);

    void IBotOutput.Blast(int id, Vector3 at) => Noise(id, at, Enemies.NoiseKind.Blast);
}

/// <summary>What the server's bots share: which bot is on its way to which vehicle (one bot a
/// vehicle, so they do not all rush the same jeep), the goals found cut off, the mesh the spawns
/// reach and the tick's goal picks.</summary>
internal sealed class BotClaims
{
    /// <summary>Picks a tick for all bots together (<see cref="DmBots"/>); a brain on its own: no cap.</summary>
    internal int PicksPerTick { get; init; } = int.MaxValue;

    private int _picksLeft = int.MaxValue;

    /// <summary>The polygons reachable from the arena's spawns (<see cref="Navigation.NavQuery.Flood"/>); null: unknown.</summary>
    internal HashSet<long>? Reach { get; set; }

    /// <summary>A new tick: its picks are free again.</summary>
    public void NewTick() => _picksLeft = PicksPerTick;

    /// <summary>One of the tick's picks; false when they are spent.</summary>
    public bool TakePick()
    {
        if (_picksLeft <= 0)
        {
            return false;
        }

        _picksLeft--;
        return true;
    }

    private readonly Dictionary<int, int> _byVehicle = [];

    /// <summary>Goals (kind, key) a bot's path search found cut off, until when: the other bots
    /// skip the search (a failed one floods the mesh).</summary>
    private readonly Dictionary<(int Kind, int Key), double> _unreached = [];

    /// <summary>Goal <paramref name="key"/> of <paramref name="kind"/> was found cut off lately.</summary>
    public bool Unreached(int kind, int key, double now) => _unreached.TryGetValue((kind, key), out var until) && until > now;

    /// <summary>A search found goal <paramref name="key"/> cut off: skipped by all bots until <paramref name="until"/>.</summary>
    public void MarkUnreached(int kind, int key, double until) => _unreached[(kind, key)] = until;

    /// <summary>Somebody but <paramref name="bot"/> is on the way to <paramref name="vehicle"/>.</summary>
    public bool TakenByOther(int vehicle, int bot) => _byVehicle.TryGetValue(vehicle, out var by) && by != bot;

    /// <summary><paramref name="bot"/> heads for <paramref name="vehicle"/> (and for nothing else).</summary>
    public void Claim(int vehicle, int bot)
    {
        Release(bot);
        _byVehicle[vehicle] = bot;
    }

    /// <summary><paramref name="bot"/> heads for no vehicle.</summary>
    public void Release(int bot)
    {
        foreach (var (vehicle, by) in _byVehicle)
        {
            if (by == bot)
            {
                _byVehicle.Remove(vehicle);
                return;
            }
        }
    }
}
