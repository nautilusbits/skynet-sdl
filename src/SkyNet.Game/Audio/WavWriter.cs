using System.Buffers.Binary;
using SkyNet.Formats;

namespace SkyNet.Game.Audio;

/// <summary>Writes rendered audio as 16-bit stereo PCM WAV (listen checks, debugging).</summary>
public static class WavWriter
{
    /// <summary>Interleaved stereo floats as a RIFF/WAVE file.</summary>
    public static byte[] Encode(ReadOnlySpan<float> stereo, int sampleRate)
    {
        var data = stereo.Length * 2;
        var b = new byte[44 + data];
        "RIFF"u8.CopyTo(b);
        BinaryPrimitives.WriteInt32LittleEndian(b.AsSpan(4), 36 + data);
        "WAVEfmt "u8.CopyTo(b.AsSpan(8));
        BinaryPrimitives.WriteInt32LittleEndian(b.AsSpan(16), 16);
        BinaryPrimitives.WriteInt16LittleEndian(b.AsSpan(20), 1);
        BinaryPrimitives.WriteInt16LittleEndian(b.AsSpan(22), 2);
        BinaryPrimitives.WriteInt32LittleEndian(b.AsSpan(24), sampleRate);
        BinaryPrimitives.WriteInt32LittleEndian(b.AsSpan(28), sampleRate * 4);
        BinaryPrimitives.WriteInt16LittleEndian(b.AsSpan(32), 4);
        BinaryPrimitives.WriteInt16LittleEndian(b.AsSpan(34), 16);
        "data"u8.CopyTo(b.AsSpan(36));
        BinaryPrimitives.WriteInt32LittleEndian(b.AsSpan(40), data);
        for (var i = 0; i < stereo.Length; i++)
        {
            var v = (short)Math.Clamp((int)MathF.Round(stereo[i] * 32767f), -32768, 32767);
            BinaryPrimitives.WriteInt16LittleEndian(b.AsSpan(44 + i * 2), v);
        }

        return b;
    }

    /// <summary>The first <paramref name="seconds"/> of a song straight from the synth (no mixer,
    /// no bus volume or filter), as a WAV file.</summary>
    public static byte[] RenderSong(HmiFile song, double seconds, int sampleRate = 44100)
    {
        var synth = new MidiSynth(sampleRate);
        synth.Play(song);
        var buffer = new float[(int)(seconds * sampleRate) * 2];
        synth.Render(buffer);
        return Encode(buffer, sampleRate);
    }
}
