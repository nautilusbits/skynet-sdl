using System.Text.RegularExpressions;

namespace SkyNet.Edit.Tests;

/// <summary>Every window the editor opens has its close button (an ImGui p_open), but the few that stay.</summary>
public sealed partial class EditorWindowsTests
{
    // The windows without an X, by Begin's first argument: the status bar, the dock host, the play-test overlay,
    // the 3D view, and the Assets focus check (the Assets window itself closes by AssetsPanel.Draw).
    private static readonly string[] Kept =
        ["\"##status\"", "\"##dockhost\"", "\"##preview\"", "ViewportTitle", "AssetsPanel.Title"];

    [GeneratedRegex(@"ImGui\.Begin\((?<args>[^;\n]*)")]
    private static partial Regex Begin();

    private static IEnumerable<(string File, string Args)> Begins()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir != null && !File.Exists(Path.Combine(dir.FullName, "SkyNet.slnx")))
        {
            dir = dir.Parent;
        }

        Assert.NotNull(dir);
        foreach (var file in Directory.EnumerateFiles(Path.Combine(dir.FullName, "src", "SkyNet.Edit"), "*.cs"))
        {
            foreach (Match m in Begin().Matches(File.ReadAllText(file)))
            {
                yield return (Path.GetFileName(file), m.Groups["args"].Value);
            }
        }
    }

    [Fact]
    public void Every_window_has_a_close_button()
    {
        var missing = Begins().Where(b => !b.Args.Contains("ref ", StringComparison.Ordinal) && !Kept.Any(k => b.Args.StartsWith(k, StringComparison.Ordinal)))
            .Select(b => $"{b.File}: {b.Args}").ToList();
        Assert.True(missing.Count == 0, "No X: " + string.Join("; ", missing));
    }

    [Fact]
    public void Closing_a_panel_clears_its_view_menu_tick()
    {
        foreach (var panel in Enum.GetValues<EditorPanel>())
        {
            var prefs = new EditorPrefs
            {
                ShowOutliner = true, ShowProperties = true, ShowPlace = true, ShowTriggers = true,
                ShowMission = true, ShowValidation = true, ShowLog = true,
            };
            Assert.True(EditorPanels.Shown(prefs, panel));
            EditorPanels.Hide(prefs, panel);
            Assert.False(EditorPanels.Shown(prefs, panel));
            Assert.Equal(Enum.GetValues<EditorPanel>().Length - 1, Enum.GetValues<EditorPanel>().Count(p => EditorPanels.Shown(prefs, p)));
        }
    }
}
