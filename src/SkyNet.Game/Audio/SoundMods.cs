using System.Collections.Immutable;
using RetroEng.Audio;
using RetroEng.Content;
using RetroEng.Diagnostics;
using SkyNet.Data;
using SkyNet.Game.Mods;

namespace SkyNet.Game.Audio;

/// <summary>A sample as it plays: the sound (null: none reads) and where it comes from
/// ("mod-name:sounds/EXPLO1.wav", or <see cref="SoundMods.Dos"/>).</summary>
public sealed record EffectiveSample(Sound? Sound, string Origin);

/// <summary>
/// The mods' say in the game's samples (docs/modding.md#sounds): a mod's <c>sounds/NAME.wav</c>
/// replaces the DOS sample NAME wherever it plays, and the table <c>sounds</c> (a mod rule: no
/// shipped file, the rows lie over an empty table) sets how it plays.
/// <code>
///   sounds/EXPLO1.wav         highest mod that has it ─► the clip  (else EXPLO1.RAW from the BSA)
///   tables/sounds.json        {"sample": "EXPLO1", "volume": 1.5, ...} ─► SoundBank's mixer calls
/// </code>
/// </summary>
public sealed class SoundMods(Func<VirtualFileSystem?> files, Func<VirtualFileSystem?> tables)
{
    /// <summary>Where a mod's replacement samples lie.</summary>
    public const string Folder = "sounds/";
    /// <summary>The only format read (RIFF/WAVE PCM, 8 or 16 bits, mono or stereo, any rate).</summary>
    public const string Extension = ".wav";
    /// <summary>The origin of a sample read from the game's own archive.</summary>
    public const string Dos = SoundBank.Archive;

    private sealed record Loaded(VirtualFileSystem? Tables, IReadOnlyDictionary<string, SoundEventRow> Rows);

    /// <summary>The row of a sample without one (its name is not read).</summary>
    private static readonly SoundEventRow Neutral = SoundEventRow.Default("");

    private Loaded? _loaded;

    /// <summary>The mounted mods (<see cref="ModContent.Files"/>, <see cref="GameTables.Mods"/>).</summary>
    public static SoundMods Mounted { get; } = new(() => ModContent.Files, () => GameTables.Mods);

    /// <summary>The mods whose <see cref="Folder"/> replaces samples now.</summary>
    public VirtualFileSystem? Files => files();

    /// <summary>The empty table a mod's rows lie over (the editor's base).</summary>
    public static TableFile<ImmutableArray<SoundEventRow>> Base => new(TableNames.Sounds, "", "mod rule", "", "SoundMods", []);

    /// <summary>The rows of <paramref name="mods"/> over <see cref="Base"/>; throws
    /// <see cref="InvalidDataException"/> for a file that does not read.</summary>
    public static ImmutableArray<SoundEventRow> Rows(VirtualFileSystem? mods) =>
        GameTables.Layer(TableNames.Sounds, TableSchemas.Sounds, Base, mods).Rows;

    /// <summary>A sample's replacement path in a mod: "explo1.raw" → "sounds/EXPLO1.wav".</summary>
    public static string PathOf(string sample) => Folder + SoundEventRow.KeyOf(sample) + Extension;

    /// <summary>The sample <paramref name="sample"/> (a DOS name, "EXPLO1.RAW") as the game plays it:
    /// the highest mod's <see cref="PathOf"/> in <paramref name="mods"/>, else <paramref name="dos"/>'s
    /// file. A replacement that is not PCM WAV is logged and the DOS sample stays.</summary>
    public static EffectiveSample Effective(VirtualFileSystem? mods, string sample, Func<string, byte[]?> dos)
    {
        var path = VfsPath.Normalize(PathOf(sample));
        foreach (var mount in mods?.Mounts ?? [])
        {
            if (mount.Mount.TryReadAllBytes(path) is not { } bytes)
            {
                continue;
            }

            if (SoundBank.DecodeWav(bytes) is { } sound)
            {
                return new EffectiveSample(sound, $"{mount.Name}:{path}");
            }

            Log.Warn("Mods", $"{mount.Name}:{path} is not PCM WAV (8 or 16 bits); {sample} stays DOS's");
            break;
        }

        var raw = dos(sample) is { } b ? SoundBank.Decode(sample, b) : null;
        return new EffectiveSample(raw, Dos);
    }

    /// <summary>How <paramref name="sample"/> plays: its row, or one that changes nothing.
    /// Read again when the mods' tables change; a table that does not read is logged and ignored.</summary>
    public SoundEventRow Event(string sample)
    {
        var rows = Current();
        return rows.Count > 0 && rows.TryGetValue(SoundEventRow.KeyOf(sample), out var row) ? row : Neutral;
    }

    private IReadOnlyDictionary<string, SoundEventRow> Current()
    {
        var mods = tables();
        var cached = _loaded;
        if (cached != null && ReferenceEquals(cached.Tables, mods))
        {
            return cached.Rows;
        }

        cached = new Loaded(mods, Load(mods));
        _loaded = cached;
        return cached.Rows;
    }

    private static Dictionary<string, SoundEventRow> Load(VirtualFileSystem? mods)
    {
        var rows = new Dictionary<string, SoundEventRow>(StringComparer.OrdinalIgnoreCase);
        if (mods == null)
        {
            return rows;
        }

        try
        {
            foreach (var row in Rows(mods))
            {
                rows[SoundEventRow.KeyOf(row.Sample)] = row;
            }
        }
        catch (InvalidDataException e)
        {
            Log.Warn("Mods", $"{e.Message}; sounds play as DOS's");
            return [];
        }

        if (rows.Count > 0)
        {
            Log.Info("Mods", $"Table {TableNames.Sounds}: {rows.Count} samples play changed");
        }

        return rows;
    }
}
