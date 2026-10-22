using System.Globalization;
using System.Numerics;
using SkyNet.Formats;
using SkyNet.Game.Behaviour;
using SkyNet.Game.Campaign;

namespace SkyNet.Game.DevTools;

/// <summary>Where the debug overlay points: a world position, what is there, how far from the feet.</summary>
public sealed record ObjectiveTarget(Vector3 Position, string Text, float Distance);

/// <summary>The mission's counter as the overlay shows it, and the pointer to the next objective.</summary>
public sealed record ObjectiveReport(int Mission, int Total, int Left, bool Complete, bool Failed,
    IReadOnlyList<string> Pending, ObjectiveTarget? Target, string Note)
{
    public static readonly ObjectiveReport None = new(0, 0, 0, false, false, [], null, "");

    public int Done => Total - Left;
}

/// <summary>
/// The debug overlay's pointer (the Godot port's debug_overlay.gd, read from the live behaviour
/// instead of its trigger graph): for the objective sections [M1]..[M5] still pending, the records
/// that raise them. An objective record (acts 0x26..0x2A, not yet spent) is reached by the chain
/// heads whose links walk to it: a trigger, a breakable or the chain's first record (else the
/// record itself, marked unsure); a robot carrying such an act (its start marker's act, run when
/// it dies) is a source too. The nearest live source on this map gets the pointer. With none here,
/// the mission's other maps (the same decade) are searched along the doorways, breadth first, in
/// their authored state, and this map's doorway that leads there gets the pointer. Nothing here
/// changes the game; the other maps' records are kept until the session changes.
/// <code>
///   pending [M2] ◄── objective @1a2b3 act 27 ◄── link ◄── trigger @1a000 (head)   ─┐
///   robots: DeathAct 0x27, DeathState bit 2, alive                                  ├─► the nearest
///   none on the map ──► its doorways ──► ... ──► a map with a source ──► this map's doorway to it
/// </code>
/// </summary>
public sealed class ObjectivePointer
{
    /// <summary>A record reached only through itself is a guess: it loses to a real head.</summary>
    private const float UnsurePenalty = 1e7f;
    private const int MaxChainDepth = 8;
    private const int DeathActBit = 4;
    private const int ActSpent = 0xFE;
    private const int TextWidth = 90;
    private const int RobotMarker = 2;
    /// <summary>Maps one search may open, and how many doorways deep it goes.</summary>
    private const int MaxMaps = 16, MaxExitDepth = 6;

    private static readonly CultureInfo C = CultureInfo.InvariantCulture;

    private readonly Dictionary<int, (MapFile Map, LevelBehaviour Behaviour)?> _maps = [];
    private GameSession? _session;

    private readonly record struct Source(Vector3 At, string Text, bool Unsure);

    /// <summary>The report for the session as it stands (<see cref="ObjectiveReport.None"/> without a mission).</summary>
    public ObjectiveReport Find(GameSession s)
    {
        if (!ReferenceEquals(s, _session))
        {
            _session = s;
            _maps.Clear();
        }

        if (s.Mission is not { } mission)
        {
            return ObjectiveReport.None;
        }

        var p = s.Progress;
        var pending = PendingSections(s);
        var lines = pending.Select(i => $"[M{i + 1}] {Entry(s, i)}").ToList();
        if (p.Left <= 0 || pending.Count == 0 || s.Behaviour is not { } behaviour || s.Map is not { } map)
        {
            return new ObjectiveReport(mission.Number, p.Total, p.Left, p.Complete, p.Failed, lines, null, "");
        }

        var feet = s.Player.Position;
        var sources = new List<Source>();
        var robots = new SortedDictionary<int, int>();
        foreach (var r in s.Robots?.All ?? [])
        {
            var index = r.DeathAct - ActRules.ObjectiveFirst;
            if (r.Alive && pending.Contains(index) && (r.DeathState & DeathActBit) != 0)
            {
                robots[index] = robots.GetValueOrDefault(index) + 1;
                sources.Add(new Source(r.Position, string.Create(C, $"[M{index + 1}] <- destroy {r.Name} @{r.MarkerOffset:x5}"), false));
            }
        }

        var note = robots.Count > 0
            ? $"robots whose destruction counts: {string.Join(", ", robots.Select(kv => $"[M{kv.Key + 1}] {kv.Value}"))}"
            : "";
        Records(map, behaviour, pending, sources);
        if (Nearest(sources, feet) is { } here)
        {
            return new ObjectiveReport(mission.Number, p.Total, p.Left, p.Complete, p.Failed, lines, here, note);
        }

        var sections = string.Join(' ', pending.Select(i => $"[M{i + 1}]"));
        var (target, elsewhere) = Elsewhere(s, map, behaviour, pending, feet);
        note = Join(note, elsewhere ?? $"no live source of {sections} on MAP.{s.MapNumber:D3} or the maps its doorways reach");
        return new ObjectiveReport(mission.Number, p.Total, p.Left, p.Complete, p.Failed, lines, target, note);
    }

    /// <summary>The sections [M1]..[M5] (0-based) whose next entry is still to come; with no briefing
    /// text, every section while the counter is not spent.</summary>
    public static IReadOnlyList<int> PendingSections(GameSession s)
    {
        var p = s.Progress;
        if (p.Left <= 0)
        {
            return [];
        }

        if (s.Briefing is not { } briefing)
        {
            return [.. Enumerable.Range(0, Briefing.MissionSections)];
        }

        var cursor = p.Snapshot().Cursor;
        return [.. Enumerable.Range(0, Math.Min(briefing.MissionTexts.Length, cursor.Length)).Where(i => cursor[i] < briefing.MissionTexts[i].Length)];
    }

    /// <summary>A record as the pointer names it: its mesh name, marker or sprite (bank/record),
    /// offset, act and kind.</summary>
    public static string Describe(MapFile map, LevelBehaviour behaviour, MapEntity e)
    {
        var who = e.Variant == MapEntityVariant.Mesh && e.NameIndex >= 0 && e.NameIndex < map.Names.Length ? map.Names[e.NameIndex]
            : e.IsMarker ? (e.EnemyType >= 0 ? string.Create(C, $"marker {e.MarkerType} enemy {e.EnemyType}") : string.Create(C, $"marker {e.MarkerType}"))
            : e.SpriteIndex >= 0 ? SpriteLabel(e)
            : "record";
        return string.Create(C, $"{who} @{e.FileOffset:x5} act {behaviour.Act(e.FileOffset):X2} {behaviour.KindOf(e.FileOffset).ToString().ToLowerInvariant()}");
    }

    /// <summary>A sprite by its texture bank and record ("sprite 214/3"), as the Godot overlay names it.</summary>
    public static string SpriteLabel(MapEntity e) => string.Create(C, $"sprite {e.SpriteBank}/{e.SpriteRecord}");

    private static ObjectiveTarget? Nearest(List<Source> sources, Vector3 feet)
    {
        ObjectiveTarget? best = null;
        var bestScore = float.PositiveInfinity;
        foreach (var source in sources)
        {
            var d = Vector3.Distance(feet, source.At);
            var score = d + (source.Unsure ? UnsurePenalty : 0f);
            if (score < bestScore)
            {
                (best, bestScore) = (new ObjectiveTarget(source.At, source.Text, d), score);
            }
        }

        return best;
    }

    /// <summary>The objective records of <paramref name="map"/> for the pending sections and the chain
    /// heads that reach them.</summary>
    private static void Records(MapFile map, LevelBehaviour behaviour, IReadOnlyList<int> pending, List<Source> into)
    {
        var predecessors = Predecessors(map);
        foreach (var e in map.Entities)
        {
            var off = e.FileOffset;
            var act = behaviour.Act(off);
            var index = act - ActRules.ObjectiveFirst;
            if (behaviour.KindOf(off) != NodeKind.Objective || !pending.Contains(index))
            {
                continue;
            }

            var label = string.Create(C, $"[M{index + 1}] @{off:x5} act {act:X2}");
            var heads = Heads(map, behaviour, predecessors, off);
            foreach (var head in heads)
            {
                into.Add(new Source(BehaviourModel.Position(head), $"{label} <- {Describe(map, behaviour, head)}", false));
            }

            if (heads.Count == 0)
            {
                into.Add(new Source(BehaviourModel.Position(e), $"{label} (no trigger found: the record itself)", true));
            }
        }
    }

    /// <summary>The robots of another map whose start marker hands them a pending act.</summary>
    private static void Robots(MapFile map, IReadOnlyList<int> pending, List<Source> into)
    {
        foreach (var e in map.Entities)
        {
            var index = e.Param8 - ActRules.ObjectiveFirst;
            if (e.Variant == MapEntityVariant.Sprite && e.MarkerType == RobotMarker && e.EnemyType >= 0 && (e.StateByte & DeathActBit) != 0 &&
                pending.Contains(index))
            {
                into.Add(new Source(BehaviourModel.Position(e), string.Create(C, $"[M{index + 1}] <- destroy enemy {e.EnemyType} @{e.FileOffset:x5}"), false));
            }
        }
    }

    /// <summary>Breadth first along the doorways through the mission's maps (the decade of this
    /// one): the first map with a source, and this map's doorway towards it.</summary>
    private (ObjectiveTarget? Target, string? Note) Elsewhere(GameSession s, MapFile map, LevelBehaviour behaviour,
        IReadOnlyList<int> pending, Vector3 feet)
    {
        var decade = s.MapNumber / 10;
        var firstExit = new Dictionary<int, MapEntity?> { [s.MapNumber] = null };
        var queue = new Queue<(int Map, int Depth)>();
        queue.Enqueue((s.MapNumber, 0));
        var opened = 0;
        while (queue.TryDequeue(out var at) && opened < MaxMaps)
        {
            if ((at.Map == s.MapNumber ? (map, behaviour) : Open(s, at.Map)) is not { } current)
            {
                continue;
            }

            var (file, rules) = current;
            opened++;
            if (at.Map != s.MapNumber)
            {
                var found = new List<Source>();
                Robots(file, pending, found);
                Records(file, rules, pending, found);
                if (found.Count > 0)
                {
                    var next = string.Create(C, $"next objective on MAP.{at.Map:D3}: {found[0].Text}");
                    if (firstExit[at.Map] is not { } door)
                    {
                        return (null, next);
                    }

                    var position = BehaviourModel.Position(door);
                    var to = door.Param16 == 0 ? "back" : string.Create(C, $"MAP.{door.Param16:D3}");
                    return (new ObjectiveTarget(position, string.Create(C, $"exit @{door.FileOffset:x5} -> {to} (towards MAP.{at.Map:D3})"),
                        Vector3.Distance(feet, position)), next);
                }
            }

            if (at.Depth >= MaxExitDepth)
            {
                continue;
            }

            foreach (var off in rules.Exits)
            {
                if (!file.TryGetEntityAt(off, out var exit) || (at.Map == s.MapNumber && rules.Act(off) >= ActSpent))
                {
                    continue;
                }

                var to = exit.Param16 == 0 && at.Map == s.MapNumber ? s.PreviousMap : exit.Param16;
                if (to <= 0 || to / 10 != decade || firstExit.ContainsKey(to))
                {
                    continue;
                }

                firstExit[to] = at.Map == s.MapNumber ? exit : firstExit[at.Map];
                queue.Enqueue((to, at.Depth + 1));
            }
        }

        return (null, null);
    }

    /// <summary>Another map's records and rules as authored (kept for the session).</summary>
    private (MapFile Map, LevelBehaviour Behaviour)? Open(GameSession s, int number)
    {
        if (!_maps.TryGetValue(number, out var opened))
        {
            opened = s.Data.LoadMap(number) is { } file ? (file, new LevelBehaviour(file, s.Transfrm, new MissionProgress(0))) : null;
            _maps[number] = opened;
        }

        return opened;
    }

    private static string Entry(GameSession s, int section)
    {
        if (s.Briefing is not { } briefing || section >= briefing.MissionTexts.Length)
        {
            return "";
        }

        var cursor = s.Progress.Snapshot().Cursor;
        var text = cursor[section] < briefing.MissionTexts[section].Length ? briefing.MissionTexts[section][cursor[section]] : "";
        text = string.Join(' ', text.Split(['\r', '\n', ' '], StringSplitOptions.RemoveEmptyEntries));
        return text.Length > TextWidth ? text[..(TextWidth - 3)] + "..." : text;
    }

    /// <summary>Each record's predecessors: the records whose link points at it.</summary>
    private static Dictionary<int, List<int>> Predecessors(MapFile map)
    {
        var into = new Dictionary<int, List<int>>();
        foreach (var e in map.Entities)
        {
            if (BehaviourModel.LinkOf(map, e) is { } next)
            {
                if (!into.TryGetValue(next.FileOffset, out var list))
                {
                    into[next.FileOffset] = list = [];
                }

                list.Add(e.FileOffset);
            }
        }

        return into;
    }

    /// <summary>The live records up the chain from <paramref name="target"/> that start it: triggers,
    /// breakables, and the chain's first records.</summary>
    private static List<MapEntity> Heads(MapFile map, LevelBehaviour behaviour, Dictionary<int, List<int>> into, int target)
    {
        var heads = new List<MapEntity>();
        var seen = new HashSet<int> { target };
        var frontier = new List<int> { target };
        for (var depth = 0; depth < MaxChainDepth && frontier.Count > 0; depth++)
        {
            var next = new List<int>();
            foreach (var off in frontier)
            {
                foreach (var from in into.GetValueOrDefault(off) ?? [])
                {
                    if (!seen.Add(from) || behaviour.Act(from) >= ActSpent || !map.TryGetEntityAt(from, out var e))
                    {
                        continue;
                    }

                    if (behaviour.KindOf(from) is NodeKind.Trigger or NodeKind.Destructible || !into.ContainsKey(from))
                    {
                        heads.Add(e);
                    }

                    next.Add(from);
                }
            }

            frontier = next;
        }

        return heads;
    }

    private static string Join(string a, string b) => a.Length == 0 ? b : $"{a}; {b}";
}
