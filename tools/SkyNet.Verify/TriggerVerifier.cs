using System.Globalization;
using System.Numerics;
using System.Text;
using SkyNet.Formats;
using SkyNet.Game.Behaviour;

namespace SkyNet.Verify;

/// <summary>One verifier row, written as the Godot port's --verify-out line:
/// <c>map id act kind mode result why</c>.</summary>
public sealed record Row(int Map, int Id, int Act, string Kind, string Mode, string Result, string Why)
{
    public const string Pass = "PASS", Fail = "FAIL", Unreachable = "UNREACHABLE", Skip = "SKIP";

    public override string ToString() =>
        string.Create(CultureInfo.InvariantCulture, $"{Map} {Id:x5} {Act:X2} {Kind} {Mode} {Result} {Why}");

    public string Key => string.Create(CultureInfo.InvariantCulture, $"{Map} {Id:x5}");
}

/// <summary>
/// The Godot port's trigger verifier (scripts/triggers/trigger_verifier.gd) over the C# level
/// behaviour, headless. The node set and the modes are enumerated from the MAP records the way the
/// Godot graph does (trigger_graph.gd _wants_node / _modes); what each activation must do comes from
/// the Godot trigger lock, which this does not rebuild. Where the player stands is not searched for
/// (there is no collision world here): the eye or the feet are put on the record itself, and the
/// activation is the node's own handler, the way the Godot verifier isolates a node from its
/// neighbours. So the physics-only UNREACHABLE rows of the Godot run (no floor in reach, no line of
/// fire) come out as PASS or FAIL here.
/// </summary>
public sealed class TriggerVerifier(GameData data, IReadOnlyList<LockNode> lockNodes)
{
    private readonly TransfrmPrs _transfrm = BehaviourModel.LoadTransfrm(data);
    private readonly ILookup<int, LockNode> _byMap = lockNodes.ToLookup(n => n.Map);

    /// <summary>Records enumerated here and in the lock that disagree: [map id what].</summary>
    public List<string> Enumeration { get; } = [];

    /// <summary>Every map of the data's map archive, in number order.</summary>
    public static List<int> MapsOf(GameData data)
    {
        using var a = data.OpenMapArchive();
        var list = new List<int>();
        foreach (var e in a.Entries)
        {
            if (e.Name.StartsWith("MAP.", StringComparison.OrdinalIgnoreCase)
                && int.TryParse(e.Name.AsSpan(4), NumberStyles.None, CultureInfo.InvariantCulture, out var n))
            {
                list.Add(n);
            }
        }

        list.Sort();
        return list;
    }

    public List<Row> Run(IEnumerable<int> maps)
    {
        var rows = new List<Row>();
        foreach (var num in maps)
        {
            if (data.LoadMap(num) is { } map)
            {
                rows.AddRange(VerifyMap(num, map));
            }
        }

        return rows;
    }

    // ------------------------------------------------------------ enumeration

    /// <summary>trigger_graph.gd _wants_node over every record: the node set of the map.</summary>
    public static SortedDictionary<int, MapEntity> Nodes(MapFile map)
    {
        var incoming = Incoming(map);
        var nodes = new SortedDictionary<int, MapEntity>();
        foreach (var e in map.Entities)
        {
            var wants = e.MarkerType >= 0
                ? e.LinkNext > 0 || incoming.Contains(e.FileOffset)
                : e.ActType > 0 || e.LinkNext > 0 || incoming.Contains(e.FileOffset) || e.HitPoints > 0 || (e.StateByte & 6) != 0;
            if (wants)
            {
                nodes[e.FileOffset] = e;
            }
        }

        return nodes;
    }

    public static HashSet<int> Incoming(MapFile map)
    {
        var set = new HashSet<int>();
        foreach (var e in map.Entities)
        {
            if (e.LinkNext > 0 && map.TryGetEntityAt(e.LinkNext, out _))
            {
                set.Add(e.LinkNext);
            }
        }

        return set;
    }

    /// <summary>The Godot rule kind of a record (rules_skynet.gd: by marker type for a marker,
    /// else by act and variant), in its snake-case spelling.</summary>
    public static string KindName(MapEntity e)
    {
        if (e.MarkerType >= 0)
        {
            return "marker";
        }

        var k = ActRules.For(e.ActType, (int)e.Variant).Kind;
        return k switch
        {
            ActKind.ProxGate => "prox_gate",
            ActKind.ProxChain => "prox_chain",
            ActKind.SoundCue => "sound_cue",
            ActKind.SoundLoop => "sound_loop",
            _ => k.ToString().ToLowerInvariant(),
        };
    }

    /// <summary>trigger_graph.gd _modes, without the vehicle path ends (the graph finds those by
    /// following the vehicles' marker paths; the lock supplies them).</summary>
    public static List<string> Modes(MapEntity e, string kind, bool incoming)
    {
        var modes = new List<string>();
        var wallButton = e.Variant == MapEntityVariant.Mesh && (e.StateByte & ActRules.WallButtonBit) != 0 && e.NameIndex >= 0;
        switch (kind)
        {
            case "prox_gate" when (e.StateByte & 6) is 0 or 6:
                modes.Add("use");
                break;
            case "prox_chain":
                modes.Add(wallButton ? "use" : "prox");
                break;
            case "exit":
                modes.Add("touch");
                break;
            case "relay":
                modes.Add("counter");
                break;
        }

        if (e.Variant == MapEntityVariant.Mesh && (e.StateByte & ActRules.PadBit) != 0 && e.ActType < ActRules.SpentFirst)
        {
            modes.Add("walk");
        }

        if ((e.StateByte & 2) != 0)
        {
            modes.Add("shot");
        }

        var deadPool = (e.StateByte & 6) == 4 && e.Variant == MapEntityVariant.Mesh && e.HitPoints <= 0;
        if ((e.StateByte & 4) != 0 && !deadPool)
        {
            modes.Add("death");
        }

        if (incoming)
        {
            modes.Add("chain");
        }

        return modes;
    }

    // ------------------------------------------------------------ one map

    private int _mapNum = -1;

    public List<Row> VerifyMap(int num, MapFile map)
    {
        _mapNum = num;
        var rows = new List<Row>();
        var nodes = Nodes(map);
        var incoming = Incoming(map);
        var locked = _byMap[num].ToDictionary(n => n.Id);
        foreach (var id in locked.Keys.Except(nodes.Keys).Order())
        {
            Enumeration.Add(string.Create(CultureInfo.InvariantCulture, $"{num} {id:x5} in the lock, not enumerated"));
        }

        foreach (var (id, e) in nodes)
        {
            var kind = KindName(e);
            var mine = Modes(e, kind, incoming.Contains(id));
            if (!locked.TryGetValue(id, out var node))
            {
                Enumeration.Add(string.Create(CultureInfo.InvariantCulture, $"{num} {id:x5} enumerated, not in the lock"));
                continue;
            }

            var theirs = node.Modes.Where(m => m != "path").ToList();
            if (!mine.SequenceEqual(theirs))
            {
                Enumeration.Add(string.Create(CultureInfo.InvariantCulture,
                    $"{num} {id:x5} modes [{string.Join(',', mine)}] here, [{string.Join(',', theirs)}] in the lock"));
            }

            rows.Add(Check(num, map, e, kind, node));
        }

        return rows;
    }

    private Driver Fresh(MapFile map, int objectives = 5) =>
        new(map, _transfrm, new MissionProgress(objectives));

    private Driver Ready(MapFile map) => Driver.Ready(map, _transfrm, new MissionProgress(5));

    private Row Check(int num, MapFile map, MapEntity e, string kind, LockNode node)
    {
        var id = e.FileOffset;
        var act = e.MarkerType >= 0 ? 0 : e.ActType;
        Row R(string mode, string res, string why) => new(num, id, act, kind, mode, res, why);
        var primary = node.Primary;
        if (primary.Length == 0)
        {
            if (e.ActType == ActRules.ProxGate && e.MarkerType < 0 && (e.StateByte & 6) is not (0 or 6))
            {
                return CheckDeadGate(map, e, R);
            }

            return R("chain", Row.Skip, "only a chain can set it off");
        }

        if (node.HasWalk && e.Variant == MapEntityVariant.Mesh)
        {
            return CheckWalk(map, node, R);
        }

        try
        {
            return primary switch
            {
                "use" => CheckUse(map, e, node, R),
                "prox" => CheckProx(map, e, node, R),
                "exit" => CheckExit(map, e, node, R),
                "shot_each" or "shot_death" => CheckShot(map, e, node, primary, R),
                "counter" => CheckRelay(map, e, node, R),
                "path" => CheckPath(map, e, node, R),
                _ => R(primary, Row.Skip, "no driver for this mode"),
            };
        }
        catch (Exception ex) when (ex is InvalidOperationException or KeyNotFoundException or IndexOutOfRangeException)
        {
            return R(primary, Row.Fail, "threw " + ex.GetType().Name);
        }
    }

    private static float Measure(int act) => act switch
    {
        ActRules.ProxChainA => ActRules.ProxChainARadius,
        ActRules.ProxChainB => ActRules.ProxChainBRadius,
        _ => ActRules.ProxGateRadius + ActRules.PlayerRadius,
    };

    private Row CheckUse(MapFile map, MapEntity e, LockNode node, Func<string, string, string, Row> r)
    {
        var d = Ready(map);
        var p = d.PositionOf(e.FileOffset);
        var why = "";
        if (Driver.IsWallButton(e))
        {
            // A wall button answers the key, never the walk: walking up to it on a map of its own
            // must leave its bits alone (what neighbouring triggers do there is theirs).
            var w = Fresh(map);
            w.Settle(Driver.SettleStart);
            var before = w.B.State(e.FileOffset);
            w.Tick(new PlayerProbe(p, p));
            w.Settle();
            if (w.B.State(e.FileOffset) != before)
            {
                why = "walking past it did " + string.Join(' ', w.Drain());
            }
        }

        var first = d.Press(e, p);
        why = Equiv.Join(Equiv.Why(node.First, first), why);
        if (why.Length > 0)
        {
            return r("use", Row.Fail, why);
        }

        var second = d.Press(e, p);
        why = Equiv.Why(node.Second, second);
        if (why.Length > 0)
        {
            return r("use", Row.Fail, "second: " + why);
        }

        if (!Driver.IsWallButton(e))
        {
            // From outside the reach, nothing.
            var out1 = Ready(map);
            var outEye = p + new Vector3(Measure(e.ActType) + 20f, 0f, 0f);
            var none = out1.Press(e, outEye);
            if (none.Count > 0)
            {
                return r("use", Row.Fail, "out of reach it still did " + string.Join(' ', none));
            }
        }

        return r("use", Row.Pass, "");
    }

    private Row CheckDeadGate(MapFile map, MapEntity e, Func<string, string, string, Row> r)
    {
        var d = Ready(map);
        var p = d.PositionOf(e.FileOffset);
        d.Tick(new PlayerProbe(p, p, UsePressed: true));
        d.Settle();
        var got = d.Drain();
        return got.Count == 0 ? r("dead", Row.Pass, "") : r("dead", Row.Fail, "the key did " + string.Join(' ', got));
    }

    private Row CheckProx(MapFile map, MapEntity e, LockNode node, Func<string, string, string, Row> r)
    {
        var d = Ready(map);
        var p = d.PositionOf(e.FileOffset);
        d.B.ProxUse(e.FileOffset, p);
        d.Settle();
        var first = d.Drain();
        var why = Equiv.Why(node.First, first);
        if (why.Length > 0)
        {
            return r("prox", Row.Fail, why);
        }

        // Leaving and coming back does not fire it again (the port's latch) until a chain re-arms it.
        d.B.ProxUse(e.FileOffset, p);
        d.Settle();
        why = Equiv.Why(node.Second.Count == 0 ? [] : node.Second, d.Drain());
        return why.Length > 0 ? r("prox", Row.Fail, "re-entry: " + why) : r("prox", Row.Pass, "");
    }

    private Row CheckExit(MapFile map, MapEntity e, LockNode node, Func<string, string, string, Row> r)
    {
        var d = Ready(map);
        var p = d.PositionOf(e.FileOffset);
        // 1. Touching it only arms it.
        var touch = d.Touch(p);
        if (touch.Any(t => t.StartsWith("exit", StringComparison.Ordinal)))
        {
            return r("exit", Row.Fail, "touching it took it");
        }

        // 2. The key takes it.
        var why = Equiv.Why(node.First, d.TakeExit(p));
        if (why.Length > 0 && ViaGate(map, e, p) is var (gate, gateWhy) && gateWhy.Length == 0)
        {
            // There is one key: standing in this doorway the eye is inside an 0xEF gate whose chain
            // ends here, and the key walks that gate first (activate_teleport); what happened is then
            // the gate's row (trigger_verifier.gd _check_exit, _gate_chaining_to).
            return r("exit", Row.Pass, string.Create(CultureInfo.InvariantCulture, $"a gate covers this doorway ({gate:x5})"));
        }

        return why.Length > 0 ? r("exit", Row.Fail, why) : r("exit", Row.Pass, "");
    }

    /// <summary>The key at a doorway with the eye over the feet, when an 0xEF gate whose chain ends
    /// in it holds that eye: the gate's id and how what happened differs from the gate's own row.</summary>
    private (int Gate, string Why)? ViaGate(MapFile map, MapEntity exit, Vector3 feet)
    {
        var probe = Fresh(map);
        foreach (var g in _byMap[_mapNum].Where(n => n.Act == ActRules.ProxGate))
        {
            if (probe.B.ChainExit(g.Id) != exit.FileOffset)
            {
                continue;
            }

            // Feet still inside the doorway's 2D touch measure, moved towards the gate.
            var gp = probe.PositionOf(g.Id);
            var flat = new Vector2(gp.X - feet.X, gp.Z - feet.Z);
            var step = flat.Length() > 1f ? flat / flat.Length() * MathF.Min(flat.Length(), ActRules.TeleportTouchRadius - 1f) : Vector2.Zero;
            var at = feet + new Vector3(step.X, 0f, step.Y);
            var eye = at + new Vector3(0f, Game.Player.PlayerController.EyeHeight, 0f);
            if (Vector3.Distance(gp, eye) > Measure(ActRules.ProxGate))
            {
                continue;
            }

            feet = at;

            var d = Ready(map);
            d.Tick(new PlayerProbe(feet, eye, UsePressed: true));
            d.Settle(1f, feet, eye);
            return (g.Id, Equiv.Why(g.First, d.Drain()));
        }

        return null;
    }

    private Row CheckShot(MapFile map, MapEntity e, LockNode node, string how, Func<string, string, string, Row> r)
    {
        if (e.Variant != MapEntityVariant.Mesh || e.NameIndex < 0)
        {
            return r(how, Row.Unreachable, "no mesh was built for it to be shot");
        }

        var d = Ready(map);
        var damage = how == "shot_death" ? Math.Max(1f, d.B.Hp(e.FileOffset)) : 1f;
        var why = Equiv.Why(node.First, d.Shoot(e.FileOffset, damage));
        return why.Length > 0 ? r(how, Row.Fail, why) : r(how, Row.Pass, "");
    }

    private Row CheckWalk(MapFile map, LockNode node, Func<string, string, string, Row> r)
    {
        var d = Ready(map);
        var off = node.Id;
        var why = Equiv.Why(node.First, d.Walk(off));
        if (why.Length > 0)
        {
            return r("walk", Row.Fail, why);
        }

        var again = d.Walk(off);
        if (again.Count > 0)
        {
            return r("walk", Row.Fail, "standing on it again did " + string.Join(' ', again));
        }

        // The key it still answers is the graph's second activation.
        if (map.TryGetEntityAt(off, out var e))
        {
            why = Equiv.Why(node.Second, d.Press(e, d.PositionOf(off)));
            if (why.Length > 0)
            {
                return r("walk", Row.Fail, "second: " + why);
            }
        }

        return r("walk", Row.Pass, "");
    }

    private Row CheckRelay(MapFile map, MapEntity e, LockNode node, Func<string, string, string, Row> r)
    {
        // The counter one above the relay's word; the relay switched on, then one objective taken.
        var d = Driver.Ready(map, _transfrm, new MissionProgress(ActRules.RelayAt + 1));
        var why = Equiv.Why(node.First, d.Count(e.FileOffset));
        return why.Length > 0 ? r("counter", Row.Fail, why) : r("counter", Row.Pass, "");
    }

    private Row CheckPath(MapFile map, MapEntity e, LockNode node, Func<string, string, string, Row> r)
    {
        // The end of the path, taken directly. The vehicle's drive itself is not modelled here.
        var d = Ready(map);
        var why = Equiv.Why(node.First, d.Chain(e.FileOffset));
        return why.Length > 0 ? r("path", Row.Fail, why) : r("path", Row.Pass, "");
    }

    public static string Format(IEnumerable<Row> rows)
    {
        var sb = new StringBuilder();
        foreach (var row in rows)
        {
            sb.Append(row).Append('\n');
        }

        return sb.ToString();
    }

    /// <summary>A Godot rows line back into a row (the why may hold spaces).</summary>
    public static Row? ParseRow(string line)
    {
        var t = line.Split(' ', 7);
        if (t.Length < 6 || !int.TryParse(t[0], CultureInfo.InvariantCulture, out var map))
        {
            return null;
        }

        return new Row(map, int.Parse(t[1], NumberStyles.HexNumber, CultureInfo.InvariantCulture),
            int.Parse(t[2], NumberStyles.HexNumber, CultureInfo.InvariantCulture), t[3], t[4], t[5], t.Length > 6 ? t[6] : "");
    }
}
