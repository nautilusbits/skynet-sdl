using System.Globalization;
using System.Numerics;
using RetroEng.Loop;
using RetroEng.Net;
using SkyNet.Formats;
using SkyNet.Game.Net;

namespace SkyNet.Game.Demos;

/// <summary>Who made a network demo: a client, a host (what a client playing as the host's player
/// would receive), or a dedicated server (what a client playing as nobody would receive).</summary>
public enum NetDemoRole { Client, Host, Dedicated }

/// <summary>The recording player's own records in a network demo (<see cref="NetDemoKind.Local"/>):
/// the first byte says which.</summary>
public static class NetDemoLocal
{
    /// <summary>Feet, view yaw, pitch, eye height, pose flags, car yaw, the weapon in hand.</summary>
    public const byte Pose = 1;
    /// <summary>A shot: weapon record, from, along.</summary>
    public const byte Fire = 2;
    /// <summary>A hit the player reported: victim, points, weapon.</summary>
    public const byte Hit = 3;
    /// <summary>A keyframe's local state: spawned, health, armour, dead (the death view), the round's end.</summary>
    public const byte State = 4;

    public static byte[] WritePose(NetWriter w, Vector3 feet, float yaw, float pitch, float eye, int flags, float carYaw, int weapon) =>
        w.Begin(Pose).Vec3(feet).F32(yaw).F32(pitch).F32(eye).I32(flags).F32(carYaw).I32(weapon).ToArray();

    public static byte[] WriteFire(NetWriter w, int weapon, Vector3 from, Vector3 dir) => w.Begin(Fire).I32(weapon).Vec3(from).Vec3(dir).ToArray();

    public static byte[] WriteHit(NetWriter w, int victim, float points, int weapon) => w.Begin(Hit).I32(victim).F32(points).I32(weapon).ToArray();

    public static byte[] WriteState(NetWriter w, bool spawned, float health, float armour, bool dead, string? roundOver) =>
        w.Begin(State).Bool(spawned).F32(health).F32(armour).Bool(dead).Str(roundOver ?? "", 256).ToArray();
}

/// <summary>One own pose of the recording player, read back.</summary>
public readonly record struct NetDemoPose(long Time, Vector3 Feet, float Yaw, float Pitch, float Eye, int Flags, float CarYaw, int Weapon)
{
    public static NetDemoPose Read(long time, byte[] data)
    {
        var r = new NetReader(data.AsSpan(1));
        return new NetDemoPose(time, r.Vec3(), r.F32(), r.F32(), r.F32(), r.I32(), r.F32(), r.I32());
    }
}

/// <summary>
/// SkyNET's network demos (docs/demos.md "Network demos") on the engine's <see cref="NetDemoData"/>:
/// the header fields, the keyframe snapshot, and the refusal of another game or protocol.
/// </summary>
public static class NetDemo
{
    /// <summary>A keyframe every this many seconds.</summary>
    public const double KeyframeSeconds = 10.0;

    /// <summary>Own poses go into the demo at most this often (a second).</summary>
    public const double PoseHz = 60.0;

    public const string KeyGame = "game", KeyBuild = "build", KeyData = "data", KeyProtocol = "protocol", KeyRole = "role",
        KeyLocalId = "local id", KeyLocalName = "local name", KeyServer = "server", KeyMap = "map", KeyArena = "arena", KeyGoal = "goal",
        KeyGoalLimit = "goal limit", KeyTime = "time limit", KeyClasses = "classes", KeyCheats = "cheats", KeyBots = "bots",
        KeyBotSkill = "bot skill", KeyReplenish = "replenish", KeyKeyframes = "keyframe seconds", KeyRecorded = "recorded";

    /// <summary>A new demo's header: the game, the build, the protocol, who records it and the host's rules.</summary>
    public static NetDemoData Create(GameData? data, NetDemoRole role, int localId, string localName, DmSettings s)
    {
        var c = CultureInfo.InvariantCulture;
        var d = new NetDemoData();
        d.SetField(KeyGame, (data?.Game ?? GameKind.SkyNet).ToString());
        d.SetField(KeyBuild, CampaignDemo.Build);
        if (data != null)
        {
            d.SetField(KeyData, CampaignDemo.DataFingerprint(data));
        }

        d.SetField(KeyProtocol, DmProtocol.Version.ToString(c));
        d.SetField(KeyRole, role.ToString().ToLowerInvariant());
        d.SetField(KeyLocalId, localId.ToString(c));
        d.SetField(KeyLocalName, localName);
        SetRules(d, s);
        d.SetField(KeyKeyframes, KeyframeSeconds.ToString(c));
        d.SetField(KeyRecorded, DateTime.UtcNow.ToString("yyyy-MM-ddTHH:mm:ssZ", c));
        return d;
    }

    /// <summary>The host's rules in the header (the welcome carries them too; these are for listing).</summary>
    public static void SetRules(NetDemoData d, DmSettings s)
    {
        var c = CultureInfo.InvariantCulture;
        d.SetField(KeyServer, s.ServerName);
        d.SetField(KeyMap, s.Map.ToString(c));
        d.SetField(KeyArena, s.Arena);
        d.SetField(KeyGoal, s.Goal.ToString().ToLowerInvariant());
        d.SetField(KeyGoalLimit, s.GoalLimit.ToString(c));
        d.SetField(KeyTime, s.TimeLimitMinutes.ToString(c));
        d.SetField(KeyClasses, DmProtocol.RulesName(s.Classes).ToLowerInvariant());
        d.SetField(KeyCheats, s.Cheats ? "on" : "off");
        d.SetField(KeyBots, s.Bots.ToString(c));
        d.SetField(KeyBotSkill, s.BotSkill.ToString(c));
        d.SetField(KeyReplenish, s.Replenish ? "on" : "off");
    }

    /// <summary>Why this build cannot play <paramref name="d"/> (another game, another network protocol,
    /// no keyframe to start from); null when it can. Another build or other data only warn.</summary>
    public static string? Refusal(NetDemoData d, GameData? data, List<string> warnings)
    {
        var protocol = d.Field(KeyProtocol);
        if (protocol != DmProtocol.Version.ToString(CultureInfo.InvariantCulture))
        {
            return $"the demo was recorded with network protocol {protocol ?? "?"}; this build plays network protocol {DmProtocol.Version} only";
        }

        if (data != null && d.Field(KeyGame) is { } game && game != data.Game.ToString())
        {
            return $"the demo is of {game}, this game is {data.Game}";
        }

        if (d.KeyframeAt(0) < 0)
        {
            return "the demo has no keyframe to start from";
        }

        if (d.Field(KeyBuild) is { } build && build != CampaignDemo.Build)
        {
            warnings.Add($"recorded by build {build}, this is {CampaignDemo.Build}");
        }

        if (data != null && d.Field(KeyData) is { } fp && fp != CampaignDemo.DataFingerprint(data))
        {
            warnings.Add("recorded on other game data");
        }

        return null;
    }

    public static int LocalId(NetDemoData d) => int.TryParse(d.Field(KeyLocalId), NumberStyles.Integer, CultureInfo.InvariantCulture, out var id) ? id : 0;

    /// <summary>
    /// A keyframe: what a client joining now would be told (the welcome as <paramref name="localId"/>,
    /// where everybody alive stands, the map's state laid over its start, the vehicle the player
    /// drives, the round's end), then the player's own state and pose.
    /// </summary>
    public static byte[] Keyframe(int localId, DmSettings settings, IEnumerable<DmPlayer> players, IEnumerable<DmPickup> pickups, double timeLeft,
        bool running, IEnumerable<DmVehicle> vehicles, Func<DmPlayer, (Vector3 Feet, float Yaw, float Pitch, int Flags, float CarYaw)> poseOf,
        LevelUpdate? overlay, string? roundOver, byte[]? localState, byte[]? localPose)
    {
        var w = new NetWriter(1024);
        var entries = new List<(NetDemoKind, byte[])>();
        var roster = players.ToList();
        var parked = vehicles.ToList();
        entries.Add((NetDemoKind.Message, DmProtocol.WriteWelcome(w, localId, settings, roster, pickups, timeLeft, running, parked).ToArray()));
        foreach (var p in roster.OrderBy(p => p.Id))
        {
            if (p.Id != localId && p.Alive)
            {
                var (feet, yaw, pitch, flags, carYaw) = poseOf(p);
                entries.Add((NetDemoKind.Message, w.Begin(DmMsg.PoseOf).I32(p.Id).Vec3(feet).F32(yaw).F32(pitch).I32(flags).F32(carYaw).ToArray()));
            }
        }

        if (overlay != null)
        {
            (overlay with { Reset = true, Whole = true, Fired = [] }).Write(w);
            entries.Add((NetDemoKind.Message, w.ToArray()));
        }

        foreach (var v in parked.Where(v => v.Driver == localId && localId != 0))
        {
            w.Begin(DmMsg.VehicleState).U8((byte)DmVehicleEvent.Changed);
            DmProtocol.WriteVehicle(w, v);
            entries.Add((NetDemoKind.Message, w.ToArray()));
        }

        if (!running)
        {
            w.Begin(DmMsg.MatchOver).Str(roundOver ?? "", 256);
            DmProtocol.WriteScores(w, roster);
            entries.Add((NetDemoKind.Message, w.ToArray()));
        }

        if (localState != null)
        {
            entries.Add((NetDemoKind.Local, localState));
        }

        if (localPose != null)
        {
            entries.Add((NetDemoKind.Local, localPose));
        }

        return NetDemoFile.Pack(entries);
    }
}

/// <summary>
/// Records a network demo: the messages tapped from the wire (<see cref="Message"/>), the player's
/// own records (<see cref="Local"/>), a keyframe when one is due. Nothing goes in before the first
/// keyframe (a join's welcome comes as one). The clock is the owner's (<see cref="Clock"/>, seconds).
/// </summary>
public sealed class NetDemoRecorder(NetDemoData demo, string path)
{
    private double _start = double.NaN;
    private int _keys;
    private double _lastPose = double.NegativeInfinity;

    public NetDemoData Demo { get; } = demo;

    public string Path { get; } = path;

    /// <summary>The owner's clock now (seconds; any origin).</summary>
    public double Clock { get; set; }

    /// <summary>The first keyframe is in: messages are kept from now on.</summary>
    public bool Started => !double.IsNaN(_start);

    /// <summary>A keyframe is due: the first, then one a frame or so before every
    /// <see cref="NetDemo.KeyframeSeconds"/> mark, so a seek to a whole mark finds the one just before it.</summary>
    public bool KeyframeDue => !Started || Seconds + 0.1 >= _keys * NetDemo.KeyframeSeconds;

    /// <summary>An own pose is due (at most <see cref="NetDemo.PoseHz"/> a second).</summary>
    public bool PoseDue => Started && Clock - _lastPose >= 1.0 / NetDemo.PoseHz - 1e-6;

    /// <summary>Seconds recorded so far.</summary>
    public double Seconds => Started ? Clock - _start : 0;

    private long Now => (long)Math.Round((Clock - _start) * 1000.0);

    public void Keyframe(byte[] packed)
    {
        if (!Started)
        {
            _start = Clock;
        }

        _keys++;
        Demo.Entries.Add(new NetDemoEntry(Now, NetDemoKind.Keyframe, packed));
    }

    /// <summary>A message from the server (the id byte first). The password challenge is never kept.</summary>
    public void Message(ReadOnlySpan<byte> data)
    {
        if (Started && !data.IsEmpty && data[0] != DmMsg.Challenge)
        {
            Demo.Entries.Add(new NetDemoEntry(Now, NetDemoKind.Message, data.ToArray()));
        }
    }

    public void Local(byte[] data)
    {
        if (!Started)
        {
            return;
        }

        if (data.Length > 0 && data[0] == NetDemoLocal.Pose)
        {
            _lastPose = Clock;
        }

        Demo.Entries.Add(new NetDemoEntry(Now, NetDemoKind.Local, data));
    }

    /// <summary>Writes the file; the path written.</summary>
    public string Save()
    {
        NetDemoFile.Write(Path, Demo);
        return Path;
    }
}

/// <summary>
/// Plays a network demo's entries against a clock: the messages go into a <see cref="ReplayTransport"/>
/// for the client code to poll, the own records to the game, a keyframe only where playback starts or
/// seeks to. Speed and pause are the owner's (it decides how far <see cref="Advance"/> goes).
/// </summary>
public sealed class NetDemoPlayer
{
    private int _next;
    private readonly List<int> _keyframes;
    private readonly List<int> _poses = [];

    public NetDemoPlayer(NetDemoData demo)
    {
        Demo = demo;
        _keyframes = demo.Keyframes();
        for (var i = 0; i < demo.Entries.Count; i++)
        {
            var e = demo.Entries[i];
            if (e.Kind == NetDemoKind.Local && e.Data.Length > 0 && e.Data[0] == NetDemoLocal.Pose)
            {
                _poses.Add(i);
            }
        }

        LocalId = NetDemo.LocalId(demo);
        Seek(0);
    }

    public NetDemoData Demo { get; }

    public ReplayTransport Transport { get; private set; } = new();

    public int LocalId { get; }

    /// <summary>The playback's time (seconds from the demo's start).</summary>
    public double Time { get; private set; }

    public double Length => Demo.Length / 1000.0;

    public bool Done => _next >= Demo.Entries.Count;

    /// <summary>Times of the keyframes (seconds).</summary>
    public IEnumerable<double> KeyframeTimes => _keyframes.Select(i => Demo.Entries[i].Time / 1000.0);

    /// <summary>The own records due (in order) since the last <see cref="Advance"/>, for the game to apply
    /// after the messages of the same step are polled.</summary>
    public List<byte[]> DueLocal { get; } = [];

    /// <summary>A seek happened: the next poll starts with a keyframe's welcome (the game resets its view).</summary>
    public bool Seeked { get; set; }

    /// <summary>Moves the clock on by <paramref name="seconds"/> and queues what came due.</summary>
    public void Advance(double seconds)
    {
        Time += Math.Max(0, seconds);
        var now = (long)Math.Round(Time * 1000.0);
        while (_next < Demo.Entries.Count && Demo.Entries[_next].Time <= now)
        {
            var e = Demo.Entries[_next++];
            switch (e.Kind)
            {
                case NetDemoKind.Message:
                    Transport.Enqueue(e.Data);
                    break;
                case NetDemoKind.Local:
                    DueLocal.Add(e.Data);
                    break;
                    // a keyframe in the stream is skipped: the state it holds came with the stream
            }
        }
    }

    /// <summary>
    /// Goes to the last keyframe at or before <paramref name="seconds"/>: its entries are queued and the
    /// clock is the keyframe's. The transport stays (the client keeps its connection); the game sees the
    /// keyframe's welcome as a fresh start of its view.
    /// </summary>
    public double Seek(double seconds)
    {
        var at = Demo.KeyframeAt((long)Math.Round(Math.Max(0, seconds) * 1000.0));
        if (at < 0)
        {
            _next = Demo.Entries.Count;
            return Time;
        }

        var key = Demo.Entries[at];
        DueLocal.Clear();
        foreach (var (kind, data) in NetDemoFile.Unpack(key.Data))
        {
            if (kind == NetDemoKind.Message)
            {
                Transport.Enqueue(data);
            }
            else
            {
                DueLocal.Add(data);
            }
        }

        _next = at + 1;
        Time = key.Time / 1000.0;
        Seeked = true;
        return Time;
    }

    /// <summary>The recorded own pose at the playback's time, interpolated between the samples around it;
    /// null before the first.</summary>
    public NetDemoPose? PoseAt(double seconds)
    {
        var now = (long)Math.Round(seconds * 1000.0);
        int lo = 0, hi = _poses.Count - 1, found = -1;
        while (lo <= hi)
        {
            var mid = (lo + hi) / 2;
            if (Demo.Entries[_poses[mid]].Time <= now)
            {
                found = mid;
                lo = mid + 1;
            }
            else
            {
                hi = mid - 1;
            }
        }

        if (found < 0)
        {
            return null;
        }

        var a = NetDemoPose.Read(Demo.Entries[_poses[found]].Time, Demo.Entries[_poses[found]].Data);
        if (found + 1 >= _poses.Count)
        {
            return a;
        }

        var b = NetDemoPose.Read(Demo.Entries[_poses[found + 1]].Time, Demo.Entries[_poses[found + 1]].Data);
        var span = b.Time - a.Time;
        // A respawn or a teleport is a jump, not a glide; and samples far apart are held.
        if (span <= 0 || span > 250 || Vector3.Distance(a.Feet, b.Feet) > 400f)
        {
            return a;
        }

        var t = Math.Clamp((float)((seconds * 1000.0 - a.Time) / span), 0f, 1f);
        return a with
        {
            Feet = Vector3.Lerp(a.Feet, b.Feet, t), Yaw = LerpAngle(a.Yaw, b.Yaw, t), Pitch = float.Lerp(a.Pitch, b.Pitch, t),
            Eye = float.Lerp(a.Eye, b.Eye, t), CarYaw = LerpAngle(a.CarYaw, b.CarYaw, t),
        };
    }

    private static float LerpAngle(float a, float b, float t)
    {
        var d = MathF.IEEERemainder(b - a, MathF.Tau);
        return a + d * t;
    }
}
