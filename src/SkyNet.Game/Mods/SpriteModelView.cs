using System.Numerics;
using RetroEng.Models;
using RetroEng.Render;

namespace SkyNet.Game.Mods;

/// <summary>
/// Draws <see cref="SpriteModel"/>s on one renderer: a static model as a prop's replacement mesh
/// (<see cref="ReplacementMesh"/>), an animated one through the engine's <see cref="AnimatedMesh"/>
/// with its first clip looping (one pose a frame, shared by every copy). Meshes are made on first
/// draw, one per model read (a remount reads new ones).
/// </summary>
public sealed class SpriteModelView(Renderer renderer, TextureHandle palette, KeptSurfaces? kept = null)
{
    private sealed class Built
    {
        public LodMesh? Mesh;
        public AnimatedMesh? Animated;
        public AnimatedInstance? Instance;
        public float PosedAt = float.NaN;
    }

    private readonly ScopedCache<SpriteModel, Built> _built = new(ReferenceEqualityComparer.Instance);

    /// <summary>The level of detail each copy (by seed) draws, kept between frames (the hysteresis).</summary>
    private readonly Dictionary<int, int> _levels = [];

    /// <summary>Queues <paramref name="model"/> standing on <paramref name="foot"/> (see <see cref="SpriteModel.World"/>).</summary>
    public void Draw(SpriteModel model, Vector3 foot, float height, float seconds, int seed) =>
        Draw(model, foot, height, seconds, seed, default);

    /// <summary><see cref="Draw(SpriteModel, Vector3, float, float, int)"/> at the level of detail
    /// <paramref name="view"/> sees (none: the model itself).</summary>
    internal void Draw(SpriteModel model, Vector3 foot, float height, float seconds, int seed, LodView view)
    {
        var built = Build(model);
        var world = model.World(foot, height, seconds, seed);
        if (built.Mesh is { } mesh)
        {
            ref var level = ref Level(seed, mesh.Levels);
            level = mesh.Pick(level, view, world);
            mesh.Draw(renderer, level, world);
            return;
        }

        if (built.Animated is not { } animated || built.Instance is not { } instance)
        {
            return;
        }

        Pose(built, instance, seconds);
        if (animated.Posed(instance) is not { } bounds)
        {
            return;
        }

        if (model.Lods is not { Levels: > 1 } lods)
        {
            animated.Draw(renderer, instance, world, bounds);
            return;
        }

        ref var at = ref Level(seed, lods.Levels);
        at = lods.Pick(at, view, world, bounds);
        foreach (var p in lods.Parts(at))
        {
            animated.DrawPart(renderer, instance, p, world, bounds, animated.MaterialOf(p));
        }
    }

    /// <summary>The level a copy draws (−1 the first time); a model without levels keeps none.</summary>
    private ref int Level(int seed, int levels)
    {
        if (levels <= 1)
        {
            _single = 0;
            return ref _single;
        }

        ref var level = ref System.Runtime.InteropServices.CollectionsMarshal.GetValueRefOrAddDefault(_levels, seed, out var known);
        if (!known)
        {
            level = -1;
        }

        return ref level;
    }

    private int _single;

    /// <summary>Models whose textures a worker is decoding (<see cref="Ready"/>).</summary>
    private readonly System.Collections.Concurrent.ConcurrentDictionary<SpriteModel, Task> _decoding = new(ReferenceEqualityComparer.Instance);

    /// <summary>Whether <paramref name="model"/> draws without a decode on this thread (its mesh made,
    /// or its textures made or kept); else its textures are decoded on a worker and false is
    /// returned (the caller draws the billboard meanwhile: a drop's first frames).</summary>
    internal bool Ready(SpriteModel model)
    {
        if (_built.Contains(model))
        {
            return true;
        }

        var pending = model.Surfaces.Concat(ReplacementSurface.Of(model.Static)).Where(s => !s.Made && kept?.Holds(s) != true).ToArray();
        if (pending.Length == 0)
        {
            _decoding.TryRemove(model, out _);
            return true;
        }

        _decoding.GetOrAdd(model, _ => Task.Run(() => ReplacementSurface.Warm(pending, kept)));
        return false;
    }

    /// <summary>Makes <paramref name="model"/>'s mesh now (a map's load) instead of on its first draw.</summary>
    internal void Prepare(SpriteModel model) => Build(model);

    /// <summary>The map's scope was released: the meshes made in it are made again on their next draw.</summary>
    internal void Release()
    {
        _built.Release();
        _levels.Clear();
    }

    /// <summary>The shared instance at <paramref name="seconds"/> on the first clip (once a frame).</summary>
    private static void Pose(Built built, AnimatedInstance instance, float seconds)
    {
        if (seconds == built.PosedAt)
        {
            return;
        }

        // The clock went back (a restart, a rewound demo): the clip from its start.
        if (seconds < instance.Time)
        {
            instance.Play(instance.Model.Clips[0], ClipLoop.Repeat);
        }

        instance.Advance(seconds - instance.Time);
        built.PosedAt = seconds;
    }

    /// <summary><see cref="Make"/> as a delegate, made once (a draw allocates nothing).</summary>
    private Func<SpriteModel, Built>? _make;

    private Built Build(SpriteModel model) => _built.Get(model, _make ??= Make);

    private Built Make(SpriteModel model)
    {
        var started = System.Diagnostics.Stopwatch.GetTimestamp();
        var built = new Built();
        if (model.Static is { } r)
        {
            built.Mesh = LodMesh.Build(renderer, palette, r, Matrix4x4.Identity, kept);
        }
        else if (model.Animated is { } a)
        {
            var materials = model.Surfaces.Select(s => ReplacementMesh.Material(renderer, palette, s, kept)).ToArray();
            built.Animated = new AnimatedMesh(renderer, a, materials);
            built.Instance = new AnimatedInstance(a);
            built.Instance.Play(a.Clips[0], ClipLoop.Repeat);
        }

        DevTools.LoadHitches.Since("sprite-model mesh", started);
        return built;
    }

    /// <summary>The materials of every model made so far, for <see cref="Renderer.WarmUp"/>.</summary>
    public IEnumerable<Material> Materials() => _built.Values.SelectMany(b =>
        b.Mesh != null ? b.Mesh.All.SelectMany(m => m.Parts).Select(p => p.Material)
        : b.Animated is { } a ? Enumerable.Range(0, a.Model.Parts.Count).Select(a.MaterialOf)
        : []);
}
