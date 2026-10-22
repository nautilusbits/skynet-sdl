using System.Globalization;
using System.Numerics;
using SkyNet.Formats;
using SkyNet.Game.Behaviour;

namespace SkyNet.Edit;

/// <summary>The colour group a link line takes: the act family of the record it leaves.</summary>
public enum LinkFamily { Trigger, Mover, Light, Exit, Mission, Destruction, Sound, Other }

/// <summary>
/// The trigger chains of a map as the editor shows them (editor plan step E8): each record's link
/// (act, next) as the SKYNET.EXE handler table names it (<see cref="ActRules"/>,
/// docs/game-logic/action-handlers.md), the chain a record belongs to (next followed both ways) and
/// the links of the whole map for the 3D arrows.
/// </summary>
public static class TriggerChains
{
    /// <summary>An act id as "0x3F slide X +64": the id and what the handler table makes of it.</summary>
    public static string ActName(int act) => string.Create(CultureInfo.InvariantCulture, $"0x{act:X2} {ActText(act)}");

    /// <summary>What act <paramref name="act"/> does, in a few words.</summary>
    public static string ActText(int act)
    {
        var rule = ActRules.For(act);
        return rule.Kind switch
        {
            ActKind.None => "none",
            ActKind.Light => "light " + ActRules.LightOp(act),
            ActKind.Destructible => act == ActRules.DestructRamp ? "destructible (ramp)" : "destructible (stage)",
            ActKind.Demolish => "demolish",
            ActKind.Hint => Inv($"hint {rule.P6 + 1}"),
            ActKind.Objective => Inv($"objective M{rule.P6 + 1}"),
            ActKind.Fail => "mission fails",
            ActKind.Relay => "counter relay",
            ActKind.Mover => MoverText(act),
            ActKind.Water => Inv($"water {(rule.P4 == 0 ? "to own Y" : rule.P4 < 0 ? "up " + -rule.P4 : "down " + rule.P4)}"),
            ActKind.SoundCue => Inv($"sound {rule.P4}"),
            ActKind.SoundLoop => "sound loop",
            ActKind.Voice => "voice",
            ActKind.ProxGate => "use gate",
            ActKind.Exit => "exit",
            ActKind.ProxChain => Inv($"proximity {rule.P4}"),
            ActKind.Spawn => "spawn",
            ActKind.Pickup => "pickup",
            ActKind.Inert => "inert",
            _ => "undecoded",
        };
    }

    private static string MoverText(int act)
    {
        var m = ActRules.Mover(act);
        var axis = "XYZ"[m.Axis];
        return m.Family switch
        {
            MoverFamily.Rot when m.Spins => Inv($"spin {axis}"),
            MoverFamily.Rot => Inv($"turn {axis} {m.Span:0}"),
            MoverFamily.Swing => Inv($"swing {axis} {(m.Sign > 0 ? "+" : "-")}{m.Span:0}"),
            MoverFamily.Jump => Inv($"jump {axis} {(m.Sign > 0 ? "+" : "-")}{m.Span:0}"),
            _ => Inv($"slide {axis} {(m.Sign > 0 ? "+" : "-")}{m.Span:0}"),
        };
    }

    /// <summary>Every act id the table knows (the act picker's rows), in id order.</summary>
    public static IReadOnlyList<int> KnownActs { get; } =
        [.. Enumerable.Range(0, 256).Where(a => a == 0 || ActRules.For(a).Kind != ActKind.Undecoded)];

    /// <summary>The line colour group of a link leaving a record with act <paramref name="act"/>.</summary>
    public static LinkFamily Family(int act) => ActRules.For(act).Kind switch
    {
        ActKind.ProxGate or ActKind.ProxChain or ActKind.Relay => LinkFamily.Trigger,
        ActKind.Mover or ActKind.Water => LinkFamily.Mover,
        ActKind.Light => LinkFamily.Light,
        ActKind.Exit => LinkFamily.Exit,
        ActKind.Objective or ActKind.Hint or ActKind.Fail => LinkFamily.Mission,
        ActKind.Destructible or ActKind.Demolish or ActKind.Spawn => LinkFamily.Destruction,
        ActKind.SoundCue or ActKind.SoundLoop or ActKind.Voice => LinkFamily.Sound,
        _ => LinkFamily.Other,
    };

    /// <summary>A family's line colour (RGBA 0-1).</summary>
    public static Vector4 Colour(LinkFamily family) => family switch
    {
        LinkFamily.Trigger => new(1f, 0.9f, 0.2f, 1f),
        LinkFamily.Mover => new(0.3f, 0.6f, 1f, 1f),
        LinkFamily.Light => new(1f, 0.55f, 0.15f, 1f),
        LinkFamily.Exit => new(1f, 0.3f, 1f, 1f),
        LinkFamily.Mission => new(0.3f, 1f, 0.4f, 1f),
        LinkFamily.Destruction => new(1f, 0.25f, 0.25f, 1f),
        LinkFamily.Sound => new(0.3f, 1f, 1f, 1f),
        _ => new(0.75f, 0.75f, 0.75f, 1f),
    };

    /// <summary>The record <paramref name="e"/>'s next link when it leads to an entity of the map; −1 otherwise.</summary>
    public static int NextOf(MapFile map, MapEntity e) => e.LinkNext > 0 && e.LinkNext != e.FileOffset && map.TryGetEntityAt(e.LinkNext, out _) ? e.LinkNext : -1;

    /// <summary>The records whose next link is <paramref name="key"/>, in file order.</summary>
    public static IReadOnlyList<int> Incoming(MapFile map, int key) =>
        [.. map.Entities.Where(e => e.FileOffset != key && e.LinkNext == key).Select(e => e.FileOffset)];

    /// <summary>
    /// The chain through <paramref name="key"/>: back along the incoming links (the first one where
    /// several lead in) to the head, then forward along next to the end or to a record already in it
    /// (a loop). The list is in walk order; <paramref name="at"/> is <paramref name="key"/>'s index.
    /// </summary>
    public static IReadOnlyList<int> Chain(MapFile map, int key, out int at)
    {
        at = -1;
        if (!map.TryGetEntityAt(key, out _))
        {
            return [];
        }

        var back = new List<int>();
        var seen = new HashSet<int> { key };
        for (var k = key; Incoming(map, k) is [var first, ..] && seen.Add(first); k = first)
        {
            back.Add(first);
        }

        back.Reverse();
        var chain = new List<int>(back) { key };
        at = back.Count;
        for (var k = key; map.TryGetEntityAt(k, out var e) && NextOf(map, e) is var n and >= 0 && seen.Add(n); k = n)
        {
            chain.Add(n);
        }

        return chain;
    }

    /// <summary>Every link of the map (from, to, the family of from's act), in file order.</summary>
    public static IEnumerable<(int From, int To, LinkFamily Family)> Links(MapFile map)
    {
        foreach (var e in map.Entities)
        {
            if (NextOf(map, e) is var n and >= 0)
            {
                yield return (e.FileOffset, n, Family(e.MarkerType >= 0 ? 0 : e.ActType));
            }
        }
    }

    /// <summary>A record's line in the chain list: key, kind and act.</summary>
    public static string Label(MapFile map, MapEntity e) =>
        EditorWorld.Label(map, e) + (e.MarkerType >= 0 ? "" : "  " + ActName(e.ActType));

    /// <summary>An arrow's line vertices from <paramref name="a"/> to <paramref name="b"/>: the shaft and two
    /// barbs at <paramref name="b"/>, <paramref name="head"/> long at most a third of the shaft.</summary>
    public static void Arrow(List<Vector3> into, Vector3 a, Vector3 b, float head = 48f)
    {
        var d = b - a;
        var length = d.Length();
        if (length < 1e-3f)
        {
            return;
        }

        into.Add(a);
        into.Add(b);
        var dir = d / length;
        var side = Vector3.Cross(dir, Vector3.UnitY);
        if (side.LengthSquared() < 1e-6f)
        {
            side = Vector3.UnitX;
        }

        side = Vector3.Normalize(side);
        var h = MathF.Min(head, length / 3f);
        foreach (var s in new[] { side, -side })
        {
            into.Add(b);
            into.Add(b - dir * h + s * h * 0.5f);
        }
    }

    private static string Inv(FormattableString s) => s.ToString(CultureInfo.InvariantCulture);
}
