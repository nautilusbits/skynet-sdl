using System.Numerics;
using RetroEng.Physics;
using SkyNet.Formats;

namespace SkyNet.Game.Enemies;

/// <summary>What the AI needs of a robot's .3D: its frames, where the feet are (the lowest vertex
/// over the living frames, the first two thirds of a long strip: a collapse pose reaches lower),
/// the frame-0 bounds and, per frame, the vertex farthest from the origin (a machine arm's claw).</summary>
public sealed record EnemyModelInfo(string File, Model3D Model, int Frames, float FootY, Box Bounds, Vector3[] Tips);

/// <summary>The robots' .3D models (MDMDENMS.BSA first, then MDMDOBJS.BSA), parsed once each.
/// Headless: the renderer builds its meshes from the same <see cref="Model3D"/>.</summary>
public sealed class EnemyModels
{
    private readonly GameData? _data;
    private readonly Dictionary<string, EnemyModelInfo?> _cache = new(StringComparer.OrdinalIgnoreCase);

    public EnemyModels(GameData? data) => _data = data;

    /// <summary>A model by file name ("T800PST.3D"); null when missing. With a
    /// <paramref name="look"/> (a mod robot's, <see cref="EnemyType.Look"/>) the model goes by that
    /// name: its ENHANCED replacement is <c>models/LOOK.glb</c>, the DOS file's other users keep theirs.</summary>
    public EnemyModelInfo? Get(string file, string look = "")
    {
        if (look.Length > 0)
        {
            return Looked(file, look);
        }

        if (string.IsNullOrEmpty(file) || _data == null)
        {
            return null;
        }

        if (_cache.TryGetValue(file, out var info))
        {
            return info;
        }

        byte[]? bytes = null;
        foreach (var archive in new[] { "MDMDENMS.BSA", "MDMDOBJS.BSA" })
        {
            if (bytes is { Length: > 0 } || !_data.Exists(archive))
            {
                continue;
            }

            using var a = _data.OpenArchive(archive);
            bytes = a.Read(file);
        }

        var model = bytes is { Length: > 0 } ? Model3D.Parse(bytes) : null;
        _cache[file] = info = model != null ? Describe(file, model) : null;
        return info;
    }

    // The DOS model under a look's name (cached by the look; the "*" keeps it apart from file names).
    private EnemyModelInfo? Looked(string file, string look)
    {
        var key = "*" + look;
        if (!_cache.TryGetValue(key, out var info))
        {
            _cache[key] = info = Get(file) is { } dos ? dos with { File = look } : null;
        }

        return info;
    }

    public static EnemyModelInfo Describe(string file, Model3D model)
    {
        var frames = Math.Max(1, model.Frames.Length);
        var living = frames < 6 ? frames : (int)MathF.Ceiling(frames * 2f / 3f);
        var foot = float.MaxValue;
        var tips = new Vector3[frames];
        var bounds = Box.Empty;
        for (var f = 0; f < frames; f++)
        {
            var verts = model.Frames.Length > 0 ? model.Frames[f] : model.Vertices;
            var best = -1f;
            foreach (var v in verts)
            {
                var p = v.ToYUp();
                if (f < living)
                {
                    foot = MathF.Min(foot, p.Y);
                }

                if (f == 0)
                {
                    bounds = bounds.Include(p);
                }

                var l = p.LengthSquared();
                if (l > best)
                {
                    (best, tips[f]) = (l, p);
                }
            }
        }

        if (bounds.IsEmpty)
        {
            bounds = new Box(Vector3.Zero, Vector3.Zero);
        }

        return new EnemyModelInfo(file, model, frames, foot == float.MaxValue ? 0f : foot, bounds, tips);
    }
}
