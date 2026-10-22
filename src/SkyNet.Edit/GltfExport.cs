using System.Numerics;
using RetroEng.Diagnostics;
using SkyNet.Data;
using SkyNet.Formats;
using SkyNet.Game;
using SkyNet.Upscale;

namespace SkyNet.Edit;

/// <summary>Whether a robot's export carries the segments its enemy-table row hangs on it.</summary>
public enum RobotSegments { Without, With }

/// <summary>
/// DOS <c>.3D</c> models to glTF 2.0 binaries (editor-tools plan, T2), for modders to open in Blender
/// and make a <c>models/NAME.glb</c> replacement (docs/modding.md, Models).
/// <code>
///   NAME.3D ─► mesh: one primitive per texture word (faces unwelded, fans counter-clockwise, Y up,
///              world units) ─► material: TEXTURE.NNN record as PNG (palette, index 0 alpha 0) or a
///              flat palette colour
///   frames 1..N-1 ─► morph targets (position deltas from frame 0) ─► animation "frames": STEP
///              weights, frame k shown at k / FramesPerSecond s (Blender: shape keys + an action)
/// </code>
/// A robot with segments is also written as NAME.robot.glb, the template of a one-file skinned robot:
/// <code>
///   segments (depth first, root first, depth ≤ 4) ─► joints "seg&lt;i&gt;_&lt;MODEL&gt;", rest pose = offset and
///              yaw·pitch·roll as Enemy places them
///   every segment's faces ─► one mesh "NAME", placed at rest, each vertex skinned to its segment (weight 1)
///   root frames 1..N-1 ─► morph targets (other segments' targets are zeros: DOS animates the root only)
///   AIS blocks ─► clips named as the Model Viewer names them (walk, loop, fire 2, once 1...): STEP morph
///              weights at the block's fps, every joint keyed at its rest pose (for the modder to replace)
/// </code>
/// UVs are frame 0's (glTF morphs move positions only). The material is opaque, as DOS draws index 0
/// of a model's texture (the haze colour); the PNG keeps index 0 as alpha 0 for the modder.
/// </summary>
public sealed class GltfExport(GameData data, AisData? ais)
{
    public const string Extension = ".glb";
    /// <summary>The file of a robot with its segments: NAME.robot.glb beside NAME.glb.</summary>
    public const string RobotSuffix = SkyNet.Game.Mods.RobotReplacement.Suffix;
    public const string AnimationName = "frames";
    /// <summary>The timeline's rate: DOS frames are stepped by the AIS scripts, not by a clock.</summary>
    public const float FramesPerSecond = 10f;
    private const string ModelExtension = ".3D";
    /// <summary>Enemy.AddSegments's depth limit.</summary>
    private const int MaxSegmentDepth = 4;

    private readonly GameData _data = data;
    private readonly AisData? _ais = ais;
    private readonly TextureStore _textures = new(null, data);
    private Palette? _palette;

    private Palette Palette => _palette ??= _data.LoadPalette() ?? throw new InvalidDataException("no game palette");

    /// <summary>--export-gltf's folder when --export-dir is not given (under the current folder).</summary>
    public const string DefaultDir = "gltf";
    /// <summary>--export-gltf's word for every model.</summary>
    public const string All = "all";

    /// <summary>--export-gltf: model <paramref name="which"/> (or <see cref="All"/>) into <paramref name="dir"/>;
    /// 0 when every one was written, 1 otherwise (each failure logged).</summary>
    public static int Run(GameData data, string which, string dir)
    {
        var export = new GltfExport(data, GameTables.Ais);
        var every = which.Equals(All, StringComparison.OrdinalIgnoreCase);
        // Both archives may hold a name: the game loads one, so it is written once.
        var names = every
            ? new GameAssets(data).Enumerate(GameAssets.Models).Select(e => e.Id).Distinct(StringComparer.OrdinalIgnoreCase).ToArray()
            : [which];
        var failed = 0;
        foreach (var name in names)
        {
            var written = export.Write(name, dir);
            if (written.Count > 0)
            {
                Log.Info("Export", string.Join(", ", written));
                continue;
            }

            // A few archive models do not parse for the game either (all: skipped; one asked for: an error).
            failed++;
            if (every)
            {
                Log.Warn("Export", $"{name}{ModelExtension}: does not load, skipped");
                continue;
            }

            Log.Error("Export", $"{name}{ModelExtension}: not found or does not load");
        }

        Log.Info("Export", $"{names.Length - failed} of {names.Length} model(s) written to {Path.GetFullPath(dir)}");
        return failed == 0 || every ? 0 : 1;
    }

    /// <summary>The file name an export is written under.</summary>
    public static string FileName(string name, RobotSegments segments) =>
        name + (segments == RobotSegments.With ? RobotSuffix : "") + Extension;

    /// <summary>The robot type whose root model is <paramref name="name"/>: the first with segments, else the first
    /// whose script plays blocks (a walker); null: none.</summary>
    public EnemyType? Robot(string name)
    {
        var built = _ais?.Types.Where(t => ModelName(t.ModelFile).Equals(name, StringComparison.OrdinalIgnoreCase)).ToArray() ?? [];
        return built.FirstOrDefault(t => t.Segments.Length > 0) ?? built.FirstOrDefault(t => ModelSubject.AnimsOf(_ais!, t).Count > 0);
    }

    /// <summary>Writes NAME.glb into <paramref name="dir"/>, and NAME.robot.glb when a robot is built
    /// on it with segments; returns the paths written (none: the model does not load).</summary>
    public IReadOnlyList<string> Write(string name, string dir)
    {
        var written = new List<string>();
        Directory.CreateDirectory(dir);
        foreach (var segments in Robot(name) != null ? [RobotSegments.Without, RobotSegments.With] : new[] { RobotSegments.Without })
        {
            if (Export(name, segments) is not { } glb)
            {
                continue;
            }

            var path = Path.Combine(dir, FileName(name, segments));
            File.WriteAllBytes(path, glb);
            written.Add(path);
        }

        return written;
    }

    /// <summary>The model <paramref name="name"/> (without .3D) as a .glb; null when it does not load.</summary>
    public byte[]? Export(string name, RobotSegments segments)
    {
        if (_data.LoadModel(name + ModelExtension) is not { } model)
        {
            return null;
        }

        var job = new Job(this, new GlbWriter());
        if (segments == RobotSegments.With && Robot(name) is { } robot)
        {
            job.Rig(name, model, robot);
            return job.Glb.Build();
        }

        var root = job.Glb.Node(name, job.Mesh(name, model));
        job.Animate(root, model);
        return job.Glb.Build();
    }

    /// <summary>Robot type <paramref name="robot"/> as its template (NAME.robot.glb), whatever segments it has; null when its model does not load.</summary>
    public byte[]? Export(EnemyType robot)
    {
        var name = ModelName(robot.ModelFile);
        if (_data.LoadModel(robot.ModelFile) is not { } model)
        {
            return null;
        }

        var job = new Job(this, new GlbWriter());
        job.Rig(name, model, robot);
        return job.Glb.Build();
    }

    /// <summary>A robot joint's name: its depth-first segment index (root 0) and the segment's model in capitals ("seg2_HVYPLSM").</summary>
    public static string JointName(int index, string model) => SkyNet.Game.Enemies.RobotRig.JointName(index, model);

    private static string ModelName(string file) =>
        file.EndsWith(ModelExtension, StringComparison.OrdinalIgnoreCase) ? file[..^ModelExtension.Length] : file;

    /// <summary>A robot segment as a skin joint: name, model, parent joint (−1: none), rest pose and its placement in the robot.</summary>
    private sealed record Joint(string Name, Model3D? Model, int Parent, Vector3 Offset, Quaternion Turn, Matrix4x4 World);

    /// <summary>The robot's segments as joints, depth first, the root (model <paramref name="name"/>) first.</summary>
    private List<Joint> Joints(EnemyType robot, string name, Model3D model)
    {
        var joints = new List<Joint> { new(JointName(0, name), model, -1, Vector3.Zero, Quaternion.Identity, Matrix4x4.Identity) };
        AddJoints(robot, 0, 0, joints);
        return joints;
    }

    // Placed as Enemy.AddSegments places them: offset (x, −y, −z) and Ry(yaw)·Rx(pitch)·Rz(−roll), for row vectors.
    private void AddJoints(EnemyType type, int parent, int depth, List<Joint> joints)
    {
        if (depth >= MaxSegmentDepth || _ais is not { } ais)
        {
            return;
        }

        foreach (var seg in type.Segments)
        {
            if (seg.ChildType < 0 || seg.ChildType >= ais.Types.Length)
            {
                continue;
            }

            var child = ais.Types[seg.ChildType];
            var turn = Matrix4x4.CreateRotationY((float)MapFile.AngleRadians(seg.Yaw))
                * Matrix4x4.CreateRotationX((float)MapFile.AngleRadians(seg.Pitch))
                * Matrix4x4.CreateRotationZ(-(float)MapFile.AngleRadians(seg.Roll));
            var offset = new Vector3(seg.X, -seg.Y, -seg.Z);
            var world = turn * Matrix4x4.CreateTranslation(offset) * joints[parent].World;
            var model = child.ModelFile.Length > 0 ? ModelName(child.ModelFile) : child.Name;
            joints.Add(new Joint(JointName(joints.Count, model), _data.LoadModel(child.ModelFile), parent, offset,
                Quaternion.CreateFromRotationMatrix(turn), world));
            AddJoints(child, joints.Count - 1, depth + 1, joints);
        }
    }

    private enum Texturing { Flat, Image }

    /// <summary>A texture word's material and, when textured, the image's size (its UVs' scale).</summary>
    private readonly record struct Surface(int Material, Texturing Textured, int Width, int Height);

    /// <summary>A skinned primitive's joint, the joint's rest placement and the mesh's morph target count.</summary>
    private readonly record struct Skinning(int Joint, Matrix4x4 World, int Targets);

    /// <summary>One file's build: its writer, materials by texture word and meshes by model name.</summary>
    private sealed class Job(GltfExport owner, GlbWriter glb)
    {
        private readonly Dictionary<int, Surface> _materials = [];
        private readonly Dictionary<string, int> _meshes = new(StringComparer.OrdinalIgnoreCase);
        private readonly Dictionary<int, int> _times = [];

        public GlbWriter Glb => glb;

        /// <summary>The model's mesh (made once per file): a primitive per texture word, a morph target per extra frame.</summary>
        public int Mesh(string name, Model3D model)
        {
            if (_meshes.TryGetValue(name, out var known))
            {
                return known;
            }

            var primitives = new List<GlbWriter.Primitive>();
            foreach (var group in model.Faces.GroupBy(f => f.Texture).OrderBy(g => g.Key))
            {
                primitives.Add(Primitive(model, [.. group], Material(group.Key)));
            }

            var targets = Enumerable.Range(1, model.Frames.Length - 1).Select(k => $"frame {k}").ToArray();
            var mesh = glb.Mesh(name, primitives, targets);
            _meshes[name] = mesh;
            return mesh;
        }

        /// <summary>The robot as one skinned mesh on a skeleton of its segments, with a clip per AIS block.</summary>
        public void Rig(string name, Model3D model, EnemyType robot)
        {
            var joints = owner.Joints(robot, name, model);
            var nodes = new int[joints.Count];
            for (var i = 0; i < joints.Count; i++)
            {
                var j = joints[i];
                nodes[i] = glb.Node(j.Name, null, j.Parent >= 0 ? nodes[j.Parent] : null, j.Offset, j.Turn);
            }

            // A vertex goes from the robot's space into its joint's: the inverse of the joint's rest placement.
            var binds = joints.Select(j => Matrix4x4.Invert(j.World, out var inverse) ? inverse : Matrix4x4.Identity).ToArray();
            var skin = glb.Skin(name, nodes, glb.Mat4s(binds));

            // Every segment's faces, placed at rest; the root's frames are the mesh's morph targets.
            var targets = model.Frames.Length - 1;
            var primitives = new List<GlbWriter.Primitive>();
            for (var i = 0; i < joints.Count; i++)
            {
                if (joints[i].Model is not { } m)
                {
                    continue;
                }

                foreach (var group in m.Faces.GroupBy(f => f.Texture).OrderBy(g => g.Key))
                {
                    primitives.Add(Primitive(m, [.. group], Material(group.Key), new Skinning(i, joints[i].World, targets)));
                }
            }

            var mesh = glb.Mesh(name, primitives, [.. Enumerable.Range(1, targets).Select(k => $"frame {k}")]);
            var body = glb.Node(name, mesh, skin: skin);
            Clips(robot, model, body, nodes, joints);
        }

        // A clip per AIS block the robot's script starts; without one, every frame in order (as NAME.glb).
        private void Clips(EnemyType robot, Model3D model, int body, int[] nodes, List<Joint> joints)
        {
            var frames = model.Frames.Length;
            var anims = owner._ais is { } ais ? ModelSubject.AnimsOf(ais, robot) : [];
            if (anims.Count == 0 && frames > 1)
            {
                Clip(AnimationName, [.. Enumerable.Range(0, frames)], FramesPerSecond, frames - 1, body, nodes, joints);
                return;
            }

            foreach (var a in anims)
            {
                var shown = a.Block.Frames.Select(f => Math.Min((int)f.Frame, frames - 1)).ToArray();
                Clip(a.Name, shown, a.Block.Fps > 0 ? a.Block.Fps : FramesPerSecond, frames - 1, body, nodes, joints);
            }
        }

        /// <summary>Clip <paramref name="clip"/>: the root shows frame shown[k] from k / fps s (STEP morph weights,
        /// the last held to the end, so the clip lasts shown.Length / fps); every joint keyed at its rest pose.</summary>
        private void Clip(string clip, int[] shown, float fps, int targets, int body, int[] nodes, List<Joint> joints)
        {
            if (shown.Length == 0)
            {
                return;
            }

            var end = shown.Length / fps;
            if (targets > 0)
            {
                var keys = shown.Length + 1;
                var weights = new float[keys * targets];
                for (var k = 0; k < keys; k++)
                {
                    var frame = shown[Math.Min(k, shown.Length - 1)];
                    if (frame > 0)
                    {
                        weights[k * targets + frame - 1] = 1f;
                    }
                }

                var times = glb.Scalars([.. Enumerable.Range(0, keys).Select(k => k / fps)], GlbWriter.Bounds.MinMax);
                glb.Animate(clip, body, GlbWriter.ChannelPath.Weights, times, glb.Scalars(weights, GlbWriter.Bounds.None),
                    GlbWriter.Interpolation.Step);
            }

            // DOS moves no segment in a block (turrets aim at run time): the rest pose, a track for the modder to key.
            var span = glb.Scalars([0f, end], GlbWriter.Bounds.MinMax);
            for (var i = 0; i < joints.Count; i++)
            {
                var (offset, turn) = (joints[i].Offset, joints[i].Turn);
                glb.Animate(clip, nodes[i], GlbWriter.ChannelPath.Translation, span, glb.Vec3s([offset, offset], GlbWriter.ViewTarget.None),
                    GlbWriter.Interpolation.Linear);
                var q = new Vector4(turn.X, turn.Y, turn.Z, turn.W);
                glb.Animate(clip, nodes[i], GlbWriter.ChannelPath.Rotation, span, glb.Vec4s([q, q], GlbWriter.ViewTarget.None),
                    GlbWriter.Interpolation.Linear);
            }
        }

        // The faces' corners unwelded (each face its own UVs), frame 0 as the base, later frames as deltas. Skinned: placed
        // at the segment's rest, bound to its joint alone; a segment other than the root gets zero targets.
        private GlbWriter.Primitive Primitive(Model3D model, List<ModelFace> faces, Surface surface, Skinning? skinning = null)
        {
            var corners = faces.SelectMany(f => f.Indices).ToArray();
            var indices = new List<uint>();
            var at = 0u;
            foreach (var face in faces)
            {
                foreach (var (a, b, c) in Model3D.TriangleFan(face))
                {
                    indices.AddRange([at + (uint)a, at + (uint)b, at + (uint)c]);
                }

                at += (uint)face.VertexCount;
            }

            var world = skinning?.World;
            var frame0 = Positions(model.Frames[0], corners, world);
            List<int> targets = skinning is not { Joint: > 0 } ? Deltas(model, corners, frame0, world)
                : skinning is { Targets: > 0 } still ? [.. Enumerable.Repeat(glb.Zeros3(frame0.Length), still.Targets)]
                : [];

            // A flat colour has no texture to map: no UVs (they would be unused).
            int? uvs = surface.Textured == Texturing.Image
                ? glb.Vec2s([.. faces.SelectMany(f => model.FaceUvs(f, surface.Width, surface.Height))])
                : null;
            var positions = glb.Vec3s(frame0);
            var index = glb.Indices([.. indices]);
            if (skinning is not { } s)
            {
                return new GlbWriter.Primitive(positions, uvs, index, surface.Material, targets);
            }

            var joints = new ushort[frame0.Length * 4];
            for (var i = 0; i < frame0.Length; i++)
            {
                joints[i * 4] = (ushort)s.Joint;
            }

            var weights = glb.Vec4s([.. Enumerable.Repeat(Vector4.UnitX, frame0.Length)], GlbWriter.ViewTarget.Vertices);
            return new GlbWriter.Primitive(positions, uvs, index, surface.Material, targets, glb.Joints4(joints), weights);
        }

        // Frames 1..N-1 as morph targets: each frame's move from frame 0.
        private List<int> Deltas(Model3D model, int[] corners, Vector3[] frame0, Matrix4x4? world)
        {
            var targets = new List<int>();
            for (var k = 1; k < model.Frames.Length; k++)
            {
                var frame = Positions(model.Frames[k], corners, world);
                for (var i = 0; i < frame.Length; i++)
                {
                    frame[i] -= frame0[i];
                }

                targets.Add(glb.Vec3s(frame));
            }

            return targets;
        }

        private static Vector3[] Positions(IReadOnlyList<ModelVertex> vertices, int[] corners, Matrix4x4? world) =>
            [.. corners.Select(i => world is { } w ? Vector3.Transform(vertices[i].ToYUp(), w) : vertices[i].ToYUp())];

        /// <summary>A texture word's material, as ModelMesh.Surfaces draws it: words ≤ 0xFF a flat palette
        /// colour, others the TEXTURE.&lt;bank&gt; record (missing: the haze colour).</summary>
        private Surface Material(int word)
        {
            if (_materials.TryGetValue(word, out var known))
            {
                return known;
            }

            var (bank, record) = (word >> 7, word & 0x7F);
            var r = owner._textures.Record(bank, record);
            var made = word <= 0xFF || r == null
                ? new Surface(glb.Material(word <= 0xFF ? $"palette {word:D3}" : $"TEXTURE.{bank:D3} #{record:D3} (missing)",
                    owner.Linear(word <= 0xFF && r != null ? r.Pixels[0] : HazeTable.HazeIndex), null), Texturing.Flat, 0, 0)
                : new Surface(glb.Material($"TEXTURE.{bank:D3} #{record:D3}", Vector4.One, owner.Png(r)), Texturing.Image, r.Width, r.Height);
            _materials[word] = made;
            return made;
        }

        /// <summary>The model's frames as one STEP channel of morph weights: at frame k, target k−1 is 1.</summary>
        public void Animate(int node, Model3D model)
        {
            var frames = model.Frames.Length;
            if (frames < 2)
            {
                return;
            }

            if (!_times.TryGetValue(frames, out var times))
            {
                times = glb.Scalars([.. Enumerable.Range(0, frames).Select(k => k / FramesPerSecond)], GlbWriter.Bounds.MinMax);
                _times[frames] = times;
            }

            var targets = frames - 1;
            var weights = new float[frames * targets];
            for (var k = 1; k < frames; k++)
            {
                weights[k * targets + k - 1] = 1f;
            }

            glb.Animate(AnimationName, node, GlbWriter.ChannelPath.Weights, times, glb.Scalars(weights, GlbWriter.Bounds.None),
                GlbWriter.Interpolation.Step);
        }
    }

    /// <summary>A record as an RGBA PNG: palette colours, index 0 the haze colour at alpha 0 (as an Assets override writes it).</summary>
    private byte[] Png(TextureRecord r)
    {
        var rgb = Palette.Rgb;
        var rgba = new byte[r.Width * r.Height * 4];
        for (var i = 0; i < r.Width * r.Height && i < r.Pixels.Length; i++)
        {
            int index = r.Pixels[i], colour = index == 0 ? HazeTable.HazeIndex : index;
            rgba[i * 4] = rgb[colour * 3];
            rgba[i * 4 + 1] = rgb[colour * 3 + 1];
            rgba[i * 4 + 2] = rgb[colour * 3 + 2];
            rgba[i * 4 + 3] = index == 0 ? (byte)0 : (byte)255;
        }

        return PngWriter.Encode(r.Width, r.Height, 4, rgba);
    }

    /// <summary>A palette colour as a linear glTF base colour (the palette is sRGB).</summary>
    private Vector4 Linear(int index)
    {
        var (r, g, b) = Palette[index];
        static float ToLinear(byte c) => (c / 255f) <= 0.04045f ? c / 255f / 12.92f : MathF.Pow((c / 255f + 0.055f) / 1.055f, 2.4f);
        return new Vector4(ToLinear(r), ToLinear(g), ToLinear(b), 1f);
    }
}
