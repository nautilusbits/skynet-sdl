using System.Numerics;

namespace SkyNet.Edit;

/// <summary>
/// Where a node's parts are, in graph units, and what lies under a point.
/// <code>
///   Position ┌──────────────────────┐
///            │ title (kind colour)  │ Header
///          ● │ in            next   │ ●   pin rows: max(inputs, outputs)
///            │ act    [slide Y +64] │     parameter rows
///            └──────────────────────┘
///            ◄──────── Width ───────►
/// </code>
/// </summary>
public static class GraphLayout
{
    public const float Width = 230f, Header = 24f, Row = 22f, Bottom = 6f, PinRadius = 5f;

    // A pin catches the pointer this many radii out (at least MinPinPixels on screen); a link this many pixels either side.
    private const float PinReach = 2f, MinPinPixels = 8f, LinkReach = 6f;

    // A link's curve: its tangents run this share of the horizontal gap out of each pin, at least MinTangent.
    private const float TangentShare = 0.5f, MinTangent = 40f;
    private const int CurveSteps = 24;

    /// <summary>A node's pin rows: the longer of its two pin lists.</summary>
    public static int PinRows(CanvasNode n) => Math.Max(n.Inputs.Count, n.Outputs.Count);

    /// <summary>A node's size.</summary>
    public static Vector2 Size(CanvasNode n) => new(Width, Header + (PinRows(n) + n.Params.Count) * Row + Bottom);

    /// <summary>A pin's centre: on the node's left (in) or right (out) edge, in the middle of its row.</summary>
    public static Vector2 Pin(CanvasNode n, PinSide side, int index, Vector2 offset = default) =>
        n.Position + offset + new Vector2(side == PinSide.In ? 0f : Width, Header + (index + 0.5f) * Row);

    /// <summary>The top left of parameter row <paramref name="index"/>.</summary>
    public static Vector2 ParamRow(CanvasNode n, int index, Vector2 offset = default) =>
        n.Position + offset + new Vector2(0f, Header + (PinRows(n) + index) * Row);

    /// <summary>A link's cubic Bézier: from an output pin rightwards, into an input pin from the left.</summary>
    public static (Vector2 A, Vector2 B, Vector2 C, Vector2 D) Curve(Vector2 from, Vector2 to)
    {
        var tangent = MathF.Max(MathF.Abs(to.X - from.X) * TangentShare, MinTangent);
        return (from, from + new Vector2(tangent, 0f), to - new Vector2(tangent, 0f), to);
    }

    /// <summary>The pin under <paramref name="at"/> (graph units) at zoom <paramref name="zoom"/>; the topmost node's first.</summary>
    public static CanvasPin? HitPin(IReadOnlyList<CanvasNode> nodes, Vector2 at, float zoom, Func<int, Vector2>? offset = null)
    {
        var reach = MathF.Max(PinRadius * PinReach, MinPinPixels / zoom);
        for (var i = nodes.Count - 1; i >= 0; i--)
        {
            var n = nodes[i];
            var shift = offset?.Invoke(n.Id) ?? Vector2.Zero;
            if (Hit(n, PinSide.Out, n.Outputs.Count, at, reach, shift) is { } o)
            {
                return o;
            }

            if (Hit(n, PinSide.In, n.Inputs.Count, at, reach, shift) is { } p)
            {
                return p;
            }
        }

        return null;
    }

    private static CanvasPin? Hit(CanvasNode n, PinSide side, int count, Vector2 at, float reach, Vector2 shift)
    {
        for (var k = 0; k < count; k++)
        {
            if (Vector2.Distance(Pin(n, side, k, shift), at) <= reach)
            {
                return new CanvasPin(n.Id, side, k);
            }
        }

        return null;
    }

    /// <summary>The topmost node whose box holds <paramref name="at"/>.</summary>
    public static int? HitNode(IReadOnlyList<CanvasNode> nodes, Vector2 at, Func<int, Vector2>? offset = null)
    {
        for (var i = nodes.Count - 1; i >= 0; i--)
        {
            var n = nodes[i];
            var min = n.Position + (offset?.Invoke(n.Id) ?? Vector2.Zero);
            var max = min + Size(n);
            if (at.X >= min.X && at.Y >= min.Y && at.X <= max.X && at.Y <= max.Y)
            {
                return n.Id;
            }
        }

        return null;
    }

    /// <summary>The link whose curve passes within a few pixels of <paramref name="at"/>; the nearest when several do.</summary>
    public static CanvasLink? HitLink(IReadOnlyList<CanvasNode> nodes, IReadOnlyList<CanvasLink> links, Vector2 at, float zoom)
    {
        var byId = ById(nodes);
        var reach = LinkReach / zoom;
        CanvasLink? best = null;
        var bestDistance = reach;
        foreach (var link in links)
        {
            if (!byId.TryGetValue(link.FromNode, out var from) || !byId.TryGetValue(link.ToNode, out var to))
            {
                continue;
            }

            var d = Distance(Curve(Pin(from, PinSide.Out, link.FromPin), Pin(to, PinSide.In, link.ToPin)), at);
            if (d <= bestDistance)
            {
                (best, bestDistance) = (link, d);
            }
        }

        return best;
    }

    /// <summary>The nodes whose box meets the rectangle between <paramref name="a"/> and <paramref name="b"/> (any two corners).</summary>
    public static IReadOnlyList<int> InBox(IReadOnlyList<CanvasNode> nodes, Vector2 a, Vector2 b)
    {
        var (min, max) = (Vector2.Min(a, b), Vector2.Max(a, b));
        var found = new List<int>();
        foreach (var n in nodes)
        {
            var nMax = n.Position + Size(n);
            if (n.Position.X <= max.X && n.Position.Y <= max.Y && nMax.X >= min.X && nMax.Y >= min.Y)
            {
                found.Add(n.Id);
            }
        }

        return found;
    }

    /// <summary>The box around <paramref name="ids"/>'s nodes (all nodes when null); null when there are none.</summary>
    public static (Vector2 Min, Vector2 Max)? Bounds(IReadOnlyList<CanvasNode> nodes, IReadOnlySet<int>? ids = null)
    {
        (Vector2 Min, Vector2 Max)? box = null;
        foreach (var n in nodes)
        {
            if (ids != null && !ids.Contains(n.Id))
            {
                continue;
            }

            var max = n.Position + Size(n);
            box = box is { } b ? (Vector2.Min(b.Min, n.Position), Vector2.Max(b.Max, max)) : (n.Position, max);
        }

        return box;
    }

    /// <summary>A point of the cubic Bézier at <paramref name="t"/> (0..1).</summary>
    public static Vector2 At((Vector2 A, Vector2 B, Vector2 C, Vector2 D) c, float t)
    {
        var u = 1f - t;
        return (u * u * u * c.A) + (3f * u * u * t * c.B) + (3f * u * t * t * c.C) + (t * t * t * c.D);
    }

    public static Dictionary<int, CanvasNode> ById(IReadOnlyList<CanvasNode> nodes)
    {
        var byId = new Dictionary<int, CanvasNode>(nodes.Count);
        foreach (var n in nodes)
        {
            byId[n.Id] = n;
        }

        return byId;
    }

    // The distance from a point to the curve, as a polyline of CurveSteps segments.
    private static float Distance((Vector2 A, Vector2 B, Vector2 C, Vector2 D) curve, Vector2 p)
    {
        var best = float.MaxValue;
        var prev = curve.A;
        for (var i = 1; i <= CurveSteps; i++)
        {
            var next = At(curve, i / (float)CurveSteps);
            best = MathF.Min(best, Segment(prev, next, p));
            prev = next;
        }

        return best;
    }

    private static float Segment(Vector2 a, Vector2 b, Vector2 p)
    {
        var ab = b - a;
        var length = ab.LengthSquared();
        var t = length > 0f ? Math.Clamp(Vector2.Dot(p - a, ab) / length, 0f, 1f) : 0f;
        return Vector2.Distance(p, a + ab * t);
    }
}
