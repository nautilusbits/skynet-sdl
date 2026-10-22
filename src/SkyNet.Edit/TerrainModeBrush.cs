using System.Numerics;
using SkyNet.Formats;

namespace SkyNet.Edit;

/// <summary>The view's pointer as the brush reads it each frame: where it meets the terrain (null:
/// off it), the left button and the modifiers.</summary>
public readonly record struct BrushInput(Vector3? Hit, bool Left, bool Shift, bool Ctrl);

/// <summary>
/// The Terrain mode's brush: tool, radius, strength, falloff, and the stroke state machine.
/// <code>
///  LMB down on terrain ─► BeginStroke ─► Dab each frame held ─► LMB up ─► EndStroke (one undo step)
///  Ctrl+LMB            ─► pick: Flatten takes the height, Paint the tile (no stroke)
///  Shift held          ─► Raise ↔ Lower
///  Turn                ─► one quarter turn of the clicked cell's tile per press
/// </code>
/// </summary>
public sealed class SculptBrush
{
    public const float MinRadius = 128f, MaxRadius = 16384f, RadiusStep = 1.25f;
    /// <summary>Raise/Lower/Noise at full strength, world units per second at the centre.</summary>
    public const float SculptRate = 1600f;
    /// <summary>Smooth/Flatten at full strength: blend per second.</summary>
    public const float BlendRate = 4f;

    public TerrainTool Tool { get; set; } = TerrainTool.Raise;
    public float Radius { get; set; } = 1024f;
    public float Strength { get; set; } = 0.5f;
    public BrushFalloff Falloff { get; set; } = BrushFalloff.Smooth;
    /// <summary>Flatten's height (world units); Ctrl+click picks it.</summary>
    public float Target { get; set; }
    /// <summary>Paint's layer-2 byte; Ctrl+click picks it.</summary>
    public int Tile { get; set; }

    public bool Stroking { get; private set; }

    private bool _wasDown, _picking;
    private int _seed;

    /// <summary>The tool a frame applies: Shift turns Raise into Lower and back.</summary>
    public TerrainTool Effective(bool shift) => (shift, Tool) switch
    {
        (true, TerrainTool.Raise) => TerrainTool.Lower,
        (true, TerrainTool.Lower) => TerrainTool.Raise,
        _ => Tool,
    };

    /// <summary>] key.</summary>
    public void Grow() => Radius = Math.Clamp(Radius * RadiusStep, MinRadius, MaxRadius);

    /// <summary>[ key.</summary>
    public void Shrink() => Radius = Math.Clamp(Radius / RadiusStep, MinRadius, MaxRadius);

    /// <summary>One frame of the pointer over the view, <paramref name="dt"/> seconds after the last.</summary>
    public void Frame(ITerrainModel model, in BrushInput input, float dt)
    {
        var pressed = input.Left && !_wasDown;
        _wasDown = input.Left;

        // Released: the stroke ends (its dabs are one undo step).
        if (!input.Left)
        {
            End(model);
            _picking = false;
            return;
        }

        if (pressed && input.Ctrl)
        {
            _picking = true;
            Pick(model, input.Hit);
            return;
        }

        if (_picking || input.Hit is not { } hit)
        {
            return;
        }

        // A stroke starts only where the press met the terrain.
        if (pressed)
        {
            Stroking = true;
            _seed++;
            model.BeginStroke(Label(Tool));
        }

        // Turn acts once per press, not every frame held.
        if (Stroking && (pressed || Tool != TerrainTool.Turn))
        {
            model.Dab(Dab(Effective(input.Shift), hit, dt));
        }
    }

    /// <summary>Ends a stroke that is on (the mode left, the level changed).</summary>
    public void End(ITerrainModel model)
    {
        if (!Stroking)
        {
            return;
        }

        Stroking = false;
        model.EndStroke();
    }

    public static string Label(TerrainTool tool) => tool is TerrainTool.Paint or TerrainTool.Turn ? $"Terrain {tool}" : $"Sculpt {tool}";

    private BrushDab Dab(TerrainTool tool, Vector3 hit, float dt)
    {
        var amount = tool switch
        {
            TerrainTool.Raise or TerrainTool.Lower or TerrainTool.Noise => Strength * SculptRate * dt,
            TerrainTool.Smooth or TerrainTool.Flatten => MathF.Min(1f, Strength * BlendRate * dt),
            _ => 1f,
        };
        return new BrushDab(tool, new Vector2(hit.X, hit.Z), Radius, Falloff, amount, Target, Tile, _seed);
    }

    // Ctrl+click: the eyedropper of the tool on.
    private void Pick(ITerrainModel model, Vector3? hit)
    {
        if (hit is not { } at)
        {
            return;
        }

        if (Tool == TerrainTool.Flatten)
        {
            Target = BrushMath.HeightAt(model, at.X, at.Z);
            return;
        }

        if (Tool == TerrainTool.Paint && BrushMath.CellAt(model, at.X, at.Z) is var (col, row) && col >= 0)
        {
            Tile = model.Tile(col, row);
        }
    }
}

/// <summary>The brush's geometry over the heightfield: the ring, world ↔ cell, the pointer's ray on
/// the terrain and the chunks a change touches.
/// World x = col·256, z = row·256 − 65 536 (row 0 north); a cell's corners NW (col, row) … SE (col+1, row+1).</summary>
public static class BrushMath
{
    public const float CellSize = (float)WldTerrain.WorldPerCell;
    private const float ZOrigin = (float)WldTerrain.ZFlipK;
    private const float Epsilon = 1e-7f;

    /// <summary>The distance from the centre where <paramref name="falloff"/>'s weight is half: the
    /// brush ring's inner circle (e.g. Linear: half the radius; Constant: the rim).</summary>
    public static float HalfWeight(float radius, BrushFalloff falloff)
    {
        var brush = new TerrainBrush { Radius = radius, Falloff = falloff };
        var (lo, hi) = (0f, radius);
        for (var i = 0; i < 24; i++)
        {
            var mid = (lo + hi) / 2f;
            (lo, hi) = brush.Weight(mid) >= 0.5 ? (mid, hi) : (lo, mid);
        }

        return lo;
    }

    /// <summary>The world point of corner (col, row) at height <paramref name="h"/>.</summary>
    public static Vector3 World(int col, int row, float h) => new(col * CellSize, h, row * CellSize - ZOrigin);

    /// <summary>The cell under world (x, z); (−1, −1) off the field.</summary>
    public static (int Col, int Row) CellAt(ITerrainModel model, float x, float z)
    {
        var (u, v) = (x / CellSize, (z + ZOrigin) / CellSize);
        var cells = model.Corners - 1;
        if (u < 0f || v < 0f || u >= cells || v >= cells)
        {
            return (-1, -1);
        }

        return ((int)u, (int)v);
    }

    /// <summary>The drawn ground's height at world (x, z): the cell's NW–SE triangle (clamped to the field).</summary>
    public static float HeightAt(ITerrainModel model, float x, float z)
    {
        var cells = model.Corners - 1;
        var (u, v) = (Math.Clamp(x / CellSize, 0f, cells - 1e-3f), Math.Clamp((z + ZOrigin) / CellSize, 0f, cells - 1e-3f));
        var (col, row) = ((int)u, (int)v);
        var (fu, fv) = (u - col, v - row);
        var (nw, ne, se, sw) = (model.Height(col, row), model.Height(col + 1, row), model.Height(col + 1, row + 1), model.Height(col, row + 1));
        // NW–SE: NE triangle (NW, NE, SE) where fu >= fv, else SW (NW, SE, SW).
        return fu >= fv ? nw + (ne - nw) * fu + (se - ne) * fv : nw + (sw - nw) * fv + (se - sw) * fu;
    }

    /// <summary>Where a ray from <paramref name="origin"/> along <paramref name="direction"/> first meets
    /// the terrain's triangles (null: it misses the field). Walks the cells under the ray (2D DDA).</summary>
    public static Vector3? Raycast(ITerrainModel model, Vector3 origin, Vector3 direction)
    {
        var cells = model.Corners - 1;
        var (u, v) = (origin.X / CellSize, (origin.Z + ZOrigin) / CellSize);
        var (du, dv) = (direction.X / CellSize, direction.Z / CellSize);

        // Clip the ray's ground track to the field: [t0, t1] in ray units.
        var (t0, t1) = (0f, float.MaxValue);
        if (!Clip(u, du, cells, ref t0, ref t1) || !Clip(v, dv, cells, ref t0, ref t1))
        {
            return null;
        }

        var (su, sv) = (u + du * t0, v + dv * t0);
        var (col, row) = (Math.Clamp((int)su, 0, cells - 1), Math.Clamp((int)sv, 0, cells - 1));
        var (stepC, stepR) = (Math.Sign(du), Math.Sign(dv));
        var tC = du > 0f ? (col + 1 - u) / du : du < 0f ? (col - u) / du : float.MaxValue;
        var tR = dv > 0f ? (row + 1 - v) / dv : dv < 0f ? (row - v) / dv : float.MaxValue;
        var (dtC, dtR) = (du != 0f ? MathF.Abs(1f / du) : float.MaxValue, dv != 0f ? MathF.Abs(1f / dv) : float.MaxValue);
        while (col >= 0 && row >= 0 && col < cells && row < cells)
        {
            if (HitCell(model, col, row, origin, direction) is { } t)
            {
                return origin + direction * t;
            }

            if (MathF.Min(tC, tR) > t1)
            {
                return null;
            }

            if (tC < tR)
            {
                (col, tC) = (col + stepC, tC + dtC);
            }
            else
            {
                (row, tR) = (row + stepR, tR + dtR);
            }
        }

        return null;
    }

    /// <summary>The chunks (<see cref="SkyNet.Game.Terrain.ChunkCells"/> cells square, index cz·across + cx)
    /// that <paramref name="rect"/> touches on a field of <paramref name="corners"/> corners.</summary>
    public static IEnumerable<int> Chunks(GridRect rect, int corners)
    {
        var size = SkyNet.Game.Terrain.ChunkCells;
        var across = ChunksAcross(corners);
        var (c0, r0) = (Math.Max(rect.Col, 0) / size, Math.Max(rect.Row, 0) / size);
        var (c1, r1) = (Math.Min((rect.EndCol - 1) / size, across - 1), Math.Min((rect.EndRow - 1) / size, across - 1));
        for (var cz = r0; cz <= r1; cz++)
        {
            for (var cx = c0; cx <= c1; cx++)
            {
                yield return cz * across + cx;
            }
        }
    }

    public static int ChunksAcross(int corners) => (corners - 1 + SkyNet.Game.Terrain.ChunkCells - 1) / SkyNet.Game.Terrain.ChunkCells;

    // The slab test on one axis of the field [0, cells].
    private static bool Clip(float p, float d, int cells, ref float t0, ref float t1)
    {
        if (MathF.Abs(d) < Epsilon)
        {
            return p >= 0f && p <= cells;
        }

        var (a, b) = ((0f - p) / d, (cells - p) / d);
        (t0, t1) = (MathF.Max(t0, MathF.Min(a, b)), MathF.Min(t1, MathF.Max(a, b)));
        return t0 <= t1;
    }

    // The nearer hit of the cell's two triangles, in ray units.
    private static float? HitCell(ITerrainModel model, int col, int row, Vector3 origin, Vector3 direction)
    {
        var nw = World(col, row, model.Height(col, row));
        var ne = World(col + 1, row, model.Height(col + 1, row));
        var se = World(col + 1, row + 1, model.Height(col + 1, row + 1));
        var sw = World(col, row + 1, model.Height(col, row + 1));
        var (a, b) = (Triangle(origin, direction, nw, ne, se), Triangle(origin, direction, nw, se, sw));
        return a is { } ta && (b is not { } tb || ta <= tb) ? ta : b;
    }

    // Möller–Trumbore, both faces.
    private static float? Triangle(Vector3 origin, Vector3 direction, Vector3 a, Vector3 b, Vector3 c)
    {
        var (e1, e2) = (b - a, c - a);
        var p = Vector3.Cross(direction, e2);
        var det = Vector3.Dot(e1, p);
        if (MathF.Abs(det) < Epsilon)
        {
            return null;
        }

        var s = origin - a;
        var bu = Vector3.Dot(s, p) / det;
        if (bu < 0f || bu > 1f)
        {
            return null;
        }

        var q = Vector3.Cross(s, e1);
        var bv = Vector3.Dot(direction, q) / det;
        if (bv < 0f || bu + bv > 1f)
        {
            return null;
        }

        var t = Vector3.Dot(e2, q) / det;
        return t >= 0f ? t : null;
    }
}
