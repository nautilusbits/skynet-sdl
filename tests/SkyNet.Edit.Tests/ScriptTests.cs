using System.Collections;
using System.Diagnostics;

namespace SkyNet.Edit.Tests;

/// <summary>SkyNetEdit's --script: the timeline's steps a frame at a time, and a hidden run driven by it.</summary>
public sealed class ScriptTests
{
    // EditorScript (internal) by reflection: Parse, then the steps each Due() call gives.
    private static List<string[]> Frames(string text, int frames)
    {
        var type = typeof(EditorPrefs).Assembly.GetType("SkyNet.Edit.EditorScript")!;
        var script = type.GetMethod("Parse")!.Invoke(null, [text])!;
        var due = type.GetMethod("Due")!;
        return [.. Enumerable.Range(0, frames).Select(_ => ((IEnumerable)due.Invoke(script, null)!).Cast<string>().ToArray())];
    }

    [Fact]
    public void Steps_run_together_until_a_wait_holds_them()
    {
        var frames = Frames("a; b; wait 3; # note; c; wait 1; d", 6);
        Assert.Equal(["a", "b"], frames[0]);
        Assert.Empty(frames[1]);
        Assert.Empty(frames[2]);
        Assert.Equal(["c"], frames[3]);
        Assert.Equal(["d"], frames[4]);
        Assert.Empty(frames[5]);
    }

    [Fact]
    public void A_file_holds_one_step_a_line()
    {
        var file = Path.GetTempFileName();
        try
        {
            File.WriteAllLines(file, ["tool x {\"a\":1; \"b\":2}", "", "wait 2", "y"]);
            var frames = Frames(file, 3);
            Assert.Equal(["tool x {\"a\":1; \"b\":2}"], frames[0]);
            Assert.Empty(frames[1]);
            Assert.Equal(["y"], frames[2]);
        }
        finally
        {
            File.Delete(file);
        }
    }

    /// <summary>A tool step places a robot, a command takes it back, quit ends the run before its frames.</summary>
    [MapDataFact]
    public void A_hidden_run_follows_its_script()
    {
        var exe = Path.Combine(AppContext.BaseDirectory, OperatingSystem.IsWindows() ? "SkyNetEdit.exe" : "SkyNetEdit");
        Assert.True(File.Exists(exe), exe + " is not built beside the tests");
        var prefs = Path.Combine(Path.GetTempPath(), $"SkyNetEdit-{Guid.NewGuid():N}.json");
        const string Script = "wait 3; cam 48150,900,-52500 30 -10; tool entity_place {\"kind\":\"robot\",\"item\":\"endo\",\"x\":48300,\"y\":800,\"z\":-52700,\"floor\":true};"
            + " wait 2; edit.undo; wait 2; quit";
        var info = new ProcessStartInfo(exe) { RedirectStandardOutput = true, UseShellExecute = false };
        foreach (var arg in new[] { "--mission", "1", "--prefs", prefs, "--data", EditDocumentTests.DataDir!, "--width", "320", "--height", "180",
            "--console-log", "--frames", "500", "--script", Script })
        {
            info.ArgumentList.Add(arg);
        }

        info.Environment["SKYNET_OFFSCREEN"] = "1";
        try
        {
            using var process = Process.Start(info)!;
            var output = process.StandardOutput.ReadToEndAsync();
            Assert.True(process.WaitForExit(80_000), "SkyNetEdit did not finish");
            Assert.Equal(0, process.ExitCode);
            Assert.Contains("Undone: Add entity", output.Result, StringComparison.Ordinal);
        }
        finally
        {
            File.Delete(prefs);
            File.Delete(Path.ChangeExtension(prefs, ".imgui.ini"));
        }
    }
}
