using System.Numerics;
using RetroEng.Audio;
using RetroEng.Editor;
using SkyNet.Data;
using SkyNet.Formats;
using SkyNet.Game.Audio;
using SkyNet.Game.Mods;

namespace SkyNet.Edit.Tests;

/// <summary>Space = play/pause per viewer, and "Replace with 3D model..." (<see cref="ModelReplace"/>): a prop's
/// models/NAME.glb + NAME.json and a robot's models/NAME.robot.glb written and removed as one undo step each,
/// the sidecar's fields read back by the game's loader.</summary>
[Collection(EditModsCollection.Name)]
public sealed class ModelReplaceTests : IDisposable
{
    private const string ModName = "models-test";
    private const string Prop = "T800PST", Robot = "HVYTRRT";
    private static readonly PlayKeyInput Space = new(Space: true, Focused: true, Typing: false);

    private readonly string _dir = Directory.CreateTempSubdirectory("skynet-edit-models-").FullName;

    public ModelReplaceTests() => ModContent.Unmount();

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

    private string Mods => Directory.CreateDirectory(Path.Combine(_dir, "mods")).FullName;

    private string Folder => Directory.CreateDirectory(Path.Combine(Mods, ModName)).FullName;

    private string InMod(string path) => Path.Combine(Folder, path.Replace('/', Path.DirectorySeparatorChar));

    // A .glb exported from the data (the robot's as its template), where a modder would pick it.
    private string Picked(string model, RobotSegments segments)
    {
        var data = GameData.Open(EditDocumentTests.DataDir!)!;
        var path = Path.Combine(Directory.CreateDirectory(Path.Combine(_dir, "picked")).FullName, model + ".glb");
        File.WriteAllBytes(path, new GltfExport(data, GameTables.Ais).Export(model, segments)!);
        return path;
    }

    [Fact]
    public void Space_toggles_only_the_focused_viewer_and_not_while_typing()
    {
        var sprite = new SpriteViewer(null!, null);
        var effects = new EffectsWindow(() => null, () => new UndoStack());
        var unfocused = Space with { Focused = false };

        // The focused one pauses, the other plays on; again: it plays.
        Assert.True(PlayKey.Handle(sprite, Space));
        Assert.False(PlayKey.Handle(effects, unfocused));
        Assert.False(sprite.Playing);
        Assert.True(effects.Playing);
        PlayKey.Handle(sprite, Space);
        Assert.True(sprite.Playing);

        // Typing in a field, or no Space: nothing.
        Assert.False(PlayKey.Handle(effects, Space with { Typing = true }));
        Assert.False(PlayKey.Handle(effects, Space with { Space = false }));
        Assert.True(effects.Playing);
        Assert.True(PlayKey.Handle(effects, Space));
        Assert.False(effects.Playing);
    }

    [Fact]
    public void Space_plays_and_stops_the_sound_shown()
    {
        using var audio = new EditorAudio(Output.Muted, _ => null);
        var sound = new SoundViewer(new SoundHost(audio));
        sound.Select("EXPLO1.RAW", new byte[SoundBank.RawRate]);
        audio.Tick(0, 0f);

        Assert.True(PlayKey.Handle(sound, Space));
        Assert.True(sound.Playing);
        Assert.False(PlayKey.Handle(sound, Space with { Typing = true }));
        Assert.True(sound.Playing);
        PlayKey.Handle(sound, Space);
        Assert.False(sound.Playing);
    }

    [MapDataFact]
    public void Space_toggles_a_models_animation_and_a_song()
    {
        var data = GameData.Open(EditDocumentTests.DataDir!)!;
        var model = ModelSubject.ForModel(new SkyNet.Game.Enemies.EnemyModels(data), Prop + ".3D")!;
        Assert.True(PlayKey.Handle(model, Space));
        Assert.False(model.Playing);

        using var audio = new EditorAudio(Output.Muted, data.ReadLoose);
        var music = new MusicViewer(audio) { Song = "T200.HMI" };
        PlayKey.Handle(music, Space);
        Assert.True(music.Playing);
        PlayKey.Handle(music, Space);
        Assert.False(music.Playing);
    }

    [MapDataFact]
    public void Replace_writes_the_model_and_its_sidecar_as_one_undo_step()
    {
        var history = new UndoStack();
        var changes = 0;
        var glb = Picked(Prop, RobotSegments.Without);

        ModelReplace.Replace(history, Folder, Prop + ".3D", ModelTarget.Prop, glb, dosFrames: 84, () => changes++);

        Assert.Equal(File.ReadAllBytes(glb), File.ReadAllBytes(InMod("models/T800PST.glb")));
        Assert.True(File.Exists(InMod("models/T800PST.json")));
        Assert.Equal(1, history.UndoCount);
        ModContent.Mount(Mods, [ModName]);
        var read = ModContent.Model(Prop)!;
        Assert.True(read.Sidecar.AllFrames);
        Assert.Equal((ModelFit.Height, ModelAlign.Base, 1f), (read.Sidecar.Fit, read.Sidecar.Align, read.Sidecar.Scale));
        ModContent.Unmount();

        // One undo takes both files back, redo puts both in.
        Assert.True(history.Undo());
        Assert.False(File.Exists(InMod("models/T800PST.glb")));
        Assert.False(File.Exists(InMod("models/T800PST.json")));
        Assert.True(history.Redo());
        Assert.True(File.Exists(InMod("models/T800PST.json")));

        // Remove: both gone in one step, back with one undo.
        ModelReplace.Remove(history, Folder, Prop, ModelTarget.Prop, () => changes++);
        Assert.False(ModelReplace.Has(Folder, Prop, ModelTarget.Prop));
        Assert.False(File.Exists(InMod("models/T800PST.json")));
        Assert.True(history.Undo());
        Assert.True(ModelReplace.Has(Folder, Prop, ModelTarget.Prop));
        Assert.Equal(5, changes);

        // Not a glTF the game reads: nothing written.
        var bad = Path.Combine(_dir, "picked", "bad.glb");
        File.WriteAllText(bad, "not a model");
        Assert.Throws<InvalidDataException>(() => ModelReplace.Replace(history, Folder, Prop, ModelTarget.Prop, bad));
        Assert.Equal(1, history.UndoCount);
    }

    [MapDataTheory]
    [InlineData("BLDG14")]
    [InlineData("CAR0S")]
    public void An_exported_model_stands_in_for_itself_exactly(string model)
    {
        var data = GameData.Open(EditDocumentTests.DataDir!)!;
        var dos = data.LoadModel(model + ".3D")!;
        ModelReplace.Replace(new UndoStack(), Folder, model, ModelTarget.Prop, Picked(model, RobotSegments.Without));
        ModContent.Mount(Mods, [ModName]);
        var r = ModContent.Model(model)!;

        // The game's replacement: every DOS triangle, in the DOS model's box, placed by the identity.
        var corners = dos.Faces.SelectMany(f => f.Indices).Select(i => dos.Vertices[i].ToYUp()).ToArray();
        var box = (corners.Aggregate(Vector3.Min), corners.Aggregate(Vector3.Max));
        Assert.True(r.Covers(0));
        Assert.Equal(dos.Faces.Sum(f => f.VertexCount - 2), r.Model.Triangles);
        Assert.Equal(box, r.Model.Bounds);
        var placed = r.Sidecar.Placement(r.Model, box);
        for (var i = 0; i < 16; i++)
        {
            Assert.Equal(Matrix4x4.Identity[i / 4, i % 4], placed[i / 4, i % 4], 3);
        }
    }

    [MapDataFact]
    public void Replace_writes_a_robot_as_one_undo_step()
    {
        var history = new UndoStack();
        var glb = Picked(Robot, RobotSegments.With);

        ModelReplace.Replace(history, Folder, Robot, ModelTarget.Robot, glb);

        Assert.Equal("models/HVYTRRT.robot.glb", ModelReplace.GlbPath(Robot, ModelTarget.Robot));
        Assert.True(ModelReplace.Has(Folder, Robot, ModelTarget.Robot));
        Assert.False(File.Exists(InMod("models/HVYTRRT.json")));
        ModContent.Mount(Mods, [ModName]);
        Assert.NotNull(ModContent.Robot(Robot));
        ModContent.Unmount();

        Assert.True(history.Undo());
        Assert.False(ModelReplace.Has(Folder, Robot, ModelTarget.Robot));
    }

    [MapDataFact]
    public void Sidecar_edits_round_trip_through_the_games_loader()
    {
        var history = new UndoStack();
        ModelReplace.Replace(history, Folder, Prop, ModelTarget.Prop, Picked(Prop, RobotSegments.Without));
        var sidecar = InMod("models/T800PST.json");
        File.WriteAllText(sidecar, """{ "licence": "CC0", "frameModels": [ { "model": "OTHER", "frames": "1-3" } ] }""");
        var (frames, all) = ModelReplace.ParseFrames("0, 2-4, 7");
        var edited = ModelReplace.ReadSidecar(Folder, Prop)! with
        {
            Rotation = new Vector3(0f, 90f, -15.5f), Fit = ModelFit.Box, Scale = 2.5f, Align = ModelAlign.Centre,
            Offset = new Vector3(1f, 2f, 3f), Frames = frames, AllFrames = all,
        };

        ModelReplace.SetSidecar(history, Folder, Prop, edited);

        ModContent.Mount(Mods, [ModName]);
        var s = ModelReplacement.Load(ModContent.Files!, Prop)!.Sidecar;
        Assert.Equal((edited.Rotation, edited.Fit, edited.Scale, edited.Align, edited.Offset), (s.Rotation, s.Fit, s.Scale, s.Align, s.Offset));
        Assert.Equal([0, 2, 3, 4, 7], s.Frames.Order());
        Assert.False(s.AllFrames);
        Assert.Equal("OTHER", Assert.Single(s.FrameModels).Model);
        Assert.Contains("CC0", File.ReadAllText(sidecar), StringComparison.Ordinal);
        Assert.Equal("0, 2-4, 7", ModelReplace.FramesText(s.Frames, s.AllFrames));

        // One undo: the file as it was.
        Assert.True(history.Undo());
        Assert.DoesNotContain("rotation", File.ReadAllText(sidecar), StringComparison.Ordinal);
        Assert.Throws<FormatException>(() => ModelReplace.ParseFrames("0, x"));
    }

    private sealed class SoundHost(EditorAudio audio) : ISoundHost
    {
        public EditorAudio Audio => audio;

        public string? SaveMod => null;

        public void Replace(string sample)
        {
        }

        public object?[]? Settings(string sample) => null;

        public void EditSettings(string sample)
        {
        }
    }
}
