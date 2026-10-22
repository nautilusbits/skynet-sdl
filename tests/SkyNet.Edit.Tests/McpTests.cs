using System.Diagnostics;
using System.Text.RegularExpressions;
using RetroEng.Editor;
using RetroEng.Json;

namespace SkyNet.Edit.Tests;

/// <summary>MCP (editor-tools plan, T9): an agent's client against the running editor (hidden window, --mcp 0)
/// edits mission 1 through the typed tools (a robot placed, a trigger added and linked, a table cell set), saves
/// it into a folder mod, verifies the graph, then undoes each step and sees it gone.</summary>
[Collection(EditModsCollection.Name)]
public sealed partial class McpTests
{
    private const int GateHalf = 0x078f7;
    private const int UseGateAct = 0xEF;
    private static readonly TimeSpan StartTimeout = TimeSpan.FromSeconds(90);

    [MapDataFact]
    public Task An_agent_edits_a_mission_saves_verifies_and_undoes_each_step() => WithEditor(Steps);

    /// <summary>The other typed tools, each called once on mission 1: they answer, and their edits are undo steps.</summary>
    [MapDataFact]
    public Task Every_typed_tool_answers() => WithEditor(Others);

    // The editor started hidden with --mcp 0, a client connected, a folder mod name for its saves; all removed after.
    private static async Task WithEditor(Func<McpClient, string, string, Task> steps)
    {
        var exe = Path.Combine(AppContext.BaseDirectory, OperatingSystem.IsWindows() ? "SkyNetEdit.exe" : "SkyNetEdit");
        Assert.True(File.Exists(exe), exe + " is not built beside the tests");
        var prefs = Path.Combine(Path.GetTempPath(), $"SkyNetEdit-{Guid.NewGuid():N}.json");
        var mod = $"mcp-{Guid.NewGuid():N}"[..12];
        var modFolder = Path.Combine(AppContext.BaseDirectory, "mods", mod);
        var info = new ProcessStartInfo(exe)
        {
            ArgumentList =
            {
                "--mission", "1", "--prefs", prefs, "--data", EditDocumentTests.DataDir!, "--width", "640", "--height", "360",
                "--frames", "1000000", "--console-log", "--mcp", "0",
            },
            RedirectStandardOutput = true,
            UseShellExecute = false,
        };
        info.Environment["SKYNET_OFFSCREEN"] = "1";
        using var process = Process.Start(info)!;
        var url = new TaskCompletionSource<Uri>(TaskCreationOptions.RunContinuationsAsynchronously);
        process.OutputDataReceived += (_, e) =>
        {
            if (e.Data != null && Endpoint().Match(e.Data) is { Success: true } m)
            {
                url.TrySetResult(new Uri(m.Groups[1].Value));
            }
        };
        process.BeginOutputReadLine();
        try
        {
            using var client = new McpClient(await url.Task.WaitAsync(StartTimeout));
            await client.Initialize();
            await steps(client, mod, modFolder);
        }
        finally
        {
            process.Kill(entireProcessTree: true);
            process.WaitForExit(10_000);
            File.Delete(prefs);
            if (Directory.Exists(modFolder))
            {
                Directory.Delete(modFolder, recursive: true);
            }
        }
    }

    private static async Task Steps(McpClient client, string mod, string modFolder)
    {
        var tools = (await client.ListTools()).Items.Select(t => t.Member("name").AsString()).ToHashSet();
        Assert.Superset(new HashSet<string> { "entity_place", "graph_connect", "table_set", "save", "undo", "redo", "screenshot", "asset_list" }, tools);

        Assert.Equal("210", (await Ok(client, "mission_get")).Member("shown").AsString());
        await Ok(client, "save_target", Args().Add("mod", mod));

        // 1: a robot beside the player start.
        var markers = await Ok(client, "entity_query", Args().Add("kinds", JsonItem.NewArray(["marker"])));
        var start = markers.Member("entities").Items.First(e => e["marker"]?.AsInt() == 0).Member("key").AsInt();
        var placed = await Ok(client, "entity_place", Args().Add("kind", "robot").Add("item", "endo").Add("near", start).Add("dx", 256));
        var robot = placed.Member("entity").Member("key").AsInt();
        Assert.Equal("robot", placed.Member("entity").Member("kind").AsString());
        Assert.True(placed.Member("changed").AsBool());

        // 2, 3: a use gate added and linked to the gate's half.
        var trigger = (await Ok(client, "graph_add", Args().Add("act", UseGateAct))).Member("node").Member("id").AsInt();
        await Ok(client, "graph_connect", Args().Add("from", trigger).Add("to", GateHalf));
        Assert.Equal(GateHalf, await Next(client, trigger));

        // 4: a table cell.
        var table = await Ok(client, "table_get", Args().Add("table", "weapons").Add("limit", 1));
        var column = table.Member("columns").Items.Skip(1).First(c => c.Member("kind").AsString() == "int" && !c.Member("readOnly").AsBool()).Member("key").AsString();
        var row = table.Member("rows").Items[0];
        var key = TableKey(row, table);
        var before = row.Member(column).AsLong();
        await Ok(client, "table_set", Args().Add("table", "weapons").Add("key", key).Add("column", column).Add("value", (before + 1).ToString(System.Globalization.CultureInfo.InvariantCulture)));
        Assert.Equal(before + 1, await Cell(client, key, column));

        // Saved into the mod, then the graph verified.
        var saved = await Ok(client, "save");
        Assert.False(saved.Member("dirty").AsBool());
        Assert.NotEmpty(Directory.GetFiles(Path.Combine(modFolder, "maps"), "*.layer.json"));
        var verify = await Ok(client, "graph_verify");
        Assert.Contains(verify.Member("chains").Items, c => c.Member("nodes").Items.Any(n => n.AsInt() == trigger));

        // Each undo takes one step back: the cell, the link, the trigger, the robot; then nothing is left.
        await Undo(client);
        Assert.Equal(before, await Cell(client, key, column));
        await Undo(client);
        Assert.NotEqual(GateHalf, await Next(client, trigger));
        await Undo(client);
        Assert.Equal(0, await Matched(client, trigger));
        await Undo(client);
        Assert.Equal(0, await Matched(client, robot));
        Assert.Equal(NothingToUndo, await Said(client, "undo"));
    }

    private static async Task Others(McpClient client, string mod, string modFolder)
    {
        Assert.NotEmpty((await Ok(client, "mission_list")).Member("missions").Items);
        Assert.NotEmpty((await Ok(client, "map_list")).Member("instances").Items);
        var robot = (await Ok(client, "entity_query", Args().Add("kinds", JsonItem.NewArray(["robot"])).Add("limit", 1))).Member("entities").Items[0];
        var key = robot.Member("key").AsInt();
        Assert.NotEmpty((await Ok(client, "entity_get", Args().Add("key", key))).Member("sections").Items);
        var edits = 0;
        async Task<JsonItem> Edit(string tool, JsonItem args)
        {
            var r = await Ok(client, tool, args);
            Assert.True(r.Member("changed").AsBool(), tool + " recorded no step");
            edits++;
            return r;
        }

        await Edit("entity_set", Args().Add("key", key).Add("field", "dormant for").Add("value", 5));
        var copy = (await Edit("entity_duplicate", Args().Add("key", key))).Member("copies").Items[0].AsInt();
        await Edit("entity_move", Args().Add("key", copy).Add("dx", 32));
        // Already down: no step.
        edits += (await Ok(client, "entity_drop", Args().Add("key", copy))).Member("changed").AsBool() ? 1 : 0;
        await Ok(client, "entity_select", Args().Add("keys", JsonItem.NewArray([key, copy])));
        await Edit("entity_delete", Args().Add("key", copy));

        var edge = (await Ok(client, "graph_get")).Member("edges").Items[0];
        await Edit("graph_disconnect", Args().Add("from", edge.Member("from").AsInt()));
        Assert.NotEmpty((await Ok(client, "table_list")).Member("tables").Items);
        var world = robot.Member("world").Items;
        await Edit("terrain_brush", Args().Add("tool", "raise").Add("x", world[0].AsInt()).Add("z", world[2].AsInt()));
        await Edit("terrain_generate", Args().Add("seed", 3));

        // A long play-test answers later: the editor serves other calls meanwhile.
        var playing = client.Call("playtest", Args().Add("seconds", LongPlay));
        await Ok(client, "mission_get");
        Assert.False(playing.IsCompleted, "the play-test ran inside one frame");
        var played = Structured("playtest", await playing);
        Assert.Equal("Playing", played.Member("phase").AsString());
        Assert.Equal(LongPlay, played.Member("seconds").AsInt());
        var shot = await client.Call("screenshot");
        Assert.Equal("image", shot.Member("content").Items[0].Member("type").AsString());
        // The view's picture (the Viewport docked under the menu), not the whole window: PNG's IHDR size.
        var png = Convert.FromBase64String(shot.Member("content").Items[0].Member("data").AsString());
        Assert.Equal(640, System.Buffers.Binary.BinaryPrimitives.ReadInt32BigEndian(png.AsSpan(16)));
        Assert.InRange(System.Buffers.Binary.BinaryPrimitives.ReadInt32BigEndian(png.AsSpan(20)), 180, 359);

        Assert.Equal(edits, await UndoAll(client));
        Assert.False((await Ok(client, "mission_get")).Member("dirty").AsBool());

        // New maps and missions are written into the save mod.
        var map = await Ok(client, "map_new", Args().Add("mod", mod));
        Assert.True(map.Member("changed").AsBool());
        await Ok(client, "mission_new", Args().Add("discard", true));
        Assert.True(Directory.Exists(modFolder));
    }

    private static JsonItem Args() => JsonItem.NewObject();

    // The engine's undo tool: "Undid <label>", or this when the stack is empty.
    private const string NothingToUndo = "Nothing to undo";
    private const string Undid = "Undid ";

    // Game seconds of the long play-test: beyond the old 30 s cap.
    private const int LongPlay = 60;

    // A tool's structured result; its error fails the test.
    private static async Task<JsonItem> Ok(McpClient client, string tool, JsonItem? args = null) =>
        Structured(tool, await client.Call(tool, args));

    private static JsonItem Structured(string tool, JsonItem result)
    {
        Assert.False(result["isError"]?.AsBool() == true, $"{tool}: {Text(result)}");
        return result.Member("structuredContent");
    }

    private static string Text(JsonItem result) => string.Join("\n", result.Member("content").Items.Select(c => c["text"]?.AsString() ?? ""));

    // A text tool's answer; its error fails the test.
    private static async Task<string> Said(McpClient client, string tool)
    {
        var result = await client.Call(tool);
        Assert.False(result["isError"]?.AsBool() == true, $"{tool}: {Text(result)}");
        return Text(result);
    }

    private static async Task Undo(McpClient client) =>
        Assert.StartsWith(Undid, await Said(client, "undo"));

    // Undoes until nothing is left: the steps taken.
    private static async Task<int> UndoAll(McpClient client)
    {
        var steps = 0;
        while ((await Said(client, "undo")).StartsWith(Undid, StringComparison.Ordinal))
        {
            steps++;
        }

        return steps;
    }

    private static async Task<int> Next(McpClient client, int node) =>
        (await Ok(client, "graph_get", Args().Add("node", node))).Member("nodes").Items[0].Member("next").AsInt();

    private static async Task<int> Matched(McpClient client, int key) =>
        (await Ok(client, "entity_query", Args().Add("key", key))).Member("matched").AsInt();

    private static async Task<long> Cell(McpClient client, string key, string column) =>
        (await Ok(client, "table_get", Args().Add("table", "weapons").Add("key", key))).Member("rows").Items[0].Member(column).AsLong();

    // The row's key: its first column's value, as text.
    private static string TableKey(JsonItem row, JsonItem table)
    {
        var first = row.Member(table.Member("columns").Items[0].Member("key").AsString());
        return first.TryString(out var s) ? s : first.NumberText;
    }

    [GeneratedRegex(@"MCP: (http://127\.0\.0\.1:\d+/mcp)")]
    private static partial Regex Endpoint();
}
