using System.Numerics;
using System.Runtime.InteropServices;
using System.Text;
using RetroEng.Physics;
using RetroEng.Render;
using SkyNet.Game.Enhanced;

namespace SkyNet.Game.Baking;

/// <summary>A model-surface bucket's key in a block: a texture record, or a flat palette index.</summary>
public readonly record struct BlockKey(TextureRef? Texture, int Flat);

/// <summary>A run of a bucket's indices that lies in one 1024-unit map cell (x, z), with its box.</summary>
public readonly record struct BlockPiece(int CellX, int CellZ, int First, int Count, Vector3 Min, Vector3 Max);

/// <summary>One surface key's placed vertices and triangles (indices into its own vertices), and
/// its pieces per map cell (empty for a dynamic mesh's stage).</summary>
public sealed record BlockBucket(BlockKey Key, Vertex[] Vertices, uint[] Indices, BlockPiece[] Pieces);

/// <summary>A mesh that moves, changes stage or vanishes: its MAP file offset and one mesh per
/// stage (null: the stage's model did not load).</summary>
public sealed record BlockDynamic(int Offset, BlockBucket[]?[] Stages);

/// <summary>The sprites of one sprite index and animation phase, in the level's draw order.</summary>
public sealed record BlockSpriteGroup(int Index, int Phase, (Vector3 Centre, Vector2 Size, int Offset)[] Sprites);

/// <summary>One placed mesh's collision triangles (world axes), owned by its MAP file offset.</summary>
public sealed record BlockCollider(int Offset, Vector3[] Corners);

/// <summary>One baked input: a name (<see cref="BakeInputs"/>) and the MD5 of its bytes ("" when absent).</summary>
public readonly record struct BlockInput(string Name, string Md5);

/// <summary>
/// The baked, game-ready form of one DOS map (docs/formats/map-block.md): everything derived from
/// MAP.NNN, WLD.NNN, the models, the texture banks and the shipped enemy table that takes time to
/// build. Behaviour stays with the MAP records; textures are referenced by bank and record, never
/// stored. The ground lives in a shared layer (<see cref="MapGround"/>, one per WLD): the block
/// keeps its crop and the layer's path, and <see cref="Read"/> cuts <see cref="Terrain"/> out of it.
/// </summary>
public sealed class MapBlock
{
    public const int FormatVersion = 3;
    public static ReadOnlySpan<byte> Magic => "SNMB"u8;
    public const int HeaderSize = 32;
    private const int SectionEntrySize = 24;

    public int Number { get; init; }
    public bool Outdoor { get; init; }

    /// <summary>The source hash: <see cref="MapBaker.SourceHashOf"/> of <see cref="Inputs"/>.</summary>
    public string SourceHash { get; init; } = "";
    public BlockInput[] Inputs { get; init; } = [];

    public int MeshInstances { get; init; }
    public int EnemyCount { get; init; }
    public int SpriteCount { get; init; }
    public int PickupCount { get; init; }
    /// <summary>Distinct models drawn without the robots, and with them.</summary>
    public int ModelCount { get; init; }
    public int ModelCountWithRobots { get; init; }
    /// <summary>What did not load: the WLD and props' models in load order; the robots' models apart.</summary>
    public string[] Missing { get; init; } = [];
    public string[] MissingRobots { get; init; } = [];

    /// <summary>The heightmap cells the ground was built over, and the ground (null indoors or without a WLD).</summary>
    public (int Col, int Row, int W, int H) TerrainCells { get; init; }
    public Terrain.TerrainGeometry? Terrain { get; init; }

    /// <summary>The shared ground layer <see cref="Terrain"/> is cut from (null indoors or without a WLD):
    /// written as its own entry (<see cref="MapGround.Path"/>), the block keeps only the path.</summary>
    public MapGround? Ground { get; init; }

    /// <summary>The ground layer's entry path ("" without one).</summary>
    public string GroundPath => Ground?.Path ?? "";

    /// <summary>The textures each phase of the build made first, in order (the GPU handle order to repeat).</summary>
    public TextureRef[] ModelTextures { get; init; } = [];
    public TextureRef[] RobotTextures { get; init; } = [];
    public TextureRef[] SpriteTextures { get; init; } = [];

    /// <summary>The static batch (props), the robots in their rest pose, the dynamic meshes, the sprites.</summary>
    public BlockBucket[] World { get; init; } = [];
    public BlockBucket[] Robots { get; init; } = [];
    public BlockDynamic[] Dynamic { get; init; } = [];
    public BlockSpriteGroup[] Sprites { get; init; } = [];

    /// <summary>Every placed variant-1 mesh's triangles (record order), and the models collision missed.</summary>
    public BlockCollider[] Colliders { get; init; } = [];
    public string[] CollisionMissing { get; init; } = [];

    /// <summary>The enhanced look's art lamps and fire lamps (<see cref="EmissiveLamps.ForMap"/>).</summary>
    public LampLight[] Lamps { get; init; } = [];

    /// <summary>The collision triangles of the colliders <paramref name="skip"/> keeps, in record order.</summary>
    public IEnumerable<Triangle> Triangles(Func<int, bool>? skip = null)
    {
        foreach (var c in Colliders)
        {
            if (skip?.Invoke(c.Offset) == true)
            {
                continue;
            }

            for (var i = 0; i + 2 < c.Corners.Length; i += 3)
            {
                yield return new Triangle(c.Corners[i], c.Corners[i + 1], c.Corners[i + 2], c.Offset);
            }
        }
    }

    // ---- Writing ----

    public byte[] ToBytes()
    {
        if (!BitConverter.IsLittleEndian)
        {
            throw new PlatformNotSupportedException("Map blocks are written on little-endian hosts only.");
        }

        var sections = new List<(string Tag, byte[] Body)>
        {
            ("SRC ", Section(w =>
            {
                Str(w, SourceHash);
                w.Write(Inputs.Length);
                foreach (var i in Inputs)
                {
                    Str(w, i.Name);
                    Str(w, i.Md5);
                }

                Str(w, GroundPath);
            })),
            ("INFO", Section(w =>
            {
                w.Write(MeshInstances);
                w.Write(EnemyCount);
                w.Write(SpriteCount);
                w.Write(PickupCount);
                w.Write(ModelCount);
                w.Write(ModelCountWithRobots);
                Strings(w, Missing);
                Strings(w, MissingRobots);
            })),
            ("TERR", Section(w =>
            {
                var (c, r, cw, ch) = TerrainCells;
                w.Write(c);
                w.Write(r);
                w.Write(cw);
                w.Write(ch);
                w.Write(Ground != null);
            })),
            ("TEXR", Section(w =>
            {
                foreach (var list in new[] { ModelTextures, RobotTextures, SpriteTextures })
                {
                    w.Write(list.Length);
                    foreach (var t in list)
                    {
                        Tex(w, t);
                    }
                }
            })),
            ("WRLD", Section(w => Buckets(w, World))),
            ("ROBT", Section(w => Buckets(w, Robots))),
            ("DYNM", Section(w =>
            {
                w.Write(Dynamic.Length);
                foreach (var d in Dynamic)
                {
                    w.Write(d.Offset);
                    w.Write(d.Stages.Length);
                    foreach (var s in d.Stages)
                    {
                        w.Write(s != null);
                        if (s != null)
                        {
                            Buckets(w, s);
                        }
                    }
                }
            })),
            ("SPRT", Section(w =>
            {
                w.Write(Sprites.Length);
                foreach (var g in Sprites)
                {
                    w.Write(g.Index);
                    w.Write(g.Phase);
                    w.Write(g.Sprites.Length);
                    foreach (var (centre, size, offset) in g.Sprites)
                    {
                        Vec3(w, centre);
                        w.Write(size.X);
                        w.Write(size.Y);
                        w.Write(offset);
                    }
                }
            })),
            ("COLL", Section(w =>
            {
                w.Write(Colliders.Length);
                foreach (var c in Colliders)
                {
                    w.Write(c.Offset);
                    w.Write(c.Corners.Length);
                    w.Write(MemoryMarshal.AsBytes(c.Corners.AsSpan()));
                }

                Strings(w, CollisionMissing);
            })),
            ("LAMP", Section(w =>
            {
                w.Write(Lamps.Length);
                foreach (var l in Lamps)
                {
                    Vec3(w, l.Position);
                    Vec3(w, l.Colour);
                    w.Write(l.Range);
                    w.Write(l.Energy);
                    w.Write(l.Flicker);
                    w.Write(l.Phase);
                    w.Write(l.Lifted);
                    Vec3(w, l.Facing);
                    w.Write(l.Coherence);
                    Str(w, l.Model);
                    w.Write(l.Texture);
                    w.Write(l.Entity);
                }
            })),
        };

        using var ms = new MemoryStream();
        using var o = new BinaryWriter(ms);
        o.Write(Magic);
        o.Write((ushort)FormatVersion);
        o.Write((ushort)HeaderSize);
        o.Write(Number);
        o.Write(Outdoor ? 1u : 0u);
        o.Write(sections.Count);
        o.Write(new byte[12]);
        long at = HeaderSize + sections.Count * SectionEntrySize;
        foreach (var (tag, body) in sections)
        {
            o.Write(Encoding.ASCII.GetBytes(tag));
            o.Write(0u);
            o.Write(at);
            o.Write((long)body.Length);
            at += body.Length;
        }

        foreach (var (_, body) in sections)
        {
            o.Write(body);
        }

        o.Flush();
        return ms.ToArray();
    }

    private static byte[] Section(Action<BinaryWriter> write)
    {
        using var ms = new MemoryStream();
        using (var w = new BinaryWriter(ms, Encoding.UTF8, leaveOpen: true))
        {
            write(w);
        }

        return ms.ToArray();
    }

    private static void Str(BinaryWriter w, string s)
    {
        var b = Encoding.UTF8.GetBytes(s);
        w.Write(b.Length);
        w.Write(b);
    }

    private static void Strings(BinaryWriter w, string[] list)
    {
        w.Write(list.Length);
        foreach (var s in list)
        {
            Str(w, s);
        }
    }

    private static void Vec3(BinaryWriter w, Vector3 v)
    {
        w.Write(v.X);
        w.Write(v.Y);
        w.Write(v.Z);
    }

    private static void Tex(BinaryWriter w, TextureRef t)
    {
        w.Write(t.Bank);
        w.Write(t.Record);
        w.Write(t.Opaque);
    }

    private static void Vertices(BinaryWriter w, Vertex[] v)
    {
        w.Write(v.Length);
        w.Write(MemoryMarshal.AsBytes(v.AsSpan()));
    }

    private static void Uints(BinaryWriter w, uint[] v)
    {
        w.Write(v.Length);
        w.Write(MemoryMarshal.AsBytes(v.AsSpan()));
    }

    private static void Buckets(BinaryWriter w, BlockBucket[] buckets)
    {
        w.Write(buckets.Length);
        foreach (var b in buckets)
        {
            w.Write(b.Key.Texture.HasValue);
            if (b.Key.Texture is { } t)
            {
                Tex(w, t);
            }

            w.Write(b.Key.Flat);
            Vertices(w, b.Vertices);
            Uints(w, b.Indices);
            w.Write(b.Pieces.Length);
            foreach (var p in b.Pieces)
            {
                w.Write(p.CellX);
                w.Write(p.CellZ);
                w.Write(p.First);
                w.Write(p.Count);
                Vec3(w, p.Min);
                Vec3(w, p.Max);
            }
        }
    }

    // ---- Reading ----

    /// <summary>The block in <paramref name="bytes"/>, its ground cut from the layer
    /// <paramref name="grounds"/>(path) gives; <see cref="InvalidDataException"/> when it is not a
    /// block of this format version, is cut short, or its ground layer is missing or not the one named.</summary>
    public static MapBlock Read(byte[] bytes, Func<string, byte[]?>? grounds = null)
    {
        var (number, outdoor, sections) = Header(bytes);
        BinaryReader R(string tag) => sections.TryGetValue(tag, out var range)
            ? new BinaryReader(new MemoryStream(bytes, range.Start.Value, range.End.Value - range.Start.Value, writable: false))
            : throw new InvalidDataException($"Map block: no {tag.Trim()} section.");

        try
        {
            var (hash, inputs, groundPath) = Source(R("SRC "));
            var info = R("INFO");
            var terrain = R("TERR");
            var cells = (terrain.ReadInt32(), terrain.ReadInt32(), terrain.ReadInt32(), terrain.ReadInt32());
            MapGround? layer = null;
            if (terrain.ReadBoolean())
            {
                var groundBytes = grounds?.Invoke(groundPath)
                    ?? throw new InvalidDataException($"Map block: its ground {groundPath} is missing.");
                layer = MapGround.Read(groundBytes);
                if (layer.Path != groundPath)
                {
                    throw new InvalidDataException($"Map block: {groundPath} holds the ground {layer.Key}.");
                }
            }

            var tex = R("TEXR");
            var dyn = R("DYNM");
            var dynamic = new BlockDynamic[Count(dyn)];
            for (var d = 0; d < dynamic.Length; d++)
            {
                var offset = dyn.ReadInt32();
                var stages = new BlockBucket[]?[Count(dyn)];
                for (var s = 0; s < stages.Length; s++)
                {
                    stages[s] = dyn.ReadBoolean() ? ReadBuckets(dyn) : null;
                }

                dynamic[d] = new BlockDynamic(offset, stages);
            }

            var spr = R("SPRT");
            var groups = new BlockSpriteGroup[Count(spr)];
            for (var g = 0; g < groups.Length; g++)
            {
                var index = spr.ReadInt32();
                var phase = spr.ReadInt32();
                var list = new (Vector3, Vector2, int)[Count(spr)];
                for (var k = 0; k < list.Length; k++)
                {
                    list[k] = (ReadVec3(spr), new Vector2(spr.ReadSingle(), spr.ReadSingle()), spr.ReadInt32());
                }

                groups[g] = new BlockSpriteGroup(index, phase, list);
            }

            var col = R("COLL");
            var colliders = new BlockCollider[Count(col)];
            for (var c = 0; c < colliders.Length; c++)
            {
                var offset = col.ReadInt32();
                var corners = new Vector3[Count(col)];
                col.BaseStream.ReadExactly(MemoryMarshal.AsBytes(corners.AsSpan()));
                colliders[c] = new BlockCollider(offset, corners);
            }

            var lamp = R("LAMP");
            var lamps = new LampLight[Count(lamp)];
            for (var k = 0; k < lamps.Length; k++)
            {
                lamps[k] = new LampLight(ReadVec3(lamp), ReadVec3(lamp), lamp.ReadSingle(), lamp.ReadSingle(), lamp.ReadBoolean(),
                    lamp.ReadSingle(), lamp.ReadBoolean(), ReadVec3(lamp), lamp.ReadSingle(), ReadStr(lamp), lamp.ReadInt32(), lamp.ReadInt32());
            }

            return new MapBlock
            {
                Number = number,
                Outdoor = outdoor,
                SourceHash = hash,
                Inputs = inputs,
                MeshInstances = info.ReadInt32(),
                EnemyCount = info.ReadInt32(),
                SpriteCount = info.ReadInt32(),
                PickupCount = info.ReadInt32(),
                ModelCount = info.ReadInt32(),
                ModelCountWithRobots = info.ReadInt32(),
                Missing = ReadStrings(info),
                MissingRobots = ReadStrings(info),
                TerrainCells = cells,
                Terrain = layer?.Crop(cells),
                Ground = layer,
                ModelTextures = ReadTextures(tex),
                RobotTextures = ReadTextures(tex),
                SpriteTextures = ReadTextures(tex),
                World = ReadBuckets(R("WRLD")),
                Robots = ReadBuckets(R("ROBT")),
                Dynamic = dynamic,
                Sprites = groups,
                Colliders = colliders,
                CollisionMissing = ReadStrings(col),
                Lamps = lamps,
            };
        }
        catch (EndOfStreamException e)
        {
            throw new InvalidDataException("Map block: a section is cut short.", e);
        }
    }

    /// <summary>Only the source section (hash, inputs and the ground layer's path, "" without one)
    /// of a block, read from the start of <paramref name="stream"/> (the rest is not read).</summary>
    public static (string SourceHash, BlockInput[] Inputs, string Ground) ReadSource(Stream stream)
    {
        var head = new byte[HeaderSize];
        stream.ReadExactly(head);
        var count = BitConverter.ToInt32(head, 16);
        if (!head.AsSpan(0, 4).SequenceEqual(Magic) || BitConverter.ToUInt16(head, 4) != FormatVersion || count is < 1 or > 256)
        {
            throw new InvalidDataException("Not a map block of this version.");
        }

        var table = new byte[count * SectionEntrySize];
        stream.ReadExactly(table);
        if (Encoding.ASCII.GetString(table, 0, 4) != "SRC ")
        {
            throw new InvalidDataException("Map block: the source section is not first.");
        }

        var length = BitConverter.ToInt64(table, 16);
        var body = new byte[length];
        stream.ReadExactly(body);
        try
        {
            return Source(new BinaryReader(new MemoryStream(body)));
        }
        catch (EndOfStreamException e)
        {
            throw new InvalidDataException("Map block: the source section is cut short.", e);
        }
    }

    private static (int Number, bool Outdoor, Dictionary<string, Range> Sections) Header(ReadOnlySpan<byte> bytes)
    {
        if (bytes.Length < HeaderSize || !bytes[..4].SequenceEqual(Magic))
        {
            throw new InvalidDataException("Not a map block.");
        }

        var version = BitConverter.ToUInt16(bytes[4..]);
        if (version != FormatVersion)
        {
            throw new InvalidDataException($"Map block version {version}, this build reads {FormatVersion}.");
        }

        var number = BitConverter.ToInt32(bytes[8..]);
        var outdoor = (BitConverter.ToUInt32(bytes[12..]) & 1) != 0;
        var count = BitConverter.ToInt32(bytes[16..]);
        if (count < 0 || HeaderSize + (long)count * SectionEntrySize > bytes.Length)
        {
            throw new InvalidDataException("Map block: the section table is cut short.");
        }

        var sections = new Dictionary<string, Range>();
        for (var i = 0; i < count; i++)
        {
            var e = bytes.Slice(HeaderSize + i * SectionEntrySize, SectionEntrySize);
            var tag = Encoding.ASCII.GetString(e[..4]);
            var offset = BitConverter.ToInt64(e[8..]);
            var length = BitConverter.ToInt64(e[16..]);
            if (offset < 0 || length < 0 || offset + length > bytes.Length)
            {
                throw new InvalidDataException($"Map block: section {tag.Trim()} lies outside the block.");
            }

            sections[tag] = new Range((int)offset, (int)(offset + length));
        }

        return (number, outdoor, sections);
    }

    private static (string, BlockInput[], string) Source(BinaryReader r)
    {
        var hash = ReadStr(r);
        var inputs = new BlockInput[Count(r)];
        for (var i = 0; i < inputs.Length; i++)
        {
            inputs[i] = new BlockInput(ReadStr(r), ReadStr(r));
        }

        return (hash, inputs, ReadStr(r));
    }

    private static int Count(BinaryReader r)
    {
        var n = r.ReadInt32();
        return n >= 0 && n <= r.BaseStream.Length - r.BaseStream.Position ? n
            : throw new InvalidDataException("Map block: a bad count.");
    }

    private static string ReadStr(BinaryReader r) => Encoding.UTF8.GetString(r.ReadBytes(Count(r)));

    private static string[] ReadStrings(BinaryReader r)
    {
        var list = new string[Count(r)];
        for (var i = 0; i < list.Length; i++)
        {
            list[i] = ReadStr(r);
        }

        return list;
    }

    private static Vector3 ReadVec3(BinaryReader r) => new(r.ReadSingle(), r.ReadSingle(), r.ReadSingle());

    private static TextureRef ReadTex(BinaryReader r) => new(r.ReadInt32(), r.ReadInt32(), r.ReadBoolean());

    private static TextureRef[] ReadTextures(BinaryReader r)
    {
        var list = new TextureRef[Count(r)];
        for (var i = 0; i < list.Length; i++)
        {
            list[i] = ReadTex(r);
        }

        return list;
    }

    private static Vertex[] ReadVertices(BinaryReader r)
    {
        var v = new Vertex[Count(r)];
        r.BaseStream.ReadExactly(MemoryMarshal.AsBytes(v.AsSpan()));
        return v;
    }

    private static uint[] ReadUints(BinaryReader r)
    {
        var v = new uint[Count(r)];
        r.BaseStream.ReadExactly(MemoryMarshal.AsBytes(v.AsSpan()));
        return v;
    }

    private static BlockBucket[] ReadBuckets(BinaryReader r)
    {
        var buckets = new BlockBucket[Count(r)];
        for (var b = 0; b < buckets.Length; b++)
        {
            TextureRef? t = r.ReadBoolean() ? ReadTex(r) : null;
            var key = new BlockKey(t, r.ReadInt32());
            var v = ReadVertices(r);
            var i = ReadUints(r);
            var pieces = new BlockPiece[Count(r)];
            for (var p = 0; p < pieces.Length; p++)
            {
                pieces[p] = new BlockPiece(r.ReadInt32(), r.ReadInt32(), r.ReadInt32(), r.ReadInt32(), ReadVec3(r), ReadVec3(r));
            }

            buckets[b] = new BlockBucket(key, v, i, pieces);
        }

        return buckets;
    }
}
