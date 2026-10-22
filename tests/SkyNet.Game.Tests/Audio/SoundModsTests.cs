using System.Numerics;
using System.Text;
using RetroEng.Audio;
using RetroEng.Content;
using RetroEng.Json;
using SkyNet.Data;
using SkyNet.Game.Audio;

namespace SkyNet.Game.Tests.Audio;

public class SoundModsTests
{
    private const string Explo = "explo1.raw";
    private const string Row = """{"sample": "EXPLO1", "volume": 0.5, "range": 600, "pitch": 1.5, "priority": 2, "loop": false}""";

    /// <summary>A DOS clip of constant level (unsigned 8-bit: 255 is full).</summary>
    private static readonly byte[] Dos = Enumerable.Repeat((byte)255, 44100).ToArray();

    /// <summary>One folder mod per (files) set, the later over the earlier; the root to delete.</summary>
    private static (VirtualFileSystem Vfs, string Root) Mods(params (string Path, byte[] Bytes)[][] mods)
    {
        var root = Directory.CreateTempSubdirectory("skynet-sounds-").FullName;
        var vfs = new VirtualFileSystem();
        for (var i = 0; i < mods.Length; i++)
        {
            var dir = Path.Combine(root, $"m{i}");
            Directory.CreateDirectory(dir);
            foreach (var (path, bytes) in mods[i])
            {
                var file = Path.Combine(dir, path);
                Directory.CreateDirectory(Path.GetDirectoryName(file)!);
                File.WriteAllBytes(file, bytes);
            }

            vfs.Mount(new FolderMount(dir), i, $"m{i}");
        }

        return (vfs, root);
    }

    private static byte[] Wav(float level, int rate) => WavWriter.Encode([.. Enumerable.Repeat(level, rate * 2)], rate);

    private static byte[] Table(params string[] rows) => Encoding.UTF8.GetBytes($$"""{"rows": [{{string.Join(",", rows)}}]}""");

    private static float Heard(Mixer mixer, VoiceHandle voice)
    {
        mixer.Mix(new float[Mixer.BlockFrames * 2]);
        return mixer.Loudness(voice);
    }

    [Fact]
    public void Path_is_the_name_without_extension()
    {
        Assert.Equal("sounds/EXPLO1.wav", SoundMods.PathOf("explo1.raw"));
        Assert.Equal("sounds/MOVEDOOR.wav", SoundMods.PathOf("MOVEDOOR.WAV"));
    }

    [Fact]
    public void Mod_wins_over_dos_and_the_later_mod_over_the_earlier()
    {
        var (vfs, root) = Mods([(SoundMods.PathOf(Explo), Wav(0.25f, 8000))], [(SoundMods.PathOf(Explo), Wav(0.5f, 16000))]);
        try
        {
            var top = SoundMods.Effective(vfs, Explo, _ => Dos);
            Assert.Equal("m1:sounds/explo1.wav", top.Origin, ignoreCase: true);
            Assert.Equal(16000, top.Sound!.SampleRate);

            var other = SoundMods.Effective(vfs, "explo2.raw", _ => Dos);
            Assert.Equal(SoundMods.Dos, other.Origin);
            Assert.Equal(SoundBank.RawRate, other.Sound!.SampleRate);
        }
        finally
        {
            vfs.Dispose();
            Directory.Delete(root, true);
        }
    }

    [Fact]
    public void A_bad_replacement_leaves_the_dos_sample()
    {
        var (vfs, root) = Mods([(SoundMods.PathOf(Explo), [1, 2, 3])]);
        try
        {
            Assert.Equal(SoundMods.Dos, SoundMods.Effective(vfs, Explo, _ => Dos).Origin);
        }
        finally
        {
            vfs.Dispose();
            Directory.Delete(root, true);
        }
    }

    [Fact]
    public void The_bank_plays_the_replacement_and_rereads_on_a_new_mount()
    {
        var (vfs, root) = Mods([(SoundMods.PathOf(Explo), Wav(0.5f, 22050))]);
        VirtualFileSystem? mounted = null;
        try
        {
            var bank = new SoundBank(new Mixer(), _ => Dos, new SoundMods(() => mounted, () => null));
            Assert.Equal(SoundBank.RawRate, bank.Get(SoundIds.Explosion1)!.SampleRate);

            mounted = vfs;
            Assert.Equal(22050, bank.Get(SoundIds.Explosion1)!.SampleRate);
        }
        finally
        {
            vfs.Dispose();
            Directory.Delete(root, true);
        }
    }

    [Fact]
    public void A_row_writes_and_reads_back()
    {
        var file = SoundMods.Base with { Rows = [new SoundEventRow("EXPLO1", 1.5, 2400, 0.75, 3, false)] };
        var json = TableSchemas.Sounds.Write(file).ToString();
        Assert.Equal(file.Rows.AsEnumerable(), TableSchemas.Sounds.Read(JsonItem.Parse(Encoding.UTF8.GetBytes(json))).Rows.AsEnumerable());
    }

    [Theory]
    [InlineData("""{"sample": "EXPLO1", "volume": 2.5, "range": 0, "pitch": 1, "priority": 0, "loop": true}""")]
    [InlineData("""{"sample": "EXPLO1", "volume": 1, "range": -1, "pitch": 1, "priority": 0, "loop": true}""")]
    [InlineData("""{"sample": "EXPLO1", "volume": 1, "range": 0, "pitch": 0, "priority": 0, "loop": true}""")]
    [InlineData("""{"sample": "EXPLO1", "volume": 1, "range": 0, "pitch": 1, "priority": 0}""")]
    public void A_bad_row_stops_the_table(string row)
    {
        var (vfs, root) = Mods([(GameTables.PathOf(TableNames.Sounds), Table(row))]);
        try
        {
            Assert.Throws<InvalidDataException>(() => SoundMods.Rows(vfs));
            Assert.Equal(SoundEventRow.Default("EXPLO1"), new SoundMods(() => null, () => vfs).Event(Explo) with { Sample = "EXPLO1" });
        }
        finally
        {
            vfs.Dispose();
            Directory.Delete(root, true);
        }
    }

    [Fact]
    public void Mods_lay_rows_over_each_other_by_sample()
    {
        var (vfs, root) = Mods(
            [(GameTables.PathOf(TableNames.Sounds), Table(Row, """{"sample": "SHOTS5", "volume": 2, "range": 0, "pitch": 1, "priority": 0, "loop": true}"""))],
            [(GameTables.PathOf(TableNames.Sounds), Table("""{"sample": "explo1", "volume": 1.5, "range": 0, "pitch": 1, "priority": 0, "loop": true}"""))]);
        try
        {
            var sounds = new SoundMods(() => null, () => vfs);
            Assert.Equal(1.5, sounds.Event("EXPLO1.RAW").Volume);
            Assert.Equal(2, sounds.Event("shots5.raw").Volume);
            Assert.Equal(1, sounds.Event("explo2.raw").Volume);
        }
        finally
        {
            vfs.Dispose();
            Directory.Delete(root, true);
        }
    }

    /// <summary>Without rows the bank's every mixer call is today's: same gains as a bank without mods.</summary>
    [Fact]
    public void Without_rows_nothing_changes()
    {
        var (vfs, root) = Mods([(GameTables.PathOf(TableNames.Sounds), Table("""{"sample": "SHOTS5", "volume": 2, "range": 50, "pitch": 2, "priority": 9, "loop": false}"""))]);
        try
        {
            var plain = new List<SoundEvent>();
            var modded = new List<SoundEvent>();
            var levels = new List<float>[] { [], [] };
            var mixers = new[] { new Mixer(), new Mixer() };
            var banks = new[]
            {
                new SoundBank(mixers[0], _ => Dos) { Trace = plain.Add },
                new SoundBank(mixers[1], _ => Dos, new SoundMods(() => vfs, () => vfs)) { Trace = modded.Add },
            };
            for (var b = 0; b < banks.Length; b++)
            {
                var (mixer, bank) = (mixers[b], banks[b]);
                bank.SetListener(Vector3.Zero, -Vector3.UnitZ, Vector3.UnitY);
                levels[b].Add(Heard(mixer, bank.PlayDos2D(SoundIds.Explosion1)));
                levels[b].Add(Heard(mixer, bank.Play3D(SoundIds.Explosion1, new Vector3(0, 0, -800), 0f)));
                levels[b].Add(Heard(mixer, bank.PlayDos3D(SoundIds.Explosion1, new Vector3(0, 0, -1200))));
                levels[b].Add(Heard(mixer, bank.PlayDosLoop3D(63, new Vector3(0, 0, -500))!.Voice));
                bank.PlayAmbient(SoundIds.Wind);
            }

            Assert.Equal(levels[0], levels[1]);
            Assert.Equal(plain, modded);
        }
        finally
        {
            vfs.Dispose();
            Directory.Delete(root, true);
        }
    }

    [Fact]
    public void Volume_and_range_reach_the_mixer()
    {
        var (vfs, root) = Mods([(GameTables.PathOf(TableNames.Sounds), Table(Row))]);
        try
        {
            var mixer = new Mixer();
            var bank = new SoundBank(mixer, _ => Dos, new SoundMods(() => null, () => vfs));
            bank.SetListener(Vector3.Zero, -Vector3.UnitZ, Vector3.UnitY);

            // 2D: half the DOS gain (EXPLO1's table volume is 128 of 128).
            Assert.Equal(0.5f, Heard(mixer, bank.PlayDos2D(SoundIds.Explosion1)), 2);

            // 3D: silent at the row's 600 units (DOS's own reach is about 1640), half way faded in between.
            Assert.True(bank.PlayDos3D(SoundIds.Explosion1, new Vector3(0, 0, -600)).IsNone);
            var mid = (SoundBank.DosNear + 600f) / 2f;
            Assert.Equal(0.25f, Heard(mixer, bank.PlayDos3D(SoundIds.Explosion1, new Vector3(0, 0, -mid))), 2);
        }
        finally
        {
            vfs.Dispose();
            Directory.Delete(root, true);
        }
    }

    [Fact]
    public void A_loop_set_to_once_waits_for_the_listener_to_leave()
    {
        var (vfs, root) = Mods([(GameTables.PathOf(TableNames.Sounds), Table("""{"sample": "PRESS1", "volume": 1, "range": 0, "pitch": 1, "priority": 0, "loop": false}"""))]);
        try
        {
            var mixer = new Mixer();
            var bank = new SoundBank(mixer, _ => new byte[100], new SoundMods(() => null, () => vfs));
            bank.Update(Vector3.Zero);
            var loop = bank.PlayDosLoop3D(62, new Vector3(0, 0, -100))!;
            Assert.True(loop.Audible);

            // The 100-frame clip ends in the first block; nearby it does not start again.
            mixer.Mix(new float[Mixer.BlockFrames * 2]);
            bank.Update(Vector3.Zero);
            bank.Update(Vector3.Zero);
            Assert.False(loop.Audible);

            // Away past the gate and back: it plays once more.
            bank.Update(new Vector3(0, 0, 5000));
            bank.Update(Vector3.Zero);
            Assert.True(loop.Audible);
        }
        finally
        {
            vfs.Dispose();
            Directory.Delete(root, true);
        }
    }
}
