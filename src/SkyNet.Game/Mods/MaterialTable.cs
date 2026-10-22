using System.Collections.Immutable;
using System.Globalization;
using System.Text.Json;
using RetroEng.Json;
using RetroEng.Render;

namespace SkyNet.Game.Mods;

/// <summary>
/// <c>materials.json</c>: what each DOS texture record is made of in the enhanced look
/// (<see cref="Renderer.SetSurface"/>; docs/modding.md, Materials). A record's surface is the first
/// of: its <see cref="Textures"/> entry, the last <see cref="Patterns"/> entry matching
/// "BBB/RRR", its bank's <see cref="Banks"/> entry, <see cref="Default"/>, matte.
/// <code>
///   { "default": "matte", "banks": { "210": "stone" },
///     "textures": { "230/014": "metal", "230/015": { "preset": "painted", "roughness": 0.8 } },
///     "patterns": { "23?/1*": "concrete" } }
/// </code>
/// Tables stack (<see cref="Over"/>): the shipped one, then each mod's in load order, a later
/// entry replacing an earlier one of the same key.
/// </summary>
public sealed record MaterialTable
{
    /// <summary>The file in a mod's root.</summary>
    public const string FileName = "materials.json";

    private const string PresetKey = "preset", RoughnessKey = "roughness", SpecularKey = "specular";
    private static readonly string[] TopKeys = ["default", "banks", "textures", "patterns", "comment"];
    private static readonly string[] EntryKeys = [PresetKey, RoughnessKey, SpecularKey];

    /// <summary>A table that sets nothing (every record matte).</summary>
    public static readonly MaterialTable Empty = new();

    /// <summary>Every record's surface where nothing else names it; null: not set by this table.</summary>
    public Surface? Default { get; init; }

    /// <summary>Surfaces per bank (TEXTURE.NNN).</summary>
    public ImmutableSortedDictionary<int, Surface> Banks { get; init; } = ImmutableSortedDictionary<int, Surface>.Empty;

    /// <summary>Surfaces per record, keyed (bank, record).</summary>
    public ImmutableSortedDictionary<(int Bank, int Record), Surface> Textures { get; init; } =
        ImmutableSortedDictionary<(int Bank, int Record), Surface>.Empty;

    /// <summary>Globs over "BBB/RRR" (<c>*</c> any digits, <c>?</c> one), in order: the last match wins.</summary>
    public ImmutableList<(string Pattern, Surface Surface)> Patterns { get; init; } = [];

    /// <summary>A record's key as the file writes it: "230/014".</summary>
    public static string Key(int bank, int record) => string.Create(CultureInfo.InvariantCulture, $"{bank:D3}/{record:D3}");

    /// <summary>What record <paramref name="record"/> of bank <paramref name="bank"/> is made of.</summary>
    public Surface Resolve(int bank, int record)
    {
        if (Textures.TryGetValue((bank, record), out var own))
        {
            return own;
        }

        var key = Key(bank, record);
        for (var i = Patterns.Count - 1; i >= 0; i--)
        {
            if (Matches(Patterns[i].Pattern, key))
            {
                return Patterns[i].Surface;
            }
        }

        return ResolveBank(bank);
    }

    /// <summary>What a whole bank is made of (the ground's atlas): its entry, else the default.</summary>
    public Surface ResolveBank(int bank) => Banks.TryGetValue(bank, out var b) ? b : Default ?? Surface.Matte;

    /// <summary>This table with <paramref name="later"/> over it: its default when it sets one, its
    /// banks and textures replacing these by key, its patterns after these.</summary>
    public MaterialTable Over(MaterialTable later) => new()
    {
        Default = later.Default ?? Default,
        Banks = Banks.SetItems(later.Banks),
        Textures = Textures.SetItems(later.Textures),
        Patterns = Patterns.AddRange(later.Patterns),
    };

    /// <summary>This table with record (<paramref name="bank"/>, <paramref name="record"/>)'s own
    /// entry set to <paramref name="surface"/>; null removes it.</summary>
    public MaterialTable WithTexture(int bank, int record, Surface? surface) => this with
    {
        Textures = surface is { } s ? Textures.SetItem((bank, record), s) : Textures.Remove((bank, record)),
    };

    // ---- reading ----

    /// <summary>Reads a <c>materials.json</c> (comments and trailing commas allowed).
    /// <see cref="JsonDataException"/> when it does not read: an unknown key or preset, a bad record key.</summary>
    public static MaterialTable Parse(ReadOnlySpan<byte> utf8)
    {
        var root = JsonItem.Parse(utf8, lenient: true).AsObject();
        Only(root, TopKeys);
        var banks = ImmutableSortedDictionary.CreateBuilder<int, Surface>();
        foreach (var (name, value) in root.Opt("banks")?.Members ?? [])
        {
            banks[Number(value, name, "a bank number (\"210\")")] = Entry(value);
        }

        var textures = ImmutableSortedDictionary.CreateBuilder<(int, int), Surface>();
        foreach (var (name, value) in root.Opt("textures")?.Members ?? [])
        {
            textures[RecordOf(value, name)] = Entry(value);
        }

        var patterns = ImmutableList.CreateBuilder<(string, Surface)>();
        foreach (var (name, value) in root.Opt("patterns")?.Members ?? [])
        {
            if (name.Length == 0 || name.Any(c => !char.IsAsciiDigit(c) && c is not ('/' or '*' or '?')))
            {
                throw value.Error($"pattern \"{name}\": digits, '/', '*' and '?' only");
            }

            patterns.Add((name, Entry(value)));
        }

        return new MaterialTable
        {
            Default = root.Opt("default") is { } d ? Entry(d) : null,
            Banks = banks.ToImmutable(),
            Textures = textures.ToImmutable(),
            Patterns = patterns.ToImmutable(),
        };
    }

    /// <summary>A preset's name in the file (lower case).</summary>
    public static string NameOf(SurfacePreset preset) => preset.ToString().ToLowerInvariant();

    /// <summary>A preset by its name (any case; "screen" is glass); null when there is none.</summary>
    public static SurfacePreset? PresetOf(string name) =>
        name.Equals("screen", StringComparison.OrdinalIgnoreCase) ? SurfacePreset.Glass
        : name.Length > 0 && char.IsAsciiLetter(name[0]) && Enum.TryParse<SurfacePreset>(name, ignoreCase: true, out var p) ? p : null;

    // A value: a preset's name, or { "preset", "roughness", "specular" }.
    private static Surface Entry(JsonItem value)
    {
        if (value.Kind == JsonKind.String)
        {
            return new Surface(Preset(value));
        }

        var o = value.AsObject();
        Only(o, EntryKeys);
        return new Surface(Preset(o.Member(PresetKey)), Scale(o, RoughnessKey), Scale(o, SpecularKey));
    }

    private static SurfacePreset Preset(JsonItem name) =>
        PresetOf(name.AsString()) ?? throw name.Error(
            $"unknown preset \"{name.AsString()}\" ({string.Join(", ", Enum.GetValues<SurfacePreset>().Select(NameOf))}, screen)");

    // A tweak: a factor of 0 or more; 1 when absent.
    private static float Scale(JsonItem o, string key)
    {
        if (o.Opt(key) is not { } v)
        {
            return 1f;
        }

        var f = v.AsDouble();
        return f >= 0 && double.IsFinite(f) ? (float)f : throw v.Error($"\"{key}\" is a factor of 0 or more");
    }

    private static int Number(JsonItem at, string text, string what) =>
        int.TryParse(text, NumberStyles.None, CultureInfo.InvariantCulture, out var n) ? n : throw at.Error($"\"{text}\" is not {what}");

    // "230/014" (or "230/14").
    private static (int, int) RecordOf(JsonItem at, string key)
    {
        const string what = "a record (\"230/014\")";
        var slash = key.IndexOf('/');
        return slash < 0 ? throw at.Error($"\"{key}\" is not {what}") : (Number(at, key[..slash], what), Number(at, key[(slash + 1)..], what));
    }

    private static void Only(JsonItem o, string[] keys)
    {
        foreach (var (name, value) in o.Members)
        {
            if (!keys.Contains(name))
            {
                throw value.Error($"unknown key \"{name}\" ({string.Join(", ", keys)})");
            }
        }
    }

    /// <summary>Whether glob <paramref name="pattern"/> (<c>*</c> any run, <c>?</c> one character) matches all of <paramref name="text"/>.</summary>
    public static bool Matches(ReadOnlySpan<char> pattern, ReadOnlySpan<char> text)
    {
        if (pattern.IsEmpty)
        {
            return text.IsEmpty;
        }

        if (pattern[0] == '*')
        {
            for (var i = 0; i <= text.Length; i++)
            {
                if (Matches(pattern[1..], text[i..]))
                {
                    return true;
                }
            }

            return false;
        }

        return !text.IsEmpty && (pattern[0] == '?' || pattern[0] == text[0]) && Matches(pattern[1..], text[1..]);
    }

    // ---- writing ----

    /// <summary>The table as a <c>materials.json</c> (UTF-8, indented; a surface without tweaks as its preset's name).</summary>
    public byte[] ToJson(string? comment = null)
    {
        using var stream = new MemoryStream();
        using (var w = new Utf8JsonWriter(stream, new JsonWriterOptions { Indented = true }))
        {
            w.WriteStartObject();
            if (comment != null)
            {
                w.WriteString("comment", comment);
            }

            if (Default is { } d)
            {
                w.WritePropertyName("default");
                Write(w, d);
            }

            Section(w, "banks", Banks.Select(b => (b.Key.ToString(CultureInfo.InvariantCulture), b.Value)));
            Section(w, "textures", Textures.Select(t => (Key(t.Key.Bank, t.Key.Record), t.Value)));
            Section(w, "patterns", Patterns.Select(p => (p.Pattern, p.Surface)));
            w.WriteEndObject();
        }

        stream.WriteByte((byte)'\n');
        return stream.ToArray();
    }

    private static void Section(Utf8JsonWriter w, string name, IEnumerable<(string Key, Surface Surface)> entries)
    {
        var list = entries.ToList();
        if (list.Count == 0)
        {
            return;
        }

        w.WriteStartObject(name);
        foreach (var (key, surface) in list)
        {
            w.WritePropertyName(key);
            Write(w, surface);
        }

        w.WriteEndObject();
    }

    private static void Write(Utf8JsonWriter w, Surface s)
    {
        if (s.Preset == SurfacePreset.Matte || (s.Roughness == 1f && s.Specular == 1f))
        {
            w.WriteStringValue(NameOf(s.Preset));
            return;
        }

        w.WriteStartObject();
        w.WriteString(PresetKey, NameOf(s.Preset));
        if (s.Roughness != 1f)
        {
            w.WriteNumber(RoughnessKey, Math.Round(s.Roughness, 3));
        }

        if (s.Specular != 1f)
        {
            w.WriteNumber(SpecularKey, Math.Round(s.Specular, 3));
        }

        w.WriteEndObject();
    }
}
