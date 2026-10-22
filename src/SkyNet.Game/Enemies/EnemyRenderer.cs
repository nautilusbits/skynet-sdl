using System.Numerics;
using RetroEng.Models;
using RetroEng.Render;
using SkyNet.Formats;
using SkyNet.Game.Menu;
using SkyNet.Game.Mods;

namespace SkyNet.Game.Enemies;

/// <summary>
/// Draws the live robots through the engine's animated models: one <see cref="RobotRig"/> per robot
/// type (its <see cref="AnimatedMesh"/> made on first use), one <see cref="RobotPose"/> per robot.
/// <code>
///   Capture (simulation thread): robot ─► RobotShot (root world, segments' turn/frame/world, AIS block)
///   Draw (render thread): RobotShot ─► RobotPose.Apply ─► AnimatedMesh.DrawPart per part (shade, texture frame)
/// </code>
/// Indoors a robot is drawn at the shade of the level's light where it stands (<see cref="Level.ShadeAt"/>).
/// Load the level with <c>staticRobots: false</c> so the frame-0 robots are not also in the world mesh.
/// In the enhanced look a mod's <c>NAME.robot.glb</c> replaces the whole robot (<see cref="RobotReplacement"/>);
/// else a segment whose model a mod replaces is drawn as the replacement for its frame
/// (<see cref="ReplacedModels"/>), at the segment's transform. Hit spheres and AI stay DOS's.
/// </summary>
public sealed class EnemyRenderer(Renderer renderer, TextureStore textures, TextureHandle palette, Level? level = null, KeptSurfaces? kept = null)
{
    private readonly RobotLooks _looks = new(textures);
    private readonly Dictionary<RobotRig, AnimatedMesh> _meshes = new(ReferenceEqualityComparer.Instance);
    private readonly ReplacedModels _replaced = new(renderer, palette, textures, kept);
    private readonly Dictionary<RobotRig, AnimatedInstance> _rests = new(ReferenceEqualityComparer.Instance);
    /// <summary>The frame <see cref="Draw(Robots, long)"/> captures into (a draw on the simulation's thread).</summary>
    private readonly RobotFrame _frame = new();
    /// <summary>Console <c>navmesh on</c>: the walkers' meshes (outlined once per <see cref="RobotNav"/>) and paths.</summary>
    private readonly Net.DmNavLines _nav = new(renderer);
    private (RobotNav? Nav, Vertex[]? Lines) _navOutline;

    /// <summary>The animated meshes made so far (one per robot type and wreck model).</summary>
    public int MeshesBuilt => _meshes.Count;

    /// <summary>The look drawn whatever the level's (the editor's model viewer compares a replacement with
    /// the original); null: the level's.</summary>
    public Look? LookOverride { get; set; }

    private Look Look => LookOverride ?? (level is { Enhanced: true } ? Look.Enhanced : Look.Original);

    /// <summary>Queues every visible robot (alive, not hidden) for this frame.</summary>
    public void Draw(Robots robots, long nowMs)
    {
        Capture(robots, _frame);
        Draw(_frame, nowMs);
    }

    /// <summary>The poses of every visible robot's segments and of the wreck parts in flight, for a
    /// draw on another thread (<see cref="Draw(RobotFrame, long)"/>), <paramref name="alpha"/> of the
    /// way from the previous tick to the latest (<see cref="Interpolation"/>).</summary>
    public void Capture(Robots robots, RobotFrame frame, float alpha = 1f)
    {
        frame.Ais = robots.Ais;
        frame.Count = 0;
        frame.Wrecks.Clear();
        foreach (var robot in robots.All)
        {
            if (!robot.Alive)
            {
                continue;
            }

            int? shade = level is { Outdoor: false } l ? l.ShadeAt(robot.Position + new Vector3(0f, 40f, 0f)) : null;
            frame.Next().Catch(robot, shade, alpha);
        }

        // The wreck parts in flight: their own model, frame 0, tumbling (Wreckage).
        foreach (ref readonly var w in robots.Wreckage.Parts)
        {
            if (w.Live && w.Model is { } model)
            {
                int? shade = level is { Outdoor: false } l ? l.ShadeAt(w.Position) : null;
                frame.Wrecks.Add(new RobotPart(model, 0, w.WorldAt(alpha), shade));
            }
        }

        CaptureNav(robots, frame);
    }

    /// <summary>The walkers' meshes and paths into <paramref name="frame"/> while <c>navmesh on</c>.</summary>
    private void CaptureNav(Robots robots, RobotFrame frame)
    {
        frame.NavMesh = null;
        frame.NavPaths.Clear();
        if (!robots.ShowNav || robots.Nav is not { } nav)
        {
            return;
        }

        if (!ReferenceEquals(_navOutline.Nav, nav))
        {
            _navOutline = (nav, [.. nav.Meshes.SelectMany(Net.DmNavLines.Outline)]);
        }

        frame.NavMesh = _navOutline.Lines;
        foreach (var r in robots.All)
        {
            if (r.Alive)
            {
                Net.DmNavLines.Path(r.Position + new Vector3(0f, r.FootOffset, 0f), r.NavPath, frame.NavPaths);
            }
        }
    }

    /// <summary>Queues the robots as <paramref name="frame"/> caught them.</summary>
    public void Draw(RobotFrame frame, long nowMs)
    {
        _nav.Draw(frame.NavMesh, frame.NavPaths);
        for (var i = 0; i < frame.Count; i++)
        {
            var shot = frame.Shots[i];
            var pose = _looks.Pose(shot.Robot, Look, frame.Ais);
            pose.Apply(shot, nowMs);
            DrawPose(pose, shot, nowMs);
        }

        _looks.Sweep();
        foreach (var wreck in frame.Wrecks)
        {
            if (Replaced(wreck.Model, 0) is { } replaced)
            {
                // A wreck flies briefly: its level without hysteresis.
                _replaced.Draw(replaced, replaced.Pick(-1, LodView, wreck.World), wreck.World);
                continue;
            }

            var rig = _looks.Single(wreck.Model);
            var (mesh, rest) = (Mesh(rig), Rest(rig));
            if (mesh.Posed(rest) is { } bounds)
            {
                DrawParts(rig, mesh, rest, wreck.World, bounds, wreck.Shade, nowMs, Skipped.None);
            }
        }
    }

    /// <summary>
    /// Makes now (a map's load) what drawing <paramref name="robots"/> would make on first sight: each
    /// type's rig and mesh, a mod's replacement of every frame of its segments, and the meshes of the
    /// wreck parts its death throws. A mod's files are read on every core first.
    /// </summary>
    public void Prepare(Robots robots)
    {
        var look = Look;
        if (look == Look.Enhanced && ModContent.HasModels)
        {
            var parts = robots.All.SelectMany(r => r.Root.Walk()).ToArray();
            // The parts' models and their wrecks' (PrepareWrecks).
            var wrecks = parts.SelectMany(s => s.Type?.Death ?? []).Where(d => d.Type >= 0 && d.Type < robots.Ais.Types.Length)
                .Select(d => robots.Ais.Types[d.Type].ModelFile);
            var files = parts.Select(s => s.Model?.File).Concat(wrecks).OfType<string>()
                .Select(Path.GetFileNameWithoutExtension).OfType<string>().Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
            RetroEng.Jobs.JobSystem.Shared.ParallelFor(files.Length, 1, new RetroEng.Jobs.ActionFor(i =>
            {
                ModContent.Robot(files[i]);
                ModContent.Model(files[i]);
            }));
            // Their textures too (decoded on first use, let go once kept on the GPU).
            ReplacementSurface.Warm(files.SelectMany(f => ModContent.Robot(f)?.Surfaces ?? ReplacementSurface.Of(ModContent.Model(f))), kept);
        }

        foreach (var robot in robots.All)
        {
            var rig = _looks.Rig(robot, look, robots.Ais);
            Mesh(rig);
            foreach (var segment in robot.Root.Walk())
            {
                if (rig.Source != RigSource.Mod && segment.Model is { } model)
                {
                    for (var frame = 0; frame < model.Frames; frame++)
                    {
                        Replaced(model, frame);
                    }
                }

                PrepareWrecks(segment, robots);
            }
        }
    }

    /// <summary>The meshes of the wreck parts <paramref name="segment"/>'s death throws (<see cref="Wreckage"/>).</summary>
    private void PrepareWrecks(EnemyPart segment, Robots robots)
    {
        foreach (var d in segment.Type?.Death ?? [])
        {
            if (d.Type < 0 || d.Type >= robots.Ais.Types.Length || robots.Models.Get(robots.Ais.Types[d.Type].ModelFile) is not { } model)
            {
                continue;
            }

            if (Replaced(model, 0) == null)
            {
                Mesh(_looks.Single(model));
            }
        }
    }

    /// <summary>A wreck model's instance, at rest (frame 0): one per rig, shared by every wreck of it.</summary>
    private AnimatedInstance Rest(RobotRig rig)
    {
        if (!_rests.TryGetValue(rig, out var rest))
        {
            _rests[rig] = rest = new AnimatedInstance(rig.Model);
        }

        return rest;
    }

    private void DrawPose(RobotPose pose, RobotShot shot, long nowMs)
    {
        var mesh = Mesh(pose.Rig);
        if (mesh.Posed(pose.Instance) is not { } bounds)
        {
            return;
        }

        if (pose.Rig.Source == RigSource.Mod)
        {
            DrawMod(pose, mesh, bounds);
            return;
        }

        // A segment a mod replaces is drawn as its replacement, its own parts skipped; each segment
        // at its own level of detail, kept in the pose between frames.
        var skipped = Skipped.None;
        var levels = SegmentLevels(pose, shot.Segments.Count);
        for (var s = 0; s < shot.Segments.Count && s < Skipped.Max; s++)
        {
            var segment = shot.Segments[s];
            if (segment.Model is { } model && Replaced(model, segment.Frame) is { } replaced)
            {
                levels[s] = replaced.Pick(levels[s], LodView, segment.World);
                _replaced.Draw(replaced, levels[s], segment.World);
                skipped = skipped.With(s);
            }
        }

        DrawParts(pose.Rig, mesh, pose.Instance, Matrix4x4.Identity, bounds, shot.Shade, nowMs, skipped);
    }

    /// <summary>A mod rig at the level of detail its posed box covers on the screen.</summary>
    private void DrawMod(RobotPose pose, AnimatedMesh mesh, Bounds bounds)
    {
        if (pose.Rig.Replacement?.Lods is not { Levels: > 1 } lods)
        {
            mesh.Draw(renderer, pose.Instance, Matrix4x4.Identity, bounds);
            return;
        }

        pose.Lod = lods.Pick(pose.Lod, LodView, Matrix4x4.Identity, bounds);
        foreach (var p in lods.Parts(pose.Lod))
        {
            mesh.DrawPart(renderer, pose.Instance, p, Matrix4x4.Identity, bounds, mesh.MaterialOf(p));
        }
    }

    /// <summary>The pose's segments' levels of detail (−1: none yet), made on first use.</summary>
    private static int[] SegmentLevels(RobotPose pose, int segments)
    {
        if (pose.SegmentLods is not { } levels || levels.Length < segments)
        {
            pose.SegmentLods = levels = new int[segments];
            Array.Fill(levels, -1);
        }

        return levels;
    }

    /// <summary>Where the frame is seen from (the level's, set by its draw); none: the models themselves.</summary>
    private LodView LodView => level?.LodView ?? default;

    /// <summary>Segments whose parts are not drawn (a bit each).</summary>
    private readonly record struct Skipped(ulong Bits)
    {
        public const int Max = 64;
        public static Skipped None => default;
        public Skipped With(int segment) => new(Bits | 1UL << segment);
        public bool Has(int segment) => segment < Max && (Bits & 1UL << segment) != 0;
    }

    /// <summary>A DOS rig's parts but the skipped segments', at the indoor shade and each texture's frame.</summary>
    private void DrawParts(RobotRig rig, AnimatedMesh mesh, AnimatedInstance instance, in Matrix4x4 world, Bounds bounds, int? shade,
        long nowMs, Skipped skipped)
    {
        for (var p = 0; p < mesh.Model.Parts.Count; p++)
        {
            if (skipped.Has(rig.PartSegments[p]))
            {
                continue;
            }

            var material = mesh.MaterialOf(p);
            material = shade is { } s ? material with { Shade = s } : material;
            var texture = rig.Surfaces[mesh.Model.Parts[p].Material].Texture;
            mesh.DrawPart(renderer, instance, p, world, bounds, material, texture?.FrameAt(nowMs) ?? 0);
        }
    }

    /// <summary>A rig's GPU mesh, made on first use: DOS surfaces as the static models' materials, a
    /// mod's glTF materials as its replacement surfaces.</summary>
    private AnimatedMesh Mesh(RobotRig rig)
    {
        if (_meshes.TryGetValue(rig, out var mesh))
        {
            return mesh;
        }

        var started = System.Diagnostics.Stopwatch.GetTimestamp();
        var materials = rig.Replacement is { } r
            ? r.Surfaces.Select(s => ReplacementMesh.Material(renderer, palette, s, kept)).ToArray()
            : rig.Surfaces.Select(s => ModelMesh.MaterialFor(s.Key, s.Texture, palette, RenderState.Solid)).ToArray();
        mesh = new AnimatedMesh(renderer, rig.Model, materials);
        _meshes[rig] = mesh;
        DevTools.LoadHitches.Since("robot mesh", started);
        return mesh;
    }

    /// <summary>A mod's replacement of the model's frame, in the enhanced look only.</summary>
    private LodMesh? Replaced(EnemyModelInfo model, int frame) =>
        Look == Look.Enhanced ? _replaced.Lods(model.File, model.Model, Math.Clamp(frame, 0, model.Frames - 1)) : null;

    /// <summary>The materials of every mesh built so far, for <see cref="Renderer.WarmUp"/>.</summary>
    public IEnumerable<Material> Materials() =>
        _meshes.Values.SelectMany(m => Enumerable.Range(0, m.Model.Parts.Count).Select(m.MaterialOf)).Concat(_replaced.Materials());

    /// <summary>Makes the skinned pipelines the robots draw with (every DOS robot part is a solid
    /// palette surface), so the first robot seen doesn't wait for them.</summary>
    public void WarmUp() => renderer.WarmUpSkinned(Material.FlatIndex(0, palette, RenderState.Solid) with { Shading = Shading.Lit });
}

/// <summary>A wreck part to draw: its model, frame, world transform and indoor shade.</summary>
public readonly record struct RobotPart(EnemyModelInfo Model, int Frame, Matrix4x4 World, int? Shade);

/// <summary>The robots' poses for one frame (<see cref="EnemyRenderer.Capture"/>), pooled: drawn on
/// another thread while the next tick runs (engine-threads plan B1).</summary>
public sealed class RobotFrame
{
    internal readonly List<RobotShot> Shots = [];
    internal readonly List<RobotPart> Wrecks = [];
    /// <summary>The robots caught (the first <see cref="Count"/> of <see cref="Shots"/>).</summary>
    internal int Count;
    internal AisData? Ais;
    /// <summary>The walkers' nav mesh outline and their paths (<c>navmesh on</c>); null / empty: not shown.</summary>
    internal Vertex[]? NavMesh;
    internal readonly List<Vertex> NavPaths = [];

    /// <summary>The next pooled shot.</summary>
    internal RobotShot Next()
    {
        if (Count == Shots.Count)
        {
            Shots.Add(new RobotShot());
        }

        return Shots[Count++];
    }
}
