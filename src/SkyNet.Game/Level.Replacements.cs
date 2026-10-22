using System.Diagnostics;
using System.Numerics;
using RetroEng.Diagnostics;
using RetroEng.Models;
using RetroEng.Render;
using SkyNet.Formats;
using SkyNet.Game.Baking;
using SkyNet.Game.Behaviour;
using SkyNet.Game.Collision;
using SkyNet.Game.Mods;

namespace SkyNet.Game;

/// <summary>
/// The enhanced look's mod content on a level (docs/modding.md): the HD textures decoded ahead,
/// and the replacement models. A static prop whose DOS model a mod replaces
/// (<c>models/NAME.glb</c>) is drawn as the glTF model in the enhanced look, from a second world
/// mesh that leaves the DOS prop's triangles out; the original look draws the DOS world mesh as
/// ever, and collision stays the DOS model's. Without a mod none of this exists.
/// </summary>
public sealed partial class Level
{
    /// <summary>Corners closer than 1/this world unit count as the same when finding a placed prop's
    /// triangles in the baked world mesh.</summary>
    private const float MatchScale = 8f;

    private bool _enhanced;
    /// <summary>A mod's surfaces kept across maps (null: made in the map's scope).</summary>
    private readonly KeptSurfaces? _kept;

    /// <summary>Whether the level is drawn in the enhanced look (<see cref="Apply"/>): replacement
    /// models of robots, avatars and cockpits are drawn only then.</summary>
    public bool Enhanced => _enhanced;
    /// <summary>The world mesh without the replaced props (null: nothing replaced on this map).</summary>
    private ModelMesh? _worldEnhanced;
    /// <summary>Per replacement model, where it stands and the level of detail each copy draws (−1: none yet).</summary>
    private readonly List<(LodMesh Mesh, Matrix4x4[] Placements, int[] Levels)> _replacements = [];
    /// <summary>Where this frame is seen from, for the levels of detail (<see cref="Draw(LevelFrame, Vector3, float)"/>).</summary>
    private LodView _lodView;

    /// <summary>The view the frame's levels of detail are picked from (the robots drawn after the level read it).</summary>
    internal LodView LodView => _lodView;
    /// <summary>The sprites a mod draws as models (made on first draw).</summary>
    private SpriteModelView? _spriteModels;
    /// <summary>Indoors, each sprite model's foot by MAP offset (<see cref="SpriteFloor"/>), found on first draw.</summary>
    private readonly Dictionary<int, float> _spriteFeet = [];
    private RetroEng.Physics.CollisionWorld? _floors;

    /// <summary>The world indoor sprite models stand on (<see cref="SpriteFloor"/>); null: on the
    /// billboard's foot.</summary>
    public RetroEng.Physics.CollisionWorld? Floors
    {
        get => _floors;
        set
        {
            _floors = value;
            _spriteFeet.Clear();
        }
    }
    /// <summary>The dynamic meshes' replacements by MAP offset: where the map placed the entity and a
    /// mesh per stage (null: that stage stays DOS's) and the level of detail drawn (one cell, −1: none yet).</summary>
    private readonly Dictionary<int, (Matrix4x4 Placed, LodMesh?[] Stages, int[] Level)> _dynamicReplacements = [];

    /// <summary>Replacement models placed on this map, and DOS triangles they hide (tests, logs).</summary>
    public int ReplacedProps { get; private set; }
    public int HiddenTriangles { get; private set; }

    /// <summary>The level's HD textures decoded on worker threads before anything asks for them.</summary>
    private static void PrefetchHd(TextureStore textures, MapBlock block)
    {
        if (!ModContent.HasTextures)
        {
            return;
        }

        var tiles = textures.Bank(Terrain.TileBank)?.Records.Length ?? 0;
        ModContent.Prefetch(block.ModelTextures.Concat(block.RobotTextures).Concat(block.SpriteTextures)
            .Select(t => (t.Bank, t.Record, t.Opaque, textures.RecordWidth(t.Bank, t.Record)))
            .Concat(block.Outdoor ? Enumerable.Range(0, tiles).Select(r => (Terrain.TileBank, r, true, textures.RecordWidth(Terrain.TileBank, r))) : []));
    }

    private void BuildReplacements(Renderer renderer, GameData data, TextureStore textures, MapBlock block, bool staticRobots)
    {
        if (!ModContent.HasModels)
        {
            return;
        }

        var clock = Stopwatch.StartNew();
        var hidden = new HashSet<(SurfaceKey, int, int, int)>();
        var placed = new Dictionary<string, List<Matrix4x4>>(StringComparer.OrdinalIgnoreCase);
        var meshes = new Dictionary<string, LodMesh?>(StringComparer.OrdinalIgnoreCase);

        // A replacement's mesh, one per model, in the DOS model's space (sized to its box).
        LodMesh? Mesh(string name, ModelReplacement r, List<ModelSurface> surfaces)
        {
            if (!meshes.TryGetValue(name, out var mesh))
            {
                var all = surfaces.SelectMany(s => s.Vertices).Select(v => v.Position).ToArray();
                meshes[name] = mesh = LodMesh.Build(renderer, Palette, r, r.Sidecar.Placement(r.Model, (all.Aggregate(Vector3.Min), all.Aggregate(Vector3.Max))), _kept);
            }

            return mesh;
        }

        ReadModels();
        var dosModels = new Dictionary<string, List<ModelSurface>?>(StringComparer.OrdinalIgnoreCase);
        using (var inputs = new BakeInputs(data))
        {
            foreach (var e in Map.Entities)
            {
                if (e.Variant != MapEntityVariant.Mesh || e.NameIndex < 0 || e.NameIndex >= Map.Names.Length)
                {
                    continue;
                }

                var name = Map.Names[e.NameIndex];
                if (string.IsNullOrEmpty(name))
                {
                    continue;
                }

                var world = Rotation(e.Yaw, e.Pitch, e.Roll) * Matrix4x4.CreateTranslation(e.X, -e.Y, -e.Z);
                if (BehaviourModel.IsDynamic(Map, e, Transfrm))
                {
                    // A mover, wreck or destructible: its own mesh per stage, so nothing to hide.
                    var stages = BehaviourModel.StageMeshes(Map, e, Transfrm)
                        .Select(n => ModContent.Model(n) is { } r && r.Covers(0) && Dos(n) is { Count: > 0 } ds ? Mesh(n, r, ds) : null)
                        .ToArray();
                    if (stages.Any(m => m != null))
                    {
                        _dynamicReplacements[e.FileOffset] = (world, stages, [-1]);
                        ReplacedProps++;
                    }

                    continue;
                }

                if (ModContent.Model(name) is not { } replacement || !replacement.Covers(0) || Dos(name) is not { Count: > 0 } surfaces)
                {
                    continue;
                }

                foreach (var s in surfaces)
                {
                    foreach (var v in s.Vertices)
                    {
                        hidden.Add(Match(s.Key, Vector3.Transform(v.Position, world)));
                    }
                }

                if (!placed.TryGetValue(name, out var at))
                {
                    placed[name] = at = [];
                }

                at.Add(world);
            }

            // A DOS model's surfaces as MapBaker places them: the objects' archive first, frame 0.
            List<ModelSurface>? Dos(string name)
            {
                if (!dosModels.TryGetValue(name, out var surfaces))
                {
                    var bytes = inputs.Read(BakeInputs.ModelPrefix + name + ".3D");
                    var model = bytes is { Length: > 0 } ? Model3D.Parse(bytes) : null;
                    dosModels[name] = surfaces = model != null ? ModelMesh.Surfaces(model, textures) : null;
                }

                return surfaces;
            }
        }

        foreach (var (name, at) in placed)
        {
            if (ModContent.Model(name) is { } r && dosModels[name] is { } surfaces && Mesh(name, r, surfaces) is { } mesh)
            {
                var levels = new int[at.Count];
                Array.Fill(levels, -1);
                _replacements.Add((mesh, [.. at], levels));
                ReplacedProps += at.Count;
            }
        }

        if (ReplacedProps == 0)
        {
            return;
        }

        var removed = 0;
        var world2 = block.World.Select(b =>
        {
            var kept = Without(b, Bucket(textures, b).Item1, hidden);
            removed += (b.Indices.Length - kept.Indices.Length) / 3;
            return kept;
        }).ToArray();
        IEnumerable<BlockBucket> buckets = staticRobots ? Merge(world2, block.Robots) : world2;
        // Nothing hidden (only dynamic props replaced): the DOS world mesh serves both looks.
        _worldEnhanced = removed == 0 ? _world
            : StaticBatch.Build(renderer, Palette, RenderState.Solid, buckets.Select(b => Bucket(textures, b)), _shading != null);
        HiddenTriangles = removed;
        Log.Info("Mods", $"MAP.{Number:D3}: {ReplacedProps} props ({_dynamicReplacements.Count} of them dynamic) of " +
            $"{meshes.Count(m => m.Value != null)} models replaced in the enhanced look " +
            $"({removed} DOS triangles hidden) in {clock.Elapsed.TotalMilliseconds:0} ms");
    }

    /// <summary>The map's replacement models read on every core (each glTF parsed and its images
    /// decoded once a mount) before the build asks for them one by one.</summary>
    private void ReadModels()
    {
        var names = Map.Entities
            .Where(e => e.Variant == MapEntityVariant.Mesh && e.NameIndex >= 0 && e.NameIndex < Map.Names.Length)
            .SelectMany(e => BehaviourModel.IsDynamic(Map, e, Transfrm) ? BehaviourModel.StageMeshes(Map, e, Transfrm) : [Map.Names[e.NameIndex]])
            .Where(n => !string.IsNullOrEmpty(n))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();
        RetroEng.Jobs.JobSystem.Shared.ParallelFor(names.Length, 1, new RetroEng.Jobs.ActionFor(i => ModContent.Model(names[i])));
        // Their textures too (decoded on first use, let go once kept on the GPU).
        ReplacementSurface.Warm(names.SelectMany(n => ModContent.Model(n)?.Surfaces ?? []), _kept);
    }

    private static (SurfaceKey, int, int, int) Match(SurfaceKey key, Vector3 p) =>
        (key, (int)MathF.Round(p.X * MatchScale), (int)MathF.Round(p.Y * MatchScale), (int)MathF.Round(p.Z * MatchScale));

    /// <summary>A world bucket without the triangles whose three corners are all corners of a replaced
    /// prop of the same texture; its pieces (map cells) shrunk to match.</summary>
    private static BlockBucket Without(BlockBucket b, SurfaceKey key, HashSet<(SurfaceKey, int, int, int)> hidden)
    {
        bool Hidden(uint i) => hidden.Contains(Match(key, b.Vertices[i].Position));
        var keep = new bool[b.Indices.Length / 3];
        var any = false;
        for (var t = 0; t < keep.Length; t++)
        {
            keep[t] = !(Hidden(b.Indices[t * 3]) && Hidden(b.Indices[t * 3 + 1]) && Hidden(b.Indices[t * 3 + 2]));
            any |= !keep[t];
        }

        if (!any)
        {
            return b;
        }

        var indices = new List<uint>(b.Indices.Length);
        var pieces = new List<BlockPiece>(b.Pieces.Length);
        foreach (var p in b.Pieces)
        {
            var first = indices.Count;
            for (var i = p.First; i < p.First + p.Count; i += 3)
            {
                if (keep[i / 3])
                {
                    indices.AddRange([b.Indices[i], b.Indices[i + 1], b.Indices[i + 2]]);
                }
            }

            if (indices.Count > first)
            {
                pieces.Add(p with { First = first, Count = indices.Count - first });
            }
        }

        if (b.Pieces.Length == 0)
        {
            for (var t = 0; t < keep.Length; t++)
            {
                if (keep[t])
                {
                    indices.AddRange([b.Indices[t * 3], b.Indices[t * 3 + 1], b.Indices[t * 3 + 2]]);
                }
            }
        }

        return b with { Indices = [.. indices], Pieces = [.. pieces] };
    }

    /// <summary>Draws a dynamic entity's replacement for <paramref name="stage"/> moved by
    /// <paramref name="delta"/>, in the enhanced look; false: it has none (DOS's is drawn).</summary>
    private bool DrawReplacedStage(int offset, int stage, Matrix4x4 delta)
    {
        if (!_enhanced || !_dynamicReplacements.TryGetValue(offset, out var r)
            || r.Stages[Math.Clamp(stage, 0, r.Stages.Length - 1)] is not { } mesh)
        {
            return false;
        }

        var world = r.Placed * delta;
        r.Level[0] = mesh.Pick(r.Level[0], _lodView, world);
        mesh.Draw(_renderer, r.Level[0], world);
        return true;
    }

    /// <summary>The map's sprite models made now (its load), so that their materials are warmed and
    /// their colour textures made with the load's, not on their first draw.</summary>
    private void PrepareSpriteModels()
    {
        // The map's sprite models' textures on every core first (decoded on first use).
        var models = _sprites.Select(g => SpriteModels.Drawn(g.Index, Menu.Look.Enhanced)).OfType<SpriteModel>();
        ReplacementSurface.Warm(models.SelectMany(m => m.Surfaces.Concat(ReplacementSurface.Of(m.Static))), _kept);
        foreach (var g in _sprites)
        {
            if (SpriteModels.Drawn(g.Index, Menu.Look.Enhanced) is not { } model)
            {
                continue;
            }

            _spriteModels ??= new SpriteModelView(_renderer, Palette, _kept);
            _spriteModels.Prepare(model);
        }
    }

    /// <summary>Draws a sprite group as the model a mod maps its sprite to
    /// (<see cref="SpriteModels"/>), standing on each sprite's foot, in the enhanced look; false: none
    /// (the billboards are drawn).</summary>
    private bool DrawSpriteModels(SpriteGroup g, HashSet<int> hidden, long now)
    {
        if (SpriteModels.Drawn(g.Index, _enhanced ? Menu.Look.Enhanced : Menu.Look.Original) is not { } model)
        {
            return false;
        }

        _spriteModels ??= new SpriteModelView(_renderer, Palette, _kept);
        for (var i = 0; i < g.Sprites.Length; i++)
        {
            if (hidden.Contains(g.Offsets[i]))
            {
                continue;
            }

            var (centre, size) = g.Sprites[i];
            var foot = centre - new Vector3(0f, size.Y / 2f, 0f);
            if (!Outdoor && _floors != null)
            {
                if (!_spriteFeet.TryGetValue(g.Offsets[i], out var y))
                {
                    var record = foot + new Vector3(0f, IndoorSpriteLift, 0f);
                    _spriteFeet[g.Offsets[i]] = y = SpriteFloor.Below(_floors, record) ?? foot.Y;
                }

                foot.Y = y;
            }

            _spriteModels.Draw(model, foot, size.Y, now / 1000f, g.Offsets[i], _lodView);
        }

        return true;
    }

    private void DrawReplacements()
    {
        // Each copy at its own level of detail, kept between frames (the hysteresis).
        foreach (var (mesh, placements, levels) in _replacements)
        {
            for (var i = 0; i < placements.Length; i++)
            {
                levels[i] = mesh.Pick(levels[i], _lodView, placements[i]);
                mesh.Draw(_renderer, levels[i], placements[i]);
            }
        }
    }
}
