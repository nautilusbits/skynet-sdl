using RetroEng.Audio;
using RetroEng.Commands;
using SkyNet.Formats;

namespace SkyNet.Edit;

/// <summary>The sound viewer's and music player's side in the editor (editor-tools plan, T4): the audio,
/// "Replace in mod..." (sound.replace), the <c>sounds</c> row in the Tables grid (sound.settings),
/// sound.uses, music.play and music.stop.</summary>
internal sealed partial class EditorApp : ISoundHost
{
    private EditorAudio? _audio;

    // The viewers for the assets of a data set (its songs), their commands registered.
    private void SoundsFor(GameAssets assets, GameData data)
    {
        _audio?.Dispose();
        _audio = new EditorAudio(_options.Offscreen ? Output.Muted : Output.Speakers, data.ReadLoose);
        assets.SoundView = new SoundViewer(this);
        assets.MusicView = new MusicViewer(_audio);
        _commands.Add("sound.replace", "sound.replace <sample> <file.wav>", a => SoundReplace(a.Word(0), a.Word(1)),
            "copies the .wav into the save mod as sounds/NAME.wav (the sample the game plays); Edit > Undo takes it back");
        _commands.Add("sound.uses", "sound.uses <sample>", a => SoundUses(a.Word(0)),
            "the DOS ids naming the sample and the weapons, ammo, robots, acts and game code playing it");
        _commands.Add("sound.settings", "sound.settings <sample>", a => SoundSettings(a.Word(0)),
            "adds (when missing) and shows the sample's row of the sounds table (Tables > Save writes it)");
        _commands.Add("music.play", "music.play <song>", a => MusicPlay(a.Word(0)), "plays an HMI song (T200.HMI, TITLE.HMI) looping");
        _commands.Add("music.stop", "music.stop", _ => MusicStop(), "stops the song");
    }

    EditorAudio ISoundHost.Audio => _audio!;

    string? ISoundHost.SaveMod => SaveFolder() != null ? _saveMod : null;

    void ISoundHost.Replace(string sample) =>
        _dialogs.OpenFile(_window, null, picked =>
        {
            if (picked != null)
            {
                RunCommand($"sound.replace {sample} \"{picked}\"");
            }
        }, $"Replace sound {sample} with a .wav");

    object?[]? ISoundHost.Settings(string sample) => _tables?.Grid(SoundSettingsSource.TableName)?.Row(SoundSettingsSource.KeyOf(sample));

    void ISoundHost.EditSettings(string sample) => RunCommand($"sound.settings {sample}");

    // sound.replace: the .wav into the save mod (an undo step), the mod mounted again, the viewer and list told.
    private string SoundReplace(string sample, string wav)
    {
        if (SaveFolder() is not { } folder)
        {
            return "No mod to write to (File > Save As picks one)";
        }

        try
        {
            return SampleEdit.Replace(EditHistory, folder, sample, wav, SampleChanged) + "; Edit > Undo takes it back";
        }
        catch (Exception e) when (e is IOException or InvalidDataException or UnauthorizedAccessException)
        {
            return $"{sample}: not replaced ({e.Message})";
        }
    }

    // A replacement written or taken back: mounted again (a folder's files are listed at mount).
    private void SampleChanged()
    {
        if (_saveMod != null)
        {
            MountOnTop(_saveMod);
        }

        _assets?.SoundView?.Refresh();
        _assetsPanel?.Browser.Refresh();
    }

    // sound.uses: the ids, then each use as "kind: what".
    private static string SoundUses(string sample)
    {
        var ids = SoundUsage.Ids(sample);
        var uses = SoundUsage.Uses(sample);
        var lines = new List<string> { $"{sample}: ids [{string.Join(", ", ids)}], {uses.Count} uses" };
        lines.AddRange(uses.Select(u => $"  {u.Kind}: {u.What}"));
        return string.Join("\n", lines);
    }

    // sound.settings: the row added at the sample's DOS values (an undo step) when missing, then shown.
    private string SoundSettings(string sample)
    {
        if (_tables?.Grid(SoundSettingsSource.TableName) is not { } grid)
        {
            return $"{SoundSettingsSource.TableName} does not read (see the log)";
        }

        var key = SoundSettingsSource.KeyOf(sample);
        var said = grid.Row(key) == null ? grid.Put(EditHistory, SoundSettingsSource.NewRow(key)) : $"{SoundSettingsSource.TableName} {key}";
        _tables.Show(SoundSettingsSource.TableName, key);
        return said;
    }

    private string MusicPlay(string song)
    {
        AssetsFor(_data);
        if (_audio!.Song(song) == null)
        {
            return $"{song}: not a song in the data";
        }

        _audio.PlaySong(song);
        return $"{song.ToUpperInvariant()} playing{(_audio.Audible ? "" : " (no audio device)")}";
    }

    private string MusicStop()
    {
        _audio?.StopSong();
        return "Music stopped";
    }
}
