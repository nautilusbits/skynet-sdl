using System.Globalization;
using System.Numerics;
using RetroEng.Physics;
using SkyNet.Formats;
using SkyNet.Game.Behaviour;
using SkyNet.Game.Campaign;
using SkyNet.Game.Collision;
using SkyNet.Game.Enemies;
using SkyNet.Game.Mods;
using SkyNet.Verify;

namespace SkyNet.Edit;

/// <summary>How bad a validation row is: an error breaks the mission, a warning may, info only tells.</summary>
public enum ValidationLevel
{
    Error,
    Warning,
    Info,
}

/// <summary>One problem of the edited mission: its code (<see cref="Validation"/>'s constants), the instance and
/// entity it is about (−1: the instance itself) and the text the panel shows.</summary>
public sealed record ValidationRow(ValidationLevel Level, string Code, string Instance, int Entity, string Text)
{
    public OutlinerItem Item => new(Instance, Entity);
}

/// <summary>A trigger chain the edited layer adds, removes or changes against its base map: its first
/// record (the lowest key), what happened and the chain's records.</summary>
public sealed record ChainChange(string Layer, int Head, string What, IReadOnlyList<int> Keys);

/// <summary>
/// The editor's validation (docs/plans/editor-vr-menus.md, 1.4 and step E10): the edited layers' records
/// (next links to no entity, chain loops nothing starts, acts the act table lacks, exits to maps the mission
/// lacks or to arrival sets their target lacks, duplicate player starts), the mission's start and exit links,
/// a collision probe of the robots, props and pickups the layers moved or added (inside geometry, floating,
/// out of reach), and the static trigger graph of each layer against its base map (the verifier's node set
/// and modes). A layer record whose problem the DOS map already has is a warning, not an error: an
/// unmodified mission has no errors of its own.
/// </summary>
public sealed class Validator(GameData data)
{
    public const string DanglingNext = "next", Loop = "loop", UnknownAct = "act", ExitTarget = "exit", Arrival = "arrival",
        DuplicateStart = "start-dup", Start = "start", LinkSource = "link", Inside = "inside", Floating = "floating",
        Reach = "reach", NoLayer = "layer";

    // Feet clearance and how far a probe looks: a robot more than RobotDrop over its floor floats, a pickup more
    // than PickupReach over it is out of the player's reach (DOS jump 177 plus the body).
    private const float RobotDrop = 64f, PropDrop = 32f, PickupReach = 240f, ProbeDepth = 4096f;

    private static readonly HashSet<int> Known = [.. TriggerChains.KnownActs];
    private readonly Dictionary<string, (MapFile Map, CollisionWorld World)> _collision = new(StringComparer.Ordinal);

    /// <summary>Every row of the document: errors first, then warnings, then info, each in instance and key order.</summary>
    public List<ValidationRow> Run(EditDocument doc)
    {
        var rows = new List<ValidationRow>();
        var mission = doc.Mission;
        var layers = doc.Layers.ToHashSet(StringComparer.Ordinal);
        MapFile? MapOf(string instance) =>
            mission?.World.Instance(instance) is { } i && layers.Contains(i.Layer) ? doc.Map(i.Layer) : null;

        foreach (var layer in doc.Layers)
        {
            var instance = mission?.World.Instances.FirstOrDefault(i => i.Layer == layer)?.Id ?? layer;
            var map = doc.Map(layer);
            var baseMap = doc.BaseMap(layer);
            var before = LayerIssues(mission, instance, baseMap, MapOf).Select(r => (r.Code, r.Entity)).ToHashSet();
            foreach (var r in LayerIssues(mission, instance, map, MapOf))
            {
                rows.Add(r.Level == ValidationLevel.Error && before.Contains((r.Code, r.Entity))
                    ? r with { Level = ValidationLevel.Warning, Text = r.Text + " (as in the DOS map)" } : r);
            }

            rows.AddRange(Probe(doc, layer, instance, map));
            foreach (var c in GraphDiff(layer, baseMap, map))
            {
                rows.Add(new ValidationRow(ValidationLevel.Info, "graph", instance, c.Head,
                    Inv($"trigger chain @{c.Head} {c.What} ({c.Keys.Count} record{(c.Keys.Count == 1 ? "" : "s")})")));
            }
        }

        if (mission != null)
        {
            rows.AddRange(MissionIssues(mission, layers, MapOf));
        }

        return [.. rows.OrderBy(r => r.Level).ThenBy(r => r.Instance, StringComparer.Ordinal).ThenBy(r => r.Entity)];
    }

    // ---- the layer's records ----

    private static IEnumerable<ValidationRow> LayerIssues(MissionFile? mission, string instance, MapFile map, Func<string, MapFile?> mapOf)
    {
        var starts = 0;
        foreach (var e in map.Entities)
        {
            if (e.LinkNext > 0 && e.LinkNext != e.FileOffset && !map.TryGetEntityAt(e.LinkNext, out _))
            {
                yield return Row(ValidationLevel.Error, DanglingNext, instance, e, Inv($"next @{e.LinkNext} is no entity of the map"));
            }

            if (e.MarkerType < 0 && e.ActType > 0 && !Known.Contains(e.ActType))
            {
                yield return Row(ValidationLevel.Error, UnknownAct, instance, e, Inv($"act 0x{e.ActType:X2} is not in the act table"));
            }

            if (e.Variant == MapEntityVariant.Sprite && e.MarkerType == 0 && ++starts > 1)
            {
                yield return Row(ValidationLevel.Error, DuplicateStart, instance, e, "a second player start (the game uses the first)");
            }

            if (mission != null && e.MarkerType < 0 && e.ActType == ActRules.Exit && mission.World.Link(instance, e.FileOffset) == null)
            {
                // No mission link: the game goes by the record's own map and marker set.
                var to = mission.InstanceOf(e.Param16);
                if (to == null)
                {
                    yield return Row(ValidationLevel.Error, ExitTarget, instance, e, Inv($"exit to MAP.{e.Param16:D3}, which the mission lacks (no exit link)"));
                }
                else if (e.Param8 > 0 && mapOf(to.Id) is { } target && !HasSet(target, e.Param8))
                {
                    yield return Row(ValidationLevel.Warning, Arrival, instance, e, Inv($"exit to {to.Id}: arrival set {e.Param8} is not in its map; the game uses its start"));
                }
            }
        }

        foreach (var (head, count) in DeadLoops(map))
        {
            yield return new ValidationRow(ValidationLevel.Warning, Loop, instance, head, Inv($"a chain loop of {count} records that nothing leads into or starts"));
        }
    }

    /// <summary>The next-link cycles of the map that no record outside leads into and no record in starts
    /// (no mode but "chain" in the verifier's graph): each by its lowest key and its length.</summary>
    public static IEnumerable<(int Head, int Count)> DeadLoops(MapFile map)
    {
        var state = new Dictionary<int, int>(); // 1 on the current walk, 2 done
        var incoming = TriggerVerifier.Incoming(map);
        foreach (var start in map.Entities)
        {
            var path = new List<int>();
            var key = start.FileOffset;
            var closed = false;
            while (!state.ContainsKey(key))
            {
                state[key] = 1;
                path.Add(key);
                var next = map.TryGetEntityAt(key, out var e) ? TriggerChains.NextOf(map, e) : -1;
                if (next < 0)
                {
                    break;
                }

                key = next;
                closed = state.GetValueOrDefault(key) == 1;
            }

            if (closed && path.IndexOf(key) is var at and >= 0)
            {
                var cycle = path.GetRange(at, path.Count - at);
                var members = cycle.ToHashSet();
                var entered = map.Entities.Any(o => !members.Contains(o.FileOffset) && TriggerChains.NextOf(map, o) is var n && members.Contains(n));
                var started = cycle.Any(k => map.TryGetEntityAt(k, out var m)
                    && TriggerVerifier.Modes(m, TriggerVerifier.KindName(m), incoming.Contains(k)).Any(x => x != "chain"));
                if (!entered && !started)
                {
                    yield return (cycle.Min(), cycle.Count);
                }
            }

            foreach (var k in path)
            {
                state[k] = 2;
            }
        }
    }

    // ---- the mission ----

    private static IEnumerable<ValidationRow> MissionIssues(MissionFile mission, HashSet<string> layers, Func<string, MapFile?> mapOf)
    {
        foreach (var i in mission.World.Instances.Where(i => !layers.Contains(i.Layer)))
        {
            yield return new ValidationRow(ValidationLevel.Error, NoLayer, i.Id, -1, $"layer {i.Layer} did not load");
        }

        var start = mission.World.Start;
        if (mapOf(start.Instance) is { } startMap
            && (start.Arrival > 0 ? !HasSet(startMap, start.Arrival) : !startMap.Entities.Any(e => e.Variant == MapEntityVariant.Sprite && e.MarkerType == 0)))
        {
            yield return new ValidationRow(ValidationLevel.Error, Start, start.Instance, -1, start.Arrival > 0
                ? Inv($"the mission starts at arrival set {start.Arrival}, which its map lacks")
                : "the mission's start instance has no player start marker");
        }

        foreach (var l in mission.World.Links)
        {
            if (mapOf(l.From) is not { } from)
            {
                continue;
            }

            if (!from.TryGetEntityAt(l.Source, out var src))
            {
                yield return new ValidationRow(ValidationLevel.Error, LinkSource, l.From, -1, Inv($"exit link from @{l.Source}, which is no entity of the map"));
                continue;
            }

            if (src.MarkerType >= 0 || src.ActType != ActRules.Exit)
            {
                yield return Row(ValidationLevel.Warning, LinkSource, l.From, src, Inv($"exit link from a record whose act is 0x{src.ActType:X2}, not an exit"));
            }

            if (l.To != null && l.Arrival > 0 && mapOf(l.To) is { } target && !HasSet(target, l.Arrival))
            {
                yield return Row(ValidationLevel.Warning, Arrival, l.From, src, Inv($"exit to {l.To}: arrival set {l.Arrival} is not in its map; the game uses its start"));
            }
        }
    }

    /// <summary>True when the map has marker set <paramref name="set"/> (PlayerStart.Find's arrival marker).</summary>
    public static bool HasSet(MapFile map, int set) =>
        map.Entities.Any(e => e.Variant == MapEntityVariant.Sprite && e.IsMarker && e.MarkerType == set);

    // ---- the collision probe ----

    private IEnumerable<ValidationRow> Probe(EditDocument doc, string layer, string instance, MapFile map)
    {
        var keys = doc.Layer(layer).Edits.Where(x => x.Op != MapEditOp.Delete).Select(x => x.Key).Distinct().ToList();
        var probed = keys.Where(k => map.TryGetEntityAt(k, out var e)
            && EditorWorld.KindOf(e) is EntityKind.Robot or EntityKind.Prop or EntityKind.Actor or EntityKind.Pickup).ToList();
        if (probed.Count == 0)
        {
            return [];
        }

        var world = World(layer, doc.MapNumber(layer), map);
        var rows = new List<ValidationRow>();
        foreach (var key in probed)
        {
            map.TryGetEntityAt(key, out var e);
            var row = EditorWorld.KindOf(e!) switch
            {
                EntityKind.Robot => ProbeRobot(world, instance, e!),
                EntityKind.Pickup => ProbePickup(world, instance, e!),
                _ => ProbeProp(world, instance, e!),
            };
            if (row != null)
            {
                rows.Add(row);
            }
        }

        return rows;
    }

    private CollisionWorld World(string layer, int number, MapFile map)
    {
        if (_collision.TryGetValue(layer, out var c) && ReferenceEquals(c.Map, map))
        {
            return c.World;
        }

        var wld = map.IsOutdoor ? data.LoadWld(number) ?? data.LoadWldFor(number)?.Wld : null;
        var world = LevelCollision.Build(data, map, wld).World;
        _collision[layer] = (map, world);
        return world;
    }

    private static ValidationRow? ProbeRobot(CollisionWorld world, string instance, MapEntity e)
    {
        var feet = new Vector3(e.X, -(e.Y + Robots.MarkerLift), -e.Z);
        var type = SkyNet.Data.GameTables.Ais.Types.FirstOrDefault(t => t.Index == e.EnemyType);
        if (world.Overlaps(new SphereBody(24f, new Vector3(0f, 48f, 0f)), feet))
        {
            return Row(ValidationLevel.Warning, Inside, instance, e, "robot inside geometry");
        }

        if (type != null && (Enemy.FlyingNames.Contains(type.Name) || type.State == 11))
        {
            return null; // flyers and path vehicles keep their height
        }

        var drop = Drop(world, feet + new Vector3(0f, 8f, 0f), -1) - 8f;
        return drop > RobotDrop
            ? Row(ValidationLevel.Warning, Floating, instance, e, drop >= ProbeDepth ? "robot with no floor under it" : Inv($"robot floats {drop:0} over the floor"))
            : null;
    }

    private static ValidationRow? ProbePickup(CollisionWorld world, string instance, MapEntity e)
    {
        var at = BehaviourModel.Position(e);
        if (world.Overlaps(new SphereBody(8f), at))
        {
            return Row(ValidationLevel.Warning, Inside, instance, e, "pickup inside geometry");
        }

        var drop = Drop(world, at, -1);
        return drop > PickupReach
            ? Row(ValidationLevel.Warning, Reach, instance, e, drop >= ProbeDepth ? "pickup with no floor under it" : Inv($"pickup {drop:0} over the floor, out of reach"))
            : null;
    }

    private static ValidationRow? ProbeProp(CollisionWorld world, string instance, MapEntity e)
    {
        // The prop's own triangles give its box; one without collision is not probed.
        var at = BehaviourModel.Position(e);
        var near = new List<Triangle>();
        world.Gather(new Box(at - new Vector3(4096f), at + new Vector3(4096f)), near);
        var box = Box.Empty;
        foreach (var t in near.Where(t => t.Owner == e.FileOffset))
        {
            box = box.Union(new Box(t.Min, t.Max));
        }

        if (box.IsEmpty)
        {
            return null;
        }

        var size = box.Max - box.Min;
        var centre = (box.Min + box.Max) * 0.5f;
        var r = MathF.Max(4f, 0.25f * MathF.Min(size.X, MathF.Min(size.Y, size.Z)));
        if (near.Any(t => t.Owner != e.FileOffset && Vector3.DistanceSquared(Intersect.ClosestPoint(centre, t.A, t.B, t.C), centre) < r * r))
        {
            return Row(ValidationLevel.Warning, Inside, instance, e, "prop inside other geometry");
        }

        var drop = Drop(world, new Vector3(centre.X, box.Min.Y - 1f, centre.Z), e.FileOffset);
        return drop > PropDrop
            ? Row(ValidationLevel.Warning, Floating, instance, e, drop >= ProbeDepth ? "prop with no floor under it" : Inv($"prop floats {drop:0} over the floor"))
            : null;
    }

    // How far down the first surface under a point is (not counting the owner's own); ProbeDepth: none.
    private static float Drop(CollisionWorld world, Vector3 from, int self)
    {
        var travelled = 0f;
        for (var i = 0; i < 8 && travelled < ProbeDepth; i++)
        {
            if (!world.Raycast(from, -Vector3.UnitY, ProbeDepth - travelled, out var hit))
            {
                break;
            }

            var d = from.Y - hit.Point.Y;
            if (hit.Owner != self || self < 0)
            {
                return travelled + d;
            }

            travelled += d + 1f;
            from = hit.Point - new Vector3(0f, 1f, 0f);
        }

        return ProbeDepth;
    }

    // ---- the trigger graph ----

    /// <summary>The verifier's static trigger graph (<see cref="TriggerVerifier.Nodes"/> with each node's kind,
    /// act, next and modes) of <paramref name="edited"/> against <paramref name="baseMap"/>, by chain: the
    /// chains whose records are all new (added), all gone (removed), or that have a record added, removed or
    /// changed (changed).</summary>
    public static List<ChainChange> GraphDiff(string layer, MapFile baseMap, MapFile edited)
    {
        var before = Graph(baseMap);
        var after = Graph(edited);
        var changed = new HashSet<int>();
        foreach (var (k, sig) in after)
        {
            if (!before.TryGetValue(k, out var old) || old != sig)
            {
                changed.Add(k);
            }
        }

        changed.UnionWith(before.Keys.Where(k => !after.ContainsKey(k)));
        var seen = new HashSet<int>();
        var list = new List<ChainChange>();
        foreach (var k in changed.Order())
        {
            if (seen.Contains(k))
            {
                continue;
            }

            var inEdited = after.ContainsKey(k);
            var chain = TriggerChains.Chain(inEdited ? edited : baseMap, k, out _).Where(x => (inEdited ? after : before).ContainsKey(x)).ToList();
            if (!chain.Contains(k))
            {
                chain.Add(k);
            }

            seen.UnionWith(chain);
            var what = inEdited && chain.All(x => !before.ContainsKey(x)) ? "added"
                : !inEdited && chain.All(x => !after.ContainsKey(x)) ? "removed" : "changed";
            list.Add(new ChainChange(layer, chain.Min(), what, chain));
        }

        return list;
    }

    private static Dictionary<int, string> Graph(MapFile map)
    {
        var incoming = TriggerVerifier.Incoming(map);
        var graph = new Dictionary<int, string>();
        foreach (var (k, e) in TriggerVerifier.Nodes(map))
        {
            var kind = TriggerVerifier.KindName(e);
            var modes = string.Join(',', TriggerVerifier.Modes(e, kind, incoming.Contains(k)));
            graph[k] = Inv($"{kind} {e.ActType:X2} {TriggerChains.NextOf(map, e)} {modes}");
        }

        return graph;
    }

    private static ValidationRow Row(ValidationLevel level, string code, string instance, MapEntity e, string text) =>
        new(level, code, instance, e.FileOffset, text);

    private static string Inv(FormattableString s) => s.ToString(CultureInfo.InvariantCulture);
}
