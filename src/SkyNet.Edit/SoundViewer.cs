using System.Globalization;
using System.Numerics;
using Hexa.NET.ImGui;
using RetroEng.Audio;

namespace SkyNet.Edit;

/// <summary>What the sound viewer asks the editor: the audio, the save mod, the actions.</summary>
public interface ISoundHost
{
    EditorAudio Audio { get; }

    /// <summary>The mod "Replace in mod..." writes into; null: none chosen.</summary>
    string? SaveMod { get; }

    /// <summary>Asks for a .wav and makes it <paramref name="sample"/>'s replacement in the save mod.</summary>
    void Replace(string sample);

    /// <summary>The <c>sounds</c> row of <paramref name="sample"/> as the Tables grid has it; null: none.</summary>
    object?[]? Settings(string sample);

    /// <summary>Adds (when missing) and shows <paramref name="sample"/>'s <c>sounds</c> row in the Tables window.</summary>
    void EditSettings(string sample);
}

/// <summary>
/// The Assets panel's sound viewer (editor-tools plan, T4): a sample's format, waveform and playback, the
/// mod's replacement (the one the game plays) with the original a toggle away, and what plays it.
/// <code>
///   EXPLO1.RAW  id 33   11025 Hz, 8-bit mono, 0.84 s
///   Mod mine: sounds/EXPLO1.wav  22050 Hz, 16-bit mono, 0.90 s   [x] Hear the original
///   [Play] [Stop]  [=====-----] 0.41 / 0.84 s
///   [Replace in mod...] [Edit sound settings]  volume 1, range 1643.5, pitch 1, priority 0, loop no
///   Played by 3:  ammo 5 gravity: impact | robot 12 hk: script | game: explosion (1 of 4)
///   (waveform, the playhead over it)
/// </code>
/// </summary>
public sealed class SoundViewer(ISoundHost host) : IPlayback
{
    private const int WaveColumns = 512;
    private const float UsesHeight = 120f, ProgressWidth = 220f;
    private static readonly Vector4 ModColour = new(0.55f, 0.85f, 0.55f, 1f);
    private static readonly Vector4 PlayheadColour = new(1f, 0.8f, 0.3f, 1f);

    private static readonly IReadOnlyList<GridColumn> SettingColumns = new SoundSettingsSource().Columns;

    private sealed record Clip(Sound Sound, SampleFormat Format, float[] Wave);

    private string? _sample;
    private Clip? _original, _modded;
    private string? _modOrigin;
    private List<SoundUse> _uses = [];
    private bool _hearOriginal;

    /// <summary>The sample shown; null: none.</summary>
    public string? Sample => _sample;

    /// <summary>Whether the mod's replacement is shown and played (else the original).</summary>
    public bool ShowsMod => _modded != null && !_hearOriginal;

    /// <summary>Shows <paramref name="sample"/>, <paramref name="original"/> its archive bytes.</summary>
    public void Select(string sample, byte[] original)
    {
        host.Audio.Stop();
        (_sample, _original, _hearOriginal) = (sample, ClipOf(sample, original), false);
        _uses = SoundUsage.Uses(sample);
        Refresh();
    }

    /// <summary>Reads the mods' replacement again (one was written or taken back).</summary>
    public void Refresh()
    {
        if (_sample == null)
        {
            return;
        }

        // The game's own pick (SoundMods.Effective): its sound plays, its file gives the format.
        (_modded, _modOrigin) = SampleEdit.Modded(_sample) is { } m && SampleFormat.Of(SampleEdit.ModPath(_sample), m.Bytes) is { } f
            ? (new Clip(m.Sound, f, Peaks(m.Sound)), m.Origin)
            : ((Clip?)null, (string?)null);
    }

    public void Draw()
    {
        if (_sample is not { } sample || _original is not { } original)
        {
            ImGui.TextDisabled("No preview: it does not load.");
            return;
        }

        host.Audio.Tick(ImGui.GetTime(), ImGui.GetIO().DeltaTime);
        var shown = ShowsMod ? _modded! : original;
        Header(sample, original);
        Playback(shown);
        Actions(sample);
        Uses();
        Waveform(shown, ImGui.GetContentRegionAvail());
    }

    private void Header(string sample, Clip original)
    {
        var ids = SoundUsage.Ids(sample);
        ImGui.TextUnformatted(sample);
        ImGui.SameLine();
        ImGui.TextDisabled((ids.Count switch { 0 => "no DOS id", 1 => "id ", _ => "ids " }) + string.Join(", ", ids) + "   " + Describe(original.Format));
        if (_modded is not { } modded)
        {
            return;
        }

        ImGui.TextColored(ModColour, $"Plays {_modOrigin}   {Describe(modded.Format)}");
        ImGui.SameLine();
        if (ImGui.Checkbox("Hear the original", ref _hearOriginal))
        {
            host.Audio.Stop();
        }
    }

    /// <summary>Whether the sample shown (the mod's or the original) is playing.</summary>
    public bool Playing => Shown is { } clip && host.Audio.Sample() is { } s && ReferenceEquals(s.Sound, clip.Sound);

    /// <summary>Plays the sample shown from its start, or stops it.</summary>
    public void TogglePlay()
    {
        if (Playing)
        {
            host.Audio.Stop();
            return;
        }

        if (Shown is { } clip)
        {
            host.Audio.Play(clip.Sound);
        }
    }

    private Clip? Shown => ShowsMod ? _modded : _original;

    // Play/stop and the bar of how far the voice is.
    private void Playback(Clip clip)
    {
        if (ImGui.Button("Play"))
        {
            host.Audio.Play(clip.Sound);
        }

        PlayKey.Tooltip();
        PlayKey.Poll(this, PlayFocus.AssetViewer);

        ImGui.SameLine();
        if (ImGui.Button("Stop"))
        {
            host.Audio.Stop();
        }

        ImGui.SameLine();
        var done = host.Audio.Sample() is { } s && ReferenceEquals(s.Sound, clip.Sound) ? s.Done : 0f;
        var seconds = clip.Format.Seconds;
        ImGui.ProgressBar(done, new Vector2(ProgressWidth, 0f), Inv($"{done * seconds:0.00} / {seconds:0.00} s"));
        if (!host.Audio.Audible)
        {
            ImGui.SameLine();
            ImGui.TextDisabled("(no audio device)");
        }
    }

    // "Replace in mod...", "Edit sound settings" and the row's values.
    private void Actions(string sample)
    {
        ImGui.BeginDisabled(host.SaveMod == null);
        if (ImGui.Button("Replace in mod..."))
        {
            host.Replace(sample);
        }

        ImGui.EndDisabled();
        if (host.SaveMod == null && ImGui.IsItemHovered(ImGuiHoveredFlags.AllowWhenDisabled))
        {
            ImGui.SetTooltip("No mod to write to: File > Save As picks one.");
        }

        ImGui.SameLine();
        if (ImGui.Button("Edit sound settings"))
        {
            host.EditSettings(sample);
        }

        if (host.Settings(sample) is not { } row)
        {
            return;
        }

        var cells = SettingColumns.Skip(1).Select((c, i) => $"{c.Key} {c.Label(row[i + 1])}");
        ImGui.SameLine();
        ImGui.TextColored(ModColour, string.Join(", ", cells));
    }

    // What plays the sample: its kind, then what.
    private void Uses()
    {
        ImGui.TextUnformatted(_uses.Count == 0 ? "Played by nothing in the tables." : Inv($"Played by {_uses.Count}:"));
        if (_uses.Count == 0)
        {
            return;
        }

        var height = MathF.Min(UsesHeight, _uses.Count * ImGui.GetTextLineHeightWithSpacing() + 4f);
        if (ImGui.BeginChild("##soundUses", new Vector2(0f, height)))
        {
            foreach (var use in _uses)
            {
                ImGui.TextDisabled(use.Kind);
                ImGui.SameLine(110f);
                ImGui.TextUnformatted(use.What);
            }
        }

        ImGui.EndChild();
    }

    // The peaks, and the playhead while the clip plays.
    private void Waveform(Clip clip, Vector2 size)
    {
        size = Vector2.Max(size, new Vector2(64f, 32f));
        var at = ImGui.GetCursorScreenPos();
        ImGui.Dummy(size);
        var list = ImGui.GetWindowDrawList();
        var colour = ImGui.GetColorU32(ImGuiCol.PlotLines);
        var middle = at.Y + size.Y * 0.5f;
        var wave = clip.Wave;
        for (var c = 0; c < wave.Length; c++)
        {
            var x = at.X + (c + 0.5f) * size.X / wave.Length;
            var half = MathF.Max(wave[c] * size.Y * 0.5f, 0.5f);
            list.AddLine(new Vector2(x, middle - half), new Vector2(x, middle + half), colour);
        }

        if (host.Audio.Sample() is { } s && ReferenceEquals(s.Sound, clip.Sound))
        {
            var x = at.X + s.Done * size.X;
            list.AddLine(new Vector2(x, at.Y), new Vector2(x, at.Y + size.Y), ImGui.GetColorU32(PlayheadColour));
        }
    }

    private static Clip? ClipOf(string name, byte[] bytes)
    {
        if (SampleFormat.Of(name, bytes) is not { } format || SkyNet.Game.Audio.SoundBank.Decode(name, bytes) is not { } sound)
        {
            return null;
        }

        return new Clip(sound, format, Peaks(sound));
    }

    /// <summary>A sound's peak level per column, over its channels.</summary>
    public static float[] Peaks(Sound sound)
    {
        var samples = sound.Samples;
        var wave = new float[Math.Max(1, Math.Min(WaveColumns, sound.Frames))];
        for (var c = 0; c < wave.Length; c++)
        {
            var (from, to) = ((long)c * samples.Length / wave.Length, (long)(c + 1) * samples.Length / wave.Length);
            for (var i = from; i < to; i++)
            {
                wave[c] = MathF.Max(wave[c], MathF.Abs(samples[i]));
            }
        }

        return wave;
    }

    /// <summary>"11025 Hz, 8-bit mono, 0.84 s".</summary>
    public static string Describe(SampleFormat f) =>
        Inv($"{f.Rate} Hz, {f.Bits}-bit {(f.Channels == 1 ? "mono" : f.Channels == 2 ? "stereo" : Inv($"{f.Channels} channels"))}, {f.Seconds:0.00} s");

    private static string Inv(FormattableString s) => s.ToString(CultureInfo.InvariantCulture);
}
