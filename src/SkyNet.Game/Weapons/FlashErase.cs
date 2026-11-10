using System.Drawing;
using SkyNet.Formats;
using SkyNet.Game.Menu;

namespace SkyNet.Game.Weapons;

/// <summary>
/// Erases the muzzle flash DOS painted into a gun's firing frames (WEAPONnn.CFA), for the enhanced
/// look, which draws its own flash (issue #60). The frames also carry the recoil, so the rest frame
/// (0) is aligned on the gun first; around the muzzle a flash-coloured pixel that differs from the
/// aligned rest pixel takes the rest pixel: transparent off the gun, the gun's own pixel under the
/// flash. The flash's core and fringe off the gun are then grown away from what was erased.
/// <code>
///   rest f0      fire f1          erased f1
///   ..####       *#####   ─►      ..####      (* flash, # gun; the gun shifted by the recoil)
/// </code>
/// </summary>
public static class FlashErase
{
    /// <summary>The flash's colours: a gun's fire (reds, oranges, yellows) or the plasma's glow (cyans).</summary>
    private enum Glow { Fire, Plasma }

    /// <param name="Keep">Frames whose gun the recoil reshapes (no rest frame to align): every flash
    /// colour goes but inside this box (fractions of the frame: the gun's own glass).</param>
    private sealed record Entry(Glow Glow, int[] Frames, Dictionary<int, RectangleF>? Keep = null, Paint Paint = Paint.Plain);

    /// <summary>A gun's own paint: plain metal, or the flash's hues (the red super UZI), whose
    /// colours in the rest frame are then the gun's, never the flash's.</summary>
    private enum Paint { Plain, FlashHues }

    /// <summary>The firing frames that carry a painted flash, per record (both art sets).</summary>
    private static readonly Dictionary<int, Entry> Flashed = new()
    {
        [1] = new(Glow.Fire, [1, 3]),
        [2] = new(Glow.Fire, [1, 3]),
        [3] = new(Glow.Fire, [1, 3]),
        [4] = new(Glow.Fire, [1]),
        [6] = new(Glow.Fire, [1, 2]),
        [7] = new(Glow.Fire, [1, 3]),
        [8] = new(Glow.Fire, [1, 3]),
        // Frame 2 raises the gun and scatters the glow in a ring; its emitter glass is cyan too.
        [9] = new(Glow.Plasma, [1, 2], new() { [2] = RectangleF.FromLTRB(0.28f, 0.10f, 0.62f, 0.31f) }),
        [10] = new(Glow.Plasma, [2]),
        [11] = new(Glow.Plasma, [2]),
        [12] = new(Glow.Fire, [1, 3], Paint: Paint.FlashHues),
    };

    /// <summary>The whites and pale greys the flash's core and its edge are drawn in (each channel at least this).</summary>
    private const int Bright = 0xA0;
    /// <summary>A flash piece's least share of its largest piece (1/16).</summary>
    private const int Piece = 16;
    /// <summary>The erased neighbours (of 8) that make a pixel a speck left in the flash.</summary>
    private const int Ring = 5;
    /// <summary>The empty neighbours (of 8) that make a warm pixel a thin line left of the flash's edge.</summary>
    private const int Thin = 6;
    /// <summary>Passes that peel such lines (2: a two-pixel edge).</summary>
    private const int ThinPasses = 2;
    /// <summary>The recoil search's coarse pixel step.</summary>
    private const int Coarse = 4;

    /// <summary>True when frame <paramref name="frame"/> of record <paramref name="record"/> carries a flash.</summary>
    private static bool Flashes(int record, int frame) => Flashed.TryGetValue(record, out var e) && e.Frames.Contains(frame);

    /// <summary>Frame <paramref name="frame"/>'s pixels of record <paramref name="record"/>'s CFA in
    /// <paramref name="look"/> (a copy): the enhanced look's without the painted flash.</summary>
    public static byte[] Pixels(int record, CfaFile cfa, int frame, Palette palette, Look look) =>
        look == Look.Enhanced ? Erase(record, cfa, frame, palette) : cfa.Frames[frame].ToArray();

    /// <summary>Frame <paramref name="frame"/>'s pixels of record <paramref name="record"/>'s CFA, its flash erased (a copy).</summary>
    private static byte[] Erase(int record, CfaFile cfa, int frame, Palette palette)
    {
        var fire = cfa.Frames[frame].ToArray();
        if (!Flashes(record, frame))
        {
            return fire;
        }

        var shot = new Shot(fire, [.. cfa.Frames[0]], cfa.Width, cfa.Height);
        var resting = new bool[Palette.Colors];
        foreach (var p in shot.Rest)
        {
            resting[p] = true;
        }

        var glow = Flashed[record].Glow;
        var plain = Flashed[record].Paint == Paint.Plain;
        var flash = new bool[Palette.Colors];
        var white = new bool[Palette.Colors];
        var dim = new bool[Palette.Colors];
        var warm = new bool[Palette.Colors];
        for (var i = 1; i < Palette.Colors; i++)
        {
            var (r, g, b) = (palette.Rgb[i * 3], palette.Rgb[i * 3 + 1], palette.Rgb[i * 3 + 2]);
            flash[i] = IsFlash(glow, r, g, b) && (plain || !resting[i]);
            white[i] = flash[i] || r >= Bright && g >= Bright && b >= Bright;
            warm[i] = glow == Glow.Fire && r >= g && r >= b;
            dim[i] = warm[i] && (plain || !resting[i]);
        }

        if (Flashed[record].Keep is { } keeps && keeps.TryGetValue(frame, out var keep))
        {
            // A dot of a colour the gun never uses is the glow's too (its dark edge).
            var loose = flash.Select((f, i) => f || i != 0 && !resting[i]).ToArray();
            return Outside(fire, cfa.Width, cfa.Height, flash, loose, keep);
        }

        shot = shot with { Recoil = Align(shot, flash) };
        var area = Muzzle(shot, flash);

        var output = (byte[])fire.Clone();
        var erased = new Queue<int>();

        // A flash colour the rest frame lacks there takes the rest pixel: 0 off the gun; on it only
        // a colour the rest frame never uses (a lit gun keeps its own shading).
        for (var y = area.Top; y < area.Bottom; y++)
        {
            for (var x = area.Left; x < area.Right; x++)
            {
                var p = fire[y * shot.W + x];
                var under = shot.Under(x, y);
                if (!flash[p] || p == under || under != 0 && resting[p])
                {
                    continue;
                }

                output[y * shot.W + x] = under;
                if (under == 0)
                {
                    erased.Enqueue(y * shot.W + x);
                }
            }
        }

        // The core and fringe off the gun (whites, pale greys; clear of it a dim warm edge or smoke),
        // grown from what was erased.
        while (erased.TryDequeue(out var i))
        {
            for (var y = i / shot.W - 1; y <= i / shot.W + 1; y++)
            {
                for (var x = i % shot.W - 1; x <= i % shot.W + 1; x++)
                {
                    var n = y * shot.W + x;
                    if (!area.Contains(x, y) || output[n] == 0)
                    {
                        continue;
                    }

                    var p = output[n];
                    var under = shot.Under(x, y);
                    if (white[p] && under == 0 || dim[p] && shot.OffGun(x, y))
                    {
                        output[n] = 0;
                        erased.Enqueue(n);
                        continue;
                    }

                    // The core's white on the gun's edge takes the gun's pixel (not grown further).
                    if (white[p] && p == fire[n])
                    {
                        output[n] = under;
                    }
                }
            }
        }

        // Left in the flash, clear of the gun: specks in the gun's own colours (the red UZI), mostly
        // ringed by what was erased (first pass only: a sight's tip loses a pixel at most); thin
        // dim lines (the edge drawn in the shotgun pump's browns), mostly empty around.
        for (var pass = 0; pass < ThinPasses; pass++)
        {
            var specks = new List<int>();
            for (var y = area.Top; y < area.Bottom; y++)
            {
                for (var x = area.Left; x < area.Right; x++)
                {
                    var p = output[y * shot.W + x];
                    if (p == 0 || !shot.OffGun(x, y))
                    {
                        continue;
                    }

                    var (erasedNear, emptyNear) = Around(fire, output, shot.W, shot.H, x, y);
                    if (pass == 0 && erasedNear >= Ring || warm[p] && emptyNear >= Thin)
                    {
                        specks.Add(y * shot.W + x);
                    }
                }
            }

            specks.ForEach(i => output[i] = 0);
        }

        Islands(shot, output, area);
        return output;
    }

    /// <summary>Erases in <paramref name="area"/> what floats free of the gun: pixels not joined
    /// (8-connected) to one by the rest frame's gun, such as a dash of the flash's dark edge.</summary>
    private static void Islands(Shot shot, byte[] output, Rectangle area)
    {
        var (w, h) = (shot.W, shot.H);
        var joined = new bool[w * h];
        var stack = new Stack<int>();
        for (var i = 0; i < w * h; i++)
        {
            if (output[i] != 0 && !shot.OffGun(i % w, i / w))
            {
                joined[i] = true;
                stack.Push(i);
            }
        }

        while (stack.TryPop(out var i))
        {
            for (var y = Math.Max(0, i / w - 1); y <= Math.Min(h - 1, i / w + 1); y++)
            {
                for (var x = Math.Max(0, i % w - 1); x <= Math.Min(w - 1, i % w + 1); x++)
                {
                    var n = y * w + x;
                    if (!joined[n] && output[n] != 0)
                    {
                        joined[n] = true;
                        stack.Push(n);
                    }
                }
            }
        }

        for (var y = area.Top; y < area.Bottom; y++)
        {
            for (var x = area.Left; x < area.Right; x++)
            {
                if (!joined[y * w + x])
                {
                    output[y * w + x] = 0;
                }
            }
        }
    }

    /// <summary>Of (x, y)'s 8 neighbours: how many the firing frame drew and are erased now, and how many are empty now.</summary>
    private static (int Erased, int Empty) Around(byte[] fire, byte[] output, int w, int h, int x, int y)
    {
        var (erased, empty) = (0, 0);
        for (var ny = y - 1; ny <= y + 1; ny++)
        {
            for (var nx = x - 1; nx <= x + 1; nx++)
            {
                if (nx == x && ny == y)
                {
                    continue;
                }

                var inside = nx >= 0 && ny >= 0 && nx < w && ny < h;
                if (!inside || output[ny * w + nx] == 0)
                {
                    empty++;
                    erased += inside && fire[ny * w + nx] != 0 ? 1 : 0;
                }
            }
        }

        return (erased, empty);
    }

    /// <summary>The frame with every flash colour transparent but the gun's glass: the flash pixels
    /// inside <paramref name="keep"/> (fractions) joined to a gun pixel (not the glow's loose dots);
    /// a <paramref name="loose"/> colour goes where no gun pixel is next to it.</summary>
    private static byte[] Outside(byte[] fire, int w, int h, bool[] flash, bool[] loose, RectangleF keep)
    {
        var box = Rectangle.FromLTRB((int)(keep.Left * w), (int)(keep.Top * h), (int)Math.Ceiling(keep.Right * w), (int)Math.Ceiling(keep.Bottom * h));
        var output = (byte[])fire.Clone();
        var kept = new bool[fire.Length];
        var stack = new Stack<int>();
        for (var i = 0; i < fire.Length; i++)
        {
            if (flash[fire[i]] && box.Contains(i % w, i / w) && Touches(fire, w, h, loose, i, Reach.Four))
            {
                kept[i] = true;
                stack.Push(i);
            }
        }

        // The glass: the flash colour grown from where it meets the gun, inside the box.
        while (stack.TryPop(out var i))
        {
            for (var y = i / w - 1; y <= i / w + 1; y++)
            {
                for (var x = i % w - 1; x <= i % w + 1; x++)
                {
                    var n = y * w + x;
                    if (box.Contains(x, y) && !kept[n] && flash[fire[n]])
                    {
                        kept[n] = true;
                        stack.Push(n);
                    }
                }
            }
        }

        for (var i = 0; i < fire.Length; i++)
        {
            if (!kept[i] && (flash[fire[i]] || loose[fire[i]] && !Touches(fire, w, h, loose, i, Reach.Eight)))
            {
                output[i] = 0;
            }
        }

        return output;
    }

    /// <summary>Which neighbours <see cref="Touches"/> looks at.</summary>
    private enum Reach { Four, Eight }

    /// <summary>True when pixel <paramref name="i"/> has a gun pixel (neither empty nor <paramref name="loose"/>) next to it.</summary>
    private static bool Touches(byte[] px, int w, int h, bool[] loose, int i, Reach reach)
    {
        var (x, y) = (i % w, i / w);
        for (var ny = y - 1; ny <= y + 1; ny++)
        {
            for (var nx = x - 1; nx <= x + 1; nx++)
            {
                var diagonal = nx != x && ny != y;
                if (diagonal && reach == Reach.Four || nx < 0 || ny < 0 || nx >= w || ny >= h)
                {
                    continue;
                }

                if (px[ny * w + nx] is var p && p != 0 && !loose[p])
                {
                    return true;
                }
            }
        }

        return false;
    }

    /// <summary>A firing frame, the rest frame and the rest frame's shift onto it (the recoil).</summary>
    private readonly record struct Shot(byte[] Fire, byte[] Rest, int W, int H, Point Recoil = default)
    {
        /// <summary>The rest frame's pixel under (x, y) of the firing frame; 0 outside it.</summary>
        public byte Under(int x, int y)
        {
            var (rx, ry) = (x - Recoil.X, y - Recoil.Y);
            return rx >= 0 && ry >= 0 && rx < W && ry < H ? Rest[ry * W + rx] : (byte)0;
        }

        /// <summary>True when the rest frame has no gun within a pixel of (x, y): its edge is kept.</summary>
        public bool OffGun(int x, int y)
        {
            for (var ny = y - 1; ny <= y + 1; ny++)
            {
                for (var nx = x - 1; nx <= x + 1; nx++)
                {
                    if (Under(nx, ny) != 0)
                    {
                        return false;
                    }
                }
            }

            return true;
        }
    }

    /// <summary>The recoil: the shift of the rest frame that best matches the firing frame's gun,
    /// searched on every <see cref="Coarse"/>th pixel, then refined on all of them.</summary>
    private static Point Align(Shot shot, bool[] flash)
    {
        var reach = Math.Max(shot.W, shot.H) / 16 + 4;
        var best = Best(shot, flash, Point.Empty, reach, Coarse);
        return Best(shot, flash, best, Coarse, 1);
    }

    /// <summary>The shift within <paramref name="reach"/> of <paramref name="from"/> matching most gun pixels.</summary>
    private static Point Best(Shot shot, bool[] flash, Point from, int reach, int step)
    {
        var (w, h) = (shot.W, shot.H);
        var best = from;
        var bestScore = -1;
        for (var dy = from.Y - reach; dy <= from.Y + reach; dy++)
        {
            for (var dx = from.X - reach; dx <= from.X + reach; dx++)
            {
                var score = 0;
                for (var y = Math.Max(0, dy); y < Math.Min(h, h + dy); y += step)
                {
                    for (var x = Math.Max(0, dx); x < Math.Min(w, w + dx); x += step)
                    {
                        var p = shot.Fire[y * w + x];
                        if (p != 0 && !flash[p] && shot.Rest[(y - dy) * w + x - dx] == p)
                        {
                            score++;
                        }
                    }
                }

                if (score > bestScore)
                {
                    (bestScore, best) = (score, new Point(dx, dy));
                }
            }
        }

        return best;
    }

    /// <summary>Around the muzzle: the box of the blobs of flash colour off the gun, padded,
    /// so a flash-coloured detail elsewhere (a glove's glint, a wooden pump) is left alone.</summary>
    private static Rectangle Muzzle(Shot shot, bool[] flash)
    {
        var (w, h) = (shot.W, shot.H);
        var seen = new bool[w * h];
        var blobs = new List<(int Size, Rectangle Box)>();
        var stack = new Stack<int>();
        for (var start = 0; start < w * h; start++)
        {
            if (seen[start] || !Seed(shot, flash, start))
            {
                continue;
            }

            // One 8-connected blob of seeds.
            var (size, x0, y0, x1, y1) = (0, w, h, 0, 0);
            seen[start] = true;
            stack.Push(start);
            while (stack.TryPop(out var i))
            {
                var (x, y) = (i % w, i / w);
                (size, x0, y0, x1, y1) = (size + 1, Math.Min(x0, x), Math.Min(y0, y), Math.Max(x1, x), Math.Max(y1, y));
                for (var ny = Math.Max(0, y - 1); ny <= Math.Min(h - 1, y + 1); ny++)
                {
                    for (var nx = Math.Max(0, x - 1); nx <= Math.Min(w - 1, x + 1); nx++)
                    {
                        var n = ny * w + nx;
                        if (!seen[n] && Seed(shot, flash, n))
                        {
                            seen[n] = true;
                            stack.Push(n);
                        }
                    }
                }
            }

            blobs.Add((size, Rectangle.FromLTRB(x0, y0, x1 + 1, y1 + 1)));
        }

        // The flash with its pieces a sight or barrel cut off (each at least 1/16 of the largest).
        if (blobs.Count == 0)
        {
            return Rectangle.Empty;
        }

        var largest = blobs.Max(b => b.Size);
        var box = Rectangle.Empty;
        foreach (var b in blobs.Where(b => b.Size * Piece >= largest))
        {
            box = box.IsEmpty ? b.Box : Rectangle.Union(box, b.Box);
        }

        var pad = Math.Max(w, h) / 16;
        box.Inflate(pad, pad);
        box.Intersect(new Rectangle(0, 0, w, h));
        return box;
    }

    private static bool Seed(Shot shot, bool[] flash, int i) => flash[shot.Fire[i]] && shot.Under(i % shot.W, i / shot.W) == 0;

    /// <summary>Whether a colour is the flash's: fire = reds, oranges, browns, yellows down to their
    /// dark edges; plasma = cyans.</summary>
    private static bool IsFlash(Glow glow, int r, int g, int b) => glow switch
    {
        Glow.Fire => r >= 0x38 && r >= g + 0x10 && r >= b + 0x10
            || r >= 0x50 && Math.Abs(r - g) <= 0x18 && b <= r - 0x30,
        _ => b >= 0x48 && b >= r + 0x18 && g >= r + 0x14,
    };
}
