using RetroEng.Audio;
using RetroEng.Content;
using RetroEng.Editor;
using SkyNet.Data;
using SkyNet.Formats;
using SkyNet.Game.Audio;
using SkyNet.Game.Mods;

namespace SkyNet.Edit.Tests;

/// <summary>The sound viewer and music player (editor-tools plan, T4): what plays a sample, "Replace in mod..."
/// writing <c>sounds/NAME.wav</c> as one undo step, the <c>sounds</c> table row, the songs' maptypes.</summary>
[Collection(EditModsCollection.Name)]
public sealed class SoundViewerTests : IDisposable
{
    private const string ModName = "sounds-test";
    private const string Sample = "EXPLO1.RAW";
    private readonly string _dir = Directory.CreateTempSubdirectory("skynet-edit-sounds-").FullName;

    public SoundViewerTests() => ModContent.Unmount();

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

    private string Wav(string name, int frames)
    {
        var path = Path.Combine(Directory.CreateDirectory(Path.Combine(_dir, "picked")).FullName, name);
        File.WriteAllBytes(path, WavWriter.Encode(new float[frames * 2], 22050));
        return path;
    }

    [Fact]
    public void Ids_are_every_id_naming_the_sample()
    {
        Assert.Equal([33], SoundUsage.Ids(Sample));
        Assert.Equal([33], SoundUsage.Ids("explo1.raw"));
        Assert.Equal([48, 49, 50, 51, 52, 53, 54, 68], SoundUsage.Ids("HK2.RAW"));
        Assert.Empty(SoundUsage.Ids("NONE.RAW"));
    }

    [Fact]
    public void Uses_come_from_the_tables_and_the_game_code()
    {
        // One weapon firing id 33, one ammo type hitting with it, a destruction row: each listed once.
        WeaponRow[] weapons = [new(1, 0, 0, 0, 0, 0, 0, 0, 0, 33, 0, 0, -1, -1, 0)];
        AmmoRow[] ammo = [new(2, 0, "gravity", "", 0, 0, 0, 0, 0, -1, 33, 0, 0, 0, 0)];
        DestructionRow[] destruction = [new(7, [], 0, 0, 33), new(8, [], 0, 0, 34)];
        var uses = SoundUsage.Uses(Sample, weapons, ammo, GameTables.Ais, destruction);

        Assert.Contains(new SoundUse(SoundUsage.Weapon, "1 UZI: fire"), uses);
        Assert.Contains(new SoundUse(SoundUsage.Ammo, "2 gravity: impact"), uses);
        Assert.Contains(new SoundUse(SoundUsage.Destruction, "row 7"), uses);
        Assert.DoesNotContain(new SoundUse(SoundUsage.Destruction, "row 8"), uses);
        Assert.Contains(new SoundUse(SoundUsage.Game, "explosion (1 of 4)"), uses);

        // Doors: act 0xDB plays DOORA.RAW (id 40); fire.raw's two ids are told apart.
        Assert.Contains(new SoundUse(SoundUsage.Act, "0xDB sound cue (doors, buttons, lifts)"), SoundUsage.Uses("DOORA.RAW"));
        Assert.Contains(SoundUsage.Uses("FIRE.RAW"), u => u.Kind == SoundUsage.Sprite && u.What.EndsWith("(id 83)", StringComparison.Ordinal));

        // The shipped tables: the robots' scripts and the weapons play something; a sample nobody names, nothing.
        Assert.Contains(SoundIds.Names.SelectMany(SoundUsage.Uses), u => u.Kind == SoundUsage.Robot);
        Assert.Contains(SoundIds.Names.SelectMany(SoundUsage.Uses), u => u.Kind == SoundUsage.Weapon);
        Assert.Empty(SoundUsage.Uses("NONE.RAW"));
    }

    [Fact]
    public void Replace_writes_the_mod_sample_as_one_undo_step()
    {
        var history = new UndoStack();
        var changes = 0;
        var target = Path.Combine(Folder, "sounds", "EXPLO1.wav");
        var first = Wav("boom.wav", 100);

        // In: sounds/EXPLO1.wav, the picked file's bytes; the game's mods read it there.
        Assert.Equal("sounds/EXPLO1.wav", SampleEdit.ModPath(Sample));
        Assert.Equal("EXPLO1.RAW -> sounds/EXPLO1.wav", SampleEdit.Replace(history, Folder, Sample, first, () => changes++));
        Assert.Equal(File.ReadAllBytes(first), File.ReadAllBytes(target));
        ModContent.Mount(Mods, [ModName]);
        var modded = SampleEdit.Modded(Sample)!.Value;
        Assert.Equal(File.ReadAllBytes(first), modded.Bytes);
        Assert.Equal($"{ModName}:sounds/explo1.wav", modded.Origin, ignoreCase: true);
        Assert.Equal(100, modded.Sound.Frames);
        ModContent.Unmount();

        // A second replacement undoes back to the first, then to none.
        var second = Wav("bigger.wav", 200);
        SampleEdit.Replace(history, Folder, Sample, second, () => changes++);
        Assert.True(history.Undo());
        Assert.Equal(File.ReadAllBytes(first), File.ReadAllBytes(target));
        Assert.True(history.Undo());
        Assert.False(File.Exists(target));
        Assert.True(history.Redo());
        Assert.Equal(File.ReadAllBytes(first), File.ReadAllBytes(target));
        Assert.Equal(5, changes);

        // Not a .wav, or not PCM the game decodes: nothing written, no step.
        var text = Path.Combine(_dir, "picked", "notes.wav");
        File.WriteAllText(text, "not a wave");
        Assert.Throws<InvalidDataException>(() => SampleEdit.Replace(history, Folder, Sample, Path.ChangeExtension(first, ".mp3")));
        Assert.Throws<InvalidDataException>(() => SampleEdit.Replace(history, Folder, Sample, text));
        Assert.Equal(1, history.UndoCount);
    }

    [Fact]
    public void Formats_read_from_raw_and_wave()
    {
        Assert.Equal(new SampleFormat(SoundBank.RawRate, 8, 1, 11025), SampleFormat.Of(Sample, new byte[11025]));
        Assert.Equal(1.0, SampleFormat.Of(Sample, new byte[11025])!.Value.Seconds);
        Assert.Equal(new SampleFormat(22050, 16, 2, 300), SampleFormat.Of("X.WAV", WavWriter.Encode(new float[600], 22050)));
        Assert.Null(SampleFormat.Of("X.WAV", [1, 2, 3]));
        Assert.Equal("22050 Hz, 16-bit stereo, 0.01 s", SoundViewer.Describe(new SampleFormat(22050, 16, 2, 300)));
    }

    [Fact]
    public void Settings_row_starts_unchanged_and_saves_as_the_game_reads_it()
    {
        var grid = new TableGrid(new SoundSettingsSource(), null, Folder);
        var history = new UndoStack();
        Assert.Empty(grid.Rows);

        // Keyed by the stem; the new row is the one that changes nothing.
        grid.Put(history, SoundSettingsSource.NewRow("hk2.raw"));
        Assert.Equal(SoundEventRow.Default("HK2"), SoundSettingsSource.RowOf(grid.Row("HK2")!));
        grid.Set(history, "HK2", SoundSettingsSource.ColumnOf(SoundSettingsSource.Loop), 0L);
        grid.Set(history, "HK2", SoundSettingsSource.ColumnOf(SoundSettingsSource.Volume), 0.5);

        // Saved as tables/sounds.json; the game's table reads it through the mod.
        Assert.Equal(["tables/sounds.json"], grid.Save());
        VirtualFileSystem mod = new ContentRoot(Mods).Mount([], [ModName]);
        Assert.Equal(new SoundEventRow("HK2", 0.5, 0, 1, 0, false), Assert.Single(SoundMods.Rows(mod)));
        var again = new TableGrid(new SoundSettingsSource(), null, Folder);
        Assert.Equal(new SoundEventRow("HK2", 0.5, 0, 1, 0, false), SoundSettingsSource.RowOf(again.Rows.Single()));

        // A volume past the game's range is not saved.
        again.Set(history, "HK2", SoundSettingsSource.ColumnOf(SoundSettingsSource.Volume), 3.0);
        Assert.Throws<InvalidDataException>(() => again.Save());
    }

    [Fact]
    public void Songs_name_their_maptypes()
    {
        Assert.Equal(["maptype 1", "maptype 6"], MusicUsage.Uses("t205.hmi"));
        Assert.Equal(["the menus"], MusicUsage.Uses(MusicTracks.Title));
        Assert.Empty(MusicUsage.Uses("T999.HMI"));
        Assert.Equal("3:05", MusicViewer.Time(185.7));
    }

    [Fact]
    public void A_muted_device_plays_a_sample_to_its_end()
    {
        using var audio = new EditorAudio(Output.Muted, _ => null);
        var sound = SoundBank.Decode(Sample, new byte[SoundBank.RawRate / 2])!;
        audio.Tick(0, 0f);
        audio.Play(sound);
        audio.Tick(0.25, 0.25f);
        Assert.Equal(0.5f, audio.Sample()!.Value.Done, 3);

        audio.Tick(1.0, 0.75f);
        Assert.Null(audio.Sample());
        Assert.False(audio.Audible);
    }
}
