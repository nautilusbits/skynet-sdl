using System.Collections.Immutable;
using System.Text;

namespace SkyNet.Formats;

/// <summary>Line handling shared by the text formats (PRS, TRANSFRM.PRS, briefing TXT).</summary>
static class TextLines
{
    /// <summary>
    /// The file as one-byte characters up to the first NUL, split on '\n', each line stripped of
    /// leading and trailing characters at or below ' ' (so CR LF and LF both work), as the Godot
    /// port's <c>get_string_from_ascii().split("\n")</c> + <c>strip_edges()</c>.
    /// </summary>
    public static string[] Split(ReadOnlySpan<byte> bytes)
    {
        int nul = bytes.IndexOf((byte)0);
        if (nul >= 0)
            bytes = bytes[..nul];
        var lines = Encoding.Latin1.GetString(bytes).Split('\n');
        for (int i = 0; i < lines.Length; i++)
            lines[i] = Strip(lines[i]);
        return lines;
    }

    public static string Strip(string s)
    {
        int a = 0, b = s.Length;
        while (a < b && s[a] <= ' ') a++;
        while (b > a && s[b - 1] <= ' ') b--;
        return s[a..b];
    }

    /// <summary>A whole line in square brackets: a section header or a tag.</summary>
    public static bool IsBracketed(string line) => line.StartsWith('[') && line.EndsWith(']');
}

/// <summary>
/// An INI-like .PRS text table (STRINGS.PRS, VOICE.PRS, CHEAT.PRS, NETLEVEL.PRS ...), ported from
/// the Godot port's prs_file.gd: <c>key = value</c> lines with both sides stripped, ';' comment
/// lines and '[section]' lines skipped, lines without '=' (or starting with it) ignored. The
/// <b>first</b> definition of a key wins, as in the DOS parser (STRINGS.PRS lists every pickup
/// message twice). Keys are case-sensitive.
/// </summary>
public sealed class PrsFile
{
    /// <summary>Key → value, first definition only.</summary>
    public ImmutableDictionary<string, string> Values { get; }
    /// <summary>Keys in first-definition order.</summary>
    public ImmutableArray<string> Keys { get; }

    PrsFile(ImmutableDictionary<string, string> values, ImmutableArray<string> keys)
    {
        Values = values;
        Keys = keys;
    }

    public static PrsFile Parse(ReadOnlySpan<byte> bytes)
    {
        var values = ImmutableDictionary.CreateBuilder<string, string>(StringComparer.Ordinal);
        var keys = ImmutableArray.CreateBuilder<string>();
        foreach (var l in TextLines.Split(bytes))
        {
            if (l.Length == 0 || l[0] == ';' || l[0] == '[')
                continue;
            int eq = l.IndexOf('=');
            if (eq <= 0)
                continue;
            string k = TextLines.Strip(l[..eq]);
            if (values.ContainsKey(k))
                continue;
            values[k] = TextLines.Strip(l[(eq + 1)..]);
            keys.Add(k);
        }
        return new PrsFile(values.ToImmutable(), keys.ToImmutable());
    }

    public string Text(string key, string fallback = "") => Values.TryGetValue(key, out var v) ? v : fallback;
}
