using System.Numerics;
using RetroEng.Render;

namespace SkyNet.Game.Mods;

/// <summary>
/// A replacement model's GPU meshes, one per level of detail (level 0: the model itself), all placed
/// alike in the DOS model's space. Each level is its own mesh, so the engine still merges the draws
/// of one level into instanced draws.
/// </summary>
internal sealed class LodMesh
{
    private readonly ModelMesh[] _levels;
    private readonly LodSettings _settings;
    private readonly Vector3 _centre;
    private readonly float _size;

    private LodMesh(ModelMesh[] levels, LodSettings settings, Vector3 centre, float size)
    {
        (_levels, _settings, _centre, _size) = (levels, settings, centre, size);
    }

    /// <summary>Level 0, the model itself.</summary>
    public ModelMesh Main => _levels[0];

    public int Levels => _levels.Length;

    /// <summary>Level <paramref name="level"/>'s mesh (clamped to the coarsest).</summary>
    public ModelMesh this[int level] => _levels[Math.Clamp(level, 0, _levels.Length - 1)];

    /// <summary><paramref name="r"/> and its levels placed by <paramref name="placement"/>; null: the
    /// model itself has no mesh. A level without one is left out (with the levels after it).</summary>
    public static LodMesh? Build(Renderer renderer, TextureHandle palette, ModelReplacement r, Matrix4x4 placement, KeptSurfaces? kept)
    {
        if (ReplacementMesh.Build(renderer, palette, r, placement, kept) is not { } main)
        {
            return null;
        }

        var levels = new List<ModelMesh> { main };
        foreach (var lod in r.Lods)
        {
            if (ReplacementMesh.Build(renderer, palette, lod, placement, kept) is not { } mesh)
            {
                break;
            }

            levels.Add(mesh);
        }

        // The placed model's box: its centre and largest side size it on the screen.
        var (min, max) = (new Vector3(float.MaxValue), new Vector3(float.MinValue));
        foreach (var p in r.Model.Primitives)
        {
            foreach (var v in p.Positions)
            {
                var q = Vector3.Transform(v, placement);
                (min, max) = (Vector3.Min(min, q), Vector3.Max(max, q));
            }
        }

        var size = max - min;
        var box = min.X <= max.X ? ((min + max) / 2f, MathF.Max(size.X, MathF.Max(size.Y, size.Z))) : (Vector3.Zero, 0f);
        return new LodMesh([.. levels], r.Sidecar.Lod, box.Item1, box.Item2);
    }

    /// <summary>The level to draw placed by <paramref name="world"/> seen by <paramref name="view"/>,
    /// now drawing <paramref name="current"/> (−1: none yet).</summary>
    public int Pick(int current, in LodView view, in Matrix4x4 world)
    {
        if (_levels.Length == 1)
        {
            return 0;
        }

        // The world's scale along its first axis (props: 1; sprite models: sized to the sprite).
        var scale = new Vector3(world.M11, world.M12, world.M13).Length();
        var fraction = view.Fraction(Vector3.Transform(_centre, world), _size * scale);
        return Math.Min(_settings.Pick(current, fraction), _levels.Length - 1);
    }

    /// <summary>Queues level <paramref name="level"/> placed by <paramref name="world"/>.</summary>
    public void Draw(Renderer renderer, int level, in Matrix4x4 world)
    {
        var mesh = this[level];
        foreach (var part in mesh.Parts)
        {
            mesh.Draw(renderer, part, 0, world);
        }
    }

    /// <summary>Every level's mesh (materials to warm up).</summary>
    public IEnumerable<ModelMesh> All => _levels;
}
