using System.Numerics;
using RetroEng.Models;
using RetroEng.Render;
using SkyNet.Formats;

namespace SkyNet.Game.Mods;

/// <summary>Builds a replacement model's GPU mesh (shared by the level's props, the robots, the
/// deathmatch avatars and the cockpits).</summary>
public static class ReplacementMesh
{
    /// <summary>The material a replacement surface draws with on its own (a skinned robot's part):
    /// the colour texture behind an opaque index texture standing for it (as <see cref="Build"/>
    /// makes them), lit, drawn as the surface's alpha mode says.</summary>
    /// <param name="kept">The surfaces kept across maps (<see cref="KeptSurfaces"/>); null: made in the current scope.</param>
    public static Material Material(Renderer renderer, TextureHandle palette, ReplacementSurface surface, KeptSurfaces? kept = null)
    {
        var layers = surface.Maps ? Renderer.SurfaceMapLayers : 1;
        var handle = Texture(renderer, kept, surface);
        var key = new SurfaceKey(handle, -1);
        return surface.Apply(ModelMesh.MaterialFor(key, new GpuTexture(handle, IndexSide, IndexSide, layers), kept?.Palette ?? palette, RenderState.Solid));
    }

    /// <summary>The index texture standing for the surface's colour texture: kept across maps
    /// (<see cref="KeptSurfaces"/>), else made in the current scope.</summary>
    private static TextureHandle Texture(Renderer renderer, KeptSurfaces? kept, ReplacementSurface surface,
        Dictionary<ReplacementSurface, TextureHandle>? made = null)
    {
        if (kept?.Texture(surface) is { } texture)
        {
            return texture;
        }

        // Made in this scope already (an avatar's 41 frames share one atlas).
        if (made != null && made.TryGetValue(surface, out var shared))
        {
            return shared;
        }

        var opaque = new byte[IndexSide * IndexSide];
        Array.Fill(opaque, (byte)1);
        var handle = renderer.CreateIndexTexture(IndexSide, IndexSide, opaque);
        // Released: a texture of its own, drawn through one palette at one layer count.
        var colour = surface.Colour;
        renderer.ReplaceTexture(handle, colour.Width, colour.Height, colour.Rgba, null, HdPixels.Released);
        if (made != null)
        {
            made[surface] = handle;
        }

        return handle;
    }

    /// <summary>The index texture standing for a colour texture: 4 x 4.</summary>
    private const int IndexSide = 4;

    /// <summary>A replacement's glTF model as a mesh in the DOS model's space (the sidecar's
    /// placement), a part per material: each an opaque index texture standing for its colour
    /// texture (<see cref="Renderer.ReplaceTexture"/>), lit in the enhanced look, drawn as its alpha
    /// mode says (<see cref="ReplacementSurface.State"/>). A material with maps
    /// (<see cref="Material.SurfaceMaps"/>) carries each vertex's tangent in its colour: the file's,
    /// else made (<see cref="TangentSpace"/>).</summary>
    /// <param name="placement">The glTF model's space to the DOS model's (<see cref="ModelSidecar.Placement"/>).</param>
    /// <param name="kept">The surfaces kept across maps (<see cref="KeptSurfaces"/>); null: made in the current scope.</param>
    /// <param name="made">Without <paramref name="kept"/>: the index textures made in the current scope so
    /// far, by surface, shared and added to; null: one made per call.</param>
    public static ModelMesh? Build(Renderer renderer, TextureHandle palette, ModelReplacement r, Matrix4x4 placement, KeptSurfaces? kept = null,
        Dictionary<ReplacementSurface, TextureHandle>? made = null)
    {
        Matrix4x4.Invert(placement, out var inverse);
        var turnNormal = Matrix4x4.Transpose(inverse);
        var mirrored = placement.GetDeterminant() < 0f;
        var buckets = new List<(SurfaceKey, GpuTexture?, IReadOnlyList<Vertex>, IReadOnlyList<uint>)>();
        var surfaces = new Dictionary<int, ReplacementSurface>();
        for (var m = 0; m < r.Surfaces.Count; m++)
        {
            var surface = r.Surfaces[m];
            var vertices = new List<Vertex>();
            var indices = new List<uint>();
            foreach (var p in r.Model.Primitives.Where(p => r.SurfaceOf(p) == m))
            {
                var at = (uint)vertices.Count;
                var tangents = surface.Maps && p.Uvs != null ? p.Tangents ?? TangentSpace.Generate(p.Positions, p.Normals, p.Uvs, p.Indices) : null;
                for (var i = 0; i < p.Positions.Length; i++)
                {
                    var n = p.Normals?[i] is { } normal ? Vector3.TransformNormal(normal, turnNormal) : Vector3.Zero;
                    var colour = tangents != null ? ReplacementSurface.PackTangent(tangents[i], placement, mirrored) : Vertex.White;
                    vertices.Add(new Vertex(Vector3.Transform(p.Positions[i], placement), p.Uvs?[i] ?? Vector2.Zero, colour, Vertex.PackNormal(n)));
                }

                for (var i = 0; i + 2 < p.Indices.Length; i += 3)
                {
                    var (a, b, c) = (p.Indices[i], p.Indices[i + 1], p.Indices[i + 2]);
                    indices.AddRange(mirrored ? [at + a, at + c, at + b] : [at + a, at + b, at + c]);
                }
            }

            if (indices.Count == 0)
            {
                continue;
            }

            // The index texture stands for the colour texture: 4 x 4 (a row per layer with maps).
            var layers = surface.Maps ? Renderer.SurfaceMapLayers : 1;
            var handle = Texture(renderer, kept, surface, made);
            buckets.Add((new SurfaceKey(handle, -1), new GpuTexture(handle, IndexSide, IndexSide, layers), vertices, indices));
            surfaces[handle.Index] = surface;
        }

        var mesh = StaticBatch.Build(renderer, kept?.Palette ?? palette, RenderState.Solid, buckets);
        if (mesh == null)
        {
            return null;
        }

        var parts = mesh.Parts.Select(p =>
        {
            var surface = surfaces[p.Material.Texture.Index];
            return p with { Material = surface.Apply(p.Material) };
        }).ToList();
        return new ModelMesh(mesh.Mesh, parts, mesh.Triangles);
    }
}

/// <summary>
/// The enhanced look's replacements of animated DOS models (docs/modding.md): a robot's segments,
/// the deathmatch avatars and their vehicles, the cockpits. Each segment keeps its DOS place, turn
/// and frame (the AIS script, the segment hierarchy, the clips); only the mesh drawn for a frame
/// changes: the glTF file standing in for that frame (<see cref="ModelReplacement.ForFrame"/>),
/// placed by the main model's sidecar against the DOS model's frame-0 box, or null where no mod
/// replaces it (the DOS frame is drawn). Meshes are built on first use, one per glTF file and DOS
/// model. Hit spheres, collision and the AI never see these meshes.
/// </summary>
public sealed class ReplacedModels(Renderer renderer, TextureHandle palette, TextureStore textures, KeptSurfaces? kept = null)
{
    // Per DOS model and main replacement: a remount (the MODS screen) brings new replacements.
    private readonly Dictionary<(string File, ModelReplacement Main), Matrix4x4?> _placements = [];
    private readonly Dictionary<(string File, ModelReplacement Model), LodMesh?> _meshes = [];
    // The index textures made in the level's scope (without kept surfaces), shared by every mesh of a surface.
    private readonly Dictionary<ReplacementSurface, TextureHandle> _textures = new(ReferenceEqualityComparer.Instance);

    /// <summary>The replacement mesh for <paramref name="frame"/> of the DOS model <paramref name="file"/>
    /// ("GUNTWR1.3D"), in the DOS model's space; null: none (draw the DOS frame).</summary>
    public ModelMesh? Mesh(string file, Model3D dos, int frame) => Lods(file, dos, frame)?.Main;

    /// <summary><see cref="Mesh"/> with its levels of detail.</summary>
    internal LodMesh? Lods(string file, Model3D dos, int frame)
    {
        if (!ModContent.HasModels)
        {
            return null;
        }

        var name = Path.GetFileNameWithoutExtension(file);
        if (ModContent.Model(name) is not { } main || main.ForFrame(frame) is not { } model)
        {
            return null;
        }

        if (_meshes.TryGetValue((file, model), out var mesh))
        {
            return mesh;
        }

        var started = System.Diagnostics.Stopwatch.GetTimestamp();
        if (!_placements.TryGetValue((file, main), out var placement))
        {
            // The DOS model's frame-0 box, from its faces as a prop's is measured (Level.BuildReplacements).
            var corners = ModelMesh.Surfaces(dos, textures).SelectMany(s => s.Vertices).Select(v => v.Position).ToArray();
            _placements[(file, main)] = placement = corners.Length > 0
                ? main.Sidecar.Placement(main.Model, (corners.Aggregate(Vector3.Min), corners.Aggregate(Vector3.Max)))
                : null;
        }

        _meshes[(file, model)] = mesh = placement is { } p ? LodMesh.Build(renderer, palette, model, p, kept, _textures) : null;
        DevTools.LoadHitches.Since("segment replacement mesh", started);
        return mesh;
    }

    /// <summary>The materials of every mesh built so far, for <see cref="Renderer.WarmUp"/>.</summary>
    public IEnumerable<Material> Materials() =>
        _meshes.Values.Where(m => m != null).SelectMany(m => m!.All).SelectMany(m => m.Parts).Select(p => p.Material);

    /// <summary>Queues a replacement mesh placed by <paramref name="world"/> (lit as the enhanced look lights it).</summary>
    public void Draw(ModelMesh mesh, in Matrix4x4 world)
    {
        foreach (var part in mesh.Parts)
        {
            mesh.Draw(renderer, part, 0, world);
        }
    }

    /// <summary>Queues level <paramref name="level"/> of a replacement placed by <paramref name="world"/>.</summary>
    internal void Draw(LodMesh mesh, int level, in Matrix4x4 world) => mesh.Draw(renderer, level, world);
}
