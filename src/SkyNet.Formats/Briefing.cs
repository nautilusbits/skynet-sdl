using System.Collections.Immutable;
using System.Globalization;

namespace SkyNet.Formats;

/// <summary>One turn of the [BR] briefing dialogue: the speaker (portrait BRIEF&lt;speaker&gt;.IMG) and the text.</summary>
public readonly record struct BriefingLine(int Speaker, string Text);

/// <summary>
/// A mission briefing NNN.TXT (MDMDBRIF.BSA), ported from the Godot port's briefing.gd. A section
/// opens with a header line <c>[XX]</c> and ends at a line starting <c>%%%%</c>; entries are split
/// by <c>####</c> lines; whole bracketed lines are tags, not text; the lines of an entry are joined
/// with one space. Header matching is exact and case-sensitive; the first matching header wins.
/// </summary>
public sealed class Briefing
{
    public const int MissionSections = 5;
    public const int HintSections = 9;

    /// <summary>The [EN] objectives paragraph (every text line of the section, joined).</summary>
    public string Objectives { get; }
    /// <summary>The [BR] dialogue; the speaker starts at 3, a <c>[NNN]</c> tag sets it, a dotted tag ends the dialogue.</summary>
    public ImmutableArray<BriefingLine> Lines { get; }
    /// <summary>The first non-blank entry of [M1]..[M5], "" where absent.</summary>
    public ImmutableArray<string> Missions { get; }
    /// <summary>
    /// Every entry of [M1]..[M5], <b>blank entries included</b>: the DOS parser counts each
    /// <c>####</c>-terminated entry into the objective counter, text or not (Future Shock's 060.TXT
    /// has twelve blank [M1] entries before its line). Text after the last <c>####</c> is one more entry.
    /// </summary>
    public ImmutableArray<ImmutableArray<string>> MissionTexts { get; }
    /// <summary>The first non-blank entry of [G1]..[G9], "" where absent.</summary>
    public ImmutableArray<string> Hints { get; }
    /// <summary>The [TA] asset names, split on spaces.</summary>
    public ImmutableArray<string> Tactical { get; }
    /// <summary>The <c>[NNN]</c> tag opening [EN] (else [BR]) before any text: the briefing picture; −1 when none.</summary>
    public int Picture { get; }

    Briefing(string objectives, ImmutableArray<BriefingLine> lines, ImmutableArray<string> missions,
        ImmutableArray<ImmutableArray<string>> missionTexts, ImmutableArray<string> hints, ImmutableArray<string> tactical, int picture)
    {
        Objectives = objectives;
        Lines = lines;
        Missions = missions;
        MissionTexts = missionTexts;
        Hints = hints;
        Tactical = tactical;
        Picture = picture;
    }

    /// <summary>The number of objectives the mission counts: every [M1]..[M5] entry.</summary>
    public int ObjectiveCount => MissionTexts.Sum(s => s.Length);

    public static Briefing Parse(ReadOnlySpan<byte> bytes)
    {
        var text = TextLines.Split(bytes);
        return new Briefing(
            ParseObjectives(text),
            Dialogue(text),
            Numbered(text, "M", MissionSections),
            [.. Enumerable.Range(1, MissionSections).Select(i => Entries(text, $"[M{i}]", keepBlank: true))],
            Numbered(text, "G", HintSections),
            [.. Entries(text, "[TA]").SelectMany(e => e.Split(' ', StringSplitOptions.RemoveEmptyEntries)).Select(TextLines.Strip).Where(w => w.Length > 0)],
            ParsePicture(text));
    }

    /// <summary>Godot's <c>String.is_valid_int</c>: an optional sign, then decimal digits only.</summary>
    static bool TryInt(string s, out int v)
    {
        v = 0;
        int i = s.Length > 0 && (s[0] == '+' || s[0] == '-') ? 1 : 0;
        if (i >= s.Length)
            return false;
        for (int k = i; k < s.Length; k++)
            if (s[k] is < '0' or > '9')
                return false;
        return int.TryParse(s, NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out v);
    }

    static int ParsePicture(string[] text)
    {
        foreach (var header in new[] { "[EN]", "[BR]" })
        {
            bool inside = false;
            foreach (var line in text)
            {
                if (!inside)
                {
                    inside = line == header;
                    continue;
                }
                if (line.StartsWith("%%%%", StringComparison.Ordinal))
                    break;
                if (line.Length == 0)
                    continue;
                if (TextLines.IsBracketed(line))
                {
                    if (TryInt(line[1..^1], out int v))
                        return v;
                    continue;
                }
                break;
            }
        }
        return -1;
    }

    static ImmutableArray<string> Numbered(string[] text, string prefix, int count) =>
        [.. Enumerable.Range(1, count).Select(i => Entries(text, $"[{prefix}{i}]") is { Length: > 0 } e ? e[0] : "")];

    /// <summary>Entries of one section: text between the header and %%%%, split on ####, tag lines dropped.</summary>
    static ImmutableArray<string> Entries(string[] text, string header, bool keepBlank = false)
    {
        var outp = ImmutableArray.CreateBuilder<string>();
        var buf = new List<string>();
        bool inside = false;
        foreach (var line in text)
        {
            if (!inside)
            {
                if (line == header)
                    inside = true;
                continue;
            }
            if (line.StartsWith("%%%%", StringComparison.Ordinal))
                break;
            if (line == "####")
            {
                if (buf.Count > 0 || keepBlank)
                {
                    outp.Add(string.Join(' ', buf));
                    buf.Clear();
                }
                continue;
            }
            if (line.Length == 0 || TextLines.IsBracketed(line))
                continue;
            buf.Add(line);
        }
        if (buf.Count > 0)
            outp.Add(string.Join(' ', buf));
        return outp.ToImmutable();
    }

    static string ParseObjectives(string[] text)
    {
        var outp = new List<string>();
        bool inside = false;
        foreach (var line in text)
        {
            if (!inside)
            {
                if (line == "[EN]")
                    inside = true;
                continue;
            }
            if (line.StartsWith("%%%%", StringComparison.Ordinal))
                break;
            if (line == "####" || line.Length == 0 || TextLines.IsBracketed(line))
                continue;
            outp.Add(line);
        }
        return TextLines.Strip(string.Join(' ', outp));
    }

    static ImmutableArray<BriefingLine> Dialogue(string[] text)
    {
        var lines = ImmutableArray.CreateBuilder<BriefingLine>();
        var buf = new List<string>();
        bool inside = false;
        int speaker = 3;
        foreach (var line in text)
        {
            if (!inside)
            {
                if (line == "[BR]")
                    inside = true;
                continue;
            }
            if (line.StartsWith("%%%%", StringComparison.Ordinal))
                break;
            if (line == "####")
            {
                if (buf.Count > 0)
                {
                    lines.Add(new BriefingLine(speaker, string.Join(' ', buf)));
                    buf.Clear();
                }
                continue;
            }
            if (line.Length == 0)
                continue;
            if (TextLines.IsBracketed(line))
            {
                string tag = line.Length >= 2 ? line[1..^1] : "";
                if (tag.Length == 0 || tag[0] is 'v' or 'V')
                    continue;
                if (tag.Contains('.'))
                    break;
                if (TryInt(tag, out int s))
                    speaker = s;
                continue;
            }
            buf.Add(line);
        }
        return lines.ToImmutable();
    }
}
