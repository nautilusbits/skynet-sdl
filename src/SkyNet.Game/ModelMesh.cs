using System.Numerics;
using RetroEng.Render;
using SkyNet.Formats;

namespace SkyNet.Game;

/// <summary>What a model surface is drawn with: an index texture, or (no texture) a flat palette index.</summary>
public readonly record struct SurfaceKey(TextureHandle Texture, int FlatIndex);

/// <summary>One texture's faces of a .3D frame: local Y-up positions, texel coordinates and the
/// GPU normal (<see cref="Vertex.Normal"/>, from <see cref="ModelMesh.CornerNormals"/>), the
/// winding normal per vertex (<see cref="Normals"/>: the indoor vertex shade and the emissive lamps
/// read it) and triangle indices into its own vertices.</summary>
public sealed record ModelSurface(SurfaceKey Key, GpuTexture? Texture, Vertex[] Vertices, Vector3[] Normals, uint[] Indices);

/// <summary>
/// A .3D model as one mesh, its faces grouped by texture word into index ranges, each with its
/// material: an index texture from TEXTURE.&lt;bank&gt; or, for the "solid colour" words (≤ 0xFF,
/// banks 0/1), a flat palette index. Positions are Y-up world units
/// (<see cref="ModelVertex.ToYUp"/>), fans kept in stored order: counter-clockwise from the
/// visible side, back faces culled, as DOS does. An animated record's part names its
/// <see cref="Part.Texture"/>, whose frame the draw picks.
/// Vertex normals (for the enhanced look; the original look ignores them): props, buildings and
/// cockpits are faceted, each face its outward normal as DOS lit it; robots and bolts are smoothed
/// across faces meeting within <see cref="SmoothAngleDeg"/>. Sprites and billboards carry none
/// (0): they turn to the camera every frame, so a lit billboard takes its normal in the shader.
/// </summary>
public sealed class ModelMesh
{
    /// <param name="Bounds">The part's vertices' box (the mesh's space): a view that cannot see it skips the draw.</param>
    /// <param name="Chunks">The part's spatial pieces (a level's world mesh: one per map cell), culled one by one.</param>
    public readonly record struct Part(int First, int Count, Material Material, GpuTexture? Texture, Bounds? Bounds = null,
        MeshChunk[]? Chunks = null);

    public MeshHandle Mesh { get; }
    /// <summary>An array (not a list behind an interface): a draw walks it every frame without boxing an enumerator. Do not write to it.</summary>
    public Part[] Parts { get; }
    public int Triangles { get; }

    internal ModelMesh(MeshHandle mesh, List<Part> parts, int triangles)
    {
        Mesh = mesh;
        Parts = [.. parts];
        Triangles = triangles;
    }

    /// <summary>Queues a part placed by <paramref name="world"/>, culled per view by its pieces or its box.</summary>
    public void Draw(Renderer renderer, in Part part, int frame, Matrix4x4 world)
    {
        if (part.Chunks is { Length: > 0 } chunks)
        {
            renderer.Draw(Mesh, chunks, part.Material, frame, world);
        }
        else if (part.Bounds is { } bounds)
        {
            renderer.Draw(Mesh, part.First, part.Count, part.Material, frame, world, bounds);
        }
        else
        {
            renderer.Draw(Mesh, part.First, part.Count, part.Material, frame, world);
        }
    }

    /// <summary>A frame's faces grouped by texture word (ascending), one surface each.</summary>
    /// <param name="smooth">Smooth the vertex normals across soft edges (robots, bolts); otherwise faceted.</param>
    /// <param name="skin">Faces textured from bank From take the same record of bank To instead (a
    /// deathmatch avatar's skin: SKYNET.EXE's avatar table +0 names the bank).</param>
    public static List<ModelSurface> Surfaces(Model3D model, TextureStore textures, int frame = 0, bool smooth = false,
        (int From, int To)? skin = null)
    {
        var verts = frame > 0 && frame < model.Frames.Length ? model.Frames[frame] : model.Vertices;
        var corners = CornerNormals(
            [.. model.Faces.Select(f => f.Indices.Select(i => verts[i].ToYUp()).ToArray())],
            [.. model.Faces.Select(f => f.StoredNormal is { } n ? n.ToYUp() : (Vector3?)null)],
            smooth ? SmoothAngleDeg : 0f);
        var cornersOf = new Dictionary<ModelFace, Vector3[]>(ReferenceEqualityComparer.Instance);
        for (var i = 0; i < model.Faces.Length; i++)
        {
            cornersOf[model.Faces[i]] = corners[i];
        }

        var surfaces = new List<ModelSurface>();
        foreach (var group in model.Faces.GroupBy(f => f.Texture).OrderBy(g => g.Key))
        {
            var bank = group.Key >> 7;
            var record = group.Key & 0x7F;
            if (skin is { } remap && bank == remap.From && group.Key > 0xFF)
            {
                bank = remap.To;
            }

            GpuTexture? texture = null;
            SurfaceKey key;
            if (group.Key <= 0xFF)
            {
                // Banks 0/1: a 1 × 1 record holding the palette index.
                var flat = textures.Record(bank, record);
                key = new SurfaceKey(default, flat?.Pixels[0] ?? HazeTable.HazeIndex);
            }
            else if ((texture = textures.Get(bank, record, opaque: true)) is { } t)
            {
                key = new SurfaceKey(t.Handle, -1);
            }
            else
            {
                key = new SurfaceKey(default, HazeTable.HazeIndex);
            }

            var vertices = new List<Vertex>();
            var normals = new List<Vector3>();
            var indices = new List<uint>();
            foreach (var face in group)
            {
                var uvs = model.FaceUvs(face, texture?.Width ?? 64, texture?.Height ?? 64, frame);
                var normal = model.FaceNormal(face, frame);
                var gpu = cornersOf[face];
                var at = (uint)vertices.Count;
                for (var k = 0; k < face.VertexCount; k++)
                {
                    vertices.Add(new Vertex(verts[face.Indices[k]].ToYUp(), uvs[k], Vertex.White, Vertex.PackNormal(gpu[k])));
                    normals.Add(normal);
                }

                foreach (var (a, b, c) in Model3D.TriangleFan(face))
                {
                    indices.AddRange([at + (uint)a, at + (uint)b, at + (uint)c]);
                }
            }

            if (indices.Count > 0)
            {
                surfaces.Add(new ModelSurface(key, texture, [.. vertices], [.. normals], [.. indices]));
            }
        }

        return surfaces;
    }

    /// <summary>The smoothing-group angle of robot and bolt normals (the Godot port's ENHANCED
    /// SMOOTH_ANGLE_DEG): faces meeting at less than this share a normal.</summary>
    public const float SmoothAngleDeg = 55f;

    /// <summary>Corners closer than 1/this unit share a position when smoothing (the Godot port's key).</summary>
    private const float WeldScale = 4f;

    /// <summary>
    /// A face's outward normal: its stored normal when that agrees with the winding (the visible,
    /// counter-clockwise side; nearly every face), otherwise the winding's own (Newell's normal over
    /// the whole polygon). Zero only when the face is degenerate and stores nothing usable.
    /// </summary>
    public static Vector3 OutwardNormal(ReadOnlySpan<Vector3> positions, Vector3? stored)
    {
        var geometric = Vector3.Zero;
        for (var k = 0; k < positions.Length; k++)
        {
            var (a, b) = (positions[k], positions[(k + 1) % positions.Length]);
            geometric += new Vector3((a.Y - b.Y) * (a.Z + b.Z), (a.Z - b.Z) * (a.X + b.X), (a.X - b.X) * (a.Y + b.Y));
        }

        geometric = geometric.LengthSquared() > 0f ? Vector3.Normalize(geometric) : Vector3.Zero;
        if (stored is { } s && s.LengthSquared() is > 0f and < float.PositiveInfinity)
        {
            s = Vector3.Normalize(s);
            if (geometric == Vector3.Zero || Vector3.Dot(s, geometric) > 0f)
            {
                return s;
            }
        }

        return geometric;
    }

    /// <summary>
    /// Every face corner's normal: the face's <see cref="OutwardNormal"/> (faceted) when
    /// <paramref name="smoothAngleDeg"/> ≤ 0; otherwise the mean of the outward normals of the faces
    /// sharing that corner's position whose normal lies within the angle of this face's (the Godot
    /// port's smooth_normals, each face counted once), so soft edges blend and hard edges stay hard.
    /// </summary>
    public static Vector3[][] CornerNormals(IReadOnlyList<Vector3[]> faces, IReadOnlyList<Vector3?> stored, float smoothAngleDeg)
    {
        var faceNormals = new Vector3[faces.Count];
        for (var f = 0; f < faces.Count; f++)
        {
            faceNormals[f] = OutwardNormal(faces[f], f < stored.Count ? stored[f] : null);
        }

        var result = new Vector3[faces.Count][];
        if (smoothAngleDeg <= 0f)
        {
            for (var f = 0; f < faces.Count; f++)
            {
                result[f] = Enumerable.Repeat(faceNormals[f], faces[f].Length).ToArray();
            }

            return result;
        }

        static (int, int, int) Key(Vector3 p) =>
            ((int)MathF.Round(p.X * WeldScale), (int)MathF.Round(p.Y * WeldScale), (int)MathF.Round(p.Z * WeldScale));

        var byCorner = new Dictionary<(int, int, int), List<int>>();
        for (var f = 0; f < faces.Count; f++)
        {
            foreach (var p in faces[f])
            {
                if (!byCorner.TryGetValue(Key(p), out var list))
                {
                    byCorner[Key(p)] = list = [];
                }

                if (list.Count == 0 || list[^1] != f)
                {
                    list.Add(f);
                }
            }
        }

        var cosLimit = MathF.Cos(smoothAngleDeg * MathF.PI / 180f);
        for (var f = 0; f < faces.Count; f++)
        {
            var n = faceNormals[f];
            result[f] = new Vector3[faces[f].Length];
            for (var k = 0; k < faces[f].Length; k++)
            {
                var sum = Vector3.Zero;
                foreach (var g in byCorner[Key(faces[f][k])])
                {
                    if (Vector3.Dot(n, faceNormals[g]) >= cosLimit)
                    {
                        sum += faceNormals[g];
                    }
                }

                result[f][k] = sum.LengthSquared() > 1e-12f ? Vector3.Normalize(sum) : n;
            }
        }

        return result;
    }

    /// <summary>The material a surface key draws with; <paramref name="shading"/> is its enhanced
    /// look (the original look ignores it).</summary>
    public static Material MaterialFor(SurfaceKey key, GpuTexture? texture, TextureHandle palette, RenderState state,
        Shading shading = Shading.Lit) =>
        key.Texture.IsNone
            ? Material.FlatIndex(key.FlatIndex, palette, state) with { Shading = shading }
            : new Material(key.Texture, palette, Vector4.One, texture?.Frames ?? 1, state, shading);

    /// <summary>Frame 0 of a model as its own mesh; null when it has no faces to draw.</summary>
    public static ModelMesh? Build(Renderer renderer, Model3D model, TextureStore textures, TextureHandle palette) =>
        Build(renderer, model, textures, palette, RenderState.Solid);

    /// <param name="smooth">Smoothed vertex normals (a bolt), else faceted (see <see cref="Surfaces"/>).</param>
    /// <param name="shading">The enhanced look's shading (the dusk dome: unlit).</param>
    public static ModelMesh? Build(Renderer renderer, Model3D model, TextureStore textures, TextureHandle palette, RenderState state,
        bool smooth = false, Shading shading = Shading.Lit)
    {
        var batch = new StaticBatch();
        batch.Add(Surfaces(model, textures, smooth: smooth), Matrix4x4.Identity);
        return batch.Build(renderer, palette, state, shading: shading);
    }
}
