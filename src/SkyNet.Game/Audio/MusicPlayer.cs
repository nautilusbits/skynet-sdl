using RetroEng.Audio;
using RetroEng.Diagnostics;
using SkyNet.Formats;
using SkyNet.Game.DevTools;

namespace SkyNet.Game.Audio;

/// <summary>
/// The score: HMI songs rendered by <see cref="MidiSynth"/> (or <see cref="SoundFontSynth"/>,
/// <see cref="UseSoundFont"/>) on a producer thread into a
/// <see cref="StreamRing"/> that one streamed voice plays on the music bus.
/// <code>
///   Play(name) ──► producer thread: parse, build instruments, fill the ring ──► PlayStream (music bus)
///                  then keeps the ring ≥ <see cref="TargetSeconds"/> full, rendering in small chunks
/// </code>
/// A new song gets a new ring and voice, filled before it starts, so a change never plays the old
/// song's tail or a gap. Songs loop unless played with <c>loop: false</c>.
/// </summary>
public sealed class MusicPlayer : IDisposable
{
    /// <summary>The ring's size: half a second.</summary>
    public const float RingSeconds = 0.5f;
    /// <summary>How full the producer keeps the ring.</summary>
    public const float TargetSeconds = 0.3f;
    private const int ChunkFrames = 1024;
    /// <summary>The Godot port's default music volume.</summary>
    public const float DefaultVolume = 0.6f;

    private readonly Mixer _mixer;
    private readonly Func<string, byte[]?> _readSong;
    private readonly Dictionary<string, HmiFile?> _songs = new(StringComparer.OrdinalIgnoreCase);
    private readonly Thread _thread;
    private readonly AutoResetEvent _wake = new(false);
    private readonly Lock _gate = new();
    private readonly float[] _chunk = new float[ChunkFrames * 2];

    // Requests, under _gate.
    private (string Name, bool Loop)? _request;
    private bool _stopRequest;
    private (string? Id, Func<byte[]?>? Read)? _fontRequest;
    private volatile bool _quit;

    // Under _gate: the .sf2 last asked for, so asking again is a no-op.
    private string? _fontAsked;

    // Producer thread state.
    private IMusicSynth _synth;
    private MeltySynth.SoundFont? _font;
    private string? _fontId;
    private (string Name, bool Loop)? _started;
    /// <summary>The song to start again on the port's synth after the SoundFont synth failed.</summary>
    private (string Name, bool Loop)? _retry;
    private StreamRing? _ring;
    private VoiceHandle _voice = VoiceHandle.None;
    private volatile string? _current;
    private float _volume = DefaultVolume;
    private long _underruns;

    /// <summary>Music for <paramref name="mixer"/>; songs are read by name (TITLE.HMI, T200.HMI)
    /// through <paramref name="readSong"/>, null when absent.</summary>
    public MusicPlayer(Mixer mixer, Func<string, byte[]?> readSong)
    {
        _mixer = mixer;
        _readSong = readSong;
        _synth = new MidiSynth(mixer.SampleRate);
        mixer.SetBusGain(Bus.Music, _volume);
        _thread = new Thread(Run) { IsBackground = true, Name = "Music synth" };
        _thread.Start();
    }

    /// <summary>Music from the game's data folder (the loose T*.HMI and TITLE.HMI).</summary>
    public MusicPlayer(Mixer mixer, GameData data) : this(mixer, data.ReadLoose)
    {
    }

    /// <summary>The song playing (or about to), null when none.</summary>
    public string? Current => _current;

    /// <summary>The music bus volume (0-1).</summary>
    public float Volume
    {
        get => _volume;
        set
        {
            _volume = Math.Clamp(value, 0f, 1f);
            _mixer.SetBusGain(Bus.Music, _volume);
        }
    }

    /// <summary>Times the producer found the ring empty while a song played (it should stay 0).</summary>
    public long Underruns => Interlocked.Read(ref _underruns);

    /// <summary>Plays a song by file name; a no-op when it already plays.</summary>
    public void Play(string name, bool loop = true)
    {
        name = name.ToUpperInvariant();
        lock (_gate)
        {
            if (_request == null && !_stopRequest && _current == name && _mixer.IsPlaying(_voice))
            {
                return;
            }

            (_request, _stopRequest) = ((name, loop), false);
        }

        _current = name;
        _wake.Set();
    }

    /// <summary>The song of a maptype (<see cref="MusicTracks"/>).</summary>
    public void PlayForMapType(int mapType) => Play(MusicTracks.ForMapType(mapType));

    /// <summary>The song of a map: its maptype marker picks it. A map with the same song as the
    /// last one keeps it playing.</summary>
    public void PlayForMap(MapFile map) => Play(MusicTracks.ForMap(map));

    /// <summary>The menus' song.</summary>
    public void PlayTitle() => Play(MusicTracks.Title);

    public void Stop()
    {
        lock (_gate)
        {
            (_request, _stopRequest) = (null, true);
        }

        _current = null;
        _wake.Set();
    }

    /// <summary>MUSIC = SOUNDFONT: songs play through the .sf2 <paramref name="id"/> that
    /// <paramref name="read"/> returns (loaded on the producer thread, the playing song started
    /// again with it); null <paramref name="id"/>: the port's synth. A .sf2 that does not read
    /// leaves the port's synth.</summary>
    public void UseSoundFont(string? id, Func<byte[]?> read)
    {
        lock (_gate)
        {
            if (id == _fontAsked)
            {
                return;
            }

            (_fontAsked, _fontRequest) = (id, (id, read));
        }

        _wake.Set();
    }

    /// <summary>A parsed song, cached; null when it is missing or does not parse.</summary>
    public HmiFile? Song(string name)
    {
        lock (_songs)
        {
            if (!_songs.TryGetValue(name, out var song))
            {
                song = _readSong(name) is { } bytes ? HmiFile.Parse(bytes) : null;
                _songs[name] = song;
            }

            return song;
        }
    }

    private void Run()
    {
        Prof.ThreadName("Music synth");
        while (!_quit)
        {
            bool busy;
            try
            {
                busy = Step();
            }
            // Nothing a user .sf2 does may end the music thread: the song starts again on the port's synth.
            catch (Exception e)
            {
                Log.Error("Music", $"Music synth failed ({e.Message}); the port's synth plays");
                _retry = _synth is SoundFontSynth && _ring != null ? _started : null;
                StopVoice();
                _font = null;
                _synth = new MidiSynth(_mixer.SampleRate);
                busy = false;
            }

            if (busy)
            {
                continue;
            }

            _wake.WaitOne(_ring != null ? 5 : 100);
        }

        StopVoice();
    }

    /// <summary>One pass of the producer: requests, then one chunk; true when it rendered one.</summary>
    private bool Step()
    {
        (string Name, bool Loop)? request;
        bool stop;
        (string? Id, Func<byte[]?>? Read)? font;
        lock (_gate)
        {
            (request, stop, font) = (_request, _stopRequest, _fontRequest);
            (_request, _stopRequest, _fontRequest) = (null, false, null);
        }

        if (stop)
        {
            StopVoice();
        }

        // Another .sf2: the playing song is silenced while the file loads, then starts again through it.
        if (font is { } f && f.Id != _fontId)
        {
            var again = _ring != null ? _started : null;
            StopVoice();
            LoadFont(f.Id, f.Read);
            request ??= again;
        }

        request ??= _retry;
        _retry = null;
        if (request is { } r)
        {
            Start(r.Name, r.Loop);
        }

        return _ring != null && Fill();
    }

    private void Start(string name, bool loop)
    {
        StopVoice();
        var song = Song(name);
        if (song == null)
        {
            _current = null;
            return;
        }

        // A fresh synth: the instruments it built are cheap to rebuild, and no voice of the old
        // song rings into the new one.
        _synth = _font != null
            ? new SoundFontSynth(_font, _mixer.SampleRate) { Loop = loop }
            : new MidiSynth(_mixer.SampleRate) { Loop = loop };
        _synth.Play(song);
        _started = (name, loop);
        _ring = new StreamRing((int)(RingSeconds * _mixer.SampleRate), _mixer.SampleRate);
        while (Fill())
        {
        }

        _voice = _mixer.PlayStream(_ring, 1f, bus: Bus.Music);
    }

    /// <summary>Producer thread: takes the .sf2 <paramref name="id"/> (null, or one that does not
    /// read: the port's synth).</summary>
    private void LoadFont(string? id, Func<byte[]?>? read)
    {
        // The old bank goes first (its synth holds it too): two large ones are never held at once.
        (_font, _fontId) = (null, id);
        _synth = new MidiSynth(_mixer.SampleRate);
        if (id == null || read == null)
        {
            return;
        }

        try
        {
            var bytes = read();
            if (_quit)
            {
                return;
            }

            if (bytes == null)
            {
                Log.Warn("Music", $"SoundFont {id} not found or too large; music plays the port's synth");
                return;
            }

            _font = SoundFontSynth.Parse(bytes);
            Log.Info("Music", $"SoundFont {id}");
        }
        // Any failure of a user file (a bad path, MeltySynth's several kinds) must not end the music thread.
        catch (Exception e)
        {
            Log.Warn("Music", $"SoundFont {id} does not read ({e.Message}); music plays the port's synth");
        }
    }

    private void StopVoice()
    {
        if (!_voice.IsNone)
        {
            _mixer.Stop(_voice);
        }

        (_voice, _ring) = (VoiceHandle.None, null);
    }

    /// <summary>Renders one chunk when the ring is below its target; false when it is full enough.</summary>
    private bool Fill()
    {
        var ring = _ring!;
        var target = (int)(TargetSeconds * ring.SampleRate);
        var queued = ring.Count;
        if (queued >= target || ring.Capacity - queued < ChunkFrames)
        {
            return false;
        }

        if (queued == 0 && !_voice.IsNone)
        {
            Interlocked.Increment(ref _underruns);
        }

        using (Prof.Zone(GameZones.MusicSynth))
        {
            _synth.Render(_chunk);
        }

        ring.Push(_chunk);
        return true;
    }

    public void Dispose()
    {
        _quit = true;
        _wake.Set();
        _thread.Join();
        _wake.Dispose();
    }
}
