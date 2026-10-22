using System.Numerics;

namespace SkyNet.Formats.Tests;

public class Model3DTests
{
    /// <summary>Header for a model; offsets are filled in by the caller.</summary>
    static Bytes Header(string version, int verts, int faces, uint frames = 0, uint table = 0, uint v30 = 0, uint n34 = 0, uint f3c = 0) =>
        new Bytes().Raw(System.Text.Encoding.ASCII.GetBytes(version)).U32(verts).U32(faces).U32(0)
            .U32(frames).U32(table).Zeros(0x30 - 0x18).U32(v30).U32(n34).U32(0).U32(f3c);

    static Bytes Vert(Bytes b, int x, int y, int z) => b.U32((uint)x).U32((uint)y).U32((uint)z);

    static Bytes Face(Bytes b, int texture, int refScale, params (int I, short Du, short Dv)[] v)
    {
        b.U8(v.Length).U8(0).U16(texture).U32(0);
        foreach (var (i, du, dv) in v)
            b.U32((uint)(i * refScale)).U16(du).U16(dv);
        return b;
    }

    [Fact]
    public void Static_model_with_normals_and_an_uncapped_face()
    {
        // 9 vertices on a plane (8 around + 1), one 9-gon, one bad face (index 99), one triangle.
        const int verts = 0x40, faces = verts + 9 * 12, normals = faces + (8 + 9 * 8) + (8 + 3 * 8) * 2;
        var b = Header("v2.7", 9, 3, v30: verts, n34: normals, f3c: faces);
        for (int i = 0; i < 9; i++)
            Vert(b, 256 * i, 0, i % 2 == 0 ? 0 : 512);
        Face(b, 300 * 128 + 5, 12, [.. Enumerable.Range(0, 9).Select(i => (i, (short)0, (short)0))]);
        Face(b, 7, 12, (0, 0, 0), (99, 0, 0), (1, 0, 0));
        Face(b, 0x80, 12, (2, 16, 32), (3, 0, 0), (4, 0, 0));
        Assert.Equal(normals, b.Length);
        Vert(b, 0, 1, 0);   // one stored normal per face, the skipped one included
        Vert(b, 0, 2, 0);
        Vert(b, 0, 3, 0);
        var m = Model3D.Parse(b.ToArray())!;
        Assert.Single(m.Frames);
        Assert.Equal(2, m.Faces.Length);
        Assert.Equal(1, m.SkippedFaces);
        var big = m.Faces[0];
        Assert.Equal((9, 300, 5, false), (big.VertexCount, big.TextureBank, big.TextureRecord, big.IsSolidColour));
        Assert.Equal(new ModelVertex(0, 1, 0), big.StoredNormal);
        var tri = m.Faces[1];
        Assert.Equal((2, true), (tri.SourceIndex, tri.IsSolidColour));
        Assert.Equal(new ModelVertex(0, 3, 0), tri.StoredNormal);
        Assert.Equal([(0, 1, 2), (0, 2, 3)], Model3D.TriangleFan(new ModelFace { Indices = [5, 6, 7, 8] }));
        Assert.Equal(7, Model3D.TriangleFan(big).Count());
        Assert.Equal(new Vector3(1, -0f, -2), m.Vertices[1].ToYUp());
    }

    [Fact]
    public void Frame_table_wins_over_the_0x30_cursor()
    {
        // 2 frames of 3 vertices, stored in reverse order; 0x30 points at frame 1.
        const int table = 0x40, f1 = table + 32, f0 = f1 + 36, faces = f0 + 36;
        var b = Header("v2.6", 3, 1, frames: 2, table: table, v30: f1, f3c: faces);
        b.U32(f0).U32(0).U32(0).U16(0).U16(0).U32(f1).U32(0).U32(0).U16(0).U16(0);
        Vert(Vert(Vert(b, 1, 1, 1), 1, 1, 1), 1, 1, 1);
        Vert(Vert(Vert(b, 0, 0, 0), 256, 0, 0), 0, 0, 256);
        Face(b, 0, 12, (0, 0, 0), (1, 0, 0), (2, 0, 0));
        var m = Model3D.Parse(b.ToArray())!;
        Assert.Equal(2, m.Frames.Length);
        Assert.Equal(new ModelVertex(256, 0, 0), m.Frames[0][1]);
        Assert.Equal(new ModelVertex(1, 1, 1), m.Frames[1][0]);
        Assert.Equal((uint)f0, m.FrameTable[0].VertexOffset);
    }

    [Fact]
    public void Legacy_packed_frames_use_quarter_references()
    {
        const int v = 0x40, faces = v + 2 * 36;
        var b = Header("v2.1", 3, 1, v30: v, f3c: faces);
        for (int f = 0; f < 2; f++)
            Vert(Vert(Vert(b, f, 0, 0), 256, 0, 0), 0, 256, 0);
        Face(b, 0, 4, (2, 0, 0), (1, 0, 0), (0, 0, 0));
        var m = Model3D.Parse(b.ToArray())!;
        Assert.Equal(2, m.Frames.Length);
        Assert.Empty(m.FrameTable);
        Assert.Equal([2, 1, 0], m.Faces[0].Indices.ToArray());
        Assert.Equal(1, m.Frames[1][0].X);
    }

    [Fact]
    public void Uvs_extend_the_first_three_points()
    {
        // A unit square (256 units = 1 world unit, scale irrelevant to barycentrics).
        const int v = 0x40, faces = v + 4 * 12;
        var b = Header("v2.7", 4, 1, v30: v, f3c: faces);
        Vert(Vert(Vert(Vert(b, 0, 0, 0), 256, 0, 0), 256, 0, 256), 0, 0, 256);
        Face(b, 300 * 128, 12, (0, 0, 0), (1, 64 * 16, 0), (2, 0, 64 * 16), (3, 12345, -999));
        var m = Model3D.Parse(b.ToArray())!;
        var uv = m.FaceUvs(m.Faces[0], 64, 64);
        Assert.Equal(new Vector2(0, 0), uv[0]);
        Assert.Equal(new Vector2(1, 0), uv[1]);
        Assert.Equal(new Vector2(1, 1), uv[2]);
        Assert.Equal(0f, uv[3].X, 5);
        Assert.Equal(1f, uv[3].Y, 5);
    }

    [Fact]
    public void Rejects_bad_files()
    {
        Assert.Null(Model3D.Parse(new byte[100]));
        Assert.Null(Model3D.Parse(Header("v2.8", 3, 1, v30: 0x40, f3c: 0x64).PadTo(0x80).ToArray()));
        Assert.Null(Model3D.Parse(Header("v2.7", 0, 1, v30: 0x40, f3c: 0x64).PadTo(0x80).ToArray()));
        // The vertex block is not a whole number of frames.
        Assert.Null(Model3D.Parse(Header("v2.7", 3, 1, v30: 0x40, f3c: 0x50).PadTo(0x80).ToArray()));
    }
}
