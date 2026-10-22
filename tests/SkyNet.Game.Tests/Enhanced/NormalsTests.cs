using System.Numerics;
using RetroEng.Render;
using SkyNet.Formats;

namespace SkyNet.Game.Tests.Enhanced;

/// <summary>The geometry's vertex normals (S11): the terrain's smooth corner normals, the models'
/// outward faceted or smoothed normals, and the batch turning them with the placement.</summary>
public class NormalsTests
{
    /// <summary>One packed-normal quantisation step of slack per component, and a little more.</summary>
    private const float Slack = 1.5f / 127f;

    private static void Near(Vector3 expected, Vector3 actual, float slack = Slack)
    {
        Assert.InRange(actual.X - expected.X, -slack, slack);
        Assert.InRange(actual.Y - expected.Y, -slack, slack);
        Assert.InRange(actual.Z - expected.Z, -slack, slack);
    }

    private static Terrain.TerrainGeometry Ground(Func<int, int, double> height, int cells) =>
        Terrain.Geometry(height, (_, _) => 0, Enumerable.Repeat((64, 64), 64).ToArray(), 64, 512, (0, 0, cells, cells));

    [Fact]
    public void FlatGroundFacesUp()
    {
        var ground = Ground((_, _) => 120.0, 4);
        Assert.NotEmpty(ground.Vertices);
        Assert.All(ground.Vertices, v => Near(Vector3.UnitY, Vertex.UnpackNormal(v.Normal)));
    }

    [Fact]
    public void SlopeTiltsTheNormalDownhill()
    {
        // Rising 64 per cell eastward (+X) and 32 per cell southward (+Z): the normal leans west and north.
        double Slope(int col, int row) => col * 64.0 + row * 32.0;
        var expected = Vector3.Normalize(new Vector3(-64f / 256f, 1f, -32f / 256f));
        Near(expected, Terrain.CornerNormal(Slope, 10, 10), 1e-5f);
        // At the field's edge the one-sided difference gives the same tilt on a plane.
        Near(expected, Terrain.CornerNormal(Slope, 0, 0), 1e-5f);

        var ground = Ground(Slope, 8);
        Assert.All(ground.Vertices, v => Near(expected, Vertex.UnpackNormal(v.Normal)));
    }

    [Fact]
    public void NormalsMatchAcrossAChunkEdge()
    {
        // A bump astride the edge between chunks 0 and 1 (col 16).
        double Bump(int col, int row) => 400.0 * Math.Exp(-((col - 16) * (col - 16) + (row - 5) * (row - 5)) / 8.0);
        var ground = Ground(Bump, 2 * Terrain.ChunkCells);
        Assert.True(ground.Chunks.Count >= 2);

        var byPosition = new Dictionary<Vector3, uint>();
        var shared = 0;
        foreach (var v in ground.Vertices)
        {
            if (byPosition.TryGetValue(v.Position, out var n))
            {
                Assert.Equal(n, v.Normal);
                shared++;
            }
            else
            {
                byPosition[v.Position] = v.Normal;
            }
        }

        Assert.True(shared > 0);
        // The vertices on the chunk edge (x = 16 · 256) tilt only along Z at the bump's ridge (row 5).
        var ridge = Vertex.UnpackNormal(byPosition.First(p => p.Key.X == 16 * 256f && p.Key.Z == 5 * 256f - 65536f).Value);
        Near(Vector3.UnitY, ridge);
        // West of the bump's peak the ground climbs eastward: the normal leans west (−X).
        var west = Vertex.UnpackNormal(byPosition.First(p => p.Key.X == 14 * 256f && p.Key.Z == 5 * 256f - 65536f).Value);
        Assert.True(west.X < -0.1f);
    }

    /// <summary>A unit cube's six faces, each counter-clockwise seen from outside.</summary>
    private static Vector3[][] Cube()
    {
        Vector3 P(int x, int y, int z) => new(x, y, z);
        return
        [
            [P(1, 0, 0), P(1, 1, 0), P(1, 1, 1), P(1, 0, 1)], // +X
            [P(0, 0, 0), P(0, 0, 1), P(0, 1, 1), P(0, 1, 0)], // −X
            [P(0, 1, 0), P(0, 1, 1), P(1, 1, 1), P(1, 1, 0)], // +Y
            [P(0, 0, 0), P(1, 0, 0), P(1, 0, 1), P(0, 0, 1)], // −Y
            [P(0, 0, 1), P(1, 0, 1), P(1, 1, 1), P(0, 1, 1)], // +Z
            [P(0, 0, 0), P(0, 1, 0), P(1, 1, 0), P(1, 0, 0)], // −Z
        ];
    }

    private static readonly Vector3[] CubeOut =
        [Vector3.UnitX, -Vector3.UnitX, Vector3.UnitY, -Vector3.UnitY, Vector3.UnitZ, -Vector3.UnitZ];

    [Fact]
    public void CubeFacesPointOutward()
    {
        var corners = ModelMesh.CornerNormals(Cube(), new Vector3?[6], 0f);
        for (var f = 0; f < 6; f++)
        {
            Assert.All(corners[f], n => Near(CubeOut[f], n, 1e-5f));
        }
    }

    [Fact]
    public void StoredNormalIsUsedWhenItAgreesWithTheWinding()
    {
        var face = Cube()[2];
        var tilted = Vector3.Normalize(new Vector3(0.1f, 1f, 0f));
        Near(tilted, ModelMesh.OutwardNormal(face, tilted * 1000f), 1e-5f);
        // A stored normal facing the hidden side loses to the winding.
        Near(Vector3.UnitY, ModelMesh.OutwardNormal(face, -Vector3.UnitY), 1e-5f);
        // A degenerate face falls back on whatever it stores.
        Near(Vector3.UnitX, ModelMesh.OutwardNormal([Vector3.Zero, Vector3.UnitX, 2 * Vector3.UnitX], Vector3.UnitX), 1e-5f);
    }

    [Fact]
    public void PlacedCubeNormalsTurnWithTheRotation()
    {
        var faces = Cube();
        var corners = ModelMesh.CornerNormals(faces, new Vector3?[6], 0f);
        var vertices = new List<Vertex>();
        var normals = new List<Vector3>();
        var indices = new List<uint>();
        for (var f = 0; f < 6; f++)
        {
            var at = (uint)vertices.Count;
            for (var k = 0; k < 4; k++)
            {
                vertices.Add(new Vertex(faces[f][k], Vector2.Zero, Vertex.White, Vertex.PackNormal(corners[f][k])));
                normals.Add(CubeOut[f]);
            }

            indices.AddRange([at, at + 1, at + 2, at, at + 2, at + 3]);
        }

        var surface = new ModelSurface(new SurfaceKey(default, 7), null, [.. vertices], [.. normals], [.. indices]);
        var rotation = Matrix4x4.CreateRotationY(MathF.PI / 2f) * Matrix4x4.CreateRotationX(0.3f);
        var batch = new StaticBatch();
        batch.Add([surface], rotation * Matrix4x4.CreateTranslation(500f, 20f, -300f));

        var placed = batch.Vertices.ToArray();
        Assert.Equal(24, placed.Length);
        for (var i = 0; i < placed.Length; i++)
        {
            var expected = Vector3.TransformNormal(CubeOut[i / 4], rotation);
            var n = Vertex.UnpackNormal(placed[i].Normal);
            Near(expected, n, 2 * Slack);
            // Outward: from the placed cube's centre toward the face.
            var centre = Vector3.Transform(new Vector3(0.5f), rotation * Matrix4x4.CreateTranslation(500f, 20f, -300f));
            Assert.True(Vector3.Dot(n, placed[i].Position - centre) > 0f);
        }

        // No normal stays none.
        Assert.Equal(0u, StaticBatch.PlaceNormal(0, rotation));
    }

    [Fact]
    public void RealModelsStoreOutwardNormalsWhenTheDataIsThere()
    {
        var dir = Environment.GetEnvironmentVariable("SKYNET_DATA");
        if (string.IsNullOrWhiteSpace(dir) || !GameDataLocator.HasData(dir) || GameData.Open(dir) is not { } data)
        {
            return;
        }

        var (stored, agree) = (0, 0);
        foreach (var archive in new[] { "MDMDOBJS.BSA", "MDMDENMS.BSA" })
        {
            if (!data.Exists(archive))
            {
                continue;
            }

            using var bsa = data.OpenArchive(archive);
            foreach (var entry in bsa.Entries.Where(e => e.Name.EndsWith(".3D", StringComparison.OrdinalIgnoreCase)))
            {
                if (bsa.Read(entry.Name) is not { Length: > 0 } bytes || Model3D.Parse(bytes) is not { } model)
                {
                    continue;
                }

                foreach (var face in model.Faces.Where(f => f.StoredNormal != null))
                {
                    var positions = face.Indices.Select(i => model.Vertices[i].ToYUp()).ToArray();
                    var normal = face.StoredNormal!.Value.ToYUp();
                    var outward = ModelMesh.OutwardNormal(positions, normal);
                    stored++;
                    if (normal != Vector3.Zero && Vector3.Distance(outward, Vector3.Normalize(normal)) < 1e-5f)
                    {
                        agree++;
                    }

                    // Whatever is picked faces the winding's side.
                    var winding = ModelMesh.OutwardNormal(positions, null);
                    Assert.True(winding == Vector3.Zero || Vector3.Dot(outward, winding) > 0f);
                }
            }
        }

        // The stored normals point outward on nearly every face (70 735 of 71 010 in the two archives).
        Assert.True(stored > 0);
        Assert.True(agree > stored * 0.98, $"{agree} of {stored} stored normals agree with the winding");
    }

    [Fact]
    public void SmoothingBlendsSoftEdgesAndKeepsHardOnes()
    {
        // Two quads hinged on the X axis at 30° (soft) and the cube's 90° corners (hard).
        var a = new[] { new Vector3(0, 0, 0), new Vector3(1, 0, 0), new Vector3(1, 0, -1), new Vector3(0, 0, -1) };
        var tilt = Matrix4x4.CreateRotationX(-MathF.PI / 6f);
        var b = new[] { new Vector3(0, 0, 1), new Vector3(1, 0, 1), new Vector3(1, 0, 0), new Vector3(0, 0, 0) }
            .Select(p => Vector3.Transform(p, tilt)).ToArray();
        var na = ModelMesh.OutwardNormal(a, null);
        var nb = ModelMesh.OutwardNormal(b, null);
        Assert.InRange(MathF.Acos(Vector3.Dot(na, nb)) * 180f / MathF.PI, 29.9f, 30.1f);

        var smooth = ModelMesh.CornerNormals([a, b], new Vector3?[2], ModelMesh.SmoothAngleDeg);
        var mean = Vector3.Normalize(na + nb);
        Near(mean, smooth[0][0], 1e-5f); // on the hinge
        Near(na, smooth[0][2], 1e-5f);   // away from it

        // At 55° or more the edge stays hard.
        var steep = Matrix4x4.CreateRotationX(-60f * MathF.PI / 180f);
        var c = new[] { new Vector3(0, 0, 1), new Vector3(1, 0, 1), new Vector3(1, 0, 0), new Vector3(0, 0, 0) }
            .Select(p => Vector3.Transform(p, steep)).ToArray();
        var hard = ModelMesh.CornerNormals([a, c], new Vector3?[2], ModelMesh.SmoothAngleDeg);
        Near(na, hard[0][0], 1e-5f);

        // The cube's corners are 90°: smoothing leaves it faceted.
        var cube = ModelMesh.CornerNormals(Cube(), new Vector3?[6], ModelMesh.SmoothAngleDeg);
        for (var f = 0; f < 6; f++)
        {
            Assert.All(cube[f], n => Near(CubeOut[f], n, 1e-5f));
        }

        // Faceted (angle 0): the hinge keeps each face's own normal.
        Near(na, ModelMesh.CornerNormals([a, b], new Vector3?[2], 0f)[0][0], 1e-5f);
    }
}
