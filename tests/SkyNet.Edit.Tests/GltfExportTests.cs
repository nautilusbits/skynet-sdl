using System.Buffers.Binary;
using System.Numerics;
using System.Runtime.InteropServices;
using System.Text.Json;
using RetroEng.Models;
using SkyNet.Data;
using SkyNet.Formats;
using SkyNet.Game.Enemies;

namespace SkyNet.Edit.Tests;

/// <summary>The .3D to glTF export (editor-tools plan, T2): read back by the engine's glTF reader and
/// its JSON, the vertex, triangle and frame counts match the DOS model's; a robot's skin, bind pose
/// and clips match the game's assembly and the Model Viewer's blocks.
/// Skipped without the game data.</summary>
public sealed class GltfExportTests
{
    private const int GlbHeader = 12, ChunkHeader = 8;

    private static GameData Data => GameData.Open(EditDocumentTests.DataDir!)!;

    private static string NameOf(EnemyType t) => Path.GetFileNameWithoutExtension(t.ModelFile);

    // The GLB's JSON chunk.
    private static JsonElement Json(byte[] glb)
    {
        var length = BinaryPrimitives.ReadInt32LittleEndian(glb.AsSpan(GlbHeader));
        return JsonDocument.Parse(glb.AsMemory(GlbHeader + ChunkHeader, length)).RootElement;
    }

    [MapDataFact]
    public void An_animated_model_keeps_its_vertices_and_frames()
    {
        var data = Data;
        var (name, model) = GameTables.Ais.Types
            .Select(t => (Name: NameOf(t), Model: data.LoadModel(t.ModelFile)))
            .First(m => m.Model is { Frames.Length: > 2 });

        var glb = new GltfExport(data, GameTables.Ais).Export(name, RobotSegments.Without)!;
        var read = Gltf.Load(glb);

        // Every face corner a vertex, every fan triangle a triangle.
        var corners = model!.Faces.Sum(f => f.VertexCount);
        var triangles = model.Faces.Sum(f => f.VertexCount - 2);
        Assert.Equal(corners, read.Primitives.Sum(p => p.Positions.Length));
        Assert.Equal(triangles, read.Triangles);

        // Frame 0 is the base, the other frames a morph target each, stepped by the animation.
        var json = Json(glb);
        var frames = model.Frames.Length;
        foreach (var p in json.GetProperty("meshes")[0].GetProperty("primitives").EnumerateArray())
        {
            Assert.Equal(frames - 1, p.GetProperty("targets").GetArrayLength());
        }

        var sampler = json.GetProperty("animations")[0].GetProperty("samplers")[0];
        var accessors = json.GetProperty("accessors");
        Assert.Equal(frames, accessors[sampler.GetProperty("input").GetInt32()].GetProperty("count").GetInt32());
        Assert.Equal(frames * (frames - 1), accessors[sampler.GetProperty("output").GetInt32()].GetProperty("count").GetInt32());
    }

    [MapDataFact]
    public void A_robot_is_one_skinned_mesh_with_its_clips()
    {
        var ais = GameTables.Ais;
        var data = Data;
        var export = new GltfExport(data, ais);
        var models = new EnemyModels(data);
        var robots = ais.Types.Select(NameOf).Distinct().Select(export.Robot).OfType<EnemyType>().ToArray();
        Assert.NotEmpty(robots);
        foreach (var robot in robots)
        {
            var name = NameOf(robot);
            var glb = export.Export(name, RobotSegments.With)!;
            var json = Json(glb);
            var nodes = json.GetProperty("nodes");
            var game = ModelSubject.ForRobot(ais, models, robot.Index)!;
            game.Robot.UpdateTransforms();
            Matrix4x4.Invert(game.Robot.Root.World, out var toRobot);

            // A joint per segment the game assembles, depth first, named seg<i>_<model>.
            var skin = Assert.Single(json.GetProperty("skins").EnumerateArray());
            var joints = skin.GetProperty("joints").EnumerateArray().Select(j => j.GetInt32()).ToArray();
            var parts = game.Parts;
            var expected = parts.Select((p, i) => GltfExport.JointName(i, NameOf(p.Type!))).ToArray();
            Assert.Equal(expected, joints.Select(j => nodes[j].GetProperty("name").GetString()).ToArray());

            // The bind pose is the game's: a joint's inverse bind undoes the segment's placement.
            var binds = MemoryMarshal.Cast<byte, Matrix4x4>(Floats(glb, json, skin.GetProperty("inverseBindMatrices").GetInt32()));
            for (var i = 0; i < parts.Count; i++)
            {
                var bound = parts[i].World * toRobot * binds[i];
                Assert.True(Near(bound, Matrix4x4.Identity), $"{name} joint {i}: {bound}");
            }

            // One skinned mesh holding every corner of every segment's model; the engine's reader takes the file.
            var mesh = Assert.Single(json.GetProperty("meshes").EnumerateArray());
            var accessors = json.GetProperty("accessors");
            var corners = parts.Sum(p => data.LoadModel(p.Type!.ModelFile)?.Faces.Sum(f => f.VertexCount) ?? 0);
            var primitives = mesh.GetProperty("primitives").EnumerateArray().Select(p => p.GetProperty("attributes")).ToArray();
            Assert.Equal(corners, primitives.Sum(a => accessors[a.GetProperty("POSITION").GetInt32()].GetProperty("count").GetInt32()));
            Assert.All(primitives, a => Assert.True(a.TryGetProperty("JOINTS_0", out _) && a.TryGetProperty("WEIGHTS_0", out _)));
            Assert.NotNull(Gltf.Load(glb));

            // The clips are the Model Viewer's names (no block: the frames in order).
            var clips = ModelSubject.AnimsOf(ais, robot).Select(a => a.Name).ToArray();
            var frames = data.LoadModel(robot.ModelFile)!.Frames.Length;
            string[] names = clips.Length > 0 ? clips : frames > 1 ? [GltfExport.AnimationName] : [];
            var read = json.TryGetProperty("animations", out var a) ? a.EnumerateArray().Select(c => c.GetProperty("name").GetString()).ToArray() : [];
            Assert.Equal(names, read);
        }
    }

    // An accessor's bytes in the BIN chunk (tightly packed, as GlbWriter writes them).
    private static ReadOnlySpan<byte> Floats(byte[] glb, JsonElement json, int accessor)
    {
        var a = json.GetProperty("accessors")[accessor];
        var view = json.GetProperty("bufferViews")[a.GetProperty("bufferView").GetInt32()];
        var jsonLength = BinaryPrimitives.ReadInt32LittleEndian(glb.AsSpan(GlbHeader));
        var bin = GlbHeader + ChunkHeader + jsonLength + ChunkHeader;
        return glb.AsSpan(bin + view.GetProperty("byteOffset").GetInt32(), view.GetProperty("byteLength").GetInt32());
    }

    private static bool Near(Matrix4x4 a, Matrix4x4 b)
    {
        const float Tolerance = 1e-3f;
        var d = a - b;
        return new[] { d.M11, d.M12, d.M13, d.M14, d.M21, d.M22, d.M23, d.M24, d.M31, d.M32, d.M33, d.M34, d.M41, d.M42, d.M43, d.M44 }
            .All(v => MathF.Abs(v) < Tolerance);
    }

    [MapDataFact]
    public void Write_names_a_robots_second_file()
    {
        var ais = GameTables.Ais;
        var robot = ais.Types.First(t => t.Segments.Length > 0);
        var dir = Directory.CreateTempSubdirectory("skynet-gltf-").FullName;
        try
        {
            var written = new GltfExport(Data, ais).Write(NameOf(robot), dir).Select(p => Path.GetFileName(p)).ToArray();
            Assert.Equal([NameOf(robot) + GltfExport.Extension, NameOf(robot) + GltfExport.RobotSuffix + GltfExport.Extension], written);
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }
}
