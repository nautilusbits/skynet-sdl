using System.Globalization;
using System.Text.RegularExpressions;

namespace SkyNet.Verify;

/// <summary>One node line of the Godot port's trigger lock: what its graph says the node is, how it
/// can be set off and what its first and second activation do.</summary>
public sealed record LockNode(int Map, int Id, int Act, IReadOnlyList<string> Modes, int DeathHp,
    IReadOnlyList<string> First, IReadOnlyList<string> Second)
{
    /// <summary>The mode the verifier drives first (trigger_verifier.gd primary_mode), in the
    /// row's vocabulary: use, prox, exit, shot_each, shot_death, counter, path; "" for none.</summary>
    public string Primary => PrimaryOf(Modes);

    /// <summary><see cref="Primary"/> of a list of lock modes (TriggerVerifier.Modes).</summary>
    public static string PrimaryOf(IReadOnlyList<string> modes)
    {
        foreach (var (want, row) in Order)
        {
            if (modes.Contains(want))
            {
                return row;
            }
        }

        return "";
    }

    public bool HasWalk => Modes.Contains("walk");

    private static readonly (string Lock, string Row)[] Order =
    [
        ("use", "use"), ("prox", "prox"), ("touch", "exit"), ("shot", "shot_each"),
        ("death", "shot_death"), ("counter", "counter"), ("path", "path"),
    ];
}

/// <summary>Reads <c>skynet.triggers.lock</c> (the Godot port's tests/rules): ids lower hex, act
/// bytes upper hex, modes, first[...] and second[...] effect lists.</summary>
public static partial class TriggerLock
{
    [GeneratedRegex(@"^(\d+) ([0-9a-f]{5}) ([0-9A-F]{2}) (\S+) chain\[[^\]]*\] first\[([^\]]*)\] second\[([^\]]*)\]")]
    private static partial Regex NodeLine();

    [GeneratedRegex(@"death:hp(-?\d+)")]
    private static partial Regex DeathHp();

    public static List<LockNode> Parse(IEnumerable<string> lines)
    {
        var list = new List<LockNode>();
        foreach (var raw in lines)
        {
            var m = NodeLine().Match(raw.Trim());
            if (!m.Success)
            {
                continue;
            }

            var modeField = m.Groups[4].Value;
            var modes = modeField == "-"
                ? []
                : modeField.Split(',').Select(s => s.Split(':')[0]).ToList();
            var hp = DeathHp().Match(modeField) is { Success: true } h ? int.Parse(h.Groups[1].Value, CultureInfo.InvariantCulture) : 0;
            list.Add(new LockNode(
                int.Parse(m.Groups[1].Value, CultureInfo.InvariantCulture),
                int.Parse(m.Groups[2].Value, NumberStyles.HexNumber, CultureInfo.InvariantCulture),
                int.Parse(m.Groups[3].Value, NumberStyles.HexNumber, CultureInfo.InvariantCulture),
                modes, hp, Effects(m.Groups[5].Value), Effects(m.Groups[6].Value)));
        }

        return list;
    }

    public static List<LockNode> Load(string path) => Parse(File.ReadLines(path));

    public static List<string> Effects(string list) =>
        list == "-" ? [] : [.. list.Split(' ', StringSplitOptions.RemoveEmptyEntries)];
}

/// <summary>The graph against the runtime by content, a multiset (trigger_equiv.gd compare).</summary>
public static class Equiv
{
    /// <summary>A mover with no travel has no direction: ±0 compare equal (trigger_equiv.gd canon).</summary>
    public static string Canon(string tok) =>
        tok.EndsWith("-0", StringComparison.Ordinal) || tok.EndsWith("+0", StringComparison.Ordinal) ? tok[..^2] + "0" : tok;

    public static (List<string> Missing, List<string> Extra) Compare(IEnumerable<string> want, IEnumerable<string> got)
    {
        var left = Counts(want);
        var right = Counts(got);
        var missing = new List<string>();
        var extra = new List<string>();
        foreach (var (t, n) in left)
        {
            for (var i = n - right.GetValueOrDefault(t); i > 0; i--)
            {
                missing.Add(t);
            }
        }

        foreach (var (t, n) in right)
        {
            for (var i = n - left.GetValueOrDefault(t); i > 0; i--)
            {
                extra.Add(t);
            }
        }

        missing.Sort(StringComparer.Ordinal);
        extra.Sort(StringComparer.Ordinal);
        return (missing, extra);
    }

    /// <summary>trigger_verifier.gd _why: "" when equal, else "missing ...; extra ...".</summary>
    public static string Why(IEnumerable<string> want, IEnumerable<string> got)
    {
        var (missing, extra) = Compare(want, got);
        var parts = new List<string>();
        if (missing.Count > 0)
        {
            parts.Add("missing " + string.Join(' ', missing));
        }

        if (extra.Count > 0)
        {
            parts.Add("extra " + string.Join(' ', extra));
        }

        return string.Join("; ", parts);
    }

    public static string Join(string a, string b) => a.Length == 0 ? b : b.Length == 0 ? a : a + "; " + b;

    private static Dictionary<string, int> Counts(IEnumerable<string> list)
    {
        var d = new Dictionary<string, int>(StringComparer.Ordinal);
        foreach (var t in list)
        {
            var k = Canon(t);
            d[k] = d.GetValueOrDefault(k) + 1;
        }

        return d;
    }
}
