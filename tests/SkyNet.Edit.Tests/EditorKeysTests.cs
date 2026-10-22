using System.Text.RegularExpressions;

namespace SkyNet.Edit.Tests;

/// <summary>Help > Keys against the key handling in the editor's source: every handled key listed,
/// every listed key handled.</summary>
public sealed partial class EditorKeysTests
{
    // ImGuiKey / SDL scancode names as the list writes them.
    private static readonly Dictionary<string, string> Spelled = new()
    {
        ["Delete"] = "Del", ["Escape"] = "Esc", ["LeftBracket"] = "[", ["RightBracket"] = "]",
        ["Key1"] = "1", ["Key2"] = "2", ["LSHIFT"] = "Shift", ["RSHIFT"] = "Shift",
    };

    private static readonly string[] Modifiers = ["Ctrl", "Shift", "Alt"];

    [GeneratedRegex(@"IsKeyPressed\(ImGuiKey\.(\w+)|SDL_SCANCODE_(\w+)")]
    private static partial Regex KeyUse();

    // "  Ctrl+C / Ctrl+X      what": the key column ends at two spaces.
    [GeneratedRegex(@"^  (\S.*?)(?:\s{2,}|$)")]
    private static partial Regex Row();

    private static string Help() =>
        (string)typeof(EditorPrefs).Assembly.GetType("EditorOptions")!.GetField("Help")!.GetValue(null)!;

    // The combos of the Keys section's key column ("Ctrl+Y / Ctrl+Shift+Z" -> two).
    private static List<string> ListedCombos()
    {
        var lines = Help().Split('\n').Select(l => l.TrimEnd('\r')).SkipWhile(l => !l.StartsWith("Keys", StringComparison.Ordinal)).Skip(1);
        return [.. lines.Select(l => Row().Match(l)).Where(m => m.Success)
            .SelectMany(m => m.Groups[1].Value.Split([" / ", ", "], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))];
    }

    private static HashSet<string> ListedKeys() =>
        [.. ListedCombos().SelectMany(c => c.Split('+')).Where(k => k.Length > 0)];

    private static HashSet<string> HandledKeys()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir != null && !File.Exists(Path.Combine(dir.FullName, "SkyNet.slnx")))
        {
            dir = dir.Parent;
        }

        Assert.NotNull(dir);
        var keys = new HashSet<string>();
        foreach (var file in Directory.EnumerateFiles(Path.Combine(dir.FullName, "src", "SkyNet.Edit"), "*.cs"))
        {
            foreach (Match m in KeyUse().Matches(File.ReadAllText(file)))
            {
                var name = m.Groups[1].Success ? m.Groups[1].Value : m.Groups[2].Value;
                keys.Add(Spelled.GetValueOrDefault(name, name));
            }
        }

        return keys;
    }

    private static bool IsMouse(string key) =>
        new[] { "click", "drag", "mouse", "wheel" }.Any(w => key.Contains(w, StringComparison.OrdinalIgnoreCase));

    [Fact]
    public void Every_handled_key_is_listed()
    {
        var listed = ListedKeys();
        var missing = HandledKeys().Where(k => !listed.Contains(k)).Order().ToList();
        Assert.True(missing.Count == 0, "Not in Help > Keys: " + string.Join(' ', missing));
    }

    [Fact]
    public void Every_listed_key_is_handled()
    {
        var handled = HandledKeys();
        var stale = ListedKeys().Where(k => !IsMouse(k) && !Modifiers.Contains(k) && !handled.Contains(k)).Order().ToList();
        Assert.True(stale.Count == 0, "Listed but not handled: " + string.Join(' ', stale));
    }

    [Fact]
    public void Terrain_mode_keys_are_listed()
    {
        var combos = ListedCombos();
        foreach (var combo in new[] { "Shift+2", "Shift+1", "[", "]", "Shift+drag", "Ctrl+click" })
        {
            Assert.Contains(combo, combos);
        }
    }
}
