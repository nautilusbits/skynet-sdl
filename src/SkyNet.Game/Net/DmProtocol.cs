using System.Collections.Immutable;
using System.Numerics;
using System.Text;
using RetroEng.Net;
using SkyNet.Game.Campaign;
using SkyNet.Formats;

namespace SkyNet.Game.Net;

/// <summary>The two deathmatch classes (net_game.gd CLASS_*). DOS's NETMENU1 picks one of thirteen
/// avatars; avatars 7 (AVTRMNTR) and 13 (AVFEMALE) are machines (SKYNET.EXE v1.01 0x134a39): the
/// TERMINATOR here.</summary>
public enum DmClass : byte
{
    Human = 0,
    Terminator = 1,
}

/// <summary>The host's CLASSES (the owner's rule, 2026-10-11): how the two classes differ. Both give
/// every avatar DOS's 150-point bar (SKYNET.EXE v1.01 0x122348) and take the motion detector from the
/// machine, which sees in red through SKYNTTRM.COL with FUN_00132b00's marks (0x134a39).</summary>
public enum DmClassRules : byte
{
    /// <summary>SKYNET.EXE as it is: the machine runs at the soldier's pace.</summary>
    Dos = 0,
    /// <summary>The port's default: the machine runs at <see cref="DmProtocol.BalancedMachineSpeed"/>
    /// of the pace, the price of seeing through walls.</summary>
    Balanced = 1,
}

/// <summary>Message ids. Client → server below 64, server → client from 64. The kick is not a
/// message: the server closes the connection with the reason as the disconnect payload.</summary>
public static class DmMsg
{
    // client → server
    public const byte Hello = 1;
    public const byte LevelReady = 2;
    public const byte Pose = 3;
    public const byte Fire = 4;
    public const byte Hit = 5;
    public const byte Pickup = 6;
    public const byte Chat = 7;
    public const byte SetClass = 8;
    /// <summary>The use key or a shot on a level record (step 8): an intent for the server's level.</summary>
    public const byte LevelIntent = 9;
    /// <summary>Step 9: the use key on a parked vehicle (its key): a seat, if the server agrees.</summary>
    public const byte VehicleEnter = 10;
    /// <summary>Step 9: the driver climbs out (where the vehicle stays, its heading).</summary>
    public const byte VehicleLeave = 11;
    /// <summary>Protocol 5: the answer to <see cref="Challenge"/> (has a password, HMAC proof).</summary>
    public const byte Auth = 12;

    // server → client
    public const byte Welcome = 64;
    public const byte PlayerAdd = 65;
    public const byte PlayerRemove = 66;
    public const byte PoseOf = 67;
    public const byte FireOf = 68;
    public const byte Health = 69;
    public const byte Died = 70;
    public const byte Respawn = 71;
    public const byte PickupTaken = 72;
    public const byte PickupSpawn = 73;
    public const byte PickupAdd = 74;
    public const byte ChatOf = 75;
    public const byte Time = 76;
    public const byte MatchOver = 77;
    public const byte Restart = 78;
    public const byte ClassOf = 79;
    /// <summary>The level's state (step 8): a joiner's overlay or a delta (<see cref="LevelUpdate"/>).</summary>
    public const byte LevelState = 80;
    /// <summary>Step 9: one vehicle's whole state and why it went out (<see cref="DmVehicleEvent"/>).</summary>
    public const byte VehicleState = 81;
    /// <summary>Protocol 5: the server wants a password: a fresh nonce to prove it with (<see cref="Auth"/>).</summary>
    public const byte Challenge = 82;
}

/// <summary>
/// The deathmatch wire protocol, after the Godot port's net_game.gd (its RPCs become messages
/// here): the constants, the hello, and the shared encodings of settings, roster and pickups.
/// Not wire-compatible with the Godot port (ENet + Godot RPCs there, LiteNetLib here).
/// </summary>
public static class DmProtocol
{
    /// <summary>Bumped whenever a message changes; a mismatched hello is kicked with the reason.
    /// 7: the avatar (<see cref="DmAvatarTable"/>, its index 0..12) in the hello, the roster, the
    /// class change and SetClass. 8: <see cref="FlagScanner"/> in the poses. 9: the weapon in hand
    /// in the poses (<see cref="WeaponBits"/>).</summary>
    public const int Version = 9;

    /// <summary>LiteNetLib's connection key: other programs on the port are turned away.</summary>
    public const string AppKey = "SKYNET-DM";

    public const int DefaultPort = 27015;
    public const int DiscoveryPort = 27016;
    public static readonly byte[] DiscoveryMagic = Encoding.ASCII.GetBytes("SKYNET_DISCOVER_1");

    public const int MaxPeers = 16;
    public const double PoseHz = 20.0;
    public const double RespawnDelay = 3.0;
    /// <summary>A taken item of a replenished arena is back after 1400 ticks of the 70 Hz clock
    /// (SKYNET.EXE v1.01 0x11c4a5: <c>taken at &lt;= clock - 0x578</c>): 20 s.</summary>
    public const double PickupRespawn = 0x578 / 70.0;

    /// <summary>A deathmatch player's bar: SKYNET.EXE v1.01 0x122348 sets the damage a soldier takes
    /// before he dies ([0x38dd0], 8.8 points) to 0x9600 = 150 in a network game ([0x30a50] bit
    /// 0x40000) instead of the campaign's 0x3e800 = 1000, the same for all thirteen avatars.</summary>
    public const float DosHealth = 150f;

    /// <summary>BALANCED's TERMINATOR pace: 0.9 of the soldier's walk and run (the owner's penalty for
    /// seeing through walls). DOS's machine avatars run at the soldier's speed.</summary>
    public const float BalancedMachineSpeed = 0.9f;

    /// <summary>The class's pace under <paramref name="rules"/>: 1 but for BALANCED's TERMINATOR.</summary>
    public static float SpeedFactor(DmClass c, DmClassRules rules) =>
        c == DmClass.Terminator && rules == DmClassRules.Balanced ? BalancedMachineSpeed : 1f;

    /// <summary>CLASSES' name in the menus, server.json and the command line (DOS, BALANCED).</summary>
    public static string RulesName(DmClassRules rules) => rules == DmClassRules.Dos ? "DOS" : "BALANCED";

    /// <summary><c>dos</c> or <c>balanced</c> (any case); null for anything else.</summary>
    public static DmClassRules? ParseRules(string? word) => word?.Trim().ToLowerInvariant() switch
    {
        "dos" => DmClassRules.Dos,
        "balanced" => DmClassRules.Balanced,
        _ => null,
    };

    public const int MaxNameLength = 16;
    public const int MaxChatLength = 120;
    public const int MaxServerNameLength = 48;

    // Pose flags: moving and firing are the owner's word, the rest the server's.
    public const int FlagMoving = 1;
    public const int FlagFiring = 2;
    public const int FlagDead = 4;
    /// <summary>Bits 3-4: the vehicle the player drives (<see cref="Vehicles.VehicleMode"/>), the
    /// server's word (net_game.gd F_VEH_SHIFT).</summary>
    public const int VehicleShift = 3;
    public const int VehicleMask = 3 << VehicleShift;
    /// <summary>The motion detector is in hand (DOS's network flag 0x10 for record 13, FUN_00118d4c):
    /// the owner's word, kept by the server for a HUMAN on foot. The others plot the holder on their
    /// scanners and hear its sweep.</summary>
    public const int FlagScanner = 1 << 5;

    /// <summary>Bits 8-15: the record in hand + 1, 0 none (DOS's player byte +0x20, which picks the
    /// avatar's gun): the owner's word.</summary>
    private const int WeaponShift = 8;
    private const int WeaponMask = 0xFF << WeaponShift;

    /// <summary>Record <paramref name="record"/> in hand as pose flags (none outside 0..254).</summary>
    public static int WeaponBits(int record) => record is >= 0 and < 0xFF ? (record + 1) << WeaponShift : 0;

    /// <summary>The record in hand the flags carry, −1 none.</summary>
    public static int WeaponOf(int flags) => ((flags & WeaponMask) >> WeaponShift) - 1;

    public static Vehicles.VehicleMode VehicleOf(int flags) => (Vehicles.VehicleMode)((flags & VehicleMask) >> VehicleShift);

    /// <summary>The use key boards the first free vehicle within this distance (SKYNET.EXE v1.01
    /// 0x130c80: 0x78).</summary>
    public const float VehicleBoardReach = 120f;

    /// <summary>How far the server's last pose of a player may be from a vehicle it boards, or from
    /// where it says it climbed out: DOS's reach plus pose lag (net_game.gd VEHICLE_REACH).</summary>
    public const float VehicleReach = 1400f;

    public const double HelloTimeout = 10.0;

    /// <summary>The password challenge's nonce (bytes).</summary>
    public const int NonceLength = 16;

    /// <summary>What a server keeps of its password and a client proves it with: SHA-256 of the UTF-8
    /// text behind a fixed label (the dedicated server's <c>password_sha256</c> is this in hex). The
    /// password itself never travels: the client answers a fresh nonce with
    /// <see cref="PasswordProof"/>.</summary>
    public static byte[] PasswordKey(string password) =>
        System.Security.Cryptography.SHA256.HashData(Encoding.UTF8.GetBytes("SKYNET-DM-PASSWORD:" + password));

    /// <summary>HMAC-SHA256 of the server's nonce under the password key.</summary>
    public static byte[] PasswordProof(ReadOnlySpan<byte> key, ReadOnlySpan<byte> nonce) =>
        System.Security.Cryptography.HMACSHA256.HashData(key, nonce);

    /// <summary>The proof matches (in constant time).</summary>
    public static bool ProofMatches(ReadOnlySpan<byte> key, ReadOnlySpan<byte> nonce, ReadOnlySpan<byte> proof) =>
        proof.Length == 32 && System.Security.Cryptography.CryptographicOperations.FixedTimeEquals(PasswordProof(key, nonce), proof);

    /// <summary>The arena is 65 536 units square; a coordinate four times that is garbage.</summary>
    public const float WorldBound = 262144f;

    /// <summary>A reported hit is capped here (the Godot port capped at its biggest blast on a
    /// 100-point scale; the bar itself is 150 points).</summary>
    public const float MaxHitDamage = 2000f;

    /// <summary>The hitscan ray (60 000) plus pose lag.</summary>
    public const float MaxHitRange = 64000f;

    /// <summary>The weapon a level blast (a broken barrel's, a car's) is credited with.</summary>
    public const int LevelBlastWeapon = -2;

    /// <summary>A rocket in the air when its shooter died still counts for this long.</summary>
    public const double DeadShotGrace = 1.5;

    /// <summary>The DOS grab box plus pose lag at full speed.</summary>
    public const float PickupReach = 800f;

    /// <summary>Per-sender budgets [per second, burst] (net_game.gd RATE_LIMITS).</summary>
    public enum Limit
    {
        Pose,
        Fire,
        Hit,
        SelfHit,
        Chat,
        Pickup,
        Vehicle,
        Class,
        Damage,
        Trigger,
    }

    public static readonly ImmutableArray<(double Rate, double Burst)> Limits =
    [
        (30, 15), (20, 20), (60, 60), (300, 300), (2, 4), (10, 10), (4, 4), (2, 3),
        // damage: the Godot port's 1500/s, 6000 burst on its 100-point bar, on DOS's 1000-point one
        (15000, 60000), (20, 30),
    ];

    /// <summary>
    /// DOS's deathmatch item table (SKYNET.EXE v1.01 0x4bc30, filled from NETLEVEL / the host menu
    /// by 0x128488): per NETLEVEL category, in the order DOS lays them out, its item sprites (keys of
    /// <see cref="PickupTable"/>) with 16.16 weights that sum to 1. <see cref="DmItemLayout"/> lays
    /// them out as 0x11c407 does. The vehicles (jeeps, hks: 0x4bc80 / 0x4bc88) are not items.
    /// </summary>
    public static readonly ImmutableArray<(string Category, ImmutableArray<(int Weight, int Sprite)> Kinds)> DosItems =
    [
        ("slugthrowers", [(29492, 25612), (22938, 25736), (6554, 25737), (6554, 25600)]),
        ("lasers", [(52428, 25611), (13108, 25609)]),
        ("plasmas", [(39322, 25729), (19661, 25731), (6554, 25613)]),
        ("launchers", [(52428, 25606), (13108, 25733)]),
        ("bullets", [(26215, 25602), (19661, 25601), (19661, 25735)]),
        ("energy", [(39322, 27406), (26215, 27405)]),
        ("grenades", [(26215, 25728), (13108, 25614), (9831, 25604), (6554, 25607), (6554, 25605), (3277, 25603)]),
        ("rockets", [(39322, 25734), (26215, 25738)]),
        ("armor", [(26215, 27392), (19661, 27393), (13108, 27394), (6554, 27395)]),
        ("health", [(26215, 27402), (19661, 27401), (13108, 27400), (6554, 27399)]),
    ];

    /// <summary>Player text made safe to show (net_game.gd _clean_text): C0/C1 controls, DEL and
    /// the bidi marks, embeddings, overrides and isolates removed, ends trimmed, length capped.</summary>
    public static string CleanText(string? s, int maxLength)
    {
        if (string.IsNullOrEmpty(s))
        {
            return "";
        }

        if (s.Length > maxLength * 4)
        {
            s = s[..(maxLength * 4)];
        }

        var sb = new StringBuilder(s.Length);
        foreach (var c in s)
        {
            if (!IsBadChar(c))
            {
                sb.Append(c);
            }
        }

        var t = sb.ToString().Trim();
        return t.Length > maxLength ? t[..maxLength].TrimEnd() : t;
    }

    private static bool IsBadChar(char c) =>
        c < 0x20 || (c >= 0x7F && c <= 0x9F) || c == 0x200E || c == 0x200F
        || (c >= 0x2028 && c <= 0x202E) || (c >= 0x2066 && c <= 0x2069);

    public static bool SanePos(Vector3 p) =>
        float.IsFinite(p.X) && float.IsFinite(p.Y) && float.IsFinite(p.Z)
        && MathF.Abs(p.X) <= WorldBound && MathF.Abs(p.Y) <= WorldBound && MathF.Abs(p.Z) <= WorldBound;

    // ---- encodings shared by both ends ----

    /// <summary>The hello: the version first (a mismatch is refused on it alone), the class, the name,
    /// the avatar's index (0..12).</summary>
    public static NetWriter WriteHello(NetWriter w, DmClass cls, string name, int avatar = 0) =>
        w.Begin(DmMsg.Hello).I32(Version).U8((byte)cls).Str(CleanText(name, MaxNameLength), 64)
            .U8(DmAvatarTable.ToWire(DmAvatarTable.Resolve(avatar, cls)));

    public static void WriteSettings(NetWriter w, DmSettings s)
    {
        w.Str(s.ServerName, 256).I32(s.Map).Str(s.Arena, 256).I32(s.MaxPlayers).I32(s.TimeLimitMinutes).U8((byte)s.Goal).I32(s.GoalLimit)
            .Bool(s.Cheats).Bool(s.Replenish).I32(s.Bots).I32(s.BotSkill).I32(s.ScoringKill).I32(s.ScoringDeath).I32(s.ScoringHit)
            .Str(s.TimeSetting, 32).U8((byte)s.Classes);
        w.U8((byte)NetLevelList.ItemKeys.Length);
        foreach (var k in NetLevelList.ItemKeys)
        {
            w.U16((ushort)Math.Clamp(s.Items.GetValueOrDefault(k), 0, ushort.MaxValue));
        }
    }

    public static DmSettings ReadSettings(ref NetReader r)
    {
        var name = r.Str(256);
        var map = r.I32();
        var arena = r.Str(256);
        var maxPlayers = r.I32();
        var time = r.I32();
        var goal = (DmGoal)Math.Min((int)r.U8(), (int)DmGoal.Time);
        var limit = r.I32();
        var cheats = r.Bool();
        var replenish = r.Bool();
        var bots = r.I32();
        var skill = r.I32();
        var kill = r.I32();
        var death = r.I32();
        var hit = r.I32();
        var timeSetting = r.Str(32);
        var classes = r.U8() == (byte)DmClassRules.Dos ? DmClassRules.Dos : DmClassRules.Balanced;
        int n = r.U8();
        var items = ImmutableDictionary.CreateBuilder<string, int>();
        for (var i = 0; i < n; i++)
        {
            var v = r.U16();
            if (i < NetLevelList.ItemKeys.Length)
            {
                items[NetLevelList.ItemKeys[i]] = v;
            }
        }

        return new DmSettings
        {
            ServerName = name, Map = map, Arena = arena, MaxPlayers = maxPlayers, TimeLimitMinutes = time, Goal = goal, GoalLimit = limit, Cheats = cheats,
            Replenish = replenish, Bots = bots, BotSkill = skill, ScoringKill = kill, ScoringDeath = death, ScoringHit = hit,
            TimeSetting = timeSetting, Classes = classes, Items = items.ToImmutable(),
        };
    }

    /// <summary>The welcome (<see cref="DmMsg.Welcome"/>): the joiner's id, the game, the roster, the
    /// items, the clock, the vehicles. A network demo's keyframe starts with one.</summary>
    public static NetWriter WriteWelcome(NetWriter w, int id, DmSettings settings, IEnumerable<DmPlayer> roster, IEnumerable<DmPickup> items,
        double timeLeft, bool running, IEnumerable<DmVehicle> parked)
    {
        var (players, pickups, vehicles) = (roster.ToList(), items.ToList(), parked.ToList());
        w.Begin(DmMsg.Welcome).I32(id);
        WriteSettings(w, settings);
        w.U16((ushort)players.Count);
        foreach (var p in players)
        {
            WritePlayer(w, p);
        }

        w.U16((ushort)pickups.Count);
        foreach (var item in pickups)
        {
            WritePickup(w, item);
        }

        w.I32((int)Math.Ceiling(timeLeft)).Bool(running);
        w.U16((ushort)vehicles.Count);
        foreach (var v in vehicles)
        {
            WriteVehicle(w, v);
        }

        return w;
    }

    public static void WritePlayer(NetWriter w, DmPlayer p) =>
        w.I32(p.Id).Str(p.Name, 64).U8((byte)p.Class).Bool(p.Bot).I32(p.Frags).I32(p.Deaths).I32(p.Points)
            .F32(p.Health).F32(p.Armour).Bool(p.Alive).Vec3(p.Position).I32(p.Weapon).U8(DmAvatarTable.ToWire(p.Avatar));

    public static DmPlayer ReadPlayer(ref NetReader r)
    {
        var p = new DmPlayer
        {
            Id = r.I32(), Name = CleanText(r.Str(64), MaxNameLength), Class = (DmClass)(r.U8() & 1), Bot = r.Bool(),
            Frags = r.I32(), Deaths = r.I32(), Points = r.I32(), Health = r.F32(), Armour = r.F32(), Alive = r.Bool(),
            Position = r.Vec3(), Weapon = r.I32(),
        };
        p.Avatar = DmAvatarTable.FromWire(r.U8(), p.Class);
        return p;
    }

    public static void WriteVehicle(NetWriter w, DmVehicle v) =>
        w.I32(v.Key).U8((byte)v.Kind).Vec3(v.Home).F32(v.HomeYaw).Vec3(v.Position).F32(v.Yaw).I32(v.Driver).F32(v.Hull).Bool(v.Wrecked);

    public static DmVehicle ReadVehicle(ref NetReader r)
    {
        var key = r.I32();
        var kind = (Vehicles.VehicleMode)Math.Clamp((int)r.U8(), 1, 2);
        var home = r.Vec3();
        var homeYaw = r.F32();
        return new DmVehicle
        {
            Key = key, Kind = kind, Home = home, HomeYaw = homeYaw, Position = r.Vec3(), Yaw = r.F32(), Driver = r.I32(), Hull = r.F32(),
            Wrecked = r.Bool(),
        };
    }

    public static void WritePickup(NetWriter w, DmPickup p) => w.I32(p.Key).I32(p.Sprite).Vec3(p.Position).Bool(p.Taken).Bool(p.Drop);

    public static DmPickup ReadPickup(ref NetReader r) => new()
    {
        Key = r.I32(), Sprite = r.I32(), Position = r.Vec3(), Taken = r.Bool(), Drop = r.Bool(),
    };

    /// <summary>Every player's frags, deaths and points (with a death and at the round's end).</summary>
    public static void WriteScores(NetWriter w, IReadOnlyCollection<DmPlayer> players)
    {
        w.U16((ushort)players.Count);
        foreach (var p in players)
        {
            w.I32(p.Id).I32(p.Frags).I32(p.Deaths).I32(p.Points);
        }
    }

    public static void ReadScores(ref NetReader r, IDictionary<int, DmPlayer> players)
    {
        int n = r.U16();
        for (var i = 0; i < n; i++)
        {
            var id = r.I32();
            var frags = r.I32();
            var deaths = r.I32();
            var points = r.I32();
            if (players.TryGetValue(id, out var p))
            {
                p.Frags = frags;
                p.Deaths = deaths;
                p.Points = points;
            }
        }
    }

    /// <summary>The game id a server announces to the master server and a browser asks for: SkyNET's
    /// and Future Shock's arenas are different games there.</summary>
    public static string GameId(SkyNet.Formats.GameKind game) => game == SkyNet.Formats.GameKind.FutureShock ? "shock" : "skynet";

    /// <summary>What a public server tells the master server and answers a ping with.</summary>
    public static RetroEng.Net.Master.ServerInfo MasterInfo(string gameId, DmSettings s, int humans, int bots, bool password) => new()
    {
        GameId = gameId, Protocol = Version, Name = CleanText(s.ServerName, MaxServerNameLength),
        Map = string.Create(System.Globalization.CultureInfo.InvariantCulture, $"MAP.{s.Map:000}"),
        Players = humans, MaxPlayers = s.MaxPlayers, Bots = bots, Password = password,
    };

    /// <summary>The LAN browser's reply: server name, map, humans, bots, cap, game port, and a flags
    /// byte (bit 0: a password; older builds' replies end before it).</summary>
    public static byte[] DiscoveryInfo(DmSettings s, int humans, int bots, int port, bool password = false) =>
        new NetWriter().Clear().Str(CleanText(s.ServerName, MaxServerNameLength), 200)
            .Str(string.Create(System.Globalization.CultureInfo.InvariantCulture, $"MAP.{s.Map:000}"), 16)
            .U16((ushort)Math.Clamp(humans, 0, 999)).U16((ushort)Math.Clamp(bots, 0, 999))
            .U16((ushort)Math.Clamp(s.MaxPlayers, 0, 999)).U16((ushort)Math.Clamp(port, 0, 65535))
            .U8(password ? (byte)1 : (byte)0).ToArray();

    /// <summary>A discovery reply read back; null when it is not one.</summary>
    public static (string Name, string Map, int Players, int Bots, int Max, int Port, bool Password)? ReadDiscoveryInfo(ReadOnlySpan<byte> info)
    {
        try
        {
            var r = new NetReader(info);
            var name = CleanText(r.Str(200), MaxServerNameLength);
            var map = CleanText(r.Str(16), 16);
            var (players, bots, max, port) = (r.U16(), r.U16(), r.U16(), r.U16());
            var password = !r.End && (r.U8() & 1) != 0;
            return (name.Length > 0 ? name : "?", map, players, bots, max, port, password);
        }
        catch (NetReadException)
        {
            return null;
        }
    }
}
