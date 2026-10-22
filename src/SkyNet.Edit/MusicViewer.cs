using System.Globalization;
using System.Numerics;
using Hexa.NET.ImGui;
using SkyNet.Game.Audio;

namespace SkyNet.Edit;

/// <summary>Which maptypes play a song (<see cref="MusicTracks.ByMapType"/>), and whether it is the menus'.</summary>
public static class MusicUsage
{
    /// <summary>"maptype 0, maptype 2" / "the menus"; empty when nothing plays it.</summary>
    public static List<string> Uses(string song)
    {
        var uses = MusicTracks.ByMapType.Select((s, i) => (s, i)).Where(p => Same(p.s, song)).Select(p => $"maptype {p.i}").ToList();
        if (Same(song, MusicTracks.Title))
        {
            uses.Add("the menus");
        }

        return uses;
    }

    private static bool Same(string a, string b) => string.Equals(a, b, StringComparison.OrdinalIgnoreCase);
}

/// <summary>
/// The Assets panel's music player (editor-tools plan, T4): an HMI song (T*.HMI, TITLE.HMI) through the
/// game's <see cref="MusicPlayer"/> and MIDI synth, looping, with its length, tracks and what plays it.
/// <code>
///   T200.HMI   4 tracks, 2 711 events, 120 ticks/s, 3:12
///   [Play] [Stop]  [=======-------] 1:24 / 3:12
///   Played by: maptype 0
/// </code>
/// </summary>
public sealed class MusicViewer(EditorAudio audio) : IPlayback
{
    private const float ProgressWidth = 260f;

    /// <summary>The song shown (the last one drawn); null: none.</summary>
    public string? Song { get; set; }

    /// <summary>Whether the song shown is playing.</summary>
    public bool Playing => Song != null && audio.SongAt() is { } p && string.Equals(p.Name, Song, StringComparison.OrdinalIgnoreCase);

    /// <summary>Plays the song shown from its start, or stops it.</summary>
    public void TogglePlay()
    {
        if (Playing)
        {
            audio.StopSong();
            return;
        }

        if (Song != null)
        {
            audio.PlaySong(Song);
        }
    }

    public void Draw(string song)
    {
        Song = song;
        audio.Tick(ImGui.GetTime(), ImGui.GetIO().DeltaTime);
        if (audio.Song(song) is not { } hmi)
        {
            ImGui.TextDisabled("No preview: it does not parse.");
            return;
        }

        var length = hmi.Rate > 0 ? (double)hmi.Length / hmi.Rate : 0;
        ImGui.TextUnformatted(song);
        ImGui.SameLine();
        ImGui.TextDisabled(Inv($"{hmi.Tracks.Length} tracks, {hmi.Events.Length} events, {hmi.Rate} ticks/s, {Time(length)}"));

        if (ImGui.Button("Play"))
        {
            audio.PlaySong(song);
        }

        PlayKey.Tooltip();
        PlayKey.Poll(this, PlayFocus.AssetViewer);

        ImGui.SameLine();
        if (ImGui.Button("Stop"))
        {
            audio.StopSong();
        }

        // How far the song is, while this one plays.
        ImGui.SameLine();
        var at = audio.SongAt() is { } p && string.Equals(p.Name, song, StringComparison.OrdinalIgnoreCase) ? p.Seconds : 0;
        ImGui.ProgressBar(length > 0 ? (float)(at / length) : 0f, new Vector2(ProgressWidth, 0f), $"{Time(at)} / {Time(length)}");
        if (!audio.Audible)
        {
            ImGui.SameLine();
            ImGui.TextDisabled("(no audio device)");
        }

        var uses = MusicUsage.Uses(song);
        ImGui.TextUnformatted(uses.Count == 0 ? "Played by no maptype (a mission instance's music property may name it)." : "Played by: " + string.Join(", ", uses));
    }

    /// <summary>"3:12".</summary>
    public static string Time(double seconds) => Inv($"{(int)seconds / 60}:{(int)seconds % 60:D2}");

    private static string Inv(FormattableString s) => s.ToString(CultureInfo.InvariantCulture);
}
