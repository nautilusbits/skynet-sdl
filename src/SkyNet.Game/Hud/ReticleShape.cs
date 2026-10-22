using System.Drawing;
using System.Numerics;

namespace SkyNet.Game.Hud;

/// <summary>How the vehicle reticle is drawn: the art's own pixels blown up, or its shape as vectors.</summary>
public enum ReticleDraw
{
    ScaledArt,
    Vectors,
}

/// <summary>Whether the data has the 640 × 480 HUD art (MDMDHRES.BSA's CROSHAIR.IMG).</summary>
public enum HiresArt
{
    Missing,
    Present,
}

/// <summary>One run of pixels on a circle round the art's centre: radius and angles (radians, y down).</summary>
public readonly record struct ReticleArc(float Radius, float From, float To);

/// <summary>
/// CROSHAIR.IMG's pixels as drawing elements, in art pixels from the art's centre (the Godot
/// port's crosshair.gd _reticle_shape), so the hi-res set can draw DOS's shape crisp at any
/// resolution: <see cref="Arcs"/>, runs of pixels on the one circle most pixels share (DOS's
/// 640 × 480 art: eight, radius ≈ 59.5), and <see cref="Rects"/>, every other pixel as row runs
/// merged downwards (the cross bars, ticks and their end caps). A straight piece centred on
/// the pixel right of / under the centre moves half a pixel, so the cross runs through it.
/// </summary>
public sealed class ReticleShape
{
    public IReadOnlyList<ReticleArc> Arcs { get; }
    public IReadOnlyList<RectangleF> Rects { get; }

    private ReticleShape(List<ReticleArc> arcs, List<RectangleF> rects)
    {
        Arcs = arcs;
        Rects = rects;
    }

    /// <summary>The shape of a <paramref name="width"/> × <paramref name="height"/> image whose
    /// non-zero pixels are the reticle.</summary>
    public static ReticleShape From(int width, int height, ReadOnlySpan<byte> pixels)
    {
        var centre = new Vector2(width, height) * 0.5f;
        var on = new HashSet<(int X, int Y)>();
        for (var y = 0; y < height; y++)
        {
            for (var x = 0; x < width; x++)
            {
                if (pixels[y * width + x] != 0)
                {
                    on.Add((x, y));
                }
            }
        }

        Vector2 Offset((int X, int Y) p) => new Vector2(p.X + 0.5f, p.Y + 0.5f) - centre;

        // The circle: the radius most pixels share (1-pixel bins).
        var bins = new Dictionary<int, int>();
        foreach (var p in on)
        {
            var k = (int)MathF.Round(Offset(p).Length());
            bins[k] = bins.GetValueOrDefault(k) + 1;
        }

        var r0 = -1;
        foreach (var (k, n) in bins)
        {
            if (k >= 8 && n >= 24 && (r0 < 0 || n > bins[r0]))
            {
                r0 = k;
            }
        }

        var arcs = new List<ReticleArc>();
        var rest = new HashSet<(int X, int Y)>(on);
        if (r0 > 0)
        {
            var ring = new List<(float Angle, (int X, int Y) P)>();
            var sum = 0f;
            foreach (var p in on)
            {
                var v = Offset(p);
                if (MathF.Abs(v.Length() - r0) <= 1f)
                {
                    ring.Add((MathF.Atan2(v.Y, v.X), p));
                    sum += v.Length();
                }
            }

            var rad = sum / Math.Max(1, ring.Count);
            ring.Sort((a, b) => a.Angle.CompareTo(b.Angle));
            var n = ring.Count;
            // Start after the widest gap, so no run straddles ±π.
            int start = 0;
            var widest = -1f;
            for (var i = 0; i < n; i++)
            {
                var gap = n > 1 ? Wrap(ring[(i + 1) % n].Angle - ring[i].Angle) : 2f * MathF.PI;
                if (gap > widest)
                {
                    widest = gap;
                    start = (i + 1) % n;
                }
            }

            var runs = new List<List<(float Angle, (int X, int Y) P)>>();
            var current = new List<(float Angle, (int X, int Y) P)>();
            for (var j = 0; j < n; j++)
            {
                var e = ring[(start + j) % n];
                if (current.Count > 0 && Wrap(e.Angle - current[^1].Angle) * rad > 2f)
                {
                    runs.Add(current);
                    current = [];
                }

                current.Add(e);
            }

            if (current.Count > 0)
            {
                runs.Add(current);
            }

            foreach (var run in runs)
            {
                var a0 = run[0].Angle;
                var span = Wrap(run[^1].Angle - a0);
                // A few pixels touching the ring are a bar's end or a cap: they stay rects.
                if (span * rad < 4f)
                {
                    continue;
                }

                var pad = 0.5f / rad;
                arcs.Add(new ReticleArc(rad, a0 - pad, a0 + span + pad));
                foreach (var e in run)
                {
                    rest.Remove(e.P);
                }
            }
        }

        // Everything else: row runs, merged down while the next row repeats them.
        var rects = new List<RectangleF>();
        var growing = new Dictionary<(int X0, int X1), RectangleF>();
        for (var y = 0; y < height; y++)
        {
            var row = new HashSet<(int X0, int X1)>();
            var x = 0;
            while (x < width)
            {
                if (!rest.Contains((x, y)))
                {
                    x++;
                    continue;
                }

                var x0 = x;
                while (x < width && rest.Contains((x, y)))
                {
                    x++;
                }

                row.Add((x0, x));
            }

            foreach (var key in growing.Keys.ToList())
            {
                if (row.Remove(key))
                {
                    var r = growing[key];
                    growing[key] = r with { Height = r.Height + 1 };
                }
                else
                {
                    rects.Add(growing[key]);
                    growing.Remove(key);
                }
            }

            foreach (var key in row)
            {
                growing[key] = new RectangleF(key.X0, y, key.X1 - key.X0, 1);
            }
        }

        rects.AddRange(growing.Values);

        // Connected pieces (a bar and its cap move together); centre those on the centre pixel.
        var piece = new int[rects.Count];
        Array.Fill(piece, -1);
        var boxes = new List<RectangleF>();
        for (var i = 0; i < rects.Count; i++)
        {
            if (piece[i] >= 0)
            {
                continue;
            }

            var stack = new Stack<int>([i]);
            piece[i] = boxes.Count;
            var box = rects[i];
            while (stack.Count > 0)
            {
                var a = rects[stack.Pop()];
                box = RectangleF.Union(box, a);
                var grown = RectangleF.Inflate(a, 0.5f, 0.5f);
                for (var j = 0; j < rects.Count; j++)
                {
                    if (piece[j] < 0 && grown.IntersectsWith(rects[j]))
                    {
                        piece[j] = boxes.Count;
                        stack.Push(j);
                    }
                }
            }

            boxes.Add(box);
        }

        var outRects = new List<RectangleF>(rects.Count);
        for (var i = 0; i < rects.Count; i++)
        {
            var box = boxes[piece[i]];
            var shift = Vector2.Zero;
            if (MathF.Abs(box.X + box.Width * 0.5f - (centre.X + 0.5f)) < 1e-3f)
            {
                shift.X = -0.5f;
            }

            if (MathF.Abs(box.Y + box.Height * 0.5f - (centre.Y + 0.5f)) < 1e-3f)
            {
                shift.Y = -0.5f;
            }

            var r = rects[i];
            outRects.Add(new RectangleF(r.X - centre.X + shift.X, r.Y - centre.Y + shift.Y, r.Width, r.Height));
        }

        return new ReticleShape(arcs, outRects);
    }

    private static float Wrap(float a)
    {
        var t = 2f * MathF.PI;
        a %= t;
        return a < 0f ? a + t : a;
    }
}
