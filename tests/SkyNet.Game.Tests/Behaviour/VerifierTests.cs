using System.Globalization;
using SkyNet.Verify;

namespace SkyNet.Game.Tests.Behaviour;

/// <summary>
/// tools/SkyNet.Verify on a fast subset: the trigger verifier over seven maps (a Godot trigger lock
/// excerpt, Behaviour/Verify/subset.triggers.lock) and every mission spec. A FAIL that the
/// checked-in expected files do not list fails the test; the expected files hold row ids and
/// results only.
/// </summary>
public class VerifierTests
{
    private static string Here(string name) => Path.Combine(AppContext.BaseDirectory, "Behaviour", "Verify", name);

    private static Dictionary<string, string> Expected(string name)
    {
        var d = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var line in File.ReadLines(Here(name)))
        {
            var t = line.Split(' ', StringSplitOptions.RemoveEmptyEntries);
            if (t.Length == 3 && !line.StartsWith('#'))
            {
                d[$"{t[0]} {t[1]}"] = t[2];
            }
        }

        return d;
    }

    [BehaviourDataFact]
    public void TriggerSubsetHasNoNewFail()
    {
        var nodes = TriggerLock.Load(Here("subset.triggers.lock"));
        var v = new TriggerVerifier(BehaviourData.Data, nodes);
        var rows = v.Run(nodes.Select(n => n.Map).Distinct().Order());
        var expected = Expected("subset.expected");
        var news = rows.Where(r => r.Result == Row.Fail && expected.GetValueOrDefault(r.Key) != Row.Fail).Select(r => r.ToString()).ToList();
        Assert.Empty(news);
        Assert.Empty(v.Enumeration);
        Assert.Equal(expected.Count, rows.Count);
    }

    [BehaviourDataFact]
    public void MissionSpecsHaveNoNewFail()
    {
        var specs = MissionVerifier.Parse(File.ReadLines(Here("skynet.missions.txt")));
        var rows = new MissionVerifier(BehaviourData.Data).RunAll(specs);
        var expected = Expected("missions.expected");
        var news = rows.Where(r => r.Result == Row.Fail
                && expected.GetValueOrDefault(string.Create(CultureInfo.InvariantCulture, $"{r.Mission} {r.N}")) != Row.Fail)
            .Select(r => r.ToString()).ToList();
        Assert.Empty(news);
        Assert.Equal(expected.Count, rows.Count);
    }
}
