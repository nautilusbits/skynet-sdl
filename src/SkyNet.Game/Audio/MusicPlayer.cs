using RetroEng.Audio;
using RetroEng.Diagnostics;
using SkyNet.Formats;
using SkyNet.Game.DevTools;

namespace SkyNet.Game.Audio;

/// <summary>
/// The score: HMI songs rendered by <see cref="MidiSynth"/> on a producer thread into a
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
    private volatile bool _quit;

    // Producer thread state.
    private MidiSynth _synth;
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
            (string Name, bool Loop)? request;
            bool stop;
            lock (_gate)
            {
                (request, stop) = (_request, _stopRequest);
                (_request, _stopRequest) = (null, false);
            }

            if (stop)
            {
                StopVoice();
            }

            if (request is { } r)
            {
                Start(r.Name, r.Loop);
            }

            if (_ring != null && Fill())
            {
                continue;
            }

            _wake.WaitOne(_ring != null ? 5 : 100);
        }

        StopVoice();
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
        _synth = new MidiSynth(_mixer.SampleRate) { Loop = loop };
        _synth.Play(song);
        _ring = new StreamRing((int)(RingSeconds * _mixer.SampleRate), _mixer.SampleRate);
        while (Fill())
        {
        }

        _voice = _mixer.PlayStream(_ring, 1f, bus: Bus.Music);
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
