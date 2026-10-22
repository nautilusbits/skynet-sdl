using System.Buffers.Binary;
using System.Numerics;
using System.Text;
using System.Text.Json.Nodes;
using RetroEng.Editor;
using RetroEng.Models;
using RetroEng.Physics;
using SkyNet.Data;
using SkyNet.Formats;
using SkyNet.Game;
using SkyNet.Game.Enemies;
using SkyNet.Game.Menu;
using SkyNet.Game.Mods;

namespace SkyNet.Edit.Tests;

/// <summary>A whole robot replaced by one glTF model from the Assets panel's Robots: one entry per robot
/// (its segments and wreck parts under it), the replacement staged unsaved (<see cref="RobotModelEdits"/>,
/// seen by the game through the composed mods), written by File > Save, reverted, undone; a .gltf packed
/// into the one .glb the game reads. Skipped without the game data.</summary>
[Collection(EditModsCollection.Name)]
public sealed class RobotReplaceTests : IDisposable
{
    private const string ModName = "robot-replace";
    private const string TRex = "T-REXLEG";
    private const int TRexType = 40, BossType = 42, T800Type = 37, TRexHead = 77, T8Arm = 133;
    private const string SavedPath = "models/T-REXLEG.robot.glb";

    private readonly string _dir = Directory.CreateTempSubdirectory("skynet-edit-robotrep-").FullName;

    public RobotReplaceTests() => ModContent.Unmount();

    public void Dispose()
    {
        ModContent.Unmount();
        try
        {
            Directory.Delete(_dir, recursive: true);
        }
        catch (IOException)
        {
        }
    }

    private static GameData Data => GameData.Open(EditDocumentTests.DataDir!)!;

    private string Mods => Directory.CreateDirectory(Path.Combine(_dir, "mods")).FullName;

    private string Folder => Directory.CreateDirectory(Path.Combine(Mods, ModName)).FullName;

    private string Saved => Path.Combine(Folder, SavedPath.Replace('/', Path.DirectorySeparatorChar));

    // The T-rex's template where a modder would pick it.
    private string Picked()
    {
        var path = Path.Combine(Directory.CreateDirectory(Path.Combine(_dir, "picked")).FullName, TRex + ".robot.glb");
        File.WriteAllBytes(path, new GltfExport(Data, GameTables.Ais).Export(TRex, RobotSegments.With)!);
        return path;
    }

    // The mods as the editor mounts them with the unsaved edits over the save mod.
    private void Mount(RobotModelEdits edits) => ModContent.Mount(edits.Compose(Mods, [ModName], ModName));

    [MapDataFact]
    public void The_robots_list_holds_whole_robots_with_their_parts_under_them()
    {
        var ais = GameTables.Ais;
        var ids = new RobotAssets(ais).Enumerate(RobotAssets.Robots).Select(e => int.Parse(e.Id, System.Globalization.CultureInfo.InvariantCulture)).ToHashSet();

        // Walkers and the boss are entries; the T-rex's head and a T800's wreck arm are parts, not entries.
        Assert.Contains(T800Type, ids);
        Assert.Contains(BossType, ids);
        Assert.DoesNotContain(TRexHead, ids);
        Assert.DoesNotContain(T8Arm, ids);

        // A robot's parts: its segments as the game assembles them (root excluded), then its wreck parts.
        var boss = ModelSubject.ForRobot(ais, new EnemyModels(Data), BossType)!;
        var parts = RobotAssets.Parts(ais, BossType);
        Assert.Equal(boss.Parts.Skip(1).Select(p => p.Type!.Index), parts.Where(p => p.Kind == RobotPartKind.Segment).Select(p => p.Type));
        Assert.Contains(RobotAssets.Parts(ais, T800Type), p => p.Kind == RobotPartKind.Wreck && p.Type == T8Arm);
    }

    [MapDataFact]
    public void A_walker_without_segments_exports_a_template_with_its_clips()
    {
        var ais = GameTables.Ais;
        var glb = new GltfExport(Data, ais).Export(ais.Types[T800Type])!;
        var model = Gltf.LoadAnimated(glb);

        Assert.Equal(RobotAnims.Of(ais, ais.Types[T800Type]).Select(a => a.Name), model.Clips.Select(c => c.Name));
    }

    [MapDataFact]
    public void Replace_stays_unsaved_until_save_and_the_game_draws_it()
    {
        var history = new UndoStack();
        var edits = new RobotModelEdits { ModFolder = Folder };
        var changes = 0;
        edits.Changed += () => changes++;

        edits.Replace(history, TRex, Picked());

        // Staged: nothing on disk, the game reads it through the editor's mount.
        Assert.False(File.Exists(Saved));
        Assert.True(edits.Dirty);
        Assert.Equal(RobotModelState.Unsaved, edits.StateOf(TRex));
        Mount(edits);
        Assert.NotNull(ModContent.Robot(TRex));

        // File > Save writes the file the game loads the robot from.
        Assert.Equal([SavedPath], edits.Save());
        Assert.False(edits.Dirty);
        Assert.True(File.Exists(Saved));
        ModContent.Mount(Mods, [ModName]);
        Assert.Equal(RigSource.Mod, Posed(TRex).Rig.Source);
        Assert.Equal(RobotModelState.Saved, edits.StateOf(TRex));
        Assert.Equal(2, changes);
    }

    [MapDataFact]
    public void Undo_takes_an_unsaved_replace_back()
    {
        var history = new UndoStack();
        var edits = new RobotModelEdits { ModFolder = Folder };

        edits.Replace(history, TRex, Picked());
        Assert.True(history.Undo());

        Assert.False(edits.Dirty);
        Assert.Equal(RobotModelState.None, edits.StateOf(TRex));
        Assert.True(history.Redo());
        Assert.Equal(RobotModelState.Unsaved, edits.StateOf(TRex));
    }

    [MapDataFact]
    public void Revert_hides_the_saved_model_until_save_deletes_it()
    {
        var history = new UndoStack();
        var edits = new RobotModelEdits { ModFolder = Folder };
        edits.Replace(history, TRex, Picked());
        edits.Save();

        edits.Revert(history, TRex);

        // Unsaved: the file is still there, the game draws the DOS robot.
        Assert.True(File.Exists(Saved));
        Assert.Equal(RobotModelState.Reverted, edits.StateOf(TRex));
        Mount(edits);
        Assert.Null(ModContent.Robot(TRex));
        Assert.Equal(RigSource.Dos, Posed(TRex).Rig.Source);

        // Undo: the replacement again; redo and save: the file gone.
        Assert.True(history.Undo());
        Assert.False(edits.Dirty);
        Assert.True(history.Redo());
        edits.Save();
        Assert.False(File.Exists(Saved));
        Assert.Equal(RobotModelState.None, edits.StateOf(TRex));

        // Nothing left to revert.
        var undos = history.UndoCount;
        edits.Revert(history, TRex);
        Assert.Equal(undos, history.UndoCount);
    }

    [MapDataFact]
    public void A_gltf_with_its_files_beside_it_is_packed_into_one_glb()
    {
        var gltf = Unpacked(File.ReadAllBytes(Picked()), Path.Combine(_dir, "gltf"));
        var edits = new RobotModelEdits { ModFolder = Folder };

        edits.Replace(new UndoStack(), TRex, gltf);
        edits.Save();

        var original = Gltf.LoadAnimated(File.ReadAllBytes(Picked()));
        var packed = Gltf.LoadAnimated(File.ReadAllBytes(Saved));
        Assert.Equal(original.Clips.Select(c => c.Name), packed.Clips.Select(c => c.Name));
        Assert.Equal(original.Parts.Sum(p => p.Indices.Length), packed.Parts.Sum(p => p.Indices.Length));
        Assert.Equal(original.Images.Count, packed.Images.Count);

        // Not a model: refused, nothing staged.
        var bad = Path.Combine(_dir, "bad.gltf");
        File.WriteAllText(bad, "{}");
        Assert.Throws<InvalidDataException>(() => edits.Replace(new UndoStack(), TRex, bad));
        Assert.False(edits.Dirty);
    }

    [MapDataFact]
    public void The_viewer_maps_each_block_to_the_clip_the_game_plays()
    {
        var ais = GameTables.Ais;
        var anims = RobotAnims.Of(ais, ais.Types[T800Type]);

        var map = RobotAssets.ClipMap(anims, ["Armature|Walk", "fire"]);

        Assert.Equal("Armature|Walk", map.Single(m => m.Block == "walk").Clip);
        Assert.All(map.Where(m => m.Block.StartsWith("fire", StringComparison.Ordinal)), m => Assert.Equal("fire", m.Clip));
        Assert.Contains(map, m => m.Clip == null);
    }

    // The robot built on model name, posed in the enhanced look by the game.
    private static RobotPose Posed(string name)
    {
        var data = Data;
        var robots = new Robots(GameTables.Ais, new EnemyModels(data), new CollisionWorld());
        var robot = robots.Add(new GltfExport(data, GameTables.Ais).Robot(name)!.Index, Vector3.Zero);
        return new RobotLooks(new TextureStore(null, data)).Pose(robot, Look.Enhanced, robots.Ais);
    }

    // A .glb as a .gltf with its buffer in a .bin and its first image in a .png beside it.
    private static string Unpacked(byte[] glb, string dir)
    {
        Directory.CreateDirectory(dir);
        var jsonLength = BinaryPrimitives.ReadInt32LittleEndian(glb.AsSpan(12));
        var json = JsonNode.Parse(glb.AsSpan(20, jsonLength))!.AsObject();
        var binStart = 20 + jsonLength + 8;
        var bin = glb.AsSpan(binStart, BinaryPrimitives.ReadInt32LittleEndian(glb.AsSpan(20 + jsonLength))).ToArray();
        File.WriteAllBytes(Path.Combine(dir, "robot.bin"), bin);
        json["buffers"]![0]!["uri"] = "robot.bin";
        if (json["images"] is JsonArray { Count: > 0 } images)
        {
            var image = images[0]!.AsObject();
            var view = json["bufferViews"]![(int)image["bufferView"]!]!;
            var offset = (int?)view["byteOffset"] ?? 0;
            File.WriteAllBytes(Path.Combine(dir, "skin.png"), bin.AsSpan(offset, (int)view["byteLength"]!).ToArray());
            image.Remove("bufferView");
            image.Remove("mimeType");
            image["uri"] = "skin.png";
        }

        var path = Path.Combine(dir, "robot.gltf");
        File.WriteAllText(path, json.ToJsonString(), Encoding.UTF8);
        return path;
    }
}
