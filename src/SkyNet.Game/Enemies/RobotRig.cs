using System.Numerics;
using RetroEng.Models;
using RetroEng.Render;
using SkyNet.Formats;
using SkyNet.Game.Mods;

namespace SkyNet.Game.Enemies;

/// <summary>Where a robot's drawn look comes from.</summary>
public enum RigSource
{
    /// <summary>The DOS segments and their .3D frames.</summary>
    Dos,
    /// <summary>A mod's skinned <c>NAME.robot.glb</c> (<see cref="RobotReplacement"/>).</summary>
    Mod,
}

/// <summary>
/// A robot type's look as one <see cref="AnimatedModel"/>, shared by every robot of that type (each
/// poses its own <see cref="RobotPose"/>). Hit spheres, feet, AI and timings never read it.
/// <code>
///   DOS: segment i (depth first, root 0) ─► node i: rest = offset + rest turn (Enemy.AddSegments)
///          └─ a rigid part per texture word ─► shapes = the .3D frames (positions, normals, texture
///             coordinates per frame: a frame may remap a quad's fourth corner)
///        the AIS script picks each segment's frame (Reshape); a segment's placement now (a
///        turret's aim) replaces its node's (OverrideMatrix)
///   Mod: NAME.robot.glb ─► joint "seg&lt;i&gt;_*" follows segment i's turn on top of the clip;
///        the AIS block playing picks the clip by name (RobotAnims), none: the rest pose
/// </code>
/// </summary>
public sealed class RobotRig
{
    private readonly int[] _nodeSegment;
    private readonly Dictionary<uint, (AnimatedClip Clip, ClipLoop Loop)> _clips;

    private RobotRig(AnimatedModel model, RigSource source, int[] nodeSegment, Segment[] segments)
    {
        Model = model;
        Source = source;
        _nodeSegment = nodeSegment;
        Segments = segments;
        _clips = [];
    }

    /// <summary>A segment's rest placement: its offset and turn (Enemy.AddSegments).</summary>
    public readonly record struct Segment(Vector3 Offset, Quaternion Rest);

    public AnimatedModel Model { get; }
    public RigSource Source { get; }
    /// <summary>The segments, depth first, root first.</summary>
    public IReadOnlyList<Segment> Segments { get; }
    /// <summary>DOS: per model material, the surface it draws (texture or flat palette index).</summary>
    public IReadOnlyList<(SurfaceKey Key, GpuTexture? Texture)> Surfaces { get; private init; } = [];
    /// <summary>DOS: per part, its segment.</summary>
    public IReadOnlyList<int> PartSegments { get; private init; } = [];
    /// <summary>Mod: the file drawn.</summary>
    public RobotReplacement? Replacement { get; private init; }

    /// <summary>The segment node <paramref name="node"/> follows; −1: none.</summary>
    public int SegmentOf(int node) => _nodeSegment[node];

    /// <summary>The clip a block plays (mod rigs); null: none (the rest pose).</summary>
    public (AnimatedClip Clip, ClipLoop Loop)? ClipOf(uint blockVa) => _clips.TryGetValue(blockVa, out var c) ? c : null;

    /// <summary>A robot template's joint name: its segment's depth-first index and model in capitals ("seg2_HVYPLSM").</summary>
    public static string JointName(int index, string model) => $"{JointPrefix(index)}{model.ToUpperInvariant()}";

    /// <summary>What a segment's joint name starts with: a mod's joint is found by it, whatever model it names.</summary>
    public static string JointPrefix(int index) => $"seg{index}_";

    /// <summary>A segment's turn as DOS places it: Ry(yaw)·Rx(pitch)·Rz(−roll), for row vectors.</summary>
    private static Quaternion Turn(float yaw, float pitch, float roll) => Quaternion.CreateFromRotationMatrix(
        Matrix4x4.CreateRotationY(yaw) * Matrix4x4.CreateRotationX(pitch) * Matrix4x4.CreateRotationZ(-roll));

    /// <summary>The robot's segments as DOS draws them: a node each, a rigid part per texture word
    /// with the model's frames as shapes (built as ModelMesh.Surfaces builds a frame).</summary>
    public static RobotRig Dos(IReadOnlyList<EnemyPart> segments, TextureStore textures)
    {
        var b = new AnimatedModelBuilder();
        var surfaces = new List<(SurfaceKey, GpuTexture?)>();
        var materials = new Dictionary<SurfaceKey, int>();
        var partSegments = new List<int>();
        var placed = Place(segments);
        for (var i = 0; i < segments.Count; i++)
        {
            var s = placed[i];
            var node = b.AddNode(i == 0 ? "root" : $"segment {i}", ParentOf(segments, i),
                i == 0 ? Trs.Identity : new Trs(s.Offset, s.Rest, Vector3.One));
            if (segments[i].Model is not { } model)
            {
                continue;
            }

            foreach (var part in Parts(model, textures, node, surface => Material(b, surfaces, materials, surface)))
            {
                b.AddPart(part);
                partSegments.Add(i);
            }
        }

        return new RobotRig(b.Build(), RigSource.Dos, [.. Enumerable.Range(0, segments.Count)], placed)
        {
            Surfaces = surfaces,
            PartSegments = partSegments,
        };
    }

    /// <summary>The robot as a mod's skinned file draws it: segment i's turn goes to the joint named
    /// "seg&lt;i&gt;_…", block <c>b</c>'s clip is the one named as <see cref="RobotAnims"/> names it.</summary>
    public static RobotRig Mod(IReadOnlyList<EnemyPart> segments, RobotReplacement replacement, AisData? ais)
    {
        var model = replacement.Model;
        var nodeSegment = new int[model.Nodes.Count];
        Array.Fill(nodeSegment, -1);
        for (var n = 0; n < model.Nodes.Count; n++)
        {
            for (var i = 1; i < segments.Count; i++)
            {
                if (model.Nodes[n].Name.StartsWith(JointPrefix(i), StringComparison.Ordinal))
                {
                    nodeSegment[n] = i;
                }
            }
        }

        var rig = new RobotRig(model, RigSource.Mod, nodeSegment, Place(segments)) { Replacement = replacement };
        if (ais == null || segments.Count == 0)
        {
            return rig;
        }

        var names = model.Clips.Select(c => c.Name).ToArray();
        foreach (var anim in RobotAnims.Of(ais, segments[0].Type))
        {
            if (RobotClips.Match(anim.Name, names) is var c and >= 0)
            {
                var loop = (anim.Block.Flags & AisAnimBlock.Loop) != 0 ? ClipLoop.Repeat : ClipLoop.Once;
                rig._clips[anim.Block.Va] = (model.Clips[c], loop);
            }
        }

        return rig;
    }

    private static Segment[] Place(IReadOnlyList<EnemyPart> segments) =>
        [.. segments.Select(s => new Segment(s.Offset, Turn(s.RestYaw, s.RestPitch, s.Roll)))];

    private static int ParentOf(IReadOnlyList<EnemyPart> segments, int i)
    {
        for (var p = 0; p < i; p++)
        {
            if (ReferenceEquals(segments[p], segments[i].Parent))
            {
                return p;
            }
        }

        return -1;
    }

    // One model material per surface key, shared by every segment drawing it.
    private static int Material(AnimatedModelBuilder b, List<(SurfaceKey, GpuTexture?)> surfaces, Dictionary<SurfaceKey, int> materials,
        ModelSurface surface)
    {
        if (materials.TryGetValue(surface.Key, out var known))
        {
            return known;
        }

        var name = surface.Key.Texture.IsNone ? $"palette {surface.Key.FlatIndex}" : $"texture {surface.Key.Texture.Index}";
        materials[surface.Key] = known = b.AddMaterial(new GltfMaterial(name, Vector4.One, null, GltfAlphaMode.Opaque, 0.5f, false));
        surfaces.Add((surface.Key, surface.Texture));
        return known;
    }

    /// <summary>A model's rigid parts on <paramref name="node"/>: per texture word (frame 0's surfaces),
    /// every frame's positions, normals and texture coordinates as its shapes.</summary>
    private static List<AnimatedPart> Parts(EnemyModelInfo model, TextureStore textures, int node, Func<ModelSurface, int> material)
    {
        var frames = Enumerable.Range(0, model.Frames).Select(f => ModelMesh.Surfaces(model.Model, textures, f, smooth: true)).ToArray();
        var parts = new List<AnimatedPart>();
        for (var s = 0; s < frames[0].Count; s++)
        {
            var first = frames[0][s];
            var shapes = frames.Select(f => Shape(f[s])).ToArray();
            parts.Add(new AnimatedPart(model.File, material(first), first.Indices, shapes[0].Uvs, shapes) { Node = node });
        }

        return parts;
    }

    // The normals unpacked: packed again by the mesh, as a static batch packs them again when it places them.
    private static AnimatedShape Shape(ModelSurface s) =>
        new([.. s.Vertices.Select(v => v.Position)], [.. s.Vertices.Select(v => Vertex.UnpackNormal(v.Normal))])
        {
            Uvs = [.. s.Vertices.Select(v => v.Uv)],
        };
}
