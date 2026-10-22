using System.Buffers.Binary;
using RetroEng.Audio;
using SkyNet.Game.DevTools;

namespace SkyNet.Game.Tests.DevTools;

/// <summary>--capture's sound: rate/fps frames of the mix a video frame (the fraction carried), as a 16-bit stereo WAV.</summary>
public class AudioCaptureTests
{
    private const int Rate = 44100;

    // The WAV's (channels, rate, bits, sample frames) from its header, and its samples.
    private static (int Channels, int Rate, int Bits, int Frames, short[] Samples) Read(string path)
    {
        var wav = File.ReadAllBytes(path);
        var data = BinaryPrimitives.ReadInt32LittleEndian(wav.AsSpan(40));
        Assert.Equal(wav.Length - 8, BinaryPrimitives.ReadInt32LittleEndian(wav.AsSpan(4)));
        var samples = new short[data / 2];
        for (var i = 0; i < samples.Length; i++)
        {
            samples[i] = BinaryPrimitives.ReadInt16LittleEndian(wav.AsSpan(44 + i * 2));
        }

        return (BinaryPrimitives.ReadInt16LittleEndian(wav.AsSpan(22)), BinaryPrimitives.ReadInt32LittleEndian(wav.AsSpan(24)),
            BinaryPrimitives.ReadInt16LittleEndian(wav.AsSpan(34)), data / 4, samples);
    }

    [Fact]
    public void Each_video_frame_takes_its_share_of_the_mix_and_a_played_sound_is_heard()
    {
        var path = Path.Combine(Path.GetTempPath(), $"capture-{Guid.NewGuid():N}.wav");
        try
        {
            var mixer = new Mixer(Rate);
            var tone = new float[Rate / 10];
            for (var i = 0; i < tone.Length; i++)
            {
                tone[i] = 0.5f * MathF.Sin(i * 0.1f);
            }

            using (var capture = new AudioCapture(path, mixer, 60))
            {
                capture.Step();
                mixer.Play(new Sound(tone, 1, Rate, Looping.Once), 1f, 0f, 1f, 0, Bus.Effects);
                for (var i = 1; i < 7; i++)
                {
                    capture.Step();
                }

                Assert.Equal(7 * Rate / 60, capture.Written);
            }

            var wav = Read(path);
            Assert.Equal((2, Rate, 16, 7 * Rate / 60), (wav.Channels, wav.Rate, wav.Bits, wav.Frames));
            Assert.Contains(wav.Samples, s => Math.Abs(s) > 1000);
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public void A_rate_that_does_not_divide_carries_the_fraction()
    {
        var path = Path.Combine(Path.GetTempPath(), $"capture-{Guid.NewGuid():N}.wav");
        try
        {
            // 44100 / 47 = 938.3 frames of sound a video frame: 47 video frames are exactly one second.
            using (var capture = new AudioCapture(path, new Mixer(Rate), 47))
            {
                for (var i = 0; i < 47; i++)
                {
                    capture.Step();
                }

                Assert.Equal(Rate, capture.Written);
            }

            Assert.Equal(Rate, Read(path).Frames);
        }
        finally
        {
            File.Delete(path);
        }
    }
}
