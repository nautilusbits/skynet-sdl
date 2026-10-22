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

    private readonly DmHost _host;
    private readonly CollisionWorld _world;
    private readonly WeaponTable _table;
    private readonly int _seed;
    private readonly Navigation.NavMesh? _nav;
    private readonly BotRides _rides;
    private readonly SortedDictionary<int, BotBrain> _brains = new(Comparer<int>.Create((a, b) => b.CompareTo(a)));
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
        server.PlayerRemoved += id => _brains.Remove(id);
        // A weapon item a bot takes goes in its hand.
        server.PickupTaken += (key, by) =>
        {
            if (server.IsBot(by) && _brains.TryGetValue(by, out var b) && server.Pickups.TryGetValue(key, out var item))
            {
                b.Took(item.Sprite);
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
        _accum = Math.Min(_accum + dt, 0.25);
        var server = _host.Server;
        while (_accum >= Tick)
        {
            _accum -= Tick;
            if (!server.MatchRunning)
            {
                continue;
            }

            foreach (var b in _brains.Values)
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

    private BotBrain Brain(int id)
    {
        if (!_brains.TryGetValue(id, out var b))
        {
            b = new BotBrain(id, Skill, _world, _table, new Random(unchecked(_seed * 7919 + id)), _nav, _rides);
            // The class's pace (BALANCED's TERMINATOR runs at 0.9), as a player's own body.
            b.Body.ClassSpeed = _host.Server.SpeedOf(id);
            _brains[id] = b;
        }

        return b;
    }

    // ---- IBotOutput: the host's calls, as its own player makes them ----

    void IBotOutput.Pose(int id, Vector3 feet, float yaw, float pitch, int flags) => _host.BotPose(id, feet, yaw, pitch, flags);

    void IBotOutput.Pose(int id, Vector3 feet, float yaw, float pitch, int flags, float carYaw) => _host.BotPose(id, feet, yaw, pitch, flags, carYaw);

    void IBotOutput.Fire(int id, int weapon, Vector3 from, Vector3 dir) => _host.BotFire(id, weapon, from, dir);

    void IBotOutput.Hit(int id, int victim, float points, int weapon) => _host.Hit(victim, points, weapon, id);

    void IBotOutput.SelfHit(int id, float points, int weapon) => _host.Hit(id, points, weapon, id);

    void IBotOutput.Pickup(int id, int key) => _host.RequestPickup(key, id);
}
