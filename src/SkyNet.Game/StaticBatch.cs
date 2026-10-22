using System.Numerics;
using RetroEng.Render;

namespace SkyNet.Game;

/// <summary>
/// Placed model surfaces merged into one world-space mesh, one index range per surface key
/// (texture or flat index): every prop, robot and segment of a level costs one draw per texture
/// rather than one per model and texture. A vertex may carry a shade level (0-63, a row of the
/// shade table) in its colour's alpha, for <see cref="Material.VertexShade"/>. Vertex normals are
/// turned by the placement, always (<see cref="PlaceNormal"/>).
/// </summary>
public sealed class StaticBatch
{
    private sealed class Bucket(GpuTexture? texture)
    {
        public readonly GpuTexture? Texture = texture;
        public readonly List<Vertex> Vertices = [];
        public readonly List<uint> Indices = [];
    }

    private readonly Dictionary<SurfaceKey, Bucket> _buckets = [];

    public int Placed { get; private set; }

    /// <summary>Adds the surfaces placed by <paramref name="world"/>; <paramref name="shade"/> gives
    /// a vertex's shade level from its world position and normal (none: the alpha stays 255).</summary>
    public void Add(IReadOnlyList<ModelSurface> surfaces, Matrix4x4 world, Func<Vector3, Vector3, byte>? shade = null)
    {
        foreach (var s in surfaces)
        {
            if (!_buckets.TryGetValue(s.Key, out var b))
            {
                _buckets[s.Key] = b = new Bucket(s.Texture);
            }

            Place(s, world, shade, b.Vertices, b.Indices);
        }

        Placed++;
    }

    /// <summary>One surface placed by <paramref name="world"/>, appended to <paramref name="vertices"/>
    /// and <paramref name="indices"/> (indices rebased onto the vertices already there).</summary>
    public static void Place(ModelSurface s, Matrix4x4 world, Func<Vector3, Vector3, byte>? shade, List<Vertex> vertices, List<uint> indices)
    {
        var at = (uint)vertices.Count;
        for (var i = 0; i < s.Vertices.Length; i++)
        {
            var v = s.Vertices[i];
            var p = Vector3.Transform(v.Position, world);
            var colour = Vertex.White;
            if (shade != null)
            {
                var n = Vector3.TransformNormal(s.Normals[i], world);
                colour = Vertex.Rgba(255, 255, 255, shade(p, n == Vector3.Zero ? n : Vector3.Normalize(n)));
            }

            vertices.Add(new Vertex(p, v.Uv, colour, PlaceNormal(v.Normal, world)));
        }

        foreach (var index in s.Indices)
        {
            indices.Add(at + index);
        }
    }

    /// <summary>The buckets so far in the order they were opened: key, texture, vertices, indices.</summary>
    public IEnumerable<(SurfaceKey Key, GpuTexture? Texture, List<Vertex> Vertices, List<uint> Indices)> Buckets =>
        _buckets.Select(b => (b.Key, b.Value.Texture, b.Value.Vertices, b.Value.Indices));

    /// <summary>A packed vertex normal turned by a placement (rotation and translation, so its
    /// 3 × 3 part turns normals as it turns positions); none stays none.</summary>
    public static uint PlaceNormal(uint packed, Matrix4x4 world)
    {
        if (packed == 0)
        {
            return 0;
        }

        return Vertex.PackNormal(Vector3.TransformNormal(Vertex.UnpackNormal(packed), world));
    }

    /// <summary>Every vertex added so far, bucket by bucket (tests and tools).</summary>
    public IEnumerable<Vertex> Vertices => _buckets.Values.SelectMany(b => b.Vertices);

    /// <summary>One mesh of every surface added, its parts in key order; null when empty.
    /// <paramref name="vertexShade"/>: the materials shade by the vertex alpha (from row 0);
    /// <paramref name="shading"/>: their enhanced look (lit unless said).</summary>
    public ModelMesh? Build(Renderer renderer, TextureHandle palette, RenderState state, bool vertexShade = false,
        Shading shading = Shading.Lit)
    {
        return Build(renderer, palette, state, _buckets.Select(b => (b.Key, b.Value.Texture, (IReadOnlyList<Vertex>)b.Value.Vertices,
            (IReadOnlyList<uint>)b.Value.Indices)), vertexShade, shading);
    }

    /// <summary>One mesh of <paramref name="buckets"/> (key, texture, vertices, indices), its parts in key
    /// order (the textures' handle index, then the flat index); null when empty.</summary>
    public static ModelMesh? Build(Renderer renderer, TextureHandle palette, RenderState state,
        IEnumerable<(SurfaceKey Key, GpuTexture? Texture, IReadOnlyList<Vertex> Vertices, IReadOnlyList<uint> Indices)> buckets,
        bool vertexShade = false, Shading shading = Shading.Lit) =>
        Build(renderer, palette, state, buckets.Select(b => (b.Key, b.Texture, b.Vertices, b.Indices, (IReadOnlyList<MeshChunk>?)null)),
            vertexShade, shading);

    /// <summary>As above, each bucket with its spatial pieces (index ranges into its own indices, in
    /// order, back to back; none: the part is culled by its box alone).</summary>
    public static ModelMesh? Build(Renderer renderer, TextureHandle palette, RenderState state,
        IEnumerable<(SurfaceKey Key, GpuTexture? Texture, IReadOnlyList<Vertex> Vertices, IReadOnlyList<uint> Indices,
            IReadOnlyList<MeshChunk>? Pieces)> buckets,
        bool vertexShade = false, Shading shading = Shading.Lit)
    {
        var vertices = new List<Vertex>();
        var indices = new List<uint>();
        var parts = new List<ModelMesh.Part>();
        foreach (var b in buckets.OrderBy(k => k.Key.Texture.Index).ThenBy(k => k.Key.FlatIndex))
        {
            var key = b.Key;
            var first = indices.Count;
            var at = (uint)vertices.Count;
            vertices.AddRange(b.Vertices);
            foreach (var i in b.Indices)
            {
                indices.Add(at + i);
            }

            var material = ModelMesh.MaterialFor(key, b.Texture, palette, state, shading);
            if (vertexShade)
            {
                material = material with { Shade = 0, VertexShade = true };
            }

            var chunks = b.Pieces is { Count: > 0 } pieces
                ? pieces.Select(p => p with { First = first + p.First }).ToArray()
                : null;
            parts.Add(new ModelMesh.Part(first, indices.Count - first, material, b.Texture,
                Bounds.Of(b.Indices.Select(i => b.Vertices[(int)i].Position)), chunks));
        }

        if (indices.Count == 0)
        {
            return null;
        }

        return new ModelMesh(renderer.CreateMesh(vertices.ToArray(), indices.ToArray()), parts, indices.Count / 3);
    }
}
