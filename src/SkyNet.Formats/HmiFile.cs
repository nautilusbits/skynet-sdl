using System.Buffers.Binary;
using System.Collections.Immutable;
using System.Text;

namespace SkyNet.Formats;

/// <summary>
/// One timed event of an HMI song. <see cref="Kind"/> is the MIDI status nibble (0x80 note off,
/// 0x90 note on, 0xB0 controller, 0xC0 program, 0xE0 pitch bend with <see cref="Data1"/> =
/// 0..16383) or 0x51 for a tempo meta (<see cref="Data1"/> = µs per beat).
/// </summary>
public readonly record struct HmiEvent(int Tick, int Kind, int Channel, int Data1, int Data2);

/// <summary>How one track's event stream ended (the Godot port's debug stats).</summary>
public readonly record struct HmiTrackInfo(int Offset, int EventStart, int EndTick, string End);

/// <summary>
/// An HMI SOS song ("HMI-MIDISONG061595"): the loose T*.HMI / TITLE.HMI the DOS game plays.
/// Header: 0xD2 u16 ticks per beat (informational), 0xD4 u16 timer rate in Hz (every song ticks
/// at it, 120), 0xE4 u16 track count, 0xE8 u32 offset of the u32 track-offset table. A track
/// ("HMI-MIDITRACK") has its event stream at track + u32 [+0x57] and ends where the next track
/// in file order starts. Events are MIDI with running status, except that a note-on carries a
/// varlen duration after the velocity (no note-offs; they are materialised here at
/// tick + max(duration, 1)) and 0xFE starts an HMI-private event of fixed length per sub-type.
/// Ported from the Godot port's hmi_file.gd; synthesis is not part of this.
/// </summary>
public sealed class HmiFile
{
    public const string Magic = "HMI-MIDISONG061595";
    public const string TrackMagic = "HMI-MIDITRACK";
    public const int MaxTracks = 256;

    /// <summary>0xD2, 96 when the file says 0.</summary>
    public int Division { get; }
    /// <summary>0xD4 ticks per second, 120 when the file says 0 or more than 1000.</summary>
    public int Rate { get; }
    /// <summary>The longest track's end tick.</summary>
    public int Length { get; }
    /// <summary>Every track's events, stably sorted by tick; at one tick controllers, programs, bends and tempo first, then note-offs, then note-ons.</summary>
    public ImmutableArray<HmiEvent> Events { get; }
    /// <summary>Per distinct, valid track in table order.</summary>
    public ImmutableArray<HmiTrackInfo> Tracks { get; }
    /// <summary>Every note-on's duration in file order (zero-velocity ones too).</summary>
    public ImmutableArray<int> NoteDurations { get; }
    /// <summary>Count of each 0xFE sub-type seen.</summary>
    public ImmutableDictionary<int, int> PrivateEvents { get; }

    HmiFile(int division, int rate, int length, ImmutableArray<HmiEvent> events, ImmutableArray<HmiTrackInfo> tracks,
        ImmutableArray<int> durs, ImmutableDictionary<int, int> fe)
    {
        Division = division; Rate = rate; Length = length; Events = events; Tracks = tracks;
        NoteDurations = durs; PrivateEvents = fe;
    }

    /// <summary>Bytes past the sub-type byte that an 0xFE event skips (FE 10 adds its byte[4] + 5); 0 for an unknown sub-type.</summary>
    public static int PrivateEventLength(int sub) => sub switch
    {
        0x10 => 2, 0x12 => 2, 0x13 => 10, 0x14 => 2, 0x15 => 6, _ => 0,
    };

    static int U16(ReadOnlySpan<byte> b, int o) => BinaryPrimitives.ReadUInt16LittleEndian(b[o..]);
    static long U32(ReadOnlySpan<byte> b, int o) => BinaryPrimitives.ReadUInt32LittleEndian(b[o..]);

    static bool HasMagic(ReadOnlySpan<byte> b, long off, string magic) =>
        off >= 0 && off + magic.Length <= b.Length && Encoding.ASCII.GetString(b.Slice((int)off, magic.Length)) == magic;

    /// <summary>Parse a song; null when the magic, the track count or the table is wrong.</summary>
    public static HmiFile? Parse(ReadOnlySpan<byte> b)
    {
        if (b.Length < 0x100 || !HasMagic(b, 0, Magic))
            return null;
        int division = U16(b, 0xD2), rate = U16(b, 0xD4);
        if (division <= 0) division = 96;
        if (rate <= 0 || rate > 1000) rate = 120;
        int ntracks = Math.Min(U16(b, 0xE4), MaxTracks);
        long table = U32(b, 0xE8);
        if (ntracks <= 0 || table <= 0 || table + ntracks * 4L > b.Length)
            return null;
        var offs = new long[ntracks];
        for (int i = 0; i < ntracks; i++)
            offs[i] = U32(b, (int)table + i * 4);
        var starts = offs.Order().ToArray();
        var rows = new List<(long Key, HmiEvent E)>();
        var durs = new List<int>();
        var fe = new Dictionary<int, int>();
        var tracks = ImmutableArray.CreateBuilder<HmiTrackInfo>();
        var seen = new HashSet<long>();
        int length = 0;
        foreach (long off in offs)
        {
            if (!seen.Add(off))
                continue;
            if (off <= 0 || off + 0x60 > b.Length || !HasMagic(b, off, TrackMagic))
                continue;
            long pos = off + U32(b, (int)off + 0x57);
            long end = b.Length;
            foreach (long s in starts)
                if (s > off) { end = Math.Min(s, end); break; }
            var (t, why) = ParseTrack(b, pos, end, rows, durs, fe);
            tracks.Add(new HmiTrackInfo((int)off, (int)Math.Min(pos, int.MaxValue), t, why));
            length = Math.Max(length, t);
        }
        var events = rows.OrderBy(r => r.Key).Select(r => r.E).ToImmutableArray();
        return new HmiFile(division, rate, length, events, tracks.ToImmutable(), [.. durs], fe.ToImmutableDictionary());
    }

    /// <summary>Standard MIDI varlen of at most 4 bytes; -1 for a longer one.</summary>
    internal static int VarLen(ReadOnlySpan<byte> b, ref long pos)
    {
        int v = 0, n = 0;
        while (pos < b.Length)
        {
            if (n == 4)
                return -1;
            int c = b[(int)pos++];
            n++;
            v = (v << 7) | (c & 0x7F);
            if ((c & 0x80) == 0)
                break;
        }
        return v;
    }

    static void Push(List<(long, HmiEvent)> rows, int tick, int kind, int ch, int d1, int d2)
    {
        int prio = kind == 0x80 ? 1 : kind == 0x90 ? 2 : 0;
        rows.Add((tick * 4L + prio, new HmiEvent(tick, kind, ch, d1, d2)));
    }

    static (int Tick, string End) ParseTrack(ReadOnlySpan<byte> b, long pos, long end, List<(long, HmiEvent)> rows,
        List<int> durs, Dictionary<int, int> fe)
    {
        int tick = 0, status = 0, size = b.Length;
        long last = -1;
        while (pos < end)
        {
            if (pos <= last) return (tick, "stalled");
            last = pos;
            int delta = VarLen(b, ref pos);
            if (delta < 0) return (tick, "corrupt delta time");
            tick += delta;
            if (pos >= end) break;
            int c = b[(int)pos];
            if (c >= 0x80)
            {
                status = c;
                pos++;
            }
            if (status == 0xFE)
            {
                int sub = pos < end ? b[(int)pos] : 0;
                fe[sub] = fe.GetValueOrDefault(sub) + 1;
                if (sub == 0x10)
                {
                    long p = pos + 3;
                    pos = p + (p < end ? b[(int)p] + 5 : 0);
                }
                else
                {
                    pos += 1 + PrivateEventLength(sub);
                }
                status = 0;
                continue;
            }
            if (status == 0xFF)
            {
                if (pos >= size) return (tick, "truncated meta event");
                int mt = b[(int)pos++];
                int ln = VarLen(b, ref pos);
                if (mt == 0x2F) return (tick, "end of track");
                if (ln < 0) return (tick, "corrupt meta length");
                if (mt == 0x51 && ln == 3)
                {
                    if (pos + 3 > size) return (tick, "truncated tempo");
                    Push(rows, tick, 0x51, 0, (b[(int)pos] << 16) | (b[(int)pos + 1] << 8) | b[(int)pos + 2], 0);
                }
                pos += ln;
                status = 0;
                continue;
            }
            if (status == 0xF0 || status == 0xF7)
            {
                int ln = VarLen(b, ref pos);
                if (ln < 0) return (tick, "corrupt sysex length");
                pos += ln;
                status = 0;
                continue;
            }
            if (status < 0x80)
                return (tick, $"lost sync (byte {c:x2})");
            int kind = status & 0xF0, ch = status & 0x0F;
            int reads = kind is 0x90 or 0xB0 or 0xE0 ? 2 : kind is 0x80 or 0xC0 ? 1 : 0;
            if (pos + reads > size) return (tick, "truncated event");
            int i = (int)pos;
            switch (kind)
            {
                case 0x80:
                    pos += 2;
                    Push(rows, tick, 0x80, ch, b[i], 0);
                    break;
                case 0x90:
                    {
                        int n = b[i], v = b[i + 1];
                        pos += 2;
                        int dur = VarLen(b, ref pos);
                        if (dur < 0) return (tick, "corrupt note duration");
                        durs.Add(dur);
                        if (v == 0)
                            Push(rows, tick, 0x80, ch, n, 0);
                        else
                        {
                            Push(rows, tick, 0x90, ch, n, v);
                            Push(rows, tick + Math.Max(dur, 1), 0x80, ch, n, 0);
                        }
                        break;
                    }
                case 0xA0: pos += 2; break;
                case 0xB0: Push(rows, tick, 0xB0, ch, b[i], b[i + 1]); pos += 2; break;
                case 0xC0: Push(rows, tick, 0xC0, ch, b[i], 0); pos += 1; break;
                case 0xD0: pos += 1; break;
                case 0xE0: Push(rows, tick, 0xE0, ch, b[i] | (b[i + 1] << 7), 0); pos += 2; break;
                default: return (tick, $"unknown status {status:x2}");
            }
        }
        return (tick, "ran off the end");
    }
}
