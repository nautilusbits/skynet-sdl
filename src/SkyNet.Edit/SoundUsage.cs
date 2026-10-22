using System.Buffers.Binary;
using System.Globalization;
using SkyNet.Data;
using SkyNet.Formats;
using SkyNet.Game.Audio;
using SkyNet.Game.Behaviour;
using SkyNet.Game.Campaign;
using SkyNet.Game.Net;
using SkyNet.Game.Vehicles;
using SkyNet.Game.Water;
using SkyNet.Game.Weapons;

namespace SkyNet.Edit;

/// <summary>One thing that plays a sample: its kind ("weapon", "robot", ...) and what it is.</summary>
public readonly record struct SoundUse(string Kind, string What);

/// <summary>
/// What plays a sample of MDMDSFXS.BSA (editor-tools plan, T4): every DOS sound id naming the file
/// (<see cref="SoundIds"/>), then the tables and the game code that play those ids.
/// <code>
///   EXPLO1.RAW ─► ids [33] ─► weapons (fire, select, dry) · ammo (fire, impact) · robots (engine,
///                             script, frame events) · acts 0xDB-0xEB · destruction · burning sprites · game
/// </code>
/// </summary>
public static class SoundUsage
{
    public const string Weapon = "weapon", Ammo = "ammo", Robot = "robot", Act = "act", Destruction = "destruction",
        Sprite = "sprite", Game = "game";

    // Ids the game code plays itself (constants and lists in SkyNet.Game), with what plays them.
    private static readonly (int Id, string What)[] CodeIds =
    [
        (PickupTable.Sound, "pickup"), (GameSession.ObjectiveSound, "objective message"), (GameSession.DeathSound, "player death"),
        (TorpedoRide.Sound, "torpedo ride"), (VehicleController.SoundCarStart, "vehicle start"), (MotionDetector.SweepSound, "motion detector sweep"),
        (WaterBreath.SplashSound, "water splash"), (WaterBreath.DrownSound, "drowning"), (WaterBreath.BubblesSound, "bubbles"),
        (WaterBreath.GetAirSound, "getting air"), (PlayerSounds.LeftWade, "wading, left foot"), (PlayerSounds.RightWade, "wading, right foot"),
        (WeaponSystem.BounceSound, "grenade bounce"), (AisData.TrapSound, "dormant trap"),
        (SoundIds.Explosion1, "explosion (1 of 4)"), (SoundIds.Explosion1 + 1, "explosion (2 of 4)"),
        (SoundIds.Explosion1 + 2, "explosion (3 of 4)"), (SoundIds.Explosion1 + 3, "explosion (4 of 4)"),
        .. PlayerSounds.Landing.Select((id, s) => (id, Inv($"landing, surface {s}"))),
        .. PlayerSounds.LeftFoot.Select((id, s) => (id, Inv($"left foot, surface {s}"))),
        .. PlayerSounds.RightFoot.Select((id, s) => (id, Inv($"right foot, surface {s}"))),
        .. PlayerSounds.PainGrunts.Select(id => (id, "pain grunt")),
    ];

    // Samples the game code plays by file name.
    private static readonly (string Name, string What)[] CodeNames = [(VehicleRig.HitSound, "vehicle hit, robot hit")];

    /// <summary>The DOS sound ids that play <paramref name="sample"/> ("EXPLO1.RAW"), in id order.</summary>
    public static IReadOnlyList<int> Ids(string sample) =>
        [.. SoundIds.Names.Select((n, i) => (n, i)).Where(p => Same(p.n, sample)).Select(p => p.i)];

    /// <summary>Everything that plays <paramref name="sample"/>, by the shipped (and mounted mods') tables.</summary>
    public static List<SoundUse> Uses(string sample) =>
        Uses(sample, GameTables.Weapons, GameTables.Ammo, GameTables.Ais, GameTables.Destruction);

    /// <summary>Everything that plays <paramref name="sample"/> by these tables.</summary>
    public static List<SoundUse> Uses(string sample, IReadOnlyList<WeaponRow> weapons, IReadOnlyList<AmmoRow> ammo, AisData ais,
        IReadOnlyList<DestructionRow> destruction)
    {
        var ids = Ids(sample).ToHashSet();
        var uses = new List<SoundUse>();

        // Weapons and ammo: their sound columns.
        foreach (var w in weapons)
        {
            var name = w.Index < WeaponTable.Names.Count ? WeaponTable.Names[w.Index] : Inv($"{w.Index}");
            Add(uses, ids, w.FireSound, Weapon, Inv($"{w.Index} {name}: fire"));
            Add(uses, ids, w.SelectSound, Weapon, Inv($"{w.Index} {name}: select"));
            Add(uses, ids, w.DrySound, Weapon, Inv($"{w.Index} {name}: dry fire"));
        }

        foreach (var a in ammo)
        {
            Add(uses, ids, a.FireSound, Ammo, Inv($"{a.Index} {a.Family}: fire"));
            Add(uses, ids, a.ImpactSound, Ammo, Inv($"{a.Index} {a.Family}: impact"));
        }

        // Robots: the engine loop, the frame events, the script's SOUND ops.
        foreach (var t in ais.Types)
        {
            var name = Inv($"{t.Index} {(t.Name.Length > 0 ? t.Name : "-")}");
            Add(uses, ids, t.EngineSound ?? -1, Robot, name + ": engine");
            foreach (var id in t.Events.Select(e => e.Sound).Distinct())
            {
                Add(uses, ids, id, Robot, name + ": animation frame");
            }

            foreach (var id in ScriptSounds(ais, t.ScriptVa).Distinct())
            {
                Add(uses, ids, id, Robot, name + ": script");
            }
        }

        // Acts (doors, buttons, levers, lifts) and destruction rows.
        foreach (var (act, id) in ActRules.SoundOneShot.OrderBy(p => p.Key))
        {
            Add(uses, ids, id, Act, Inv($"0x{act:X2} sound cue (doors, buttons, lifts)"));
        }

        foreach (var d in destruction)
        {
            Add(uses, ids, d.Sound, Destruction, Inv($"row {d.Index}"));
        }

        // Burning sprites' loops, then the game code's own.
        foreach (var (sprite, id) in GameSession.SpriteSounds.OrderBy(p => p.Key))
        {
            Add(uses, ids, id, Sprite, SkyNet.Game.Mods.SpriteModels.KeyOf(sprite) + " burning loop");
        }

        foreach (var (id, what) in CodeIds)
        {
            Add(uses, ids, id, Game, what);
        }

        uses.AddRange(CodeNames.Where(c => Same(c.Name, sample)).Select(c => new SoundUse(Game, c.What)));
        return uses;
    }

    /// <summary>The SOUND ops' ids reachable from a script's first op (both ways of each IF).</summary>
    private static IEnumerable<int> ScriptSounds(AisData ais, uint? start)
    {
        if (start is not { } first)
        {
            yield break;
        }

        var seen = new HashSet<uint>();
        var open = new Stack<uint>([first]);
        while (open.TryPop(out var va))
        {
            if (!seen.Add(va) || !ais.Ops.TryGetValue(va, out var op))
            {
                continue;
            }

            switch (op.Kind)
            {
                case AisOpKind.End:
                    continue;
                case AisOpKind.Goto:
                    open.Push(op.Target);
                    continue;
                case AisOpKind.If:
                    open.Push(op.Target);
                    break;
                case AisOpKind.Sound:
                    yield return op.A;
                    break;
            }

            open.Push(op.Next);
        }
    }

    private static void Add(List<SoundUse> uses, HashSet<int> ids, int id, string kind, string what)
    {
        if (id < 0 || !ids.Contains(id))
        {
            return;
        }

        uses.Add(new SoundUse(kind, ids.Count > 1 ? Inv($"{what} (id {id})") : what));
    }

    private static bool Same(string a, string b) => string.Equals(a, b, StringComparison.OrdinalIgnoreCase);

    private static string Inv(FormattableString s) => s.ToString(CultureInfo.InvariantCulture);
}

/// <summary>A sample's format as stored: a DOS .RAW (unsigned 8-bit mono, 11025 Hz) or a RIFF/WAVE's fmt chunk.</summary>
public readonly record struct SampleFormat(int Rate, int Bits, int Channels, int Frames)
{
    public double Seconds => Rate > 0 ? (double)Frames / Rate : 0;

    /// <summary>The format of <paramref name="bytes"/> named <paramref name="name"/>; null for a WAVE it cannot read.</summary>
    public static SampleFormat? Of(string name, ReadOnlySpan<byte> bytes)
    {
        if (!name.EndsWith(".WAV", StringComparison.OrdinalIgnoreCase))
        {
            return new SampleFormat(SoundBank.RawRate, 8, 1, bytes.Length);
        }

        // RIFF chunks after "RIFF size WAVE": "fmt " (format, channels, rate, ..., bits) and "data".
        const int Header = 12, ChunkHead = 8, FmtSize = 16;
        if (bytes.Length < Header || !bytes[..4].SequenceEqual("RIFF"u8) || !bytes[8..12].SequenceEqual("WAVE"u8))
        {
            return null;
        }

        int channels = 0, rate = 0, bits = 0;
        for (var off = Header; off + ChunkHead <= bytes.Length;)
        {
            var id = bytes.Slice(off, 4);
            var size = (int)Math.Min(BinaryPrimitives.ReadUInt32LittleEndian(bytes[(off + 4)..]), int.MaxValue);
            off += ChunkHead;
            if (id.SequenceEqual("fmt "u8) && off + FmtSize <= bytes.Length)
            {
                channels = BinaryPrimitives.ReadUInt16LittleEndian(bytes[(off + 2)..]);
                rate = BinaryPrimitives.ReadInt32LittleEndian(bytes[(off + 4)..]);
                bits = BinaryPrimitives.ReadUInt16LittleEndian(bytes[(off + 14)..]);
            }
            else if (id.SequenceEqual("data"u8) && channels > 0 && bits > 0)
            {
                var frameBytes = channels * bits / 8;
                return new SampleFormat(rate, bits, channels, frameBytes > 0 ? Math.Min(size, bytes.Length - off) / frameBytes : 0);
            }

            off += size + (size & 1);
        }

        return null;
    }
}
