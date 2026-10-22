using System.Globalization;
using SkyNet.Formats;
using SkyNet.Verify;

// SkyNet.Verify triggers|missions [options] - see README.md.
var opts = new Dictionary<string, string>(StringComparer.Ordinal);
var mode = "";
foreach (var a in args)
{
    if (a.StartsWith("--", StringComparison.Ordinal))
    {
        var eq = a.IndexOf('=', StringComparison.Ordinal);
        opts[eq > 0 ? a[2..eq] : a[2..]] = eq > 0 ? a[(eq + 1)..] : "true";
    }
    else
    {
        mode = a;
    }
}

if (mode is not ("triggers" or "missions"))
{
    Console.Error.WriteLine("usage: SkyNet.Verify triggers --rules=DIR [--maps=210,215] [--out=FILE] [--expect=FILE]");
    Console.Error.WriteLine("       SkyNet.Verify missions --rules=DIR [--only=210,230] [--out=FILE] [--expect=FILE]");
    Console.Error.WriteLine("  --data=DIR   the game data (default: SKYNET_DATA)");
    Console.Error.WriteLine("  --rules=DIR  the Godot port's tests/rules (skynet.triggers.lock, skynet.missions.txt, skynet.xfail)");
    return 2;
}

var dir = opts.GetValueOrDefault("data") ?? Environment.GetEnvironmentVariable("SKYNET_DATA");
if (string.IsNullOrWhiteSpace(dir) || GameData.Open(dir) is not { } data)
{
    Console.Error.WriteLine("no game data: pass --data=DIR or set SKYNET_DATA");
    return 2;
}

if (!opts.TryGetValue("rules", out var rules))
{
    Console.Error.WriteLine("--rules=DIR is required (the trigger lock and the mission specs live there)");
    return 2;
}

var game = data.Game == GameKind.FutureShock ? "shock" : "skynet";
var sw = System.Diagnostics.Stopwatch.StartNew();
return mode == "triggers" ? Triggers() : Missions();

int Triggers()
{
    var lockPath = Path.Combine(rules, $"{game}.triggers.lock");
    if (!File.Exists(lockPath))
    {
        Console.Error.WriteLine($"no {game}.triggers.lock in the rules folder");
        return 2;
    }

    var nodes = TriggerLock.Load(lockPath);
    var v = new TriggerVerifier(data, nodes);
    var maps = opts.TryGetValue("maps", out var list)
        ? [.. list.Split(',').Select(s => int.Parse(s, CultureInfo.InvariantCulture))]
        : nodes.Select(n => n.Map).Distinct().Order().ToList();
    var rows = v.Run(maps);
    if (opts.TryGetValue("out", out var outPath))
    {
        File.WriteAllText(outPath, TriggerVerifier.Format(rows));
        Console.WriteLine($"[verify] rows written: {outPath}");
    }

    var xfail = LoadXfail(Path.Combine(rules, $"{game}.xfail"));
    var by = rows.GroupBy(r => r.Result).ToDictionary(g => g.Key, g => g.Count());
    var fails = rows.Where(r => r.Result == Row.Fail).ToList();
    var newFails = fails.Where(r => !xfail.Contains(r.Key)).ToList();
    Console.WriteLine($"[verify] {rows.Count(r => r.Result != Row.Skip)} checks: {by.GetValueOrDefault(Row.Pass)} PASS, "
        + $"{by.GetValueOrDefault(Row.Fail)} FAIL ({newFails.Count} not in the xfail list), {by.GetValueOrDefault(Row.Unreachable)} UNREACHABLE, "
        + $"{by.GetValueOrDefault(Row.Skip)} SKIP in {sw.Elapsed.TotalSeconds:0} s");
    foreach (var g in fails.GroupBy(GroupOf).OrderByDescending(g => g.Count()))
    {
        Console.WriteLine($"[verify]   {g.Count(),4}  {g.Key,-28}  {string.Join(' ', g.Take(4).Select(r => $"{r.Map}@{r.Id:x5}"))}");
    }

    Console.WriteLine($"[verify] enumeration: {v.Enumeration.Count} difference(s) against the lock");
    foreach (var line in v.Enumeration.Take(20))
    {
        Console.WriteLine("[verify]   " + line);
    }

    if (opts.TryGetValue("expect", out var expectPath))
    {
        return Diff(rows.Select(r => (r.Key, r.Result, r.ToString())).ToList(), expectPath, maps.ToHashSet()) ? 0 : 1;
    }

    return newFails.Count > 0 ? 1 : 0;
}

int Missions()
{
    var specPath = Path.Combine(rules, $"{game}.missions.txt");
    if (!File.Exists(specPath))
    {
        Console.Error.WriteLine($"no {game}.missions.txt in the rules folder");
        return 2;
    }

    var specs = MissionVerifier.Parse(File.ReadLines(specPath));
    if (opts.TryGetValue("only", out var only))
    {
        var keep = only.Split(',').Select(s => int.Parse(s, CultureInfo.InvariantCulture)).ToHashSet();
        specs = [.. specs.Where(s => keep.Contains(s.Key))];
    }

    var rows = new MissionVerifier(data).RunAll(specs);
    if (opts.TryGetValue("out", out var outPath))
    {
        File.WriteAllLines(outPath, rows.Select(r => r.ToString()));
        Console.WriteLine($"[mission] rows written: {outPath}");
    }

    var by = rows.GroupBy(r => r.Result).ToDictionary(g => g.Key, g => g.Count());
    Console.WriteLine($"[mission] {rows.Count} step(s): {by.GetValueOrDefault(Row.Pass)} PASS, {by.GetValueOrDefault(Row.Fail)} FAIL, "
        + $"{by.GetValueOrDefault("XFAIL")} XFAIL, {by.GetValueOrDefault("XPASS")} XPASS in {sw.Elapsed.TotalSeconds:0} s");
    foreach (var r in rows.Where(r => r.Result == Row.Fail))
    {
        Console.WriteLine($"[mission] FAIL {r.Mission} step {r.N} {r.Verb} {r.Map} @{r.Id:x5} - {r.Why}");
    }

    if (opts.TryGetValue("expect", out var expectPath))
    {
        var keys = rows.Select(r => (Key: $"{r.Mission} {r.N}", r.Result, Line: r.ToString())).ToList();
        return Diff(keys, expectPath, rows.Select(r => r.Mission).ToHashSet()) ? 0 : 1;
    }

    return rows.Any(r => r.Result == Row.Fail) ? 1 : 0;
}

// Lays this run's rows against a rows file (a Godot run's --verify-out, or an expected file of
// "<key> <result>" lines): every row whose result differs, grouped by the pair of results.
bool Diff(List<(string Key, string Result, string Line)> mine, string path, HashSet<int> scope)
{
    var theirs = new Dictionary<string, (string Result, string Line)>(StringComparer.Ordinal);
    foreach (var raw in File.ReadLines(path))
    {
        var line = raw.TrimEnd();
        if (line.Length == 0 || line.StartsWith('#'))
        {
            continue;
        }

        var t = line.Split(' ');
        // A trigger row is "map id act kind mode result ...", a mission row "mission n verb map id result ...",
        // an expected file "map id result" or "mission n result".
        string key, res;
        if (t.Length == 3)
        {
            (key, res) = ($"{t[0]} {t[1]}", t[2]);
        }
        else if (t.Length >= 6)
        {
            (key, res) = ($"{t[0]} {t[1]}", t[5]);
        }
        else
        {
            continue;
        }

        if (int.TryParse(t[0], CultureInfo.InvariantCulture, out var m) && scope.Contains(m))
        {
            theirs[key] = (res, line);
        }
    }

    var changes = new List<(string From, string To, string Key, string Mine, string Theirs)>();
    foreach (var (key, res, line) in mine)
    {
        if (!theirs.Remove(key, out var t))
        {
            changes.Add(("absent", res, key, line, ""));
        }
        else if (t.Result != res)
        {
            changes.Add((t.Result, res, key, line, t.Line));
        }
    }

    foreach (var (key, t) in theirs)
    {
        changes.Add((t.Result, "absent", key, "", t.Line));
    }

    Console.WriteLine($"[diff] {changes.Count} row(s) differ from {Path.GetFileName(path)}");
    foreach (var g in changes.GroupBy(c => $"{c.From} -> {c.To}").OrderByDescending(g => g.Count()))
    {
        Console.WriteLine($"[diff]   {g.Count(),4}  {g.Key}");
        foreach (var c in g.Take(opts.ContainsKey("all") ? int.MaxValue : 6))
        {
            Console.WriteLine($"[diff]         here:  {c.Mine}");
            if (c.Theirs.Length > 0)
            {
                Console.WriteLine($"[diff]         there: {c.Theirs}");
            }
        }
    }

    // Only a new FAIL fails the run.
    return !changes.Any(c => c.To == Row.Fail);
}

static HashSet<string> LoadXfail(string path)
{
    var set = new HashSet<string>(StringComparer.Ordinal);
    if (!File.Exists(path))
    {
        return set;
    }

    foreach (var raw in File.ReadLines(path))
    {
        var t = raw.Trim().Split(' ', StringSplitOptions.RemoveEmptyEntries);
        if (t.Length >= 5 && !t[0].StartsWith('#'))
        {
            set.Add($"{int.Parse(t[0], CultureInfo.InvariantCulture)} {t[1]}");
        }
    }

    return set;
}

static string GroupOf(Row row)
{
    var why = row.Why;
    if (why.StartsWith("second: ", StringComparison.Ordinal))
    {
        return $"{row.Kind} second activation";
    }

    if (why.StartsWith("re-entry: ", StringComparison.Ordinal))
    {
        return $"{row.Kind} re-entry";
    }

    if (why.Contains("missing loop", StringComparison.Ordinal))
    {
        return "ambient loop never starts";
    }

    if (why.Contains("missing break@", StringComparison.Ordinal))
    {
        return $"{row.Kind} damage stage";
    }

    if (why.Contains("missing move@", StringComparison.Ordinal))
    {
        return $"{row.Kind} mover silent";
    }

    if (why.Contains("missing spin@", StringComparison.Ordinal))
    {
        return $"{row.Kind} rotator";
    }

    if (why.StartsWith("extra ", StringComparison.Ordinal))
    {
        return $"{row.Kind} did more";
    }

    if (why.Contains("missing", StringComparison.Ordinal))
    {
        return $"{row.Kind} did less";
    }

    return $"{row.Kind} {why.Split(';')[0].Split(' ')[0]}";
}
