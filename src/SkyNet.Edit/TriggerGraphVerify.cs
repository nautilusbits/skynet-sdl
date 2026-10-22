using System.Globalization;
using System.Text.RegularExpressions;
using SkyNet.Formats;
using SkyNet.Game.Behaviour;
using SkyNet.Verify;

namespace SkyNet.Edit;

/// <summary>
/// One start of a chain fired on the edited map (<see cref="TriggerFire"/>, the verifier's driver): how
/// it was set off, the effects in the verifier's vocabulary, the records it moved, lit, broke or started
/// (<see cref="Reached"/>), the movers and lights its chain walk passes that did nothing
/// (<see cref="Missed"/>), the objectives ([M1] = 1), hints, exits ("exit212/0") and a fail it caused.
/// </summary>
public sealed record SourceRun(int Source, string Mode, IReadOnlyList<string> Effects, IReadOnlyList<int> Reached,
    IReadOnlyList<int> Missed, IReadOnlyList<int> Objectives, IReadOnlyList<int> Hints, IReadOnlyList<string> Exits, bool Fails)
{
    /// <summary>"use @0773f: exit MAP.212 set 0, sound 40; reaches 2".</summary>
    public string Summary
    {
        get
        {
            var parts = new List<string>();
            parts.AddRange(Objectives.Select(o => Inv($"objective M{o}")));
            parts.AddRange(Hints.Select(h => Inv($"hint {h}")));
            parts.AddRange(Exits);
            if (Fails)
            {
                parts.Add("mission fails");
            }

            var head = Inv($"{Mode} @{Source:x5}: ");
            var what = parts.Count == 0 && Effects.Count == 0 ? "nothing happens" : string.Join(", ", parts.DefaultIfEmpty("no mission effect"));
            var reach = Inv($"; reaches {Reached.Count}") + (Missed.Count == 0 ? "" : Inv($", misses {Missed.Count}"));
            return head + what + reach;
        }
    }

    private static string Inv(FormattableString s) => s.ToString(CultureInfo.InvariantCulture);
}

/// <summary>A chain the layer adds or changes (or removes) against its DOS map, and each of its starts fired.</summary>
public sealed record ChainVerdict(int Head, string What, IReadOnlyList<int> Keys, IReadOnlyList<SourceRun> Runs)
{
    /// <summary>True when no start does nothing or fails the mission, and each reaches the movers and lights its walk passes.</summary>
    public bool Passed => Problems.Count == 0;

    /// <summary>What went wrong, by node: a start that does nothing or fails the mission, a record a start's walk
    /// passes but does not reach ("@07851 not reached from @077f3").</summary>
    public IReadOnlyList<(int Node, string Text)> Problems =>
    [
        .. Runs.SelectMany(r => (r.Effects.Count == 0 && r.Reached.Count == 0 ? [(r.Source, Inv($"{r.Mode}: nothing happens"))] : Array.Empty<(int, string)>())
            .Concat(r.Fails ? [(r.Source, Inv($"{r.Mode}: the mission fails"))] : [])
            .Concat(r.Missed.Select(k => (k, Inv($"not reached from @{r.Source:x5}"))))),
    ];

    private static string Inv(FormattableString s) => s.ToString(CultureInfo.InvariantCulture);
}

/// <summary>
/// The trigger graph's verify hook (editor plan step T8): the chains of a layer that differ from its DOS
/// map (<see cref="Validator.GraphDiff"/>, the verifier's static graph), each start of each one fired on a
/// fresh, headless copy of the edited map by the verifier's <see cref="SkyNet.Verify.Driver"/> — the run
/// <c>SkyNet.Verify</c> makes of that record. Nothing of the document changes.
/// </summary>
public static partial class GraphVerify
{
    /// <summary>Every edited chain of <paramref name="layer"/>, its starts fired.</summary>
    public static IReadOnlyList<ChainVerdict> Edited(EditDocument doc, string layer, TransfrmPrs transfrm)
    {
        var map = doc.Map(layer);
        return [.. Validator.GraphDiff(layer, doc.BaseMap(layer), map)
            .Select(c => c.What == "removed" ? new ChainVerdict(c.Head, c.What, c.Keys, []) : Verdict(map, transfrm, c.Head, c.What, c.Keys))];
    }

    /// <summary>
    /// Every chain of <paramref name="layer"/>: the verifier's nodes grouped by chain, each chain's starts fired
    /// (a start several chains share fired once), named as <see cref="Validator.GraphDiff"/> names an edited one
    /// ("added", "changed"), else "chain"; a removed chain listed without runs.
    /// </summary>
    public static IReadOnlyList<ChainVerdict> All(EditDocument doc, string layer, TransfrmPrs transfrm)
    {
        var map = doc.Map(layer);
        var diff = Validator.GraphDiff(layer, doc.BaseMap(layer), map);
        var edited = new Dictionary<int, string>();
        foreach (var c in diff.Where(c => c.What != "removed"))
        {
            c.Keys.ToList().ForEach(k => edited.TryAdd(k, c.What));
        }

        // Each node in the first chain that walks it, as GraphDiff groups them.
        var nodes = TriggerVerifier.Nodes(map);
        var seen = new HashSet<int>();
        var runs = new Dictionary<int, SourceRun?>();
        var list = new List<ChainVerdict>();
        foreach (var k in nodes.Keys)
        {
            if (seen.Contains(k))
            {
                continue;
            }

            var chain = TriggerChains.Chain(map, k, out _).Where(nodes.ContainsKey).ToList();
            if (!chain.Contains(k))
            {
                chain.Add(k);
            }

            seen.UnionWith(chain);
            var what = chain.Select(edited.GetValueOrDefault).FirstOrDefault(w => w != null);
            if (what == null && !Started(map, chain))
            {
                // Nothing sets it off: the graph's checks warn of that (unfired, dead loop); firing it says nothing more.
                continue;
            }

            what ??= "chain";
            list.Add(Verdict(map, transfrm, chain.Min(), what, chain, runs));
        }

        list.AddRange(diff.Where(c => c.What == "removed").Select(c => new ChainVerdict(c.Head, c.What, c.Keys, [])));
        return list;
    }

    /// <summary>The chain through record <paramref name="key"/> of <paramref name="map"/>, its starts fired.</summary>
    public static ChainVerdict Chain(MapFile map, TransfrmPrs transfrm, int key) =>
        Verdict(map, transfrm, key, "chain", TriggerChains.Chain(map, key, out _));

    private static ChainVerdict Verdict(MapFile map, TransfrmPrs transfrm, int head, string what, IReadOnlyList<int> keys,
        Dictionary<int, SourceRun?>? fired = null)
    {
        fired ??= [];
        var runs = Starts(map, keys).Select(s => fired.TryGetValue(s, out var r) ? r : fired[s] = Run(map, transfrm, s)).OfType<SourceRun>().ToList();
        return new ChainVerdict(head, what, keys, runs);
    }

    // True when the player sets off a record of the chain or one leading into it.
    private static bool Started(MapFile map, IReadOnlyList<int> keys) =>
        Starts(map, keys).Any(k => map.TryGetEntityAt(k, out var e) && TriggerFire.Mode(e) != "chain");

    /// <summary>The records that set the chain off: every record leading into it (any distance) or in it
    /// that the player sets off; none: its first records, by the chain alone.</summary>
    private static List<int> Starts(MapFile map, IReadOnlyList<int> keys)
    {
        var upstream = new HashSet<int>(keys);
        var queue = new Queue<int>(keys);
        while (queue.TryDequeue(out var k))
        {
            foreach (var from in TriggerChains.Incoming(map, k).Where(upstream.Add))
            {
                queue.Enqueue(from);
            }
        }

        var starts = upstream.Order().Where(k => map.TryGetEntityAt(k, out var e) && TriggerFire.Mode(e) != "chain").ToList();
        return starts.Count > 0 ? starts : [.. upstream.Where(k => !TriggerChains.Incoming(map, k).Any()).Order().DefaultIfEmpty(keys.Count > 0 ? keys[0] : -1)];
    }

    private static SourceRun? Run(MapFile map, TransfrmPrs transfrm, int key)
    {
        if (TriggerFire.Fire(map, transfrm, key) is not { } r)
        {
            return null;
        }

        var reached = r.Touched.Skip(1).ToList();
        var missed = Walk(map, key).Where(k => k != key && !r.Touched.Contains(k) && Visible(map, k)).ToList();
        return new SourceRun(key, r.Mode, r.Effects, reached, missed,
            Numbers(r.Effects, Objective(), 1), Numbers(r.Effects, Hint(), 1),
            [.. r.Effects.Where(f => f.StartsWith("exit", StringComparison.Ordinal))], r.Effects.Contains("fail"));
    }

    /// <summary>The records ObjFlipLink walks from <paramref name="key"/>: next to the end, a loop or an actor.</summary>
    private static List<int> Walk(MapFile map, int key)
    {
        var walk = new List<int>();
        for (var k = key; map.TryGetEntityAt(k, out var e) && !walk.Contains(k); k = TriggerChains.NextOf(map, e))
        {
            walk.Add(k);
            if (e.IsActor)
            {
                break;
            }
        }

        return walk;
    }

    /// <summary>True for a record whose firing shows in the effects by its key: a mover that travels, a light.</summary>
    private static bool Visible(MapFile map, int key)
    {
        map.TryGetEntityAt(key, out var e);
        var rule = ActRules.For(e!.ActType, (int)e.Variant);
        return e.MarkerType < 0 && (rule.Kind == ActKind.Light || (rule.Kind == ActKind.Mover && ActRules.Mover(e.ActType).Span > 0f));
    }

    private static List<int> Numbers(IReadOnlyList<string> effects, Regex pattern, int offset) =>
        [.. effects.Select(f => pattern.Match(f)).Where(m => m.Success)
            .Select(m => int.Parse(m.Groups[1].ValueSpan, CultureInfo.InvariantCulture) + offset)];

    [GeneratedRegex(@"^obj(\d+)$")]
    private static partial Regex Objective();

    [GeneratedRegex(@"^hint(\d+)$")]
    private static partial Regex Hint();
}
