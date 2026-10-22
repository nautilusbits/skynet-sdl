using System.Buffers.Binary;
using System.Numerics;
using System.Runtime.InteropServices;
using System.Text.Json;

namespace SkyNet.Edit;

/// <summary>
/// A small glTF 2.0 binary (<c>.glb</c>) writer (Khronos glTF 2.0 specification): one buffer, the
/// accessors, meshes with morph targets, nodes, a skin, materials with embedded PNG images and the
/// named animations (morph weights, joint translation and rotation) a <see cref="GltfExport"/> needs. AOT-safe (<see cref="Utf8JsonWriter"/>, no reflection).
/// <code>
///   GLB = header 12 B │ JSON chunk (space-padded) │ BIN chunk (zero-padded): views → accessors
/// </code></summary>
internal sealed class GlbWriter
{
    private const uint Magic = 0x46546C67, Version = 2, ChunkJson = 0x4E4F534A, ChunkBin = 0x004E4942;
    private const int HeaderSize = 12, ChunkHeaderSize = 8, Align = 4;
    // Component types and buffer-view targets (glTF 2.0, 3.6.2 and 5.11).
    private const int Float = 5126, UnsignedShort = 5123, UnsignedInt = 5125, ArrayBuffer = 34962, ElementArrayBuffer = 34963;
    // Sampler filters and wrap (glTF 2.0, 5.26): nearest keeps the DOS texels sharp.
    private const int Nearest = 9728, NearestMipmapNearest = 9984, Repeat = 10497;
    private const string Generator = "SkyNetEdit .3D export";

    /// <summary>A triangle list: accessors of positions, texture coordinates (null: untextured), indices, its material,
    /// morph targets and, when skinned, its joints and weights.</summary>
    public sealed record Primitive(int Positions, int? Uvs, int Indices, int Material, IReadOnlyList<int> Targets,
        int? Joints = null, int? Weights = null);

    /// <summary>What an animation channel drives (glTF 2.0, 5.5.1).</summary>
    public enum ChannelPath { Weights, Translation, Rotation }

    /// <summary>How a sampler goes between keys: STEP shows a DOS frame whole, LINEAR moves a joint.</summary>
    public enum Interpolation { Step, Linear }

    public enum ViewTarget { None, Vertices }

    private sealed record Channel(int Node, ChannelPath Path, int Input, int Output, Interpolation Interpolation);

    private sealed record MeshDef(string Name, IReadOnlyList<Primitive> Primitives, IReadOnlyList<string> TargetNames);

    private sealed record NodeDef(string Name, int? Mesh, Vector3 Translation, Quaternion Rotation, int? Skin)
    {
        public List<int> Children { get; } = [];
    }

    private readonly MemoryStream _bin = new();
    private readonly List<Action<Utf8JsonWriter>> _views = [], _accessors = [], _materials = [], _images = [], _textures = [];
    private readonly List<MeshDef> _meshes = [];
    private readonly List<NodeDef> _nodes = [];
    private readonly List<Action<Utf8JsonWriter>> _skins = [];
    // The animations by name, in the order they were begun.
    private readonly List<(string Name, List<Channel> Channels)> _animations = [];

    public int MeshCount => _meshes.Count;

    // ---- data ----

    /// <summary>Vec3 floats with their box: vertex positions and morph deltas (glTF requires the box for both), or,
    /// <paramref name="target"/> none, translation keys.</summary>
    public int Vec3s(ReadOnlySpan<Vector3> values, ViewTarget target = ViewTarget.Vertices)
    {
        var (min, max) = (new Vector3(float.MaxValue), new Vector3(float.MinValue));
        foreach (var v in values)
        {
            (min, max) = (Vector3.Min(min, v), Vector3.Max(max, v));
        }

        var view = View(MemoryMarshal.AsBytes(values), target == ViewTarget.Vertices ? ArrayBuffer : null);
        return Accessor(view, Float, values.Length, "VEC3", [min.X, min.Y, min.Z], [max.X, max.Y, max.Z]);
    }

    public int Vec2s(ReadOnlySpan<Vector2> values) => Accessor(View(MemoryMarshal.AsBytes(values), ArrayBuffer), Float, values.Length, "VEC2");

    public int Indices(ReadOnlySpan<uint> values) => Accessor(View(MemoryMarshal.AsBytes(values), ElementArrayBuffer), UnsignedInt, values.Length, "SCALAR");

    /// <summary><paramref name="count"/> zero vec3s without data (glTF: an accessor with no buffer view is
    /// zeros): a morph target that leaves its primitive where it is.</summary>
    public int Zeros3(int count) => Accessor(null, Float, count, "VEC3", [0f, 0f, 0f], [0f, 0f, 0f]);

    /// <summary>Four joint indices per vertex (JOINTS_0).</summary>
    public int Joints4(ReadOnlySpan<ushort> values) =>
        Accessor(View(MemoryMarshal.AsBytes(values), ArrayBuffer), UnsignedShort, values.Length / 4, "VEC4");

    /// <summary>Vec4 floats: skin weights (WEIGHTS_0, <paramref name="target"/> the vertices) or rotation keys.</summary>
    public int Vec4s(ReadOnlySpan<Vector4> values, ViewTarget target) =>
        Accessor(View(MemoryMarshal.AsBytes(values), target == ViewTarget.Vertices ? ArrayBuffer : null), Float, values.Length, "VEC4");

    /// <summary>Matrices (inverse bind). System.Numerics' row-vector memory order is glTF's column-major one.</summary>
    public int Mat4s(ReadOnlySpan<Matrix4x4> values) => Accessor(View(MemoryMarshal.AsBytes(values), null), Float, values.Length, "MAT4");

    /// <summary>Scalars (animation times and weights); <paramref name="bounds"/>: with min and max (times need them).</summary>
    public int Scalars(ReadOnlySpan<float> values, Bounds bounds)
    {
        var view = View(MemoryMarshal.AsBytes(values), null);
        if (bounds == Bounds.None || values.IsEmpty)
        {
            return Accessor(view, Float, values.Length, "SCALAR");
        }

        var (min, max) = (float.MaxValue, float.MinValue);
        foreach (var v in values)
        {
            (min, max) = (MathF.Min(min, v), MathF.Max(max, v));
        }

        return Accessor(view, Float, values.Length, "SCALAR", [min], [max]);
    }

    public enum Bounds { None, MinMax }

    // ---- materials ----

    /// <summary>A material: the base colour (linear) times the PNG's colours when one is given; not
    /// metallic, fully rough (glTF's defaults would make it metal).</summary>
    public int Material(string name, Vector4 colour, byte[]? png)
    {
        int? texture = png != null ? Texture(png) : null;
        _materials.Add(w =>
        {
            w.WriteString("name", name);
            w.WriteStartObject("pbrMetallicRoughness");
            WriteFloats(w, "baseColorFactor", [colour.X, colour.Y, colour.Z, colour.W]);
            if (texture is { } t)
            {
                w.WriteStartObject("baseColorTexture");
                w.WriteNumber("index", t);
                w.WriteEndObject();
            }

            w.WriteNumber("metallicFactor", 0);
            w.WriteNumber("roughnessFactor", 1);
            w.WriteEndObject();
        });
        return _materials.Count - 1;
    }

    private int Texture(byte[] png)
    {
        var view = View(png, null);
        _images.Add(w =>
        {
            w.WriteNumber("bufferView", view);
            w.WriteString("mimeType", "image/png");
        });
        var image = _images.Count - 1;
        _textures.Add(w =>
        {
            w.WriteNumber("sampler", 0);
            w.WriteNumber("source", image);
        });
        return _textures.Count - 1;
    }

    // ---- scene ----

    /// <summary>A mesh; every primitive has as many morph targets as <paramref name="targetNames"/> (one per extra frame).</summary>
    public int Mesh(string name, IReadOnlyList<Primitive> primitives, IReadOnlyList<string> targetNames)
    {
        _meshes.Add(new MeshDef(name, primitives, targetNames));
        return _meshes.Count - 1;
    }

    /// <summary>A node, under <paramref name="parent"/> when given (else a scene root); its mesh drawn with <paramref name="skin"/> when given.</summary>
    public int Node(string name, int? mesh, int? parent = null, Vector3 translation = default, Quaternion? rotation = null, int? skin = null)
    {
        _nodes.Add(new NodeDef(name, mesh, translation, rotation ?? Quaternion.Identity, skin));
        var index = _nodes.Count - 1;
        if (parent is { } p)
        {
            _nodes[p].Children.Add(index);
        }

        return index;
    }

    /// <summary>A skin of <paramref name="joints"/> (nodes; the first is the skeleton's root) and their inverse bind matrices.</summary>
    public int Skin(string name, IReadOnlyList<int> joints, int inverseBinds)
    {
        _skins.Add(w =>
        {
            w.WriteString("name", name);
            w.WriteNumber("inverseBindMatrices", inverseBinds);
            w.WriteNumber("skeleton", joints[0]);
            WriteInts(w, "joints", joints);
        });
        return _skins.Count - 1;
    }

    /// <summary>A channel of animation <paramref name="animation"/> (begun by its first channel): the node's
    /// <paramref name="path"/> over the time accessor.</summary>
    public void Animate(string animation, int node, ChannelPath path, int times, int values, Interpolation interpolation)
    {
        var at = _animations.FindIndex(a => a.Name == animation);
        if (at < 0)
        {
            _animations.Add((animation, []));
            at = _animations.Count - 1;
        }

        _animations[at].Channels.Add(new Channel(node, path, times, values, interpolation));
    }

    // ---- file ----

    /// <summary>The <c>.glb</c>: the nodes without a parent make the one scene.</summary>
    public byte[] Build()
    {
        var json = Json();
        var jsonLength = Padded(json.Length);
        var binLength = Padded((int)_bin.Length);
        var total = HeaderSize + ChunkHeaderSize + jsonLength + (binLength > 0 ? ChunkHeaderSize + binLength : 0);
        var glb = new byte[total];
        var span = glb.AsSpan();
        BinaryPrimitives.WriteUInt32LittleEndian(span, Magic);
        BinaryPrimitives.WriteUInt32LittleEndian(span[4..], Version);
        BinaryPrimitives.WriteUInt32LittleEndian(span[8..], (uint)total);

        // The JSON chunk is padded with spaces, the binary one with zeros.
        var at = HeaderSize;
        BinaryPrimitives.WriteUInt32LittleEndian(span[at..], (uint)jsonLength);
        BinaryPrimitives.WriteUInt32LittleEndian(span[(at + 4)..], ChunkJson);
        at += ChunkHeaderSize;
        json.CopyTo(span[at..]);
        span.Slice(at + json.Length, jsonLength - json.Length).Fill((byte)' ');
        at += jsonLength;
        if (binLength == 0)
        {
            return glb;
        }

        BinaryPrimitives.WriteUInt32LittleEndian(span[at..], (uint)binLength);
        BinaryPrimitives.WriteUInt32LittleEndian(span[(at + 4)..], ChunkBin);
        _bin.GetBuffer().AsSpan(0, (int)_bin.Length).CopyTo(span[(at + ChunkHeaderSize)..]);
        return glb;
    }

    private static int Padded(int length) => (length + Align - 1) & ~(Align - 1);

    private int View(ReadOnlySpan<byte> bytes, int? target)
    {
        var offset = (int)_bin.Length;
        _bin.Write(bytes);
        while (_bin.Length % Align != 0)
        {
            _bin.WriteByte(0);
        }

        var length = bytes.Length;
        _views.Add(w =>
        {
            w.WriteNumber("buffer", 0);
            w.WriteNumber("byteOffset", offset);
            w.WriteNumber("byteLength", length);
            if (target is { } t)
            {
                w.WriteNumber("target", t);
            }
        });
        return _views.Count - 1;
    }

    private int Accessor(int? view, int component, int count, string type, float[]? min = null, float[]? max = null)
    {
        _accessors.Add(w =>
        {
            if (view is { } v)
            {
                w.WriteNumber("bufferView", v);
            }

            w.WriteNumber("componentType", component);
            w.WriteNumber("count", count);
            w.WriteString("type", type);
            if (min != null && max != null)
            {
                WriteFloats(w, "min", min);
                WriteFloats(w, "max", max);
            }
        });
        return _accessors.Count - 1;
    }

    private byte[] Json()
    {
        using var stream = new MemoryStream();
        using (var w = new Utf8JsonWriter(stream))
        {
            w.WriteStartObject();
            w.WriteStartObject("asset");
            w.WriteString("version", "2.0");
            w.WriteString("generator", Generator);
            w.WriteEndObject();

            w.WriteNumber("scene", 0);
            w.WriteStartArray("scenes");
            w.WriteStartObject();
            var children = _nodes.SelectMany(n => n.Children).ToHashSet();
            WriteInts(w, "nodes", Enumerable.Range(0, _nodes.Count).Where(i => !children.Contains(i)));
            w.WriteEndObject();
            w.WriteEndArray();

            w.WriteStartArray("nodes");
            foreach (var n in _nodes)
            {
                WriteNode(w, n);
            }

            w.WriteEndArray();
            w.WriteStartArray("meshes");
            foreach (var m in _meshes)
            {
                WriteMesh(w, m);
            }

            w.WriteEndArray();
            Objects(w, "skins", _skins);
            WriteAnimations(w);
            Objects(w, "materials", _materials);
            Objects(w, "textures", _textures);
            Objects(w, "images", _images);
            if (_textures.Count > 0)
            {
                w.WriteStartArray("samplers");
                w.WriteStartObject();
                w.WriteNumber("magFilter", Nearest);
                w.WriteNumber("minFilter", NearestMipmapNearest);
                w.WriteNumber("wrapS", Repeat);
                w.WriteNumber("wrapT", Repeat);
                w.WriteEndObject();
                w.WriteEndArray();
            }

            Objects(w, "accessors", _accessors);
            Objects(w, "bufferViews", _views);
            if (_bin.Length > 0)
            {
                w.WriteStartArray("buffers");
                w.WriteStartObject();
                w.WriteNumber("byteLength", Padded((int)_bin.Length));
                w.WriteEndObject();
                w.WriteEndArray();
            }

            w.WriteEndObject();
        }

        return stream.ToArray();
    }

    private static void WriteNode(Utf8JsonWriter w, NodeDef n)
    {
        w.WriteStartObject();
        w.WriteString("name", n.Name);
        if (n.Mesh is { } mesh)
        {
            w.WriteNumber("mesh", mesh);
        }

        if (n.Skin is { } skin)
        {
            w.WriteNumber("skin", skin);
        }

        if (n.Translation != Vector3.Zero)
        {
            WriteFloats(w, "translation", [n.Translation.X, n.Translation.Y, n.Translation.Z]);
        }

        if (n.Rotation != Quaternion.Identity)
        {
            var q = Quaternion.Normalize(n.Rotation);
            WriteFloats(w, "rotation", [q.X, q.Y, q.Z, q.W]);
        }

        if (n.Children.Count > 0)
        {
            WriteInts(w, "children", n.Children);
        }

        w.WriteEndObject();
    }

    // Morph target names go in extras.targetNames (what Blender names the shape keys by).
    private static void WriteMesh(Utf8JsonWriter w, MeshDef m)
    {
        w.WriteStartObject();
        w.WriteString("name", m.Name);
        w.WriteStartArray("primitives");
        foreach (var p in m.Primitives)
        {
            w.WriteStartObject();
            w.WriteStartObject("attributes");
            w.WriteNumber("POSITION", p.Positions);
            if (p.Uvs is { } uvs)
            {
                w.WriteNumber("TEXCOORD_0", uvs);
            }

            if (p.Joints is { } joints && p.Weights is { } weights)
            {
                w.WriteNumber("JOINTS_0", joints);
                w.WriteNumber("WEIGHTS_0", weights);
            }

            w.WriteEndObject();
            w.WriteNumber("indices", p.Indices);
            w.WriteNumber("material", p.Material);
            if (p.Targets.Count > 0)
            {
                w.WriteStartArray("targets");
                foreach (var t in p.Targets)
                {
                    w.WriteStartObject();
                    w.WriteNumber("POSITION", t);
                    w.WriteEndObject();
                }

                w.WriteEndArray();
            }

            w.WriteEndObject();
        }

        w.WriteEndArray();
        if (m.TargetNames.Count > 0)
        {
            WriteFloats(w, "weights", new float[m.TargetNames.Count]);
            w.WriteStartObject("extras");
            w.WriteStartArray("targetNames");
            foreach (var name in m.TargetNames)
            {
                w.WriteStringValue(name);
            }

            w.WriteEndArray();
            w.WriteEndObject();
        }

        w.WriteEndObject();
    }

    // The animations, a sampler per channel.
    private void WriteAnimations(Utf8JsonWriter w)
    {
        if (_animations.Count == 0)
        {
            return;
        }

        w.WriteStartArray("animations");
        foreach (var (name, channels) in _animations)
        {
            w.WriteStartObject();
            w.WriteString("name", name);
            w.WriteStartArray("samplers");
            foreach (var c in channels)
            {
                w.WriteStartObject();
                w.WriteNumber("input", c.Input);
                w.WriteNumber("output", c.Output);
                w.WriteString("interpolation", c.Interpolation == Interpolation.Step ? "STEP" : "LINEAR");
                w.WriteEndObject();
            }

            w.WriteEndArray();
            w.WriteStartArray("channels");
            for (var i = 0; i < channels.Count; i++)
            {
                w.WriteStartObject();
                w.WriteNumber("sampler", i);
                w.WriteStartObject("target");
                w.WriteNumber("node", channels[i].Node);
                w.WriteString("path", channels[i].Path switch
                {
                    ChannelPath.Weights => "weights",
                    ChannelPath.Translation => "translation",
                    _ => "rotation",
                });
                w.WriteEndObject();
                w.WriteEndObject();
            }

            w.WriteEndArray();
            w.WriteEndObject();
        }

        w.WriteEndArray();
    }

    private static void Objects(Utf8JsonWriter w, string name, List<Action<Utf8JsonWriter>> items)
    {
        if (items.Count == 0)
        {
            return;
        }

        w.WriteStartArray(name);
        foreach (var write in items)
        {
            w.WriteStartObject();
            write(w);
            w.WriteEndObject();
        }

        w.WriteEndArray();
    }

    private static void WriteFloats(Utf8JsonWriter w, string name, IEnumerable<float> values)
    {
        w.WriteStartArray(name);
        foreach (var v in values)
        {
            w.WriteNumberValue(v);
        }

        w.WriteEndArray();
    }

    private static void WriteInts(Utf8JsonWriter w, string name, IEnumerable<int> values)
    {
        w.WriteStartArray(name);
        foreach (var v in values)
        {
            w.WriteNumberValue(v);
        }

        w.WriteEndArray();
    }
}
