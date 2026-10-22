using RetroEng.Editor;
using SkyNet.Formats;
using SkyNet.Game.Mods;

namespace SkyNet.Edit;

/// <summary>A cell tile's turn (WLD layer 2 bits 6 and 7).</summary>
public enum TileTurn : byte
{
    None = 0,
    Quarter = 0x40,
    Half = 0x80,
    ThreeQuarters = 0xC0,
}

/// <summary>A rectangle of the grid in corners (or cells): columns <see cref="Col"/>.. and rows <see cref="Row"/>..</summary>
public readonly record struct GridRect(int Col, int Row, int Width, int Height)
{
    public bool IsEmpty => Width <= 0 || Height <= 0;
    public int EndCol => Col + Width;
    public int EndRow => Row + Height;

    /// <summary>The smallest rectangle holding both (an empty one adds nothing).</summary>
    public GridRect Union(GridRect o)
    {
        if (IsEmpty)
        {
            return o;
        }

        if (o.IsEmpty)
        {
            return this;
        }

        var (c, r) = (Math.Min(Col, o.Col), Math.Min(Row, o.Row));
        return new GridRect(c, r, Math.Max(EndCol, o.EndCol) - c, Math.Max(EndRow, o.EndRow) - r);
    }

    /// <summary>This rectangle grown by <paramref name="n"/> on every side and cut to <paramref name="w"/> × <paramref name="h"/>.</summary>
    public GridRect Grow(int n, int w, int h)
    {
        if (IsEmpty)
        {
            return this;
        }

        var (c0, r0) = (Math.Max(Col - n, 0), Math.Max(Row - n, 0));
        var (c1, r1) = (Math.Min(EndCol + n, w), Math.Min(EndRow + n, h));
        return new GridRect(c0, r0, c1 - c0, r1 - r0);
    }
}

/// <summary>
/// The terrain of one open outdoor map for the editor's Landscape tools (editor-tools plan, T7): its WLD
/// (<see cref="WldFile"/>, any size) with heights, tiles and their turns, every change a step of
/// <see cref="History"/> (the document's), and what changed since the views last looked
/// (<see cref="TakeDirty"/>). Saved into a mod as <c>maps/WLD.NNN</c> (<see cref="MapTerrains"/>),
/// which the game then stands the map on (drawn ground and collision).
/// <code>
///   brush dab ─► TerrainCommand (byte diffs, merged per stroke) ─► History ─► Changed / dirty rect
///                                                                       └─► views: TakeDirty → rebuild chunks
///   Save ─► mods/&lt;mod&gt;/maps/WLD.NNN ─► game: GameData.LoadWld, bake input WLD.NNN
/// </code>
/// Coordinates: corner (col, row), row 0 north; world x = col · <see cref="CellSize"/>, DOS z =
/// (<see cref="Height"/> − row) · <see cref="CellSize"/> (render z = −DOS z). Heights are world units, Y up.
/// </summary>
public sealed class TerrainEdit
{
    public const double CellSize = WldTerrain.WorldPerCell;
    private const int HeightMask = 0x7F;
    private const int FlagMask = 0x80;
    private const int TileMask = 0x3F;
    private const int TurnMask = 0xC0;

    /// <summary>Tiles a cell can take (TEXTURE.302 records 0-63).</summary>
    public const int TileCount = TileMask + 1;

    private readonly WldFile _file;
    private byte[] _saved;
    private GridRect _dirty;
    private WldTerrain? _shown;
    private int _version;
    private (int Version, bool Value) _changed;

    public TerrainEdit(int map, WldFile file, UndoStack? history = null)
    {
        Map = map;
        _file = file;
        History = history ?? new UndoStack();
        _saved = file.Write();
        Brushes = new TerrainBrushes(this);
    }

    /// <summary>MAP.<paramref name="map"/>'s terrain as the game would load it (a mounted mod's, else the
    /// data folder's); null when there is none or it does not read.</summary>
    public static TerrainEdit? Open(GameData data, int map, UndoStack? history = null) =>
        data.WldBytes(map) is { } b && WldFile.Read(b) is { } f ? new TerrainEdit(map, f, history) : null;

    /// <summary>A flat terrain of <paramref name="width"/> × <paramref name="height"/> corners for a new map.</summary>
    public static TerrainEdit Create(int map, int width = WldTerrain.GridW, int height = WldTerrain.GridH, UndoStack? history = null) =>
        new(map, WldFile.Create(width, height), history);

    /// <summary>The map number (the file is WLD.NNN).</summary>
    public int Map { get; }

    /// <summary>The undo stack every change goes on.</summary>
    public UndoStack History { get; }

    /// <summary>The sculpt and paint brushes over this terrain.</summary>
    public TerrainBrushes Brushes { get; }

    /// <summary>Corners per row.</summary>
    public int Width => _file.Width;

    /// <summary>Rows of corners.</summary>
    public int Height => _file.Height;

    /// <summary>The highest height a corner can take (the curve's last entry).</summary>
    public static double MaxHeight => WldTerrain.HeightCurve[HeightMask];

    /// <summary>Raised after every change, undo and redo with the corners / cells it touched.</summary>
    public event Action<GridRect>? Changed;

    /// <summary>True when the terrain differs from what was loaded or last saved.</summary>
    public bool IsChanged
    {
        get
        {
            if (_changed.Version != _version)
            {
                _changed = (_version, !_file.Write().AsSpan().SequenceEqual(_saved));
            }

            return _changed.Value;
        }
    }

    /// <summary>The height curve index (0-127) at corner (col, row).</summary>
    public int HeightIndex(int col, int row) => _file.Get(WldLayer.Height, col, row) & HeightMask;

    /// <summary>The height at corner (col, row), world units.</summary>
    public double CornerHeight(int col, int row) => WldTerrain.HeightCurve[HeightIndex(col, row)];

    /// <summary>The tile (0-63) of cell (col, row) as stored (the game smooths tile edges when it loads).</summary>
    public int Tile(int col, int row) => _file.Get(WldLayer.Tile, col, row) & TileMask;

    /// <summary>The turn of cell (col, row)'s tile.</summary>
    public TileTurn Turn(int col, int row) => (TileTurn)(_file.Get(WldLayer.Tile, col, row) & TurnMask);

    /// <summary>The raw byte of any layer at (col, row).</summary>
    public byte Raw(WldLayer layer, int col, int row) => _file.Get(layer, col, row);

    /// <summary>The corner nearest world (x, DOS z), clamped into the grid.</summary>
    public (int Col, int Row) CornerAt(double x, double z) =>
        (Math.Clamp((int)Math.Round(x / CellSize), 0, Width - 1), Math.Clamp((int)Math.Round(Height - z / CellSize), 0, Height - 1));

    /// <summary>The cell under world (x, DOS z), clamped into the grid.</summary>
    public (int Col, int Row) CellAt(double x, double z) =>
        (Math.Clamp((int)Math.Floor(x / CellSize), 0, Width - 1), Math.Clamp((int)Math.Floor(Height - z / CellSize), 0, Height - 1));

    /// <summary>World (x, DOS z) of corner (col, row).</summary>
    public (double X, double Z) WorldOf(double col, double row) => (col * CellSize, (Height - row) * CellSize);

    /// <summary>The ground height at world (x, DOS z), split NW–SE as the game draws it.</summary>
    public double HeightAt(double x, double z)
    {
        var (fx, fz) = (x / CellSize, Height - z / CellSize);
        var (c, r) = (Math.Clamp((int)Math.Floor(fx), 0, Width - 1), Math.Clamp((int)Math.Floor(fz), 0, Height - 1));
        var (u, v) = (Math.Clamp(fx - c, 0, 1), Math.Clamp(fz - r, 0, 1));
        var (c1, r1) = (Math.Min(c + 1, Width - 1), Math.Min(r + 1, Height - 1));
        double nw = CornerHeight(c, r), ne = CornerHeight(c1, r), sw = CornerHeight(c, r1), se = CornerHeight(c1, r1);
        return u >= v ? nw + u * (ne - nw) + v * (se - ne) : nw + v * (sw - nw) + u * (se - sw);
    }

    /// <summary>The curve index nearest <paramref name="height"/>; <paramref name="keep"/> wins a tie of equal heights
    /// (the curve repeats values), so an unchanged height keeps its byte.</summary>
    public static int IndexFor(double height, int keep = -1)
    {
        var curve = WldTerrain.HeightCurve;
        var h = Math.Clamp(height, 0, MaxHeight);
        var best = 0;
        for (var i = 1; i < curve.Length; i++)
        {
            if (Math.Abs(curve[i] - h) < Math.Abs(curve[best] - h))
            {
                best = i;
            }
        }

        return keep >= 0 && keep < curve.Length && curve[keep] == curve[best] ? keep : best;
    }

    /// <summary>The terrain as the game parses it (tile smoothing included), for drawing and collision; null for a
    /// size the game does not take (not 256 × 256). Cached until the next change.</summary>
    public WldTerrain? ToWld() => _shown ??= WldTerrain.Parse(_file.Write());

    /// <summary>The WLD file's bytes (unedited: the file as read, byte for byte).</summary>
    public byte[] ToBytes() => _file.Write();

    /// <summary>
    /// What changed since the last call, in cells, grown by one for the corner normals: rebuild the ground chunks
    /// and collision it touches (<see cref="ChunksOf"/>). Empty when nothing did.
    /// </summary>
    public GridRect TakeDirty()
    {
        var d = _dirty.Grow(1, Width, Height);
        _dirty = default;
        return d;
    }

    /// <summary>The chunks (of <paramref name="chunkCells"/> cells a side, e.g. Terrain.ChunkCells) <paramref name="cells"/> touches.</summary>
    public static IEnumerable<(int ChunkX, int ChunkY)> ChunksOf(GridRect cells, int chunkCells)
    {
        if (cells.IsEmpty)
        {
            yield break;
        }

        for (var cy = cells.Row / chunkCells; cy <= (cells.EndRow - 1) / chunkCells; cy++)
        {
            for (var cx = cells.Col / chunkCells; cx <= (cells.EndCol - 1) / chunkCells; cx++)
            {
                yield return (cx, cy);
            }
        }
    }

    /// <summary>Sets every corner height from <paramref name="heights"/> (row-major, <see cref="Width"/> ×
    /// <see cref="Height"/>, world units) as one undo step; null entries stay.</summary>
    public void SetHeights(IReadOnlyList<double?> heights, string label) => Apply(heights, null, label);

    /// <summary>Sets every cell's tile from <paramref name="tiles"/> (row-major, <see cref="Width"/> ×
    /// <see cref="Height"/>; the turn kept) as one undo step; null entries stay.</summary>
    public void SetTiles(IReadOnlyList<int?> tiles, string label) => Apply(null, tiles, label);

    /// <summary>Heights and tiles (either may be null) as one undo step.</summary>
    internal void Apply(IReadOnlyList<double?>? heights, IReadOnlyList<int?>? tiles, string label)
    {
        var size = Width * Height;
        if ((heights != null && heights.Count != size) || (tiles != null && tiles.Count != size))
        {
            throw new ArgumentException($"{heights?.Count ?? tiles?.Count} values for {Width} x {Height} corners");
        }

        var diff = new TerrainDiff(Width);
        for (var row = 0; row < Height; row++)
        {
            for (var col = 0; col < Width; col++)
            {
                var i = row * Width + col;
                if (heights?[i] is { } h)
                {
                    diff.Add(WldLayer.Height, col, row, Raw(WldLayer.Height, col, row), HeightByte(col, row, h));
                }

                if (tiles?[i] is { } t)
                {
                    diff.Add(WldLayer.Tile, col, row, Raw(WldLayer.Tile, col, row), TileByte(t, Turn(col, row)));
                }
            }
        }

        Execute(diff, label, merge: null);
    }

    /// <summary>Sets cell (col, row)'s tile and turn as one undo step.</summary>
    public void SetTile(int col, int row, int tile, TileTurn turn = TileTurn.None)
    {
        var diff = new TerrainDiff(Width);
        diff.Add(WldLayer.Tile, col, row, Raw(WldLayer.Tile, col, row), TileByte(tile, turn));
        Execute(diff, $"Tile {tile}", merge: null);
    }

    /// <summary>Turns cell (col, row)'s tile a quarter clockwise (the format has no per-cell diagonal: every cell
    /// splits NW–SE; a tile's turn is what a cell can flip).</summary>
    public void TurnTile(int col, int row)
    {
        var next = (TileTurn)((((int)Turn(col, row) >> 6) + 1 & 3) << 6);
        var diff = new TerrainDiff(Width);
        diff.Add(WldLayer.Tile, col, row, Raw(WldLayer.Tile, col, row), TileByte(Tile(col, row), next));
        Execute(diff, "Turn tile", merge: null);
    }

    /// <summary>Writes the terrain into <paramref name="modFolder"/> as <c>maps/WLD.NNN</c> (replacing it at
    /// once) and makes this the saved state; the path written.</summary>
    public string Save(string modFolder)
    {
        var path = WriteCopy(modFolder);
        _saved = _file.Write();
        _version++;
        return path;
    }

    /// <summary>Writes the terrain into <paramref name="modFolder"/> (a play-test's scratch mod) without
    /// moving the saved state; the path written.</summary>
    public string WriteCopy(string modFolder)
    {
        var path = Path.Combine([modFolder, .. MapTerrains.PathOf(Map).Split('/')]);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var temp = path + ".tmp";
        File.WriteAllBytes(temp, _file.Write());
        File.Move(temp, path, overwrite: true);
        return path;
    }

    /// <summary>The layer-0 byte for <paramref name="height"/> at a corner: the nearest curve index, the flag bit kept.</summary>
    internal byte HeightByte(int col, int row, double height)
    {
        var old = Raw(WldLayer.Height, col, row);
        return (byte)(old & FlagMask | IndexFor(height, old & HeightMask));
    }

    internal static byte TileByte(int tile, TileTurn turn) => (byte)(Math.Clamp(tile, 0, TileMask) | (int)turn);

    /// <summary>Runs <paramref name="diff"/> as a step; with <paramref name="merge"/> it joins the previous step of
    /// the same key while the merge window is open (one brush stroke = one undo step).</summary>
    internal void Execute(TerrainDiff diff, string label, object? merge)
    {
        if (diff.IsEmpty)
        {
            return;
        }

        History.Execute(new TerrainCommand(this, diff, label, merge));
    }

    private void Put(TerrainDiff diff, bool after)
    {
        foreach (var (layer, index, before, now) in diff.Entries)
        {
            _file.Layer(layer)[index] = after ? now : before;
        }

        _shown = null;
        _version++;
        _dirty = _dirty.Union(diff.Box);
        Changed?.Invoke(diff.Box);
    }

    /// <summary>One undo step: byte changes of the layers; merges the next step of the same stroke.</summary>
    private sealed class TerrainCommand(TerrainEdit terrain, TerrainDiff diff, string label, object? merge) : IUndoCommand
    {
        private readonly TerrainEdit _terrain = terrain;
        private readonly TerrainDiff _diff = diff;
        private readonly object? _merge = merge;

        public string Label { get; } = label;

        public void Do() => _terrain.Put(_diff, after: true);

        public void Undo() => _terrain.Put(_diff, after: false);

        public bool TryMerge(IUndoCommand next)
        {
            if (_merge == null || next is not TerrainCommand c || !ReferenceEquals(c._merge, _merge) || c._terrain != _terrain)
            {
                return false;
            }

            _diff.Absorb(c._diff);
            return true;
        }
    }
}

/// <summary>Byte changes of the WLD layers of a grid <c>width</c> corners wide: per (layer, index) the first
/// before and the latest after, and the box round them.</summary>
internal sealed class TerrainDiff(int width)
{
    private readonly Dictionary<(WldLayer Layer, int Index), (byte Before, byte After)> _bytes = [];
    private GridRect _box;

    public bool IsEmpty => _bytes.Count == 0;

    /// <summary>The corners / cells the diff touches.</summary>
    public GridRect Box => _box;

    public IEnumerable<(WldLayer Layer, int Index, byte Before, byte After)> Entries =>
        _bytes.Select(b => (b.Key.Layer, b.Key.Index, b.Value.Before, b.Value.After));

    /// <summary>Records (col, row) of <paramref name="layer"/> going from <paramref name="before"/> to
    /// <paramref name="after"/>; nothing when they are equal.</summary>
    public void Add(WldLayer layer, int col, int row, byte before, byte after)
    {
        if (before == after)
        {
            return;
        }

        _bytes[(layer, row * width + col)] = (before, after);
        _box = _box.Union(new GridRect(col, row, 1, 1));
    }

    /// <summary>Takes in <paramref name="next"/>, a diff done after this one: its afters win, this one's befores stay.</summary>
    public void Absorb(TerrainDiff next)
    {
        foreach (var (key, (before, after)) in next._bytes)
        {
            _bytes[key] = _bytes.TryGetValue(key, out var mine) ? (mine.Before, after) : (before, after);
        }

        _box = _box.Union(next._box);
    }
}
