using System.Numerics;
using RetroEng.Audio;
using SkyNet.Game.Audio;

namespace SkyNet.Game.Tests.Audio;

public class SoundBankTests
{
    [Fact]
    public void IdTableMatchesThePort()
    {
        Assert.Equal(126, SoundIds.Names.Count);
        Assert.Equal("pipehit1.raw", SoundIds.NameOf(0));
        Assert.Equal("explo1.raw", SoundIds.NameOf(33));
        Assert.Equal("jmpmet.raw", SoundIds.NameOf(0x18));
        Assert.Equal("torpedo.wav", SoundIds.NameOf(125));
        Assert.Null(SoundIds.NameOf(-1));
        Assert.Null(SoundIds.NameOf(126));
    }

    [Fact]
    public void RawIsUnsigned8BitMonoAt11025()
    {
        var sound = SoundBank.Decode("X.RAW", [128, 255, 0, 192])!;
        Assert.Equal(1, sound.Channels);
        Assert.Equal(11025, sound.SampleRate);
        Assert.Equal(4, sound.Frames);
        Assert.Equal([0f, 127f / 128f, -1f, 0.5f], sound.Samples);
    }

    [Fact]
    public void WavPcmIsRead()
    {
        float[] pcm = [0f, 0.5f, -0.5f, 0.25f];
        var sound = SoundBank.Decode("y.wav", WavWriter.Encode(pcm, 22050))!;
        Assert.Equal(2, sound.Channels);
        Assert.Equal(22050, sound.SampleRate);
        Assert.Equal(2, sound.Frames);
        for (var i = 0; i < pcm.Length; i++)
        {
            Assert.Equal(pcm[i], sound.Samples[i], 3);
        }

        Assert.Null(SoundBank.Decode("bad.wav", [1, 2, 3]));
    }

    [Fact]
    public void LoadsByIdOnceAndPlays()
    {
        var reads = 0;
        var mixer = new Mixer();
        var bank = new SoundBank(mixer, name =>
        {
            reads++;
            return name == "explo1.raw" ? new byte[1000] : null;
        });
        Assert.Same(bank.Get(SoundIds.Explosion1), bank.Get(SoundIds.Explosion1));
        Assert.Equal(Looping.Forever, bank.Get(SoundIds.Explosion1, loop: true)!.Looping);
        Assert.Equal(1, reads);
        Assert.False(bank.Play2D(SoundIds.Explosion1).IsNone);
        Assert.True(bank.Play2D(SoundIds.PipeHit).IsNone);
        Assert.True(bank.Play2D(-1).IsNone);
    }

    [Fact]
    public void ThreeDFadesInAStraightLineToDosFar()
    {
        var mixer = new Mixer();
        var clip = new byte[44100];
        Array.Fill(clip, (byte)255);
        var bank = new SoundBank(mixer, _ => clip);
        bank.SetListener(Vector3.Zero, -Vector3.UnitZ, Vector3.UnitY);
        Assert.True(bank.Play3D(SoundIds.Explosion1, new Vector3(0, 0, -SoundBank.DosFar)).IsNone);
        var mid = (SoundBank.DosNear + SoundBank.DosFar) / 2;
        var voice = bank.Play3D(SoundIds.Explosion1, new Vector3(0, 0, -mid), 0f);
        mixer.Mix(new float[Mixer.BlockFrames * 2]);
        Assert.Equal(0.5f, mixer.Loudness(voice), 2);
        bank.SetListener(new Vector3(0, 0, -mid + 100), -Vector3.UnitZ, Vector3.UnitY);
        mixer.Mix(new float[Mixer.BlockFrames * 2]);
        Assert.Equal(1f, mixer.Loudness(voice), 3);
    }

    [Fact]
    public void PlacedOneShotsShareSixVoicesNearestFirst()
    {
        var mixer = new Mixer();
        var bank = new SoundBank(mixer, _ => new byte[44100]);
        bank.SetListener(Vector3.Zero, -Vector3.UnitZ, Vector3.UnitY);
        for (var i = 0; i < SoundBank.DosVoices; i++)
        {
            Assert.False(bank.Play3D(SoundIds.Explosion1, new Vector3(0, 0, -200 - 100 * i)).IsNone);
        }

        // Full: a farther one gets no voice, a nearer one takes the farthest's.
        Assert.True(bank.Play3D(SoundIds.Explosion1, new Vector3(0, 0, -1500)).IsNone);
        Assert.False(bank.Play3D(SoundIds.Explosion1, new Vector3(0, 0, -150)).IsNone);
    }

    [Fact]
    public void LoopsStartNearAndStopFar()
    {
        var mixer = new Mixer();
        var bank = new SoundBank(mixer, _ => new byte[1000]);
        bank.Update(new Vector3(0, 0, 10000));
        var loop = bank.PlayLoop3D(63, Vector3.Zero)!;
        Assert.False(loop.Audible);
        bank.Update(new Vector3(0, 0, 4000));
        Assert.True(loop.Audible);
        bank.Update(new Vector3(0, 0, 5400));
        Assert.True(loop.Audible);
        bank.Update(new Vector3(0, 0, 5600));
        Assert.False(loop.Audible);
        loop.Stop();
        Assert.False(loop.Audible);
    }
}
