using System.Collections.Immutable;
using System.Numerics;
using SkyNet.Formats;

namespace SkyNet.Game.Net;

/// <summary>What ends a round: NETLEVEL.PRS <c>goal</c> (SKYNET.EXE v1.01 FUN_00127dfb, table
/// 0x4c2cb: POINTS 0, KILLS 1, LIVES 2, TIME 3; checked by the host's FUN_00121370 every frame).</summary>
public enum DmGoal : byte
{
    /// <summary>The leader's points reach the limit.</summary>
    Points = 0,
    /// <summary>The leader's kills reach the limit.</summary>
    Kills = 1,
    /// <summary>Somebody has died the limit's number of times (nobody is put out before).</summary>
    Lives = 2,
    /// <summary>The limit's minutes have run.</summary>
    Time = 3,
}

/// <summary>A deathmatch game's parameters (the Godot port's Net.settings): the arena from
/// NETLEVEL.PRS, the host's limits, bots.</summary>
public sealed record DmSettings
{
    /// <summary>The goal limits DOS starts with (FUN_00128449: 10000 points, 10 kills, 10 lives,
    /// 5 minutes; NETLEVEL.PRS has no amount, the host menu sets it, at least 1).</summary>
    public static int DefaultLimit(DmGoal goal) => goal switch
    {
        DmGoal.Points => 10000,
        DmGoal.Kills => 10,
        DmGoal.Lives => 10,
        _ => 5,
    };

    /// <summary>NETLEVEL.PRS's goal word (time, points, kills, lives); kills for anything else.</summary>
    public static DmGoal ParseGoal(string? word) => word?.Trim().ToLowerInvariant() switch
    {
        "points" => DmGoal.Points,
        "lives" => DmGoal.Lives,
        "time" => DmGoal.Time,
        _ => DmGoal.Kills,
    };

    public string ServerName { get; init; } = "SKYNET";
    public int Map { get; init; } = 605;
    public string Arena { get; init; } = "";
    /// <summary>Humans at most; 0 = <see cref="DmProtocol.MaxPeers"/>.</summary>
    public int MaxPlayers { get; init; }
    /// <summary>Minutes; 0 = no time limit (the port's TIME box, on any goal; a TIME goal's limit is
    /// <see cref="GoalLimit"/> minutes).</summary>
    public int TimeLimitMinutes { get; init; }
    /// <summary>What ends the round (NETLEVEL.PRS <c>goal</c>, or the host menu's GOAL).</summary>
    public DmGoal Goal { get; init; } = DmGoal.Kills;
    /// <summary>The goal's limit: points, kills, deaths of one player, or minutes; 0 = none.</summary>
    public int GoalLimit { get; init; }
    /// <summary>The kill limit when the goal is KILLS (the HUD's FRAGS n/limit), else 0.</summary>
    public int FragLimit => Goal == DmGoal.Kills ? GoalLimit : 0;
    /// <summary>The round's length in seconds: the TIME goal's minutes and the TIME box, the shorter
    /// (0 = untimed).</summary>
    public double RoundSeconds
    {
        get
        {
            var goal = Goal == DmGoal.Time ? GoalLimit : 0;
            var minutes = goal > 0 && TimeLimitMinutes > 0 ? Math.Min(goal, TimeLimitMinutes) : Math.Max(goal, TimeLimitMinutes);
            return minutes * 60.0;
        }
    }
    /// <summary>The console's cheats work in this game (the host's <c>--cheats</c> or the menu's
    /// CHEATS; DOS refuses its flagged CHEAT.PRS codes in a network game, FUN_00142658).</summary>
    public bool Cheats { get; init; }
    public bool Replenish { get; init; } = true;
    public int Bots { get; init; }
    public int BotSkill { get; init; } = 1;
    public int ScoringKill { get; init; } = 500;
    public int ScoringDeath { get; init; } = 400;
    public int ScoringHit { get; init; } = 100;
    public string TimeSetting { get; init; } = "night";
    /// <summary>The host's CLASSES (sent in the welcome, so every machine plays the same rules).</summary>
    public DmClassRules Classes { get; init; } = DmClassRules.Balanced;
    public ImmutableDictionary<string, int> Items { get; init; } = ImmutableDictionary<string, int>.Empty;

    /// <summary>The settings NETLEVEL.PRS gives <paramref name="level"/>.</summary>
    public static DmSettings From(NetLevel level, string serverName) => new()
    {
        ServerName = serverName, Map = level.Map, Arena = level.Name, MaxPlayers = level.MaxPlayers, Replenish = level.Replenish,
        ScoringKill = level.ScoringKill, ScoringDeath = level.ScoringDeath, ScoringHit = level.ScoringHit,
        TimeSetting = level.TimeSetting, Items = level.Items, Goal = ParseGoal(level.Goal),
    };
}

/// <summary>One player of the roster (human or bot). Ids: the host 1, clients 2 and up, bots
/// negative, 0 = the world (drowning, radiation).</summary>
public sealed class DmPlayer
{
    public int Id { get; init; }
    public string Name { get; set; } = "";
    public DmClass Class { get; set; }
    /// <summary>Which of DOS's thirteen avatars (<see cref="DmAvatarTable"/>, 1..13) the body wears,
    /// always one of <see cref="Class"/>.</summary>
    public int Avatar { get; set; } = 1;
    public bool Bot { get; init; }
    /// <summary>Kills (DOS's stat 1, player record +0x41): +1 a kill; a suicide or a death to the
    /// world takes none away (FUN_00122b52: killer 0, only the death is counted).</summary>
    public int Frags { get; set; }
    public int Deaths { get; set; }
    /// <summary>NETLEVEL.PRS scoring: + scoring_kill a kill, + scoring_hit a hit, − scoring_death a death.</summary>
    public int Points { get; set; }
    public float Health { get; set; }
    /// <summary>0..1 of the bar (DOS armour, see <see cref="DmServer.ApplyDamage"/>).</summary>
    public float Armour { get; set; }
    public bool Alive { get; set; }
    public Vector3 Position { get; set; }
    public float Yaw { get; set; }
    public float Pitch { get; set; }
    public int Weapon { get; set; } = 1;
}

/// <summary>An item lying in the arena: a sprite index of <see cref="Campaign.PickupTable"/>.
/// A drop (what a broken crate left) does not come back.</summary>
public sealed class DmPickup
{
    public int Key { get; init; }
    public int Sprite { get; init; }
    public Vector3 Position { get; init; }
    public bool Taken { get; set; }
    public bool Drop { get; init; }
}

/// <summary>A spawn: feet and yaw (radians about +Y, 0 looking −Z).</summary>
public readonly record struct DmSpawn(Vector3 Feet, float Yaw);

/// <summary>A deathmatch vehicle (NETLEVEL.PRS <c>jeeps</c> / <c>hks</c>, step 9; SKYNET.EXE v1.01's
/// 0x2E-byte records at 0x51F00): where it is parked (or where its driver last said it was), who
/// drives it (0 nobody; DOS's occupied bit 4), its hull and whether it is a wreck (DOS's bit 8). The
/// server's state; clients keep a copy from its messages.</summary>
public sealed class DmVehicle
{
    public int Key { get; init; }
    public Vehicles.VehicleMode Kind { get; init; }
    /// <summary>Its marker: a new round parks it here again.</summary>
    public Vector3 Home { get; init; }
    public float HomeYaw { get; init; }
    /// <summary>The bottom of the hull (the driver's body position while driven).</summary>
    public Vector3 Position { get; set; }
    /// <summary>The heading (the jeep's car yaw; the HK's is the view's).</summary>
    public float Yaw { get; set; }
    /// <summary>The player in the seat; 0 parked.</summary>
    public int Driver { get; set; }
    /// <summary>The hull's points (<see cref="Vehicles.Hull"/>: 1000 jeep, 1500 HK).</summary>
    public float Hull { get; set; }
    /// <summary>Blown up: not drawn, not boarded, for the rest of the round (DOS never clears bit 8).</summary>
    public bool Wrecked { get; set; }
}

/// <summary>Why a vehicle's state went out (<see cref="DmMsg.VehicleState"/>).</summary>
public enum DmVehicleEvent : byte
{
    /// <summary>A seat taken or left, a hull worn, a place: just the new state.</summary>
    Changed = 0,
    /// <summary>The hull reached zero now: the explosion where it stands (DOS event 0xF).</summary>
    Wrecked = 1,
    /// <summary>Back at its marker, whole: a new round.</summary>
    Returned = 2,
}
