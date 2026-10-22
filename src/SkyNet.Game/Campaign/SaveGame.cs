using System.Globalization;
using System.Numerics;
using System.Text;
using SkyNet.Formats;
using SkyNet.Game.Behaviour;
using SkyNet.Game.Enemies;
using SkyNet.Game.Weapons;

namespace SkyNet.Game.Campaign;

/// <summary>An ambient loop running on a map when it was left, started again on re-entry.</summary>
public readonly record struct SavedLoop(int Source, int Sound, Vector3 At);

/// <summary>One visited map of the mission: its behaviour, the pickups taken, the robots killed
/// (by marker or spawn-sprite offset), its running loops and (format 3) what destroyed records
/// left lying there with the next drop's id; (format 6) the mission file's instance id it is kept
/// under (null in an older save: the instance the mission file gives <see cref="Map"/>).</summary>
public sealed record SavedMap(int Map, BehaviourSnapshot Behaviour, int[] Taken, int[] Dead, SavedLoop[] Loops)
{
    public DroppedSprite[] Drops { get; init; } = [];
    public int NextDrop { get; init; } = -1;
    public string? Instance { get; init; }
}

/// <summary>The player's ride in a save (format 2): the mode, the hull's points, the jeep's speed,
/// heading and turret, the vehicle energy pool and whether the gun is overheated, and the body's
/// height (the HK's altitude: a level entry would raise it again).</summary>
public sealed record SavedVehicle(PlayerMode Mode, float Hull, float Speed, float CarYaw, float AimYaw, float AimPitch,
    int Energy, bool Overheated, float Altitude);

/// <summary>The vehicle layer as a save sees it (<see cref="GameSession.Ride"/>): what to keep, and
/// putting it back once the loaded map has been entered.</summary>
public interface IVehicleSaver
{
    /// <summary>The ride as it stands; null on foot.</summary>
    SavedVehicle? Save();

    /// <summary>Climbs back into <paramref name="saved"/> (called after the map is entered).</summary>
    void Restore(SavedVehicle saved);
}

/// <summary>
/// A saved game of the C# port (<see cref="GameSession.Capture"/> / <see cref="GameSession.Load"/>).
/// DOS's own save format is not decoded: this is the port's own. <see cref="Mission"/> 0 is a loose
/// map (no mission). <see cref="CompleteIn"/> is the wait left before MISSION COMPLETE when the
/// counter has run out but the banner has not come up yet. <see cref="Ride"/> is null on foot and
/// in a format-1 save (the vehicle then comes from the mission table, a fresh ride).
/// <see cref="Stats"/> is null in a format-1 or 2 save (the mission's row starts afresh).
/// </summary>
public sealed record SaveGame(
    GameKind Game,
    int Mission,
    int Map,
    int PreviousMap,
    Vector3 Feet,
    float Yaw,
    float Pitch,
    bool Noclip,
    PlayerMode Vehicle,
    float Health,
    float Armour,
    InventoryState Kit,
    InventoryState StartKit,
    float StartHealth,
    float StartArmour,
    ProgressSnapshot Progress,
    float? CompleteIn,
    SavedMap[] Maps,
    RobotSnapshot[] Robots,
    DateTime Saved,
    SavedVehicle? Ride = null)
{
    /// <summary>The STATISTICS counters (format 3).</summary>
    public SavedStats? Stats { get; init; }

    /// <summary>The player's name and call sign from the new-game dialog (format 4); null in an
    /// older save (the session keeps the ones it has).</summary>
    public Pilot? Pilot { get; init; }

    /// <summary>The mission file's instance being played and the one before it (format 6); null in
    /// an older save (the instances the mission file gives <see cref="Map"/> and <see cref="PreviousMap"/>).</summary>
    public string? Instance { get; init; }
    public string? PreviousInstance { get; init; }

    /// <summary>The simulation generator's state (<see cref="GameSession.Random"/>, format 7): a
    /// loaded game draws the numbers the saved one would have; null in an older save (the session's
    /// generator goes on from where it stands).</summary>
    public ulong? RandomState { get; init; }

    /// <summary>The automap's seen set on the current map (format 9): its map records' file
    /// offsets; empty in an older save (nothing seen yet).</summary>
    internal int[] Seen { get; init; } = [];

    /// <summary>The LOAD/SAVE bar's text, DOS-style: the mission and map, then the date and time.</summary>
    public string Label => string.Create(CultureInfo.InvariantCulture,
        $"{(Mission > 0 ? $"MISSION {Mission} " : "")}MAP {Map:D3}  {Saved:yyyy-MM-dd HH:mm}");
}

/// <summary>A save file that cannot be read: not a save, damaged, or from another format version.</summary>
public sealed class SaveFormatException(string message, Exception? inner = null) : Exception(message, inner);

/// <summary>
/// The binary save format, little-endian:
/// <code>
///   "SKYNETCS"            8 bytes, the magic
///   u16 version           <see cref="Version"/> (or 1, read without the ride); any other is refused
///   string label          the slot bar's text (read alone by the LOAD screen)
///   i32 bodyLength, u64 FNV-1a hash of the body, then the body
/// </code>
/// The body holds the <see cref="SaveGame"/> fields in declaration order: strings and arrays are
/// length-prefixed (i32), vectors are three f32, a nullable is a flag byte and the value; format 2
/// appends the ride (a flag byte, then <see cref="SavedVehicle"/>'s fields); format 3 adds each
/// map's drops after its loops (a list of {i32 id, i32 sprite, vector}, then i32 next id) and
/// appends the statistics (a flag byte and the mission's row, i32 row index, then the career and
/// the career at the mission's start; a row is four i32: shots, hits, enemies, kills); format 4
/// appends the pilot (a flag byte, then the name and the call sign as strings); format 5 adds each
/// map's turned wall switches after its next drop id (a list of i32 offsets); format 6 adds each
/// map's instance id after its switches (a flag byte and a string) and appends the current and the
/// previous instance ids (each a flag byte and a string); format 7 appends the simulation
/// generator's state (a flag byte, then a u64); format 8 appends each robot's perception state (a
/// list, in the robots' order, of {u8 counter 0..3, flag byte last answer}); format 9 appends the
/// automap's seen set (a list of i32 map-record offsets).
/// </summary>
public static class SaveFile
{
    public static ReadOnlySpan<byte> Magic => "SKYNETCS"u8;
    /// <summary>1: the first format (2026-10-10); 2: the ride appended (2026-10-10); 3: the maps'
    /// drops and the statistics (2026-10-10); 4: the pilot's name and call sign (2026-10-10); 5: the
    /// wall switches turned to their ON face (2026-10-10); 6: maps kept per mission-file instance (2026-10-11);
    /// 7: the simulation generator's state (2026-10-11); 8: the robots' perception counter and last
    /// answer (2026-10-11); 9: the automap's seen set (2026-10-21).</summary>
    public const ushort Version = 9;
    /// <summary>The oldest format still read.</summary>
    public const ushort OldestVersion = 1;
    /// <summary>A sanity cap on any length read from a file.</summary>
    private const int MaxCount = 1 << 24;

    public static byte[] Write(SaveGame save) => Write(save, Version);

    /// <summary>The save in an older format <paramref name="version"/> (what that format cannot
    /// hold is left out): how files of earlier builds look, for tests.</summary>
    public static byte[] Write(SaveGame save, ushort version)
    {
        if (version is < OldestVersion or > Version)
        {
            throw new ArgumentOutOfRangeException(nameof(version), version, "not a format this build writes");
        }

        using var body = new MemoryStream();
        using (var w = new BinaryWriter(body, Encoding.UTF8, leaveOpen: true))
        {
            WriteBody(w, save, version);
        }

        var bytes = body.ToArray();
        using var file = new MemoryStream();
        using (var w = new BinaryWriter(file, Encoding.UTF8, leaveOpen: true))
        {
            w.Write(Magic);
            w.Write(version);
            w.Write(save.Label);
            w.Write(bytes.Length);
            w.Write(Hash(bytes));
            w.Write(bytes);
        }

        return file.ToArray();
    }

    /// <summary>The save in <paramref name="bytes"/>; <see cref="SaveFormatException"/> when it is
    /// not one, is damaged or has another version.</summary>
    public static SaveGame Read(byte[] bytes)
    {
        try
        {
            using var r = new BinaryReader(new MemoryStream(bytes, writable: false), Encoding.UTF8);
            var (version, _) = ReadHeader(r);
            var length = r.ReadInt32();
            var hash = r.ReadUInt64();
            if (length < 0 || length > r.BaseStream.Length - r.BaseStream.Position)
            {
                throw new SaveFormatException("the save is cut short");
            }

            var body = r.ReadBytes(length);
            if (Hash(body) != hash)
            {
                throw new SaveFormatException("the save is damaged");
            }

            using var b = new BinaryReader(new MemoryStream(body, writable: false), Encoding.UTF8);
            var save = ReadBody(b, version);
            if (b.BaseStream.Position != b.BaseStream.Length)
            {
                throw new SaveFormatException("the save has bytes past its end");
            }

            return save;
        }
        catch (Exception e) when (e is EndOfStreamException or IOException or ArgumentException or FormatException
                                      or OverflowException or DecoderFallbackException)
        {
            throw new SaveFormatException($"the save does not read ({e.Message})", e);
        }
    }

    /// <summary>The slot bar's text of the save in <paramref name="stream"/>, null when it is not one
    /// this build reads.</summary>
    public static string? ReadLabel(Stream stream)
    {
        try
        {
            using var r = new BinaryReader(stream, Encoding.UTF8, leaveOpen: true);
            return ReadHeader(r).Label;
        }
        catch (Exception e) when (e is SaveFormatException or EndOfStreamException or IOException or FormatException
                                      or DecoderFallbackException)
        {
            return null;
        }
    }

    private static (ushort Version, string Label) ReadHeader(BinaryReader r)
    {
        Span<byte> magic = stackalloc byte[8];
        if (r.Read(magic) != magic.Length || !magic.SequenceEqual(Magic))
        {
            throw new SaveFormatException("not a save file");
        }

        var version = r.ReadUInt16();
        if (version is < OldestVersion or > Version)
        {
            throw new SaveFormatException($"save format {version}, this build reads {OldestVersion} to {Version}");
        }

        return (version, r.ReadString());
    }

    /// <summary>FNV-1a, 64 bits.</summary>
    private static ulong Hash(ReadOnlySpan<byte> bytes)
    {
        var h = 14695981039346656037UL;
        foreach (var b in bytes)
        {
            h = (h ^ b) * 1099511628211UL;
        }

        return h;
    }

    // ---------------------------------------------------------------- the body

    private static void WriteBody(BinaryWriter w, SaveGame s, ushort version)
    {
        w.Write((int)s.Game);
        w.Write(s.Mission);
        w.Write(s.Map);
        w.Write(s.PreviousMap);
        Vec(w, s.Feet);
        w.Write(s.Yaw);
        w.Write(s.Pitch);
        w.Write(s.Noclip);
        w.Write((int)s.Vehicle);
        w.Write(s.Health);
        w.Write(s.Armour);
        Kit(w, s.Kit);
        Kit(w, s.StartKit);
        w.Write(s.StartHealth);
        w.Write(s.StartArmour);
        w.Write(s.Progress.Left);
        w.Write(s.Progress.Complete);
        w.Write(s.Progress.Failed);
        Ints(w, s.Progress.Cursor);
        w.Write(s.CompleteIn.HasValue);
        w.Write(s.CompleteIn ?? 0f);
        Array(w, s.Maps, (w2, m) => MapState(w2, m, version));
        Array(w, s.Robots, Robot);
        w.Write(s.Saved.Ticks);
        if (version < 2)
        {
            return;
        }

        w.Write(s.Ride != null);
        if (s.Ride is { } v)
        {
            w.Write((int)v.Mode);
            w.Write(v.Hull);
            w.Write(v.Speed);
            w.Write(v.CarYaw);
            w.Write(v.AimYaw);
            w.Write(v.AimPitch);
            w.Write(v.Energy);
            w.Write(v.Overheated);
            w.Write(v.Altitude);
        }

        if (version < 3)
        {
            return;
        }

        var stats = s.Stats ?? new SavedStats(null, -1, default, default);
        w.Write(stats.Mission.HasValue);
        Counters(w, stats.Mission ?? default);
        w.Write(stats.Row);
        Counters(w, stats.Career);
        Counters(w, stats.CareerAtStart);
        if (version < 4)
        {
            return;
        }

        w.Write(s.Pilot != null);
        if (s.Pilot is { } pilot)
        {
            w.Write(pilot.Name);
            w.Write(pilot.CallSign);
        }

        if (version < 6)
        {
            return;
        }

        OptString(w, s.Instance);
        OptString(w, s.PreviousInstance);
        if (version < 7)
        {
            return;
        }

        w.Write(s.RandomState.HasValue);
        w.Write(s.RandomState ?? 0UL);
        if (version < 8)
        {
            return;
        }

        Array(w, s.Robots, (w2, x) =>
        {
            w2.Write((byte)x.SenseCount);
            w2.Write(x.Perceived);
        });
        if (version < 9)
        {
            return;
        }

        Ints(w, s.Seen);
    }

    private static void OptString(BinaryWriter w, string? text)
    {
        w.Write(text != null);
        if (text != null)
        {
            w.Write(text);
        }
    }

    private static string? OptString(BinaryReader r)
    {
        if (!r.ReadBoolean())
        {
            return null;
        }

        var text = r.ReadString();
        return text.Length is > 0 and <= 256 ? text : throw new SaveFormatException("an instance id that is empty or too long");
    }

    private static void Counters(BinaryWriter w, StatCounters c)
    {
        w.Write(c.Shots);
        w.Write(c.Hits);
        w.Write(c.Enemies);
        w.Write(c.Kills);
    }

    private static StatCounters Counters(BinaryReader r) => new(r.ReadInt32(), r.ReadInt32(), r.ReadInt32(), r.ReadInt32());

    private static SaveGame ReadBody(BinaryReader r, ushort version)
    {
        var game = (GameKind)r.ReadInt32();
        var mission = r.ReadInt32();
        var map = r.ReadInt32();
        var previous = r.ReadInt32();
        var feet = Vec(r);
        var yaw = r.ReadSingle();
        var pitch = r.ReadSingle();
        var noclip = r.ReadBoolean();
        var vehicle = (PlayerMode)r.ReadInt32();
        var health = r.ReadSingle();
        var armour = r.ReadSingle();
        var kit = Kit(r);
        var startKit = Kit(r);
        var startHealth = r.ReadSingle();
        var startArmour = r.ReadSingle();
        var progress = new ProgressSnapshot(r.ReadInt32(), r.ReadBoolean(), r.ReadBoolean(), Ints(r));
        var hasComplete = r.ReadBoolean();
        var completeIn = r.ReadSingle();
        var maps = Array(r, x => MapState(x, version));
        var robots = Array(r, Robot);
        var saved = new DateTime(r.ReadInt64());
        SavedVehicle? ride = null;
        if (version >= 2 && r.ReadBoolean())
        {
            ride = new SavedVehicle((PlayerMode)r.ReadInt32(), r.ReadSingle(), r.ReadSingle(), r.ReadSingle(), r.ReadSingle(),
                r.ReadSingle(), r.ReadInt32(), r.ReadBoolean(), r.ReadSingle());
            if (!Enum.IsDefined(ride.Mode) || ride.Mode == PlayerMode.Foot)
            {
                throw new SaveFormatException($"unknown vehicle mode {(int)ride.Mode}");
            }
        }

        SavedStats? stats = null;
        if (version >= 3)
        {
            var hasRow = r.ReadBoolean();
            var row = Counters(r);
            stats = new SavedStats(hasRow ? row : null, r.ReadInt32(), Counters(r), Counters(r));
        }

        Pilot? pilot = null;
        if (version >= 4 && r.ReadBoolean())
        {
            var name = r.ReadString();
            var callSign = r.ReadString();
            if (name.Length > Pilot.MaxLength * 4 || callSign.Length > Pilot.MaxLength * 4)
            {
                throw new SaveFormatException("a pilot's name too long");
            }

            pilot = new Pilot(name, callSign);
        }

        string? instance = null, previousInstance = null;
        if (version >= 6)
        {
            instance = OptString(r);
            previousInstance = OptString(r);
        }

        ulong? randomState = null;
        if (version >= 7)
        {
            var has = r.ReadBoolean();
            var state = r.ReadUInt64();
            randomState = has ? state : null;
        }

        if (version >= 8)
        {
            var senses = Array(r, x => (Count: x.ReadByte() & 3, Perceived: x.ReadBoolean()));
            if (senses.Length != robots.Length)
            {
                throw new SaveFormatException("the robots' perception list does not match the robots");
            }

            for (var i = 0; i < robots.Length; i++)
            {
                robots[i] = robots[i] with { SenseCount = senses[i].Count, Perceived = senses[i].Perceived };
            }
        }

        var seen = version >= 9 ? Ints(r) : [];
        if (!Enum.IsDefined(game) || game == GameKind.None)
        {
            throw new SaveFormatException($"unknown game {(int)game}");
        }

        return new SaveGame(game, mission, map, previous, feet, yaw, pitch, noclip, vehicle, health, armour, kit, startKit,
            startHealth, startArmour, progress, hasComplete ? completeIn : null, maps, robots, saved, ride)
        {
            Stats = stats,
            Pilot = pilot,
            Instance = instance,
            PreviousInstance = previousInstance,
            RandomState = randomState,
            Seen = seen,
        };
    }

    private static void MapState(BinaryWriter w, SavedMap m, ushort version)
    {
        w.Write(m.Map);
        Behaviour(w, m.Behaviour);
        Ints(w, m.Taken);
        Ints(w, m.Dead);
        Array(w, m.Loops, (w2, l) =>
        {
            w2.Write(l.Source);
            w2.Write(l.Sound);
            Vec(w2, l.At);
        });
        if (version < 3)
        {
            return;
        }

        Array(w, m.Drops, (w2, d) =>
        {
            w2.Write(d.Id);
            w2.Write(d.Sprite);
            Vec(w2, d.At);
        });
        w.Write(m.NextDrop);
        if (version >= 5)
        {
            Ints(w, m.Behaviour.Turned ?? []);
        }

        if (version >= 6)
        {
            OptString(w, m.Instance);
        }
    }

    private static SavedMap MapState(BinaryReader r, ushort version)
    {
        var map = new SavedMap(r.ReadInt32(), Behaviour(r), Ints(r), Ints(r), Array(r, r2 => new SavedLoop(r2.ReadInt32(), r2.ReadInt32(), Vec(r2))));
        if (version < 3)
        {
            return map;
        }

        var drops = Array(r, x => new DroppedSprite(x.ReadInt32(), x.ReadInt32(), Vec(x)));
        if (drops.Any(d => d.Id >= 0))
        {
            throw new SaveFormatException("a drop with a record's id");
        }

        map = map with { Drops = drops, NextDrop = r.ReadInt32() };
        if (version < 5)
        {
            return map;
        }

        map = map with { Behaviour = map.Behaviour with { Turned = Ints(r) } };
        return version < 6 ? map : map with { Instance = OptString(r) };
    }

    private static void Behaviour(BinaryWriter w, BehaviourSnapshot b)
    {
        Array(w, b.Records, (w2, x) =>
        {
            w2.Write(x.Offset);
            w2.Write(x.State);
            w2.Write(x.Act);
            w2.Write(x.Link);
        });
        Array(w, b.HitPoints, (w2, x) =>
        {
            w2.Write(x.Offset);
            w2.Write(x.Hp);
        });
        Ints(w, b.Spent);
        Ints(w, b.CueSpent);
        Ints(w, b.Hidden);
        Ints(w, b.NoCollide);
        Array(w, b.Movers, (w2, x) =>
        {
            w2.Write(x.Offset);
            w2.Write(x.Progress);
            w2.Write(x.Dir);
            w2.Write(x.Running);
        });
        Array(w, b.Triggers, (w2, x) =>
        {
            w2.Write(x.Offset);
            w2.Write(x.Latched);
            w2.Write(x.EdgeDone);
            w2.Write(x.ExitWalked);
        });
        Array(w, b.Doorways, (w2, x) =>
        {
            w2.Write(x.Offset);
            w2.Write(x.Touching);
        });
        Array(w, b.Wrecks, (w2, x) =>
        {
            w2.Write(x.Offset);
            w2.Write(x.Stage);
            w2.Write(x.Accum);
        });
        Array(w, b.Lights, (w2, x) =>
        {
            w2.Write(x.Offset);
            w2.Write(x.Enable);
            w2.Write(x.Intensity);
            w2.Write(x.FxShowing);
            w2.Write(x.FxOn);
            w2.Write(x.Announced);
        });
        w.Write(b.LightClock);
        Array(w, b.Paths, (w2, x) =>
        {
            w2.Write(x.Id);
            w2.Write(x.Started);
            Vec(w2, x.Position);
            w2.Write(x.Yaw);
            w2.Write(x.Target);
            w2.Write(x.Speed);
            w2.Write(x.TargetSpeed);
            w2.Write(x.Finished);
            w2.Write(x.Dead);
        });
    }

    private static BehaviourSnapshot Behaviour(BinaryReader r) => new(
        Array(r, x => new RecordBytes(x.ReadInt32(), x.ReadInt32(), x.ReadInt32(), x.ReadInt32())),
        Array(r, x => (x.ReadInt32(), x.ReadSingle())),
        Ints(r),
        Ints(r),
        Ints(r),
        Ints(r),
        Array(r, x => new MoverSnapshot(x.ReadInt32(), x.ReadSingle(), x.ReadSingle(), x.ReadBoolean())),
        Array(r, x => new TriggerSnapshot(x.ReadInt32(), x.ReadBoolean(), x.ReadBoolean(), x.ReadBoolean())),
        Array(r, x => (x.ReadInt32(), x.ReadBoolean())),
        Array(r, x => new WreckSnapshot(x.ReadInt32(), x.ReadInt32(), x.ReadSingle())),
        Array(r, x => new LightSnapshot(x.ReadInt32(), x.ReadInt32(), x.ReadInt32(), x.ReadBoolean(), x.ReadBoolean(), x.ReadBoolean())),
        r.ReadSingle(),
        Array(r, x => new PathVehicleSnapshot(x.ReadInt32(), x.ReadBoolean(), Vec(x), x.ReadSingle(), x.ReadInt32(), x.ReadSingle(),
            x.ReadSingle(), x.ReadBoolean(), x.ReadBoolean())));

    private static void Robot(BinaryWriter w, RobotSnapshot x)
    {
        w.Write(x.MarkerOffset);
        w.Write(x.SpawnSource);
        Vec(w, x.Position);
        w.Write(x.Yaw);
        w.Write(x.Hp);
        w.Write(x.Dead);
        w.Write(x.Hidden);
        w.Write(x.DormantDistance);
        w.Write(x.DeathAct);
        w.Write(x.InitDone);
        w.Write(x.Alert);
    }

    private static RobotSnapshot Robot(BinaryReader r) => new(r.ReadInt32(), r.ReadInt32(), Vec(r), r.ReadSingle(), r.ReadSingle(),
        r.ReadBoolean(), r.ReadBoolean(), r.ReadSingle(), r.ReadInt32(), r.ReadBoolean(), r.ReadBoolean());

    private static void Kit(BinaryWriter w, InventoryState k)
    {
        Array(w, k.Owned, (w2, b) => w2.Write(b));
        Ints(w, k.Pools);
        w.Write(k.Primary);
        w.Write(k.Thrown);
    }

    private static InventoryState Kit(BinaryReader r) => new(Array(r, x => x.ReadBoolean()), Ints(r), r.ReadInt32(), r.ReadInt32());

    private static void Vec(BinaryWriter w, Vector3 v)
    {
        w.Write(v.X);
        w.Write(v.Y);
        w.Write(v.Z);
    }

    private static Vector3 Vec(BinaryReader r) => new(r.ReadSingle(), r.ReadSingle(), r.ReadSingle());

    private static void Ints(BinaryWriter w, int[] values) => Array(w, values, (w2, v) => w2.Write(v));

    private static int[] Ints(BinaryReader r) => Array(r, x => x.ReadInt32());

    private static void Array<T>(BinaryWriter w, IReadOnlyList<T> items, Action<BinaryWriter, T> write)
    {
        w.Write(items.Count);
        foreach (var item in items)
        {
            write(w, item);
        }
    }

    private static T[] Array<T>(BinaryReader r, Func<BinaryReader, T> read)
    {
        var count = r.ReadInt32();
        if (count < 0 || count > MaxCount || count > r.BaseStream.Length - r.BaseStream.Position)
        {
            throw new SaveFormatException($"a list of {count} items does not fit the save");
        }

        var items = new T[count];
        for (var i = 0; i < count; i++)
        {
            items[i] = read(r);
        }

        return items;
    }
}

/// <summary>
/// The ten LOAD/SAVE slots on disk: <c>slotN.sav</c> (N = 0..9) in <see cref="DefaultFolder"/>
/// (<c>%APPDATA%/SkyNET-CSharp/saves</c>, Future Shock's under <c>saves/shock</c>: the two games
/// number their maps differently). Apart from the Godot port's <c>user://</c> saves, which this
/// never reads or writes. A write goes through a temporary file, so a crash leaves the old save.
/// </summary>
public sealed class SaveSlots(string folder)
{
    public const int Count = 10;
    /// <summary>F6 / F7 (the Godot port's quicksave slot: the first LOAD bar).</summary>
    public const int QuickSlot = 0;

    public string Folder { get; } = folder;

    /// <summary>The slots of <paramref name="game"/>: saves\ beside the game (Future Shock's in saves\shock).</summary>
    public static string DefaultFolder(GameKind game)
    {
        var root = Path.Combine(Menu.SettingsStore.UserFolder, "saves");
        return game == GameKind.FutureShock ? Path.Combine(root, "shock") : root;
    }

    public string PathOf(int slot) => Path.Combine(Folder, string.Create(CultureInfo.InvariantCulture, $"slot{slot}.sav"));

    /// <summary>The slot's bar text, null when it is empty or not a save this build reads.</summary>
    public string? Label(int slot)
    {
        if (slot < 0 || slot >= Count || !File.Exists(PathOf(slot)))
        {
            return null;
        }

        try
        {
            using var f = File.OpenRead(PathOf(slot));
            return SaveFile.ReadLabel(f);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    /// <summary>Writes <paramref name="save"/> to the slot; null on success, else why it failed.</summary>
    public string? Write(int slot, SaveGame save)
    {
        if (slot < 0 || slot >= Count)
        {
            return $"no slot {slot}";
        }

        var path = PathOf(slot);
        var temp = path + ".tmp";
        try
        {
            Directory.CreateDirectory(Folder);
            File.WriteAllBytes(temp, SaveFile.Write(save));
            File.Move(temp, path, overwrite: true);
            return null;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            try
            {
                File.Delete(temp);
            }
            catch (Exception d) when (d is IOException or UnauthorizedAccessException)
            {
                // The temporary file stays; the slot itself is untouched.
            }

            return e.Message;
        }
    }

    /// <summary>The save in the slot; throws <see cref="SaveFormatException"/> when it is empty, unreadable or refused.</summary>
    public SaveGame Read(int slot)
    {
        var path = PathOf(slot);
        if (slot < 0 || slot >= Count || !File.Exists(path))
        {
            throw new SaveFormatException($"slot {slot} is empty");
        }

        try
        {
            return SaveFile.Read(File.ReadAllBytes(path));
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            throw new SaveFormatException($"slot {slot} does not read ({e.Message})", e);
        }
    }
}
