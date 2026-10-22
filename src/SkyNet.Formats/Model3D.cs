using System.Buffers.Binary;
using System.Collections.Immutable;
using System.Numerics;
using System.Text;

namespace SkyNet.Formats;

/// <summary>A .3D vertex or stored normal, raw s32 in DOS axes (Y down). Vertices are 1/256 world unit.</summary>
public readonly record struct ModelVertex(int X, int Y, int Z)
{
    /// <summary>
    /// World units, Y up: (x, −y, −z) / 256, a rotation (Rx 180°) so the winding is kept. Computed
    /// exactly as the Godot port does, so the floats (signed zeros included) match it bit for bit.
    /// </summary>
    public Vector3 ToYUp()
    {
        float x = (float)(X * Model3D.VertexScale);
        float y = (float)(Y * Model3D.VertexScale);
        float z = (float)(Z * Model3D.VertexScale);
        return new Vector3(x, -y, -z);
    }
}

/// <summary>One 16-byte frame-table entry. Only <see cref="VertexOffset"/> is understood.</summary>
public readonly record struct ModelFrameEntry(uint VertexOffset, uint UvOffset, uint AuxOffset, ushort Word12, ushort Word14);

/// <summary>
/// One polygon of a .3D model, as stored: vertex indices in file order (counter-clockwise from the
/// visible side), the texture word and the raw per-vertex (du, dv).
/// </summary>
public sealed class ModelFace
{
    /// <summary>Position in the file's face array (faces the parser skipped leave gaps).</summary>
    public int SourceIndex { get; init; }
    public int FileOffset { get; init; }
    /// <summary>+1, meaning unknown (0 in the files checked).</summary>
    public byte Unknown1 { get; init; }
    /// <summary>+2: texture type, bank = &gt;&gt; 7, record = &amp; 0x7F.</summary>
    public ushort Texture { get; init; }
    /// <summary>+4, meaning unknown (0 in the files checked).</summary>
    public uint Unknown4 { get; init; }
    public ImmutableArray<int> Indices { get; init; } = [];
    /// <summary>Per vertex, 1/16 texel: point 0 absolute, points 1 and 2 deltas, later points ignored by the engine.</summary>
    public ImmutableArray<short> Du { get; init; } = [];
    public ImmutableArray<short> Dv { get; init; } = [];
    /// <summary>The stored face normal from the list at header 0x34, when that list lies inside the file.</summary>
    public ModelVertex? StoredNormal { get; init; }

    public int VertexCount => Indices.Length;
    public int TextureBank => Texture >> 7;
    public int TextureRecord => Texture & 0x7F;
    /// <summary>Texture ≤ 0xFF: banks 0/1 ("Solid Colors"), a flat palette colour.</summary>
    public bool IsSolidColour => Texture <= 0xFF;
}

/// <summary>
/// An XnGine v2.x .3D model (ported from the Godot port's mesh_3d.gd parser; the ArrayMesh
/// building is left to the renderer, with <see cref="TriangleFan"/> and <see cref="FaceUvs"/> as
/// helpers). Vertices stay raw (s32, DOS axes, 1/256 world unit); faces stay polygons.
/// </summary>
public sealed class Model3D
{
    public const double VertexScale = 1.0 / 256.0;
    public const int HeaderSize = 0x40;
    public const int FrameEntrySize = 16;
    public const int MaxFrames = 4096;
    public const int MaxFrameVertices = 2_000_000;
    public const int MaxFrameCorners = 4_000_000;

    /// <summary>"v2.1" .. "v2.7".</summary>
    public string Version { get; }
    /// <summary>The raw header (0x40 bytes, or what the file has).</summary>
    public ImmutableArray<byte> Header { get; }
    /// <summary>Vertices per frame (+4).</summary>
    public int VertexCount { get; }
    /// <summary>Faces the header announces (+8); <see cref="Faces"/> may hold fewer.</summary>
    public int FaceCountField { get; }
    /// <summary>+0x10 frame count (0 = static) and +0x14 frame table offset, as stored.</summary>
    public uint FrameCountField { get; }
    public uint FrameTableOffset { get; }
    /// <summary>+0x30: the vertex block cursor (not always frame 0; ignored when a frame table exists).</summary>
    public uint VertexBlockOffset { get; }
    /// <summary>+0x34: the per-face normal list.</summary>
    public uint NormalsOffset { get; }
    /// <summary>+0x3C: the face array.</summary>
    public uint FacesOffset { get; }
    /// <summary>The frame table, when the file has a valid one (else empty: frames are packed from +0x30).</summary>
    public ImmutableArray<ModelFrameEntry> FrameTable { get; }
    /// <summary>Vertex blocks, one per frame (at least one). Faces and UVs are shared by all frames.</summary>
    public ImmutableArray<ImmutableArray<ModelVertex>> Frames { get; }
    public ImmutableArray<ModelVertex> Vertices => Frames[0];
    public ImmutableArray<ModelFace> Faces { get; }
    /// <summary>Faces read but dropped: fewer than 3 vertices, or a vertex index out of range.</summary>
    public int SkippedFaces { get; }

    Model3D(string version, byte[] header, int vc, int fc, uint fcount, uint ftab, uint v30, uint n34, uint f3c,
        ImmutableArray<ModelFrameEntry> table, ImmutableArray<ImmutableArray<ModelVertex>> frames,
        ImmutableArray<ModelFace> faces, int skipped)
    {
        Version = version;
        Header = [.. header];
        VertexCount = vc;
        FaceCountField = fc;
        FrameCountField = fcount;
        FrameTableOffset = ftab;
        VertexBlockOffset = v30;
        NormalsOffset = n34;
        FacesOffset = f3c;
        FrameTable = table;
        Frames = frames;
        Faces = faces;
        SkippedFaces = skipped;
    }

    /// <summary>At least 48 bytes and "v2.1" .. "v2.7".</summary>
    public static bool LooksValid(ReadOnlySpan<byte> b) =>
        b.Length >= 48 && b[0] == 'v' && b[1] == '2' && b[2] == '.' && b[3] >= '1' && b[3] <= '7';

    /// <summary>Face vertex reference to index: ÷ 12 (byte offset) for v2.6/v2.7, ÷ 4 for v2.1-v2.5.</summary>
    public static int FaceVertexDivisor(ReadOnlySpan<byte> b) => b[3] >= '6' ? 12 : 4;

    /// <summary>
    /// Parse a model. Null when the magic is wrong, a count is 0, the face array offset is out of
    /// range, or frame 0's vertices are. A later bad or repeated frame ends the frame list; a bad
    /// face is skipped and the next one read at the real stride (no vertex-count cap).
    /// </summary>
    public static Model3D? Parse(ReadOnlySpan<byte> bytes)
    {
        if (!LooksValid(bytes))
            return null;
        int size = bytes.Length;
        uint vcField = U32(bytes, 4), fcField = U32(bytes, 8);
        uint frameCount = U32(bytes, 0x10), tableOff = U32(bytes, 0x14);
        uint v30 = U32(bytes, 0x30), n34 = bytes.Length >= 0x38 ? U32(bytes, 0x34) : 0, facesOff = U32(bytes, 0x3C);
        if (vcField == 0 || fcField == 0 || vcField > int.MaxValue / 12 || fcField > int.MaxValue)
            return null;
        if (facesOff < 48 || facesOff > size)
            return null;
        int vc = (int)vcField, fc = (int)fcField;
        long stride = (long)vc * 12;

        var offsets = new List<long>();
        var table = ImmutableArray<ModelFrameEntry>.Empty;
        bool haveTable = frameCount > 0 && frameCount <= MaxFrames && tableOff >= 64
                         && tableOff + (long)frameCount * FrameEntrySize <= size;
        if (haveTable)
        {
            var tb = ImmutableArray.CreateBuilder<ModelFrameEntry>((int)frameCount);
            for (int i = 0; i < frameCount; i++)
            {
                int o = (int)tableOff + i * FrameEntrySize;
                var e = new ModelFrameEntry(U32(bytes, o), U32(bytes, o + 4), U32(bytes, o + 8),
                    BinaryPrimitives.ReadUInt16LittleEndian(bytes[(o + 12)..]), BinaryPrimitives.ReadUInt16LittleEndian(bytes[(o + 14)..]));
                tb.Add(e);
                // The Godot port keeps these in an int32 array: an offset ≥ 2^31 goes negative and fails.
                offsets.Add((int)e.VertexOffset);
            }
            table = tb.MoveToImmutable();
        }
        else
        {
            if (v30 < 48 || v30 >= size || facesOff <= v30)
                return null;
            long vbytes = facesOff - v30;
            if (vbytes < stride || vbytes % stride != 0)
                return null;
            long n = Math.Min(vbytes / stride, MaxFrames);
            for (long i = 0; i < n; i++)
                offsets.Add(v30 + i * stride);
        }

        var frames = ImmutableArray.CreateBuilder<ImmutableArray<ModelVertex>>();
        var seen = new HashSet<long>();
        for (int fi = 0; fi < offsets.Count; fi++)
        {
            long b = offsets[fi];
            if (b < 48 || b + stride > size || seen.Contains(b) || (long)(fi + 1) * vc > MaxFrameVertices)
            {
                if (fi == 0) return null;
                break;
            }
            seen.Add(b);
            var verts = new ModelVertex[vc];
            for (int i = 0; i < vc; i++)
            {
                int o = (int)b + i * 12;
                verts[i] = new ModelVertex(S32(bytes, o), S32(bytes, o + 4), S32(bytes, o + 8));
            }
            frames.Add([.. verts]);
        }

        bool haveNormals = n34 >= HeaderSize && n34 + (long)fc * 12 <= size;
        int div = FaceVertexDivisor(bytes);
        var faces = ImmutableArray.CreateBuilder<ModelFace>();
        int skipped = 0;
        long off = facesOff;
        for (int fi = 0; fi < fc; fi++)
        {
            if (off + 8 > size)
                break;
            int fvc = bytes[(int)off];
            long fstride = 8 + fvc * 8L;
            if (off + fstride > size)
                break;
            int fo = (int)off;
            off += fstride;
            if (fvc < 3)
            {
                skipped++;
                continue;
            }
            var idx = new int[fvc];
            var du = new short[fvc];
            var dv = new short[fvc];
            bool ok = true;
            for (int k = 0; k < fvc; k++)
            {
                int p = fo + 8 + k * 8;
                long index = U32(bytes, p) / div;
                if (index >= vc)
                {
                    ok = false;
                    break;
                }
                idx[k] = (int)index;
                du[k] = BinaryPrimitives.ReadInt16LittleEndian(bytes[(p + 4)..]);
                dv[k] = BinaryPrimitives.ReadInt16LittleEndian(bytes[(p + 6)..]);
            }
            if (!ok)
            {
                skipped++;
                continue;
            }
            ModelVertex? normal = null;
            if (haveNormals)
            {
                int no = (int)n34 + fi * 12;
                normal = new ModelVertex(S32(bytes, no), S32(bytes, no + 4), S32(bytes, no + 8));
            }
            faces.Add(new ModelFace
            {
                SourceIndex = fi,
                FileOffset = fo,
                Unknown1 = bytes[fo + 1],
                Texture = BinaryPrimitives.ReadUInt16LittleEndian(bytes[(fo + 2)..]),
                Unknown4 = U32(bytes, fo + 4),
                Indices = [.. idx],
                Du = [.. du],
                Dv = [.. dv],
                StoredNormal = normal,
            });
        }

        // The Godot port's corner budget: keep only as many frames as fit (at least one).
        long corners = 0;
        foreach (var f in faces)
            corners += (f.VertexCount - 2) * 3;
        if (corners > 0 && frames.Count * corners > MaxFrameCorners)
        {
            int keep = (int)Math.Max(1, MaxFrameCorners / corners);
            if (keep < frames.Count)
                frames.RemoveRange(keep, frames.Count - keep);
        }

        return new Model3D(Encoding.ASCII.GetString(bytes[..4]), bytes[..Math.Min(size, HeaderSize)].ToArray(),
            vc, fc, frameCount, tableOff, v30, n34, facesOff, table, frames.ToImmutable(), faces.ToImmutable(), skipped);
    }

    /// <summary>
    /// The face's triangles as corner positions into <see cref="ModelFace.Indices"/>: a fan
    /// (0, k, k+1) in stored order, i.e. counter-clockwise seen from the visible side (in DOS
    /// axes and, since the Y-up conversion is a rotation, in Y-up too). A renderer whose front
    /// faces are clockwise (Godot) emits (0, k+1, k) instead, as the Godot port does.
    /// </summary>
    public static IEnumerable<(int A, int B, int C)> TriangleFan(ModelFace face)
    {
        for (int k = 1; k < face.VertexCount - 1; k++)
            yield return (0, k, k + 1);
    }

    /// <summary>
    /// Texture coordinates for every vertex of a face (texture-space, 1 = the texture's size):
    /// point 0 absolute, points 1 and 2 chained deltas, points 3.. from the affine map of points
    /// 0-2 extended over the (coplanar) positions of frame <paramref name="frame"/>, as the Godot
    /// port computes them. No Daggerfall UV wrap is applied.
    /// </summary>
    public Vector2[] FaceUvs(ModelFace face, int textureWidth, int textureHeight, int frame = 0)
    {
        if (textureWidth <= 0) textureWidth = 64;
        if (textureHeight <= 0) textureHeight = 64;
        float dx = textureWidth * 16f, dy = textureHeight * 16f;
        var verts = frame > 0 && frame < Frames.Length ? Frames[frame] : Vertices;
        int n = face.VertexCount;
        var uv = new Vector2[n];
        int u = 0, v = 0;
        for (int k = 0; k < Math.Min(n, 3); k++)
        {
            if (k == 0) { u = face.Du[0]; v = face.Dv[0]; }
            else { u += face.Du[k]; v += face.Dv[k]; }
            uv[k] = new Vector2(u / dx, v / dy);
        }
        if (n > 3)
        {
            var a = verts[face.Indices[0]].ToYUp();
            var v0 = verts[face.Indices[1]].ToYUp() - a;
            var v1 = verts[face.Indices[2]].ToYUp() - a;
            float d00 = Vector3.Dot(v0, v0), d01 = Vector3.Dot(v0, v1), d11 = Vector3.Dot(v1, v1);
            float denom = d00 * d11 - d01 * d01;
            Vector2 uvA = uv[0], uvAB = uv[1] - uvA, uvAC = uv[2] - uvA;
            for (int k = 3; k < n; k++)
            {
                if (MathF.Abs(denom) < 1e-9f)
                {
                    uv[k] = uvA;
                    continue;
                }
                var v2 = verts[face.Indices[k]].ToYUp() - a;
                float d20 = Vector3.Dot(v2, v0), d21 = Vector3.Dot(v2, v1);
                float s = (d11 * d20 - d01 * d21) / denom;
                float t = (d00 * d21 - d01 * d20) / denom;
                uv[k] = uvA + uvAB * s + uvAC * t;
            }
        }
        return uv;
    }

    /// <summary>The face's winding normal (b − a) × (c − a) in Y-up axes, normalised (the visible side).</summary>
    public Vector3 FaceNormal(ModelFace face, int frame = 0)
    {
        var verts = frame > 0 && frame < Frames.Length ? Frames[frame] : Vertices;
        var a = verts[face.Indices[0]].ToYUp();
        var n = Vector3.Cross(verts[face.Indices[1]].ToYUp() - a, verts[face.Indices[2]].ToYUp() - a);
        return n == Vector3.Zero ? n : Vector3.Normalize(n);
    }

    static uint U32(ReadOnlySpan<byte> b, long off) => off >= 0 && off + 4 <= b.Length ? BinaryPrimitives.ReadUInt32LittleEndian(b[(int)off..]) : 0;
    static int S32(ReadOnlySpan<byte> b, long off) => (int)U32(b, off);
}
