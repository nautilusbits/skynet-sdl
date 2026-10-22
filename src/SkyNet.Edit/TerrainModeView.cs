using System.Numerics;
using System.Runtime.InteropServices;
using RetroEng.Editor;
using RetroEng.Render;
using SkyNet.Game;

namespace SkyNet.Edit;

/// <summary>
/// The edited terrain in the viewport. Until the first change the level's own ground draws; from then
/// on the level's ground is hidden and this draws the model's field as one dynamic mesh per chunk,
/// rebuilt only where <see cref="ITerrainModel.TakeDirty"/> says. Same atlas and material as the
/// level's ground, so ORIGINAL and ENHANCED look as the game draws them. Also the brush ring.
/// The meshes are made after the level's scope mark: showing another level releases them.
/// </summary>
internal sealed class TerrainModeView
{
    private const int RingSegments = 64;
    // Above the ground so the ring is not lost in it.
    private const float RingLift = 6f;
    private static readonly Vector4 OuterColour = new(1f, 0.85f, 0.2f, 1f);
    private static readonly Vector4 InnerColour = new(1f, 1f, 1f, 0.6f);

    private readonly Renderer _renderer;
    private readonly Level _level;
    private readonly ITerrainModel _model;
    private readonly Terrain.AtlasLayout _layout;
    private readonly MeshHandle[] _chunks;
    private readonly Bounds[] _bounds;
    private readonly int[] _counts;
    private readonly HashSet<int> _dirty = [];
    private readonly MeshHandle _ring;
    private readonly List<Vertex> _ringVertices = [];
    private int _outerCount;

    public TerrainModeView(Renderer renderer, Level level, ITerrainModel model)
    {
        (_renderer, _level, _model) = (renderer, level, model);
        _layout = Terrain.Layout(level.Textures.Bank(Terrain.TileBank));
        var across = BrushMath.ChunksAcross(model.Corners);
        _chunks = new MeshHandle[across * across];
        _bounds = new Bounds[_chunks.Length];
        _counts = new int[_chunks.Length];
        _ring = renderer.CreateDynamicMesh(RingSegments * 4);
    }

    public Level Level => _level;

    public ITerrainModel Model => _model;

    /// <summary>True once the field differs from the level's baked ground (it then draws instead).</summary>
    public bool Active { get; private set; }

    /// <summary>Chunks rebuilt by the last <see cref="Update"/> (the status bar shows it).</summary>
    public int Rebuilt { get; private set; }

    /// <summary>The cells the last <see cref="Update"/> took from the model (the collision field follows them).</summary>
    public IReadOnlyList<GridRect> Changed { get; private set; } = [];

    /// <summary>The field drawn from the model from now on, the level's ground off (a model edited
    /// before this level was built: its baked ground is stale).</summary>
    public void Activate()
    {
        if (Active)
        {
            return;
        }

        Active = true;
        _level.HideGround = true;
        _dirty.UnionWith(Enumerable.Range(0, _chunks.Length));
    }

    /// <summary>The model's changes into the chunk meshes: only the chunks they touch.</summary>
    public void Update()
    {
        Changed = _model.TakeDirty();
        foreach (var rect in Changed)
        {
            Activate();
            _dirty.UnionWith(BrushMath.Chunks(rect, _model.Corners));
        }

        Rebuilt = _dirty.Count;
        foreach (var chunk in _dirty)
        {
            Build(chunk);
        }

        _dirty.Clear();
    }

    public void Draw()
    {
        if (!Active || _level.Ground is not { } ground)
        {
            return;
        }

        for (var i = 0; i < _chunks.Length; i++)
        {
            if (_counts[i] > 0)
            {
                _renderer.Draw(_chunks[i], 0, _counts[i], ground.Material, 0, Matrix4x4.Identity, _bounds[i]);
            }
        }
    }

    /// <summary>The brush ring at <paramref name="hit"/>: the radius, and where the falloff's weight is half.</summary>
    public void DrawRing(Vector3 hit, float radius, BrushFalloff falloff)
    {
        _ringVertices.Clear();
        Circle(hit, radius);
        _outerCount = _ringVertices.Count;
        Circle(hit, BrushMath.HalfWeight(radius, falloff));
        _ringVertices.Add(new Vertex(hit, Vector2.Zero, 0xFFFFFFFFu));
        _ringVertices.Add(new Vertex(hit + new Vector3(0f, radius * 0.25f, 0f), Vector2.Zero, 0xFFFFFFFFu));
        _renderer.UpdateMesh(_ring, CollectionsMarshal.AsSpan(_ringVertices));
        _renderer.DrawLines(_ring, 0, _outerCount, Material.Flat(OuterColour, SelectionOutline.State) with { Unfogged = true });
        _renderer.DrawLines(_ring, _outerCount, _ringVertices.Count - _outerCount, Material.Flat(InnerColour, SelectionOutline.State) with { Unfogged = true });
    }

    // A circle draped on the ground, as line pairs.
    private void Circle(Vector3 centre, float radius)
    {
        if (radius <= 0f)
        {
            return;
        }

        Vector3 At(int i)
        {
            var a = i * MathF.Tau / RingSegments;
            var (x, z) = (centre.X + MathF.Cos(a) * radius, centre.Z + MathF.Sin(a) * radius);
            return new Vector3(x, BrushMath.HeightAt(_model, x, z) + RingLift, z);
        }

        var previous = At(0);
        for (var i = 1; i <= RingSegments; i++)
        {
            var next = At(i);
            _ringVertices.Add(new Vertex(previous, Vector2.Zero, 0xFFFFFFFFu));
            _ringVertices.Add(new Vertex(next, Vector2.Zero, 0xFFFFFFFFu));
            previous = next;
        }
    }

    // One chunk's mesh from the model, as the game's Terrain builds its ground.
    private void Build(int chunk)
    {
        var size = Terrain.ChunkCells;
        var across = BrushMath.ChunksAcross(_model.Corners);
        var (col, row) = (chunk % across * size, chunk / across * size);
        var geometry = Terrain.Geometry((c, r) => _model.Height(c, r), _model.Tile, _layout.Sizes, _layout.Slot, _layout.Side, (col, row, size, size));
        var indices = geometry.Indices;
        if (_chunks[chunk].IsNone)
        {
            _chunks[chunk] = _renderer.CreateDynamicMesh(size * size * 4, size * size * 6);
        }

        _counts[chunk] = indices.Length;
        if (geometry.Chunks is [var only])
        {
            _bounds[chunk] = new Bounds(only.Min, only.Max);
        }

        if (indices.Length > 0)
        {
            _renderer.UpdateMesh(_chunks[chunk], geometry.Vertices, indices);
        }
    }
}
