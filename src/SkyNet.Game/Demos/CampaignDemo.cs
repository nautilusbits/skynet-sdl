using System.Globalization;
using System.Reflection;
using System.Text;
using RetroEng.Commands;
using RetroEng.Loop;
using SkyNet.Formats;
using SkyNet.Game.Campaign;
using SkyNet.Game.DevTools;
using SkyNet.Game.Enemies;
using SkyNet.Game.Weapons;

namespace SkyNet.Game.Demos;

/// <summary>How a demo's run starts: a mission from its first map (or a side map), or a save.</summary>
public enum DemoStartKind { Mission, Save }

/// <summary>
/// SkyNET's demos on the engine's <see cref="DemoData"/> (docs/demos.md): what the header holds,
/// how a fresh session is put where the recording began, and the events between ticks (console
/// commands that change the game, RESTART MISSION, a DIFFICULTY change).
/// <code>
///   record:   fresh session ─► Start(mission | save) ─► each tick: input ─► session ─► hashes ─► file
///   playback: fresh session ─► Begin(demo)           ─► each tick: events, recorded input ─► session ─► compare
/// </code>
/// </summary>
public static class CampaignDemo
{
    public const string Extension = ".sndemo";
    /// <summary>The folder beside the game the demos go in.</summary>
    public const string FolderName = "demos";

    /// <summary>Header keys.</summary>
    public const string KeyGame = "game", KeyBuild = "build", KeyData = "data", KeyStart = "start", KeyMission = "mission",
        KeyMap = "map", KeyDifficulty = "difficulty", KeySeed = "seed", KeyMods = "mods", KeyBlocks = "blocks",
        KeyTickRate = "tick rate", KeyRecorded = "recorded", KeyRobotAi = "robot ai";

    /// <summary>The event that restarts the mission (RESTART MISSION chosen on the box).</summary>
    public const string RestartEvent = "!restart";
    /// <summary>The event that changes DIFFICULTY ("!difficulty High").</summary>
    public const string DifficultyEvent = "!difficulty";
    /// <summary>The event that changes ROBOT AI ("!robotai Improved").</summary>
    public const string RobotAiEvent = "!robotai";

    /// <summary>The demos folder beside the game.</summary>
    public static string DefaultFolder => Path.Combine(Menu.SettingsStore.UserFolder, FolderName);

    /// <summary>The file of demo <paramref name="name"/>: a path as given, else <c>demos/&lt;name&gt;.sndemo</c>.</summary>
    public static string PathOf(string name, string? folder = null)
    {
        if (name.Contains('/', StringComparison.Ordinal) || name.Contains('\\', StringComparison.Ordinal) || Path.IsPathRooted(name))
        {
            return Path.HasExtension(name) ? name : name + Extension;
        }

        return Path.Combine(folder ?? DefaultFolder, name.EndsWith(Extension, StringComparison.OrdinalIgnoreCase) ? name : name + Extension);
    }

    /// <summary>This build (the assembly's informational version: the version and the commit).</summary>
    public static string Build =>
        typeof(CampaignDemo).Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion
        ?? typeof(CampaignDemo).Assembly.GetName().Version?.ToString() ?? "?";

    /// <summary>The session commands a recording keeps (the rest only read the game).</summary>
    public static IReadOnlySet<string> RecordedCommands { get; } = SessionCommandNames();

    private static HashSet<string> SessionCommandNames()
    {
        var registry = new CommandRegistry();
        SessionCommands.Register(registry, () => throw new InvalidOperationException());
        var names = registry.Commands.Select(c => c.Name).ToHashSet(StringComparer.OrdinalIgnoreCase);
        // Those that only print.
        names.ExceptWith(["cheats", "maps", "pos", "whoami", "enemies", "objectives"]);
        return names;
    }

    // ---------------------------------------------------------------- the header

    /// <summary>A new demo of <paramref name="session"/> about to start: the header but the start.</summary>
    public static DemoData Create(GameSession session, CampaignControls controls, DifficultyLevel difficulty, string modsDirectory, bool baked)
    {
        var d = new DemoData
        {
            Actions = controls.Names,
            HashNames = [.. SessionHash.Components],
            ProbeNames = [.. SessionHash.ProbeNames],
            HashInterval = 1,
        };
        d.SetField(KeyGame, session.Data.Game.ToString());
        d.SetField(KeyBuild, Build);
        d.SetField(KeyData, DataFingerprint(session.Data));
        d.SetField(KeyDifficulty, difficulty.ToString());
        d.SetField(KeyRobotAi, session.RobotAi.ToString());
        d.SetField(KeyMods, ModsField(modsDirectory, Mods.ModContent.TablesFrom));
        d.SetField(KeyBlocks, baked ? "baked" : "raw");
        d.SetField(KeyTickRate, GameSession.TicksPerSecond.ToString(CultureInfo.InvariantCulture));
        d.SetField(KeyRecorded, DateTime.UtcNow.ToString("yyyy-MM-dd HH:mm:ss'Z'", CultureInfo.InvariantCulture));
        return d;
    }

    /// <summary>The start: mission <paramref name="mission"/> (on <paramref name="map"/>, a side map of
    /// it, when given) with the kit the soldier carries now and the generator's state now.</summary>
    public static void SetMissionStart(DemoData d, GameSession session, int mission, int? map)
    {
        d.SetField(KeyStart, nameof(DemoStartKind.Mission).ToLowerInvariant());
        d.SetField(KeyMission, mission.ToString(CultureInfo.InvariantCulture));
        d.SetField(KeyMap, (map ?? session.Table.ByNumber(mission)?.StartMap ?? 0).ToString(CultureInfo.InvariantCulture));
        d.SetField(KeySeed, session.Random.State.ToString("x16", CultureInfo.InvariantCulture));
        using var m = new MemoryStream();
        using (var w = new BinaryWriter(m, Encoding.UTF8, leaveOpen: true))
        {
            w.Write(mission);
            w.Write(map ?? -1);
            var kit = session.Weapons.Inventory.Save();
            w.Write(kit.Owned.Length);
            foreach (var o in kit.Owned)
            {
                w.Write(o);
            }

            w.Write(kit.Pools.Length);
            foreach (var p in kit.Pools)
            {
                w.Write(p);
            }

            w.Write(kit.Primary);
            w.Write(kit.Thrown);
        }

        d.Start = m.ToArray();
    }

    /// <summary>The start: <paramref name="save"/> (its generator state included).</summary>
    public static void SetSaveStart(DemoData d, SaveGame save)
    {
        d.SetField(KeyStart, nameof(DemoStartKind.Save).ToLowerInvariant());
        d.SetField(KeyMission, save.Mission.ToString(CultureInfo.InvariantCulture));
        d.SetField(KeyMap, save.Map.ToString(CultureInfo.InvariantCulture));
        d.SetField(KeySeed, (save.RandomState ?? 0).ToString("x16", CultureInfo.InvariantCulture));
        d.Start = SaveFile.Write(save);
    }

    public static DemoStartKind StartKind(DemoData d) => d.Field(KeyStart) switch
    {
        "mission" => DemoStartKind.Mission,
        "save" => DemoStartKind.Save,
        var other => throw new DemoFormatException($"unknown start '{other}'"),
    };

    public static DifficultyLevel DifficultyOf(DemoData d) =>
        Enum.TryParse<DifficultyLevel>(d.Field(KeyDifficulty), out var level) ? level : DifficultyLevel.Medium;

    /// <summary>The ROBOT AI the demo was recorded with; a demo from before the setting: CLASSIC.</summary>
    public static RobotAi RobotAiOf(DemoData d) =>
        Enum.TryParse<RobotAi>(d.Field(KeyRobotAi), out var ai) && Enum.IsDefined(ai) ? ai : RobotAi.Classic;

    /// <summary>Why <paramref name="d"/> cannot be played on <paramref name="data"/> (null: it can), and
    /// the warnings that may explain a desync (another build, other data, other mods).</summary>
    public static string? Refusal(DemoData d, GameData data, string modsDirectory, bool baked, List<string> warnings)
    {
        if (d.Field(KeyGame) != data.Game.ToString())
        {
            return $"the demo is of {d.Field(KeyGame)}, the data is {data.Game}";
        }

        if (d.Field(KeyTickRate) != GameSession.TicksPerSecond.ToString(CultureInfo.InvariantCulture))
        {
            return $"the demo ticks at {d.Field(KeyTickRate)} a second, this build at {GameSession.TicksPerSecond}";
        }

        try
        {
            _ = StartKind(d);
        }
        catch (DemoFormatException e)
        {
            return e.Message;
        }

        if (d.Field(KeyBuild) != Build)
        {
            warnings.Add($"recorded by build {d.Field(KeyBuild)}, this is {Build}");
        }

        if (d.Field(KeyData) != DataFingerprint(data))
        {
            warnings.Add("recorded on other game data");
        }

        if (d.Field(KeyMods) != ModsField(modsDirectory, Mods.ModContent.TablesFrom))
        {
            warnings.Add($"recorded with mods [{d.Field(KeyMods)}], now [{ModsField(modsDirectory, Mods.ModContent.TablesFrom)}]");
        }

        if (d.Field(KeyBlocks) != (baked ? "baked" : "raw"))
        {
            warnings.Add($"recorded with {d.Field(KeyBlocks)} map collision, now {(baked ? "baked" : "raw")}");
        }

        return null;
    }

    /// <summary>Puts a fresh <paramref name="session"/> where the recording began: DIFFICULTY, then the
    /// save loaded, or the kit and the generator set and the mission started.</summary>
    public static void Begin(GameSession session, DemoData d)
    {
        session.Difficulty = Difficulty.Of(DifficultyOf(d));
        session.RobotAi = RobotAiOf(d);
        if (StartKind(d) == DemoStartKind.Save)
        {
            session.Load(SaveFile.Read(d.Start));
            return;
        }

        using var r = new BinaryReader(new MemoryStream(d.Start, writable: false), Encoding.UTF8);
        var mission = r.ReadInt32();
        var map = r.ReadInt32();
        var owned = new bool[r.ReadInt32()];
        for (var i = 0; i < owned.Length; i++)
        {
            owned[i] = r.ReadBoolean();
        }

        var pools = new int[r.ReadInt32()];
        for (var i = 0; i < pools.Length; i++)
        {
            pools[i] = r.ReadInt32();
        }

        session.Weapons.Inventory.Restore(new InventoryState(owned, pools, r.ReadInt32(), r.ReadInt32()));
        session.Random.State = ulong.Parse(d.Field(KeySeed) ?? "1", NumberStyles.HexNumber, CultureInfo.InvariantCulture);
        if (session.Table.ByNumber(mission) is { } m)
        {
            session.StartMission(m, map >= 0 ? map : null);
        }
        else
        {
            session.StartAt(map);
        }
    }

    /// <summary>Applies an event before its tick: RESTART MISSION, a DIFFICULTY change, or a console
    /// command run through <paramref name="commands"/>.</summary>
    public static void Apply(GameSession session, string text, CommandRegistry commands)
    {
        if (text == RestartEvent)
        {
            session.RestartMission();
        }
        else if (text.StartsWith(DifficultyEvent + " ", StringComparison.Ordinal)
                 && Enum.TryParse<DifficultyLevel>(text[(DifficultyEvent.Length + 1)..], out var level))
        {
            session.Difficulty = Difficulty.Of(level);
        }
        else if (text.StartsWith(RobotAiEvent + " ", StringComparison.Ordinal)
                 && Enum.TryParse<RobotAi>(text[(RobotAiEvent.Length + 1)..], out var ai))
        {
            session.RobotAi = ai;
        }
        else
        {
            commands.Run(text);
        }
    }

    /// <summary>A command's words as one line that parses back to them.</summary>
    public static string Line(string[] words) => string.Join(' ', words.Select(w =>
        w.Length == 0 || w.Any(c => char.IsWhiteSpace(c) || c is ';' or '"') ? $"\"{w.Replace("\"", "", StringComparison.Ordinal)}\"" : w));

    // ---------------------------------------------------------------- fingerprints

    /// <summary>The data folder's files by name and size (cheap; a different set of archives shows).</summary>
    public static string DataFingerprint(GameData data)
    {
        var h = new StateHash();
        try
        {
            foreach (var f in new DirectoryInfo(data.Dir).GetFiles().OrderBy(f => f.Name.ToUpperInvariant(), StringComparer.Ordinal))
            {
                foreach (var c in f.Name.ToUpperInvariant())
                {
                    h.Add(c);
                }

                h.Add(f.Length);
            }
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            return "?";
        }

        return h.Value.ToString("x16", CultureInfo.InvariantCulture);
    }

    /// <summary>The mods the tables come from, each with a hash: a folder's tables, maps and missions
    /// by content and its other files by size; a ZIP by its size.</summary>
    public static string ModsField(string modsDirectory, IReadOnlyList<string> mods) =>
        string.Join(';', mods.Select(m => $"{m}={ModHash(modsDirectory, m)}"));

    private static string ModHash(string modsDirectory, string name)
    {
        var h = new StateHash();
        try
        {
            var dir = Path.Combine(modsDirectory, name);
            var zip = dir + ".zip";
            if (Directory.Exists(dir))
            {
                foreach (var f in Directory.EnumerateFiles(dir, "*", SearchOption.AllDirectories).Order(StringComparer.Ordinal))
                {
                    var rel = Path.GetRelativePath(dir, f).Replace('\\', '/');
                    foreach (var c in rel)
                    {
                        h.Add(c);
                    }

                    var info = new FileInfo(f);
                    h.Add(info.Length);
                    if (rel.StartsWith("tables/", StringComparison.OrdinalIgnoreCase) || rel.StartsWith("missions/", StringComparison.OrdinalIgnoreCase))
                    {
                        foreach (var b in File.ReadAllBytes(f))
                        {
                            h.Add(b);
                        }
                    }
                }
            }
            else if (File.Exists(zip))
            {
                h.Add(new FileInfo(zip).Length);
            }
            else
            {
                return "missing";
            }
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            return "?";
        }

        return h.Value32.ToString("x8", CultureInfo.InvariantCulture);
    }
}

/// <summary>A recording under way: the tick's input and hashes after each tick, the events between.</summary>
public sealed class CampaignRecorder
{
    private readonly DemoRecorder _recorder;
    private DifficultyLevel _difficulty;

    public CampaignRecorder(DemoData demo, string path)
    {
        _recorder = new DemoRecorder(demo);
        Path = path;
        _difficulty = CampaignDemo.DifficultyOf(demo);
        _robotAi = CampaignDemo.RobotAiOf(demo);
    }

    private RobotAi _robotAi;

    /// <summary>Before a tick: a ROBOT AI changed since the last one is kept as an event.</summary>
    public void BeforeTick(DifficultyLevel difficulty, RobotAi robotAi)
    {
        BeforeTick(difficulty);
        if (robotAi != _robotAi)
        {
            _robotAi = robotAi;
            _recorder.Event($"{CampaignDemo.RobotAiEvent} {robotAi}");
        }
    }

    public DemoData Demo => _recorder.Demo;
    public string Path { get; }
    public int Ticks => _recorder.Tick;

    /// <summary>Before a tick: a DIFFICULTY changed since the last one is kept as an event.</summary>
    public void BeforeTick(DifficultyLevel difficulty)
    {
        if (difficulty != _difficulty)
        {
            _difficulty = difficulty;
            _recorder.Event($"{CampaignDemo.DifficultyEvent} {difficulty}");
        }
    }

    /// <summary>After a tick that ran with <paramref name="input"/>.</summary>
    public void AfterTick(GameSession session, in TickInput input)
    {
        Span<uint> hashes = stackalloc uint[SessionHash.Components.Length];
        Span<float> probes = stackalloc float[SessionHash.ProbeNames.Length];
        SessionHash.Compute(session, hashes, probes);
        _recorder.Record(input, hashes, probes);
    }

    /// <summary>A console command ran: kept when it changes the game.</summary>
    public void Command(Command command, string[] words)
    {
        if (CampaignDemo.RecordedCommands.Contains(command.Name))
        {
            _recorder.Event(CampaignDemo.Line(words));
        }
    }

    /// <summary>RESTART MISSION was chosen.</summary>
    public void Restart() => _recorder.Event(CampaignDemo.RestartEvent);

    /// <summary>Writes the demo (its folder made); the file's path.</summary>
    public string Save()
    {
        Directory.CreateDirectory(System.IO.Path.GetDirectoryName(System.IO.Path.GetFullPath(Path))!);
        DemoFile.Write(Path, Demo);
        return Path;
    }
}

/// <summary>A demo played back on a session: before each tick the due events and the recorded
/// input, after it the hashes compared.</summary>
public sealed class CampaignPlayback(DemoData demo, CampaignControls controls)
{
    public DemoCursor Cursor { get; } = new(demo, controls.Names);
    public DemoData Demo => Cursor.Demo;
    public bool Done => Cursor.Done;
    public DemoDesync? Desync => Cursor.Desync;

    /// <summary>One tick: the events due (through <paramref name="apply"/>), the recorded input into
    /// <paramref name="tick"/> (the caller's own work around the tick, then <see cref="AfterTick"/>).</summary>
    public TickInput BeforeTick(Action<string> apply)
    {
        foreach (var e in Cursor.DueEvents())
        {
            apply(e.Text);
        }

        return Cursor.Input();
    }

    /// <summary>After the tick: the hashes compared. The first desync, when this tick found it.</summary>
    public DemoDesync? AfterTick(GameSession session)
    {
        Span<uint> hashes = stackalloc uint[SessionHash.Components.Length];
        Span<float> probes = stackalloc float[SessionHash.ProbeNames.Length];
        SessionHash.Compute(session, hashes, probes);
        return Cursor.Check(hashes, probes);
    }

    /// <summary>One whole tick on <paramref name="session"/> through <paramref name="controls"/>.</summary>
    public DemoDesync? Step(GameSession session, Action<string> apply)
    {
        var input = BeforeTick(apply);
        controls.Tick(session, input);
        return AfterTick(session);
    }
}
