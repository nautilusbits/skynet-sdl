using System.Text;

namespace SkyNet.Game.Tests.Audio;

/// <summary>
/// A minimal SoundFont 2 file built in memory: one preset (bank 0, program 0) whose one
/// instrument loops a 440 Hz sine over every key. Enough for MeltySynth to sound a note.
/// <code>
///   RIFF sfbk ─ LIST INFO (ifil, isng, INAM)
///             ─ LIST sdta (smpl: the sine, then 46 zero samples)
///             ─ LIST pdta (phdr pbag pmod pgen inst ibag imod igen shdr, each with its terminal)
/// </code>
/// </summary>
static class TinySoundFont
{
    private const int Rate = 22050;
    private const int Period = 50;
    private const int Periods = 40;
    private const int SamplePad = 46;
    private const ushort GenInstrument = 41;
    private const ushort GenSampleId = 53;
    private const ushort GenSampleModes = 54;
    private const ushort LoopContinuous = 1;
    private const ushort MonoSample = 1;
    private const byte RootKey = 69;

    public static byte[] Build()
    {
        var length = Period * Periods;
        var smpl = new byte[(length + SamplePad) * 2];
        for (var i = 0; i < length; i++)
        {
            var v = (short)(Math.Sin(2 * Math.PI * i / Period) * 16000);
            BitConverter.TryWriteBytes(smpl.AsSpan(i * 2), v);
        }

        var info = List("INFO", Chunk("ifil", U16(2), U16(1)), Chunk("isng", Name("EMU8000", 8)), Chunk("INAM", Name("tiny", 8)));
        var sdta = List("sdta", Chunk("smpl", smpl));
        var pdta = List("pdta",
            Chunk("phdr", PresetHeader("sine", 0, 0), PresetHeader("EOP", 0, 1)),
            Chunk("pbag", U16(0), U16(0), U16(1), U16(0)),
            Chunk("pmod", new byte[10]),
            Chunk("pgen", U16(GenInstrument), U16(0), U16(0), U16(0)),
            Chunk("inst", Name("sine", 20), U16(0), Name("EOI", 20), U16(1)),
            Chunk("ibag", U16(0), U16(0), U16(2), U16(0)),
            Chunk("imod", new byte[10]),
            Chunk("igen", U16(GenSampleModes), U16(LoopContinuous), U16(GenSampleId), U16(0), U16(0), U16(0)),
            Chunk("shdr", SampleHeader("sine", length), new byte[46]));
        return Chunk("RIFF", Encoding.ASCII.GetBytes("sfbk"), info, sdta, pdta);
    }

    private static byte[] SampleHeader(string name, int length) =>
    [
        .. Name(name, 20), .. U32(0), .. U32((uint)length), .. U32(0), .. U32((uint)length),
        .. U32(Rate), RootKey, 0, .. U16(0), .. U16(MonoSample),
    ];

    private static byte[] PresetHeader(string name, ushort program, ushort bag) =>
        [.. Name(name, 20), .. U16(program), .. U16(0), .. U16(bag), .. U32(0), .. U32(0), .. U32(0)];

    private static byte[] List(string type, params byte[][] chunks) => Chunk("LIST", [Encoding.ASCII.GetBytes(type), .. chunks]);

    private static byte[] Chunk(string id, params byte[][] parts)
    {
        var body = parts.SelectMany(p => p).ToArray();
        return [.. Encoding.ASCII.GetBytes(id), .. U32((uint)body.Length), .. body];
    }

    private static byte[] Name(string text, int size)
    {
        var b = new byte[size];
        Encoding.ASCII.GetBytes(text).CopyTo(b, 0);
        return b;
    }

    private static byte[] U16(ushort v) => BitConverter.GetBytes(v);

    private static byte[] U32(uint v) => BitConverter.GetBytes(v);
}
