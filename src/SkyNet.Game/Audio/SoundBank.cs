using System.Buffers.Binary;
using System.Numerics;
using RetroEng.Audio;
using RetroEng.Content;
using RetroEng.Diagnostics;
using SkyNet.Data;
using SkyNet.Formats;

namespace SkyNet.Game.Audio;

/// <summary>
/// The game's sound effects (MDMDSFXS.BSA) on the mixer, by DOS sound id (<see cref="SoundIds"/>)
/// or file name, with the Godot port's levels (audio.gd):
/// <list type="bullet">
/// <item>2D one-shots at 0 dB (<see cref="Play2D"/>), the ambient bed at -13 dB, looping;</item>
/// <item>3D one-shots at -6 dB, faded by DOS's rule below (<see cref="Play3D"/>);</item>
/// <item>3D loops at -10 dB, inverse-square from 300 units, stopped past 5000 + 500 and started
/// again inside 5000 (<see cref="PlayLoop3D"/>).</item>
/// </list>
/// The mixer's own roll-off is 1/d; <see cref="Update"/> multiplies each tracked 3D voice by the
/// other 1/d every tick, so the heard level is the port's (unit / d)². Call it (or
/// <see cref="SetListener"/>, which calls it) once per tick. Game thread only.
/// <para>The <c>Dos</c> methods play by SKYNET.EXE's own rules instead (v1.01): each id at its table
/// volume and pitch (<see cref="SoundIds.DosVolume"/>), 2D voices that skip or restart the same id
/// (<see cref="DosOverlap"/>, FUN_0012ef06), and 3D voices at full level up to 120 units, falling in
/// a straight line to silence at <see cref="DosFar"/>, half as loud straight behind (FUN_0012f2c0),
/// never occluded.</para>
/// </summary>
public sealed class SoundBank : IDisposable
{
    public const string Archive = "MDMDSFXS.BSA";
    /// <summary>The DOS .RAW clips: unsigned 8-bit mono at this rate.</summary>
    public const int RawRate = 11025;
    public const float Default2DDb = 0f;
    public const float Default3DDb = -6f;
    public const float AmbientDb = -13f;
    public const float LoopDb = -10f;
    public const float LoopUnitSize = 300f;
    public const float LoopMaxDistance = 5000f;
    /// <summary>A loop is stopped this far past its maximum distance (hysteresis).</summary>
    public const float LoopGateMargin = 500f;
    /// <summary>DOS 3D (FUN_0012f2c0): full level up to here.</summary>
    public const float DosNear = 120f;
    /// <summary>DOS 3D: 127 − (d − 120) × 0x1557 / 65536 reaches 0 here (≈ 1643.5 units).</summary>
    public const float DosFar = DosNear + 127f * 65536f / 0x1557;
    /// <summary>DOS 3D: closer than this a source is centred and never turned down for being behind.</summary>
    public const float DosCentre = 60f;
    /// <summary>A DOS loop is stopped this far past <see cref="DosFar"/> (DOS stops it at the edge
    /// and starts it again inside; the margin only keeps it from flapping).</summary>
    public const float DosGateMargin = 60f;
    /// <summary>DOS mixes at most this many placed one-shots and this many loops at once (six voices
    /// per kind); the nearest win.</summary>
    public const int DosVoices = 6;
    /// <summary>How a sample without a <c>sounds</c> row plays: unchanged.</summary>
    private static readonly SoundEventRow Neutral = SoundEventRow.Default("");

    private readonly Mixer _mixer;
    private readonly Func<string, byte[]?> _read;
    private readonly IDisposable? _owned;
    private readonly SoundMods? _mods;
    /// <summary>The mods' files the cache was read through (a mount empties it).</summary>
    private VirtualFileSystem? _cacheMods;
    private readonly Dictionary<string, (Sound? Once, Sound? Loop)> _cache = new(StringComparer.OrdinalIgnoreCase);
    private readonly List<Tracked> _tracked = [];
    private readonly Dictionary<int, VoiceHandle> _dos2D = [];
    private VoiceHandle _ambient = VoiceHandle.None;
    private string? _ambientName;
    private Vector3 _listener;
    private Vector3 _forward = -Vector3.UnitZ;

    /// <summary>A 3D voice the bank keeps at the inverse-square level, or a gated loop.</summary>
    internal sealed class Tracked
    {
        public VoiceHandle Voice;
        public required Sound Sound;
        public string Name = "";
        public int Id = -1;
        public Vector3 Position;
        public float Volume;
        public float Unit;
        public float Max;
        public bool Loop;
        /// <summary>DOS's straight-line fade and behind rule instead of the inverse square.</summary>
        public bool Dos;
        public float Pitch = 1f;
        public int Priority;
        /// <summary>A loop the <c>sounds</c> table plays once per approach.</summary>
        public bool Once;
        /// <summary>That once-only loop has played: not again until the listener leaves.</summary>
        public bool Spent;
        /// <summary>A loop the distance gate stopped (or never started).</summary>
        public bool Gated;
        public bool Removed;
    }

    /// <summary>A looping 3D sound: move it, stop it.</summary>
    public sealed class LoopHandle
    {
        private readonly SoundBank _bank;
        private readonly Tracked _tracked;

        internal LoopHandle(SoundBank bank, Tracked tracked) => (_bank, _tracked) = (bank, tracked);

        public Vector3 Position
        {
            get => _tracked.Position;
            set
            {
                _tracked.Position = value;
                if (!_tracked.Gated)
                {
                    _bank._mixer.SetEmitterPosition(_tracked.Voice, value);
                }
            }
        }

        /// <summary>Its voice while the gate has it playing (none when gated).</summary>
        public VoiceHandle Voice => Audible ? _tracked.Voice : VoiceHandle.None;

        /// <summary>Whether the gate has it playing (it is near enough).</summary>
        public bool Audible => !_tracked.Gated && !_tracked.Removed;

        public void Stop() => _bank.Remove(_tracked);
    }

    /// <summary>Sounds read by file name through <paramref name="read"/> (null when absent), the
    /// samples and the <c>sounds</c> table of <paramref name="mods"/> over them (null: none).</summary>
    public SoundBank(Mixer mixer, Func<string, byte[]?> read, SoundMods? mods = null)
    {
        _mixer = mixer;
        _read = read;
        _mods = mods;
    }

    private SoundBank(Mixer mixer, BsaArchive archive) : this(mixer, archive.Read, SoundMods.Mounted) => _owned = archive;

    /// <summary>The game's MDMDSFXS.BSA, kept open until disposed; null when the folder has none.</summary>
    public static SoundBank? Open(Mixer mixer, GameData data) =>
        data.Exists(Archive) ? new SoundBank(mixer, data.OpenArchive(Archive)) : null;

    /// <summary>The listener's position as the bank last heard it.</summary>
    public Vector3 ListenerPosition => _listener;

    /// <summary>Called with every request (a sound-event trace for logs and tests); null: none.</summary>
    public Action<SoundEvent>? Trace { get; set; }

    /// <summary>The loops set up and not stopped (near or gated).</summary>
    public int LoopCount => _tracked.Count(t => t.Loop);

    public static float DbToLinear(float db) => MathF.Pow(10f, db / 20f);

    /// <summary>A sound by file name (a mod's replacement first, <see cref="SoundMods.Effective"/>),
    /// decoded once per mount; null when it is missing or unreadable.</summary>
    public Sound? Get(string name, bool loop = false)
    {
        var mods = _mods?.Files;
        if (!ReferenceEquals(mods, _cacheMods))
        {
            _cache.Clear();
            _cacheMods = mods;
        }

        if (!_cache.TryGetValue(name, out var pair))
        {
            var (once, origin) = SoundMods.Effective(mods, name, _read);
            if (origin != SoundMods.Dos)
            {
                Log.Info("Mods", $"{name} plays {origin}");
            }

            pair = (once, once == null ? null : new Sound(once.Samples, once.Channels, once.SampleRate, Looping.Forever));
            _cache[name] = pair;
        }

        return loop ? pair.Loop : pair.Once;
    }

    /// <summary>A sound by DOS id; null for -1, a bad id or a missing file.</summary>
    public Sound? Get(int id, bool loop = false) => SoundIds.NameOf(id) is { } name ? Get(name, loop) : null;

    /// <summary>A .RAW (unsigned 8-bit mono, 11025 Hz) or RIFF/WAVE PCM clip as a sound.</summary>
    public static Sound? Decode(string name, byte[] bytes, Looping looping = Looping.Once)
    {
        if (bytes.Length == 0)
        {
            return null;
        }

        return name.EndsWith(".WAV", StringComparison.OrdinalIgnoreCase)
            ? DecodeWav(bytes, looping)
            : Sound.FromPcm(bytes, 1, RawRate, 8, looping);
    }

    /// <summary>A RIFF/WAVE PCM file (8 or 16 bits); null for anything else.</summary>
    public static Sound? DecodeWav(ReadOnlySpan<byte> b, Looping looping = Looping.Once)
    {
        if (b.Length < 44 || !b[..4].SequenceEqual("RIFF"u8) || !b[8..12].SequenceEqual("WAVE"u8))
        {
            return null;
        }

        int channels = 0, rate = 0, bits = 0;
        var off = 12;
        while (off + 8 <= b.Length)
        {
            var id = b.Slice(off, 4);
            var size = (int)Math.Min(BinaryPrimitives.ReadUInt32LittleEndian(b[(off + 4)..]), int.MaxValue);
            off += 8;
            if (id.SequenceEqual("fmt "u8) && off + 16 <= b.Length)
            {
                if (BinaryPrimitives.ReadUInt16LittleEndian(b[off..]) != 1)
                {
                    return null;
                }

                channels = BinaryPrimitives.ReadUInt16LittleEndian(b[(off + 2)..]);
                rate = BinaryPrimitives.ReadInt32LittleEndian(b[(off + 4)..]);
                bits = BinaryPrimitives.ReadUInt16LittleEndian(b[(off + 14)..]);
            }
            else if (id.SequenceEqual("data"u8))
            {
                if (channels is < 1 or > 2 || rate <= 0 || bits is not (8 or 16))
                {
                    return null;
                }

                var data = b.Slice(off, Math.Min(size, b.Length - off));
                var frameBytes = channels * bits / 8;
                return Sound.FromPcm(data[..(data.Length / frameBytes * frameBytes)].ToArray(), channels, rate, bits, looping);
            }

            off += size + (size & 1);
        }

        return null;
    }

    /// <summary>A one-shot without a place (the player's own sounds), <paramref name="volumeDb"/> as in the port.</summary>
    public VoiceHandle Play2D(int id, float volumeDb = Default2DDb) =>
        SoundIds.NameOf(id) is { } name ? Play2D(name, volumeDb, id) : VoiceHandle.None;

    public VoiceHandle Play2D(string name, float volumeDb = Default2DDb) => Play2D(name, volumeDb, -1);

    /// <summary>A menu's sound: on the interface bus, so it plays while the game's sounds are paused.</summary>
    public VoiceHandle PlayInterface(string name, float volumeDb = Default2DDb) => Play2D(name, volumeDb, -1, Bus.Interface);

    /// <summary>Holds every game sound (loops and one-shots in flight) where it is, or lets them go on;
    /// the music and the menus' sounds are not touched.</summary>
    public bool Paused
    {
        get => _mixer.IsBusPaused(Bus.Effects);
        set => _mixer.SetBusPaused(Bus.Effects, value);
    }

    private VoiceHandle Play2D(string name, float volumeDb, int id, Bus bus = Bus.Effects)
    {
        var e = Event(name);
        var gain = DbToLinear(volumeDb) * (float)e.Volume;
        var voice = Get(name) is { } sound ? _mixer.Play(sound, gain, 0f, (float)e.Pitch, e.Priority, bus) : VoiceHandle.None;
        Trace?.Invoke(new SoundEvent(SoundEventKind.Play2D, name, id, null, gain, !voice.IsNone));
        return voice;
    }

    /// <summary>How a DOS 2D sound meets the same id still playing (FUN_0012ef06's flags).</summary>
    public enum DosOverlap
    {
        /// <summary>Plays over it (no flag).</summary>
        Always,
        /// <summary>Not played (flag 2: the landing, the pain grunts).</summary>
        Skip,
        /// <summary>The old voice stops and the sound starts again (flags 6: the footsteps).</summary>
        Restart,
    }

    /// <summary>A one-shot without a place at the id's DOS volume and pitch (FUN_0012ef06).</summary>
    public VoiceHandle PlayDos2D(int id, DosOverlap overlap = DosOverlap.Always)
    {
        if (SoundIds.NameOf(id) is not { } name)
        {
            return VoiceHandle.None;
        }

        var e = Event(name);
        var gain = SoundIds.DosGain(id) * (float)e.Volume;
        if (overlap != DosOverlap.Always && _dos2D.TryGetValue(id, out var old) && _mixer.IsPlaying(old))
        {
            if (overlap == DosOverlap.Skip)
            {
                Trace?.Invoke(new SoundEvent(SoundEventKind.Play2D, name, id, null, gain, false));
                return VoiceHandle.None;
            }

            _mixer.Stop(old);
        }

        var voice = Get(name) is { } sound ? _mixer.Play(sound, gain, 0f, SoundIds.DosRate(id) * (float)e.Pitch, e.Priority) : VoiceHandle.None;
        _dos2D[id] = voice;
        Trace?.Invoke(new SoundEvent(SoundEventKind.Play2D, name, id, null, gain, !voice.IsNone));
        return voice;
    }

    /// <summary>A one-shot at a world position at the caller's level, faded as DOS fades every placed
    /// sound (FUN_0012f018): full to <see cref="DosNear"/>, silent at <see cref="DosFar"/>, half behind, never
    /// muffled by walls.</summary>
    public VoiceHandle Play3D(int id, Vector3 position, float volumeDb = Default3DDb) =>
        SoundIds.NameOf(id) is { } name ? Play3D(name, position, volumeDb, id) : VoiceHandle.None;

    public VoiceHandle Play3D(string name, Vector3 position, float volumeDb = Default3DDb) => Play3D(name, position, volumeDb, -1);

    private VoiceHandle Play3D(string name, Vector3 position, float volumeDb, int id)
    {
        var e = Event(name);
        var max = Reach(e, DosFar);
        var distance = Vector3.Distance(_listener, position);
        if (distance >= max || Get(name) is not { } sound)
        {
            Trace?.Invoke(new SoundEvent(SoundEventKind.Play3D, name, id, position, DbToLinear(volumeDb) * (float)e.Volume, false));
            return VoiceHandle.None;
        }

        var tracked = new Tracked
        {
            Sound = sound, Name = name, Id = id, Position = position, Volume = DbToLinear(volumeDb) * (float)e.Volume,
            Pitch = (float)e.Pitch, Priority = e.Priority, Unit = MathF.Min(DosNear, max), Max = max, Dos = true,
        };
        tracked.Voice = MakeRoom(distance, e.Priority) ? _mixer.Play(sound, Emitter(tracked, distance, Looping.Once), e.Priority) : VoiceHandle.None;
        if (!tracked.Voice.IsNone)
        {
            _tracked.Add(tracked);
        }

        Trace?.Invoke(new SoundEvent(SoundEventKind.Play3D, name, id, position, tracked.Volume, !tracked.Voice.IsNone));
        return tracked.Voice;
    }

    /// <summary>A one-shot at a place by DOS's rules (FUN_0012f018): the id's volume and pitch, the
    /// straight-line fade to <see cref="DosFar"/>, half as loud behind; none past the fade.</summary>
    public VoiceHandle PlayDos3D(int id, Vector3 position)
    {
        if (SoundIds.NameOf(id) is not { } name)
        {
            return VoiceHandle.None;
        }

        var e = Event(name);
        var gain = SoundIds.DosGain(id) * (float)e.Volume;
        var max = Reach(e, DosFar);
        var distance = Vector3.Distance(_listener, position);
        if (distance >= max || Get(name) is not { } sound)
        {
            Trace?.Invoke(new SoundEvent(SoundEventKind.Play3D, name, id, position, gain, false));
            return VoiceHandle.None;
        }

        var tracked = new Tracked
        {
            Sound = sound, Name = name, Id = id, Position = position, Volume = gain, Pitch = SoundIds.DosRate(id) * (float)e.Pitch,
            Priority = e.Priority, Unit = MathF.Min(DosNear, max), Max = max, Dos = true,
        };
        tracked.Voice = MakeRoom(distance, e.Priority) ? _mixer.Play(sound, Emitter(tracked, distance, Looping.Once), e.Priority) : VoiceHandle.None;
        if (!tracked.Voice.IsNone)
        {
            _tracked.Add(tracked);
        }

        Trace?.Invoke(new SoundEvent(SoundEventKind.Play3D, name, id, position, gain, !tracked.Voice.IsNone));
        return tracked.Voice;
    }

    /// <summary>A looping sound at a place (fires, engines, 0xEE nodes): it plays while the
    /// listener is near; null for a bad id or a missing file.</summary>
    public LoopHandle? PlayLoop3D(int id, Vector3 position, float volumeDb = LoopDb)
    {
        if (SoundIds.NameOf(id) is not { } name)
        {
            return null;
        }

        var e = Event(name);
        if (Get(name, loop: e.Loop) is not { } sound)
        {
            return null;
        }

        var max = Reach(e, LoopMaxDistance);
        var tracked = new Tracked
        {
            Sound = sound, Name = name, Id = id, Position = position, Volume = DbToLinear(volumeDb) * (float)e.Volume,
            Pitch = (float)e.Pitch, Priority = e.Priority, Unit = MathF.Min(LoopUnitSize, max), Max = max,
            Loop = true, Once = !e.Loop, Gated = true,
        };
        return StartLoop(tracked);
    }

    /// <summary>A looping sound at a place by DOS's rules (the 0xEE nodes and the burning sprites,
    /// handler 0x12a97a, which plays the clip again whenever it ends): it plays while the listener
    /// is inside <see cref="DosFar"/>, at the id's volume and pitch with DOS's fade; null for a bad
    /// id or a missing file.</summary>
    public LoopHandle? PlayDosLoop3D(int id, Vector3 position)
    {
        if (SoundIds.NameOf(id) is not { } name)
        {
            return null;
        }

        var e = Event(name);
        if (Get(name, loop: e.Loop) is not { } sound)
        {
            return null;
        }

        var max = Reach(e, DosFar);
        var tracked = new Tracked
        {
            Sound = sound, Name = name, Id = id, Position = position, Volume = SoundIds.DosGain(id) * (float)e.Volume,
            Pitch = SoundIds.DosRate(id) * (float)e.Pitch, Priority = e.Priority, Unit = MathF.Min(DosNear, max), Max = max,
            Loop = true, Once = !e.Loop, Gated = true, Dos = true,
        };
        return StartLoop(tracked);
    }

    private LoopHandle StartLoop(Tracked tracked)
    {
        _tracked.Add(tracked);
        Gate(tracked, Vector3.Distance(_listener, tracked.Position));
        Trace?.Invoke(new SoundEvent(SoundEventKind.LoopStart, tracked.Name, tracked.Id, tracked.Position, tracked.Volume, !tracked.Gated));
        return new LoopHandle(this, tracked);
    }

    /// <summary>The looping ambient bed (replaces the current one; the same one keeps playing).</summary>
    public void PlayAmbient(int id, float volumeDb = AmbientDb)
    {
        if (SoundIds.NameOf(id) is { } name)
        {
            PlayAmbient(name, volumeDb);
        }
    }

    public void PlayAmbient(string name, float volumeDb = AmbientDb)
    {
        if (string.Equals(name, _ambientName, StringComparison.OrdinalIgnoreCase) && _mixer.IsPlaying(_ambient))
        {
            return;
        }

        StopAmbient();
        var e = Event(name);
        var gain = DbToLinear(volumeDb) * (float)e.Volume;
        if (Get(name, loop: e.Loop) is { } sound)
        {
            (_ambient, _ambientName) = (_mixer.Play(sound, gain, 0f, (float)e.Pitch, e.Priority), name);
        }

        Trace?.Invoke(new SoundEvent(SoundEventKind.Ambient, name, -1, null, gain, !_ambient.IsNone));
    }

    public void StopAmbient()
    {
        _mixer.Stop(_ambient);
        (_ambient, _ambientName) = (VoiceHandle.None, null);
    }

    /// <summary>Moves the ears (also the mixer's) and updates every 3D voice.</summary>
    public void SetListener(Vector3 position, Vector3 forward, Vector3 up)
    {
        _mixer.SetListener(position, forward, up);
        _forward = forward;
        Update(position);
    }

    /// <summary>Once per tick: each 3D voice's level for where the listener is, loops started and
    /// stopped by distance, ended one-shots forgotten.</summary>
    public void Update(Vector3 listener)
    {
        _listener = listener;
        var loopLimit = NearestLoopsLimit(listener);
        for (var i = _tracked.Count - 1; i >= 0; i--)
        {
            var t = _tracked[i];
            var distance = Vector3.Distance(listener, t.Position);
            if (t.Loop)
            {
                Gate(t, distance);
                if (!t.Gated)
                {
                    var heard = !t.Dos || distance <= loopLimit;
                    _mixer.SetVolume(t.Voice, heard ? t.Volume * ExtraFalloff(t, distance) : 0f);
                }
            }
            else if (!_mixer.IsPlaying(t.Voice))
            {
                _tracked.RemoveAt(i);
            }
            else
            {
                _mixer.SetVolume(t.Voice, t.Volume * ExtraFalloff(t, distance));
            }
        }
    }

    /// <summary>The distance of the <see cref="DosVoices"/>-th nearest DOS loop within its fade
    /// (farther ones are silent), or infinity when fewer are in range.</summary>
    private float NearestLoopsLimit(Vector3 listener)
    {
        Span<float> nearest = stackalloc float[DosVoices];
        nearest.Fill(float.PositiveInfinity);
        var count = 0;
        foreach (var t in _tracked)
        {
            if (!t.Loop || !t.Dos || t.Removed)
            {
                continue;
            }

            var d = Vector3.Distance(listener, t.Position);
            if (d > t.Max)
            {
                continue;
            }

            count++;
            for (var k = 0; k < DosVoices; k++)
            {
                if (d < nearest[k])
                {
                    for (var m = DosVoices - 1; m > k; m--)
                    {
                        nearest[m] = nearest[m - 1];
                    }

                    nearest[k] = d;
                    break;
                }
            }
        }

        return count <= DosVoices ? float.PositiveInfinity : nearest[DosVoices - 1];
    }

    /// <summary>The second 1/d of the inverse square (the mixer applies the first), 0 past the
    /// maximum. For a DOS voice the mixer's straight line is the whole fade and this is DOS's
    /// behind rule.</summary>
    private float ExtraFalloff(Tracked t, float distance) =>
        distance > t.Max ? 0f : t.Dos ? DosBehind(t.Position - _listener, _forward) : distance <= t.Unit ? 1f : t.Unit / distance;

    private Emitter3D Emitter(Tracked t, float distance, Looping loop) =>
        new(t.Position, t.Unit, t.Max, t.Dos ? Rolloff.Linear : Rolloff.Inverse, t.Volume * ExtraFalloff(t, distance), loop, t.Pitch);

    /// <summary>DOS's level for a source off the listener's heading (FUN_0012f2c0, on the horizontal
    /// angle): full ahead and to the sides, then down in a straight line to half straight behind;
    /// full within <see cref="DosCentre"/>.</summary>
    public static float DosBehind(Vector3 offset, Vector3 forward)
    {
        var o = new Vector2(offset.X, offset.Z);
        var f = new Vector2(forward.X, forward.Z);
        if (o.Length() < DosCentre || f.LengthSquared() < 1e-8f)
        {
            return 1f;
        }

        var cos = Math.Clamp(Vector2.Dot(Vector2.Normalize(o), Vector2.Normalize(f)), -1f, 1f);
        var degrees = MathF.Acos(cos) * 180f / MathF.PI;
        return degrees <= 90f ? 1f : 1f - (degrees - 90f) / 180f;
    }

    /// <summary>A DOS 3D voice's level at <paramref name="distance"/> before its volume and the
    /// behind rule: 127 − (d − 120) × 0x1557 / 65536 of 127.</summary>
    public static float DosFade(float distance) => Spatial.Attenuation(distance, DosNear, DosFar, Rolloff.Linear);

    /// <summary>Whether a placed one-shot at <paramref name="distance"/> gets a voice: under
    /// <see cref="DosVoices"/> playing, or by taking the voice of the lowest priority (the farthest
    /// of those) when that is below <paramref name="priority"/>, or the same and farther.</summary>
    private bool MakeRoom(float distance, int priority)
    {
        Tracked? farthest = null;
        var far = -1f;
        var playing = 0;
        foreach (var t in _tracked)
        {
            if (t.Loop || !t.Dos || t.Removed || !_mixer.IsPlaying(t.Voice))
            {
                continue;
            }

            playing++;
            var d = Vector3.Distance(_listener, t.Position);
            if (farthest == null || t.Priority < farthest.Priority || (t.Priority == farthest.Priority && d > far))
            {
                (far, farthest) = (d, t);
            }
        }

        if (playing < DosVoices)
        {
            return true;
        }

        if (farthest == null || farthest.Priority > priority || (farthest.Priority == priority && far <= distance))
        {
            return false;
        }

        Remove(farthest);
        return true;
    }

    /// <summary>Starts a loop inside its maximum distance, stops it past the maximum plus the margin.
    /// A once-only loop (<see cref="Tracked.Once"/>) that has played waits for the listener to leave.</summary>
    private void Gate(Tracked t, float distance)
    {
        var gate = t.Max + (t.Dos ? DosGateMargin : LoopGateMargin);
        if (t.Spent && distance > gate)
        {
            t.Spent = false;
        }

        if (t.Gated && !t.Spent && distance <= t.Max)
        {
            t.Voice = _mixer.Play(t.Sound, Emitter(t, distance, t.Once ? Looping.Once : Looping.Forever), t.Priority);
            t.Gated = t.Voice.IsNone;
        }
        else if (!t.Gated && (distance > gate || !_mixer.IsPlaying(t.Voice)))
        {
            // Past the gate, or its voice was stolen (or a once-only loop ended): wait to be near again.
            t.Spent = t.Once && !_mixer.IsPlaying(t.Voice);
            _mixer.Stop(t.Voice);
            t.Gated = true;
        }
    }

    /// <summary>How <paramref name="name"/> plays (the mods' <c>sounds</c> table; none: unchanged).</summary>
    private SoundEventRow Event(string name) => _mods?.Event(name) ?? Neutral;

    /// <summary>A placed sound's audible distance: the row's range, else the game's own.</summary>
    private static float Reach(SoundEventRow e, float own) => e.Range > 0 ? (float)e.Range : own;

    private void Remove(Tracked t)
    {
        if (t.Removed)
        {
            return;
        }

        t.Removed = true;
        _mixer.Stop(t.Voice);
        _tracked.Remove(t);
        if (t.Loop)
        {
            Trace?.Invoke(new SoundEvent(SoundEventKind.LoopStop, t.Name, t.Id, t.Position, t.Volume, true));
        }
    }

    /// <summary>Stops every 3D voice and loop and the ambient bed (a level ends).</summary>
    public void StopAll()
    {
        foreach (var t in _tracked.ToArray())
        {
            Remove(t);
        }

        StopAmbient();
    }

    public void Dispose()
    {
        StopAll();
        _owned?.Dispose();
    }
}
