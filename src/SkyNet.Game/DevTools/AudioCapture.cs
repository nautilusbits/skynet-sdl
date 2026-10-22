using System.Buffers.Binary;
using RetroEng.Audio;

namespace SkyNet.Game.DevTools;

/// <summary>--capture's sound: the mixer pulled as the audio thread would (<see cref="Mixer.Mix"/>, its only
/// consumer then), rate/fps sample frames a video frame with the fraction carried (frame k ends at
/// k * rate / fps), written as a 16-bit stereo WAV at the mixer's rate.
/// <code>
/// video:  | frame 0 | frame 1 | frame 2 | ...      Step() after each frame
/// audio:  [ 735     ][ 735     ][ 735     ] ...     44100 / 60 sample frames each
/// </code></summary>
public sealed class AudioCapture : IDisposable
{
    private const int Channels = 2;
    private const int BytesPerSample = 2;
    private const int HeaderBytes = 44;

    private readonly Mixer _mixer;
    private readonly int _fps;
    private readonly FileStream _file;
    private float[] _mix = [];
    private byte[] _pcm = [];
    private long _steps;

    public AudioCapture(string path, Mixer mixer, int fps)
    {
        (_mixer, _fps) = (mixer, fps);
        _file = new FileStream(path, FileMode.Create, FileAccess.Write, FileShare.Read);
        _file.Write(Header(0));
    }

    /// <summary>The sample frames written so far.</summary>
    public long Written { get; private set; }

    /// <summary>One video frame's sound: what the mix owes up to the end of the next frame.</summary>
    public void Step()
    {
        _steps++;
        var due = (int)(_steps * _mixer.SampleRate / _fps - Written);
        if (due <= 0)
        {
            return;
        }

        var floats = due * Channels;
        if (_mix.Length < floats)
        {
            (_mix, _pcm) = (new float[floats], new byte[floats * BytesPerSample]);
        }

        _mixer.Mix(_mix.AsSpan(0, floats));
        for (var i = 0; i < floats; i++)
        {
            var s = (short)(Math.Clamp(_mix[i], -1f, 1f) * short.MaxValue);
            BinaryPrimitives.WriteInt16LittleEndian(_pcm.AsSpan(i * BytesPerSample), s);
        }

        _file.Write(_pcm, 0, floats * BytesPerSample);
        Written += due;
    }

    /// <summary>The header's sizes filled in, the file closed.</summary>
    public void Dispose()
    {
        _file.Position = 0;
        _file.Write(Header(Written * Channels * BytesPerSample));
        _file.Dispose();
    }

    // RIFF / WAVE / fmt (PCM) / data.
    private byte[] Header(long dataBytes)
    {
        var h = new byte[HeaderBytes];
        var span = h.AsSpan();
        "RIFF"u8.CopyTo(span);
        BinaryPrimitives.WriteInt32LittleEndian(span[4..], (int)(HeaderBytes - 8 + dataBytes));
        "WAVEfmt "u8.CopyTo(span[8..]);
        BinaryPrimitives.WriteInt32LittleEndian(span[16..], 16);
        BinaryPrimitives.WriteInt16LittleEndian(span[20..], 1);
        BinaryPrimitives.WriteInt16LittleEndian(span[22..], Channels);
        BinaryPrimitives.WriteInt32LittleEndian(span[24..], _mixer.SampleRate);
        BinaryPrimitives.WriteInt32LittleEndian(span[28..], _mixer.SampleRate * Channels * BytesPerSample);
        BinaryPrimitives.WriteInt16LittleEndian(span[32..], Channels * BytesPerSample);
        BinaryPrimitives.WriteInt16LittleEndian(span[34..], BytesPerSample * 8);
        "data"u8.CopyTo(span[36..]);
        BinaryPrimitives.WriteInt32LittleEndian(span[40..], (int)dataBytes);
        return h;
    }
}
