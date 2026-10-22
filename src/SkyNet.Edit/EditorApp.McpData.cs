using System.Numerics;
using RetroEng.Editor;
using RetroEng.Json;
using SkyNet.Formats;
using SkyNet.Game.Player;

namespace SkyNet.Edit;

/// <summary>The MCP tools of the trigger graph (<see cref="GraphEdits"/>, the Trigger graph window's Verify), the
/// game tables (<see cref="TablesWindow"/>'s grids) and the terrain (<see cref="SculptBrush"/>,
/// <see cref="TerrainActions"/>): each edit one undo step.</summary>
internal sealed partial class EditorApp
{
    // A brush call's dabs are this far apart in time (the view's frames).
    private const float DabSeconds = 1f / 30f;
    private const int DefaultDabs = 10, MaxDabs = 600, DefaultRows = 50;

    // ---- the trigger graph ----

    private void AddGraphTools(McpServer s)
    {
        McpArg node = new("node", "integer", "the node's entity key", true);
        Tool(s, "graph_get", "The trigger graph of an instance's map: nodes (act, next, the records leading in, how the player sets it off), edges, issues.",
            [InstanceArg, new("node", "integer", "only this node, with its parameters"), new("full", "boolean", "every node's parameters too")], GraphGet);
        Tool(s, "graph_connect", "Links node from's next to node to (replacing its old next): one undo step.",
            [new("from", "integer", "the node whose next is set", true), new("to", "integer", "the node it leads to", true), InstanceArg],
            a => GraphEdit(a, l => GraphEdits.Connect(_doc!, l, a.Int("from"), a.Int("to"))));
        Tool(s, "graph_disconnect", "Cuts node from's next (its chain ends there): one undo step.",
            [new("from", "integer", "the node", true), InstanceArg], a => GraphEdit(a, l => GraphEdits.Disconnect(_doc!, l, a.Int("from"))));
        Tool(s, "graph_set_act", "Gives a node an act (id from the act table; a mesh without a link record gets one): one undo step.",
            [node, new("act", "integer", "the act id (e.g. 239 = 0xEF use gate)", true), InstanceArg],
            a => GraphEdit(a, l => GraphEdits.SetAct(_doc!, l, a.Int("node"), a.Int("act"))));
        Tool(s, "graph_set_param", "Sets a node's parameter (graph_get with node lists them; a fixed one changes with the act): one undo step.",
            [node, new("name", "string", "the parameter", true), new("value", "integer", "its value (a bool 0 or 1)", true), InstanceArg],
            a => GraphEdit(a, l => GraphEdits.SetParam(_doc!, l, a.Int("node"), a.Text("name"), a.Int("value"))));
        Tool(s, "graph_add", "Adds a node: a logic sprite (or a mesh of model) with an act at a DOS position (default before the camera); a trigger starts armed. One undo step.",
            [new("act", "integer", "the act id", true), new("x", "integer", "DOS x"), new("y", "integer", "DOS y"), new("z", "integer", "DOS z"),
             new("model", "string", "a mesh node's model name (default: a logic sprite)"), InstanceArg], GraphAdd);
        Tool(s, "graph_delete", "Deletes a node; the links into it go on to its next (splice) or end (cut): one undo step.",
            [node, new("rewire", "string", "splice or cut (default splice)"), InstanceArg],
            a => GraphEdit(a, l => GraphEdits.DeleteNode(_doc!, l, a.Int("node"), a.OptText("rewire") == "cut" ? Rewire.Cut : Rewire.Splice)));
        Tool(s, "graph_verify", "The Trigger graph's Verify: every chain the layer adds or changes, each start fired on a headless copy of the edited map "
            + "by the game's verifier (PASS / FAIL with what happened), and the graph's issues.", [InstanceArg], GraphVerifyTool);
    }

    private McpResult GraphGet(JsonItem a)
    {
        var graph = TriggerGraph.Build(_doc!, McpLayer(a));
        var only = a.OptInt("node");
        var nodes = graph.Nodes.Where(n => only == null || n.Id == only).ToList();
        if (only != null && nodes.Count == 0)
        {
            return McpResult.Error($"@{only} is not a node of the graph");
        }

        var full = only != null || a.Flag("full");
        return McpResult.Json(JsonItem.NewObject().Add("layer", graph.Layer).Add("map", graph.MapNumber)
            .Add("nodes", JsonItem.NewArray(nodes, n => NodeJson(n, full)))
            .Add("edges", JsonItem.NewArray(graph.Edges.Where(e => only == null || e.From == only || e.To == only),
                e => JsonItem.NewObject().Add("from", e.From).Add("to", e.To).Add("family", e.Family.ToString())))
            .Add("issues", JsonItem.NewArray(graph.Issues.Where(i => only == null || i.Node == only), IssueJson)));
    }

    private static JsonItem NodeJson(GraphNode n, bool full)
    {
        var o = JsonItem.NewObject().Add("id", n.Id).Add("title", n.Title).Add("does", n.Label).Add("body", n.Body.ToString().ToLowerInvariant())
            .Add("act", n.Act).Add("actName", n.Info.Name).Add("next", n.Next).Add("in", JsonItem.NewArray(n.In, i => i))
            .Add("modes", JsonItem.NewArray(n.Modes, m => m));
        if (n.Exit is { } x)
        {
            o.Add("exit", JsonItem.NewObject().Add("map", x.Map).Add("arrival", x.Arrival).Add("instance", x.Instance).Add("back", x.Back));
        }

        if (full)
        {
            o.Add("params", JsonItem.NewArray(n.Params, p => JsonItem.NewObject().Add("name", p.Name).Add("type", p.Type.ToString().ToLowerInvariant())
                .Add("value", p.Value).Add("min", p.Min).Add("max", p.Max).Add("fixed", p.ReadOnly).Add("text", p.Text)));
        }

        return o;
    }

    private static JsonItem IssueJson(GraphIssue i) =>
        JsonItem.NewObject().Add("node", i.Node).Add("level", i.Level.ToString().ToLowerInvariant()).Add("code", i.Code).Add("text", i.Text);

    // A GraphEdits call as one step; the node's links after it.
    private McpResult GraphEdit(JsonItem a, Action<string> edit)
    {
        var layer = McpLayer(a);
        return Edited(() =>
        {
            edit(layer);
            return JsonItem.NewObject().Add("issues", TriggerGraph.Build(_doc!, layer).Issues.Count);
        });
    }

    private McpResult GraphAdd(JsonItem a)
    {
        var layer = McpLayer(a);

        // Before the camera, as the graph window's menu puts a new node.
        var w = _eye + PlayerController.Direction(_yaw, _pitch) * NewNodeDistance;
        var (x, y, z) = (a.OptInt("x") ?? (int)MathF.Round(w.X), a.OptInt("y") ?? (int)MathF.Round(-w.Y), a.OptInt("z") ?? (int)MathF.Round(-w.Z));
        var model = a.OptText("model");
        return Edited(() =>
        {
            var id = GraphEdits.AddNode(_doc!, layer, a.Int("act"), model != null ? NewNodeBody.Mesh : NewNodeBody.Logic, x, y, z, model);
            return JsonItem.NewObject().Add("node", NodeJson(TriggerGraph.Build(_doc!, layer).Node(id)!, full: true));
        });
    }

    private McpResult GraphVerifyTool(JsonItem a)
    {
        var layer = McpLayer(a);
        var summary = _graphWindow!.Verify();
        var chains = _graphWindow.Results ?? [];
        return McpResult.Json(JsonItem.NewObject().Add("summary", summary).Add("passed", chains.All(c => c.Passed))
            .Add("chains", JsonItem.NewArray(chains, c => JsonItem.NewObject().Add("name", c.Name).Add("passed", c.Passed).Add("text", c.Text)
                .Add("nodes", JsonItem.NewArray(c.Nodes, n => n))))
            .Add("issues", JsonItem.NewArray(TriggerGraph.Build(_doc!, layer).Issues, IssueJson)));
    }

    // ---- the tables ----

    private void AddTableTools(McpServer s)
    {
        McpArg table = new("table", "string", "the table (table_list names them)", true);
        McpArg key = new("key", "string", "the row's key (column 0)", true);
        Tool(s, "table_list", "The game tables the Tables window edits: rows, rows changed against the base, unsaved.", [], _ => TableList());
        Tool(s, "table_get", "A table's columns (kind, choices) and rows: one row, the rows a filter matches, or the changed ones.",
            [table, new("key", "string", "one row"), new("filter", "string", "rows whose cells hold this text"),
             new("changed", "boolean", "only rows that differ from the base"), new("limit", "integer", "most rows (default 50)")], TableGet);
        Tool(s, "table_set", "Sets a cell (Tables window, Edit > Undo): one undo step; save writes the changed rows into the save mod (pick it first: save_target).",
            [table, key, new("column", "string", "the column's key", true), new("value", "string", "the value: a number, text, or a choice's label (\"-\": none)", true)], TableSet);
        Tool(s, "table_add", "Adds a row copied from another under a free key (a robot type: a new type over the same base): one undo step.",
            [table, new("from", "string", "the row copied", true)], a => TableEdit(a, g => g.AddRow(EditHistory, a.Text("from")) is { } k ? $"row {k} added" : "takes no new rows"));
        Tool(s, "table_revert", "Puts a row (or one cell) back to the base: one undo step.",
            [table, key, new("column", "string", "only this cell")], a => TableEdit(a, g => a.OptText("column") is { } c
                ? g.RevertCell(EditHistory, a.Text("key"), ColumnIndex(g, c)) : g.RevertRow(EditHistory, a.Text("key"))));
    }

    private TableGrid TableOf(JsonItem a)
    {
        AssetsFor(_data);
        var name = a.Text("table");
        return _tables?.Grid(name) ?? throw new ArgumentException($"No table {name} (table_list)");
    }

    private static int ColumnIndex(TableGrid g, string key)
    {
        var i = g.Columns.ToList().FindIndex(c => string.Equals(c.Key, key, StringComparison.OrdinalIgnoreCase));
        return i >= 0 ? i : throw new ArgumentException($"{g.Source.Name} has no column {key}: {string.Join(", ", g.Columns.Select(c => c.Key))}");
    }

    private McpResult TableList()
    {
        AssetsFor(_data);
        return McpResult.Json(JsonItem.NewObject().Add("saveMod", _saveMod).Add("tables", JsonItem.NewArray(_tableNames, n => _tables!.Grid(n) is { } g
            ? JsonItem.NewObject().Add("name", n).Add("rows", g.Rows.Count).Add("changed", g.ChangedRows).Add("unsaved", g.Dirty)
            : JsonItem.NewObject().Add("name", n).Add("error", "does not read"))));
    }

    private McpResult TableGet(JsonItem a)
    {
        var g = TableOf(a);
        var (key, filter, changed) = (a.OptText("key"), a.OptText("filter"), a.Flag("changed"));
        var rows = g.Rows.Where(r => (key == null || TableGrid.KeyOf(r) == key) && (!changed || g.RowChanged(r))
            && (filter == null || r.Any(c => TableGrid.Text(c).Contains(filter, StringComparison.OrdinalIgnoreCase)))).ToList();
        var limit = a.OptInt("limit") ?? DefaultRows;
        var columns = g.Columns.Select(c =>
        {
            var o = JsonItem.NewObject().Add("key", c.Key).Add("kind", c.Kind.ToString().ToLowerInvariant()).Add("readOnly", c.ReadOnly);
            return c.Choices is { Count: <= MaxChoices } choices ? o.Add("choices", JsonItem.NewArray(choices, ch => ch.Label)) : o;
        });
        return McpResult.Json(JsonItem.NewObject().Add("table", g.Source.Name).Add("columns", JsonItem.NewArray(columns))
            .Add("matched", rows.Count).Add("truncated", rows.Count > limit)
            .Add("rows", JsonItem.NewArray(rows.Take(limit), r =>
            {
                var o = JsonItem.NewObject();
                for (var i = 0; i < r.Length && i < g.Columns.Count; i++)
                {
                    o.Add(g.Columns[i].Key, TableGrid.JsonOf(r[i]));
                }

                return o.Add("changed", g.RowChanged(r));
            })));
    }

    private McpResult TableSet(JsonItem a)
    {
        var v = a.Member("value");
        var text = v.TryString(out var s) ? s : v.IsNull ? "-" : v.NumberText;
        return TableEdit(a, g =>
        {
            var c = ColumnIndex(g, a.Text("column"));
            return TableGrid.TryParse(g.Columns[c], text, out var value)
                ? g.Set(EditHistory, a.Text("key"), c, value) : throw new ArgumentException($"{text} is not a {g.Columns[c].Kind} value");
        });
    }

    // A grid edit as one step: the grid's line.
    private McpResult TableEdit(JsonItem a, Func<TableGrid, string> edit)
    {
        var g = TableOf(a);
        return Edited(() => Said(edit(g)));
    }

    // ---- the terrain ----

    private void AddTerrainTools(McpServer s)
    {
        Tool(s, "terrain_brush", "One stroke of the Terrain mode's brush on the shown outdoor level at world x, z (a press held for dabs frames): one undo step.",
            [new("tool", "string", "raise, lower, smooth, flatten, noise, paint or turn", true), new("x", "number", "world x", true), new("z", "number", "world z", true),
             new("radius", "number", "world units (default the brush's)"), new("strength", "number", "0..1 (default the brush's)"),
             new("falloff", "string", "constant, linear, smooth or sphere"), new("height", "number", "flatten's height"),
             new("tile", "integer", "paint's layer-2 byte (tile + quarter turns << 6)"), new("dabs", "integer", "frames the press is held (default 10)"),
             InstanceArg], TerrainBrushTool);
        Tool(s, "terrain_generate", "Generate terrain (the Terrain panel's): a new height field for the shown outdoor level, applied as one undo step.",
            [new("seed", "integer", "the noise seed", true), new("roughness", "number", "0..1"), new("lowest", "number", "lowest height"),
             new("highest", "number", "highest height"), InstanceArg], TerrainGenerateTool);
    }

    private ITerrainModel TerrainModel(JsonItem a)
    {
        _ = McpLayer(a);
        return TerrainView(ModelUse.Create)?.Model ?? throw new InvalidOperationException($"{_levelInstance} has no terrain (an outdoor level has one)");
    }

    private McpResult TerrainBrushTool(JsonItem a)
    {
        var model = TerrainModel(a);
        var brush = new SculptBrush
        {
            Tool = Enum.Parse<TerrainTool>(a.Text("tool"), ignoreCase: true),
            Radius = Math.Clamp(a.OptFloat("radius") ?? _brush.Radius, SculptBrush.MinRadius, SculptBrush.MaxRadius),
            Strength = Math.Clamp(a.OptFloat("strength") ?? _brush.Strength, 0f, 1f),
            Falloff = a.OptText("falloff") is { } f ? Enum.Parse<BrushFalloff>(f, ignoreCase: true) : _brush.Falloff,
            Target = a.OptFloat("height") ?? _brush.Target,
            Tile = a.OptInt("tile") ?? _brush.Tile,
        };
        var (x, z) = (a.Float("x"), a.Float("z"));
        if (BrushMath.CellAt(model, x, z).Col < 0)
        {
            return McpResult.Error($"{x}, {z} is off the terrain");
        }

        // A press held for dabs frames, then let go: the stroke's dabs are one undo step.
        var hit = new Vector3(x, BrushMath.HeightAt(model, x, z), z);
        var dabs = Math.Clamp(a.OptInt("dabs") ?? DefaultDabs, 1, MaxDabs);
        return Edited(() =>
        {
            for (var i = 0; i < dabs; i++)
            {
                brush.Frame(model, new BrushInput(hit, Left: true, Shift: false, Ctrl: false), DabSeconds);
            }

            brush.Frame(model, new BrushInput(hit, Left: false, Shift: false, Ctrl: false), DabSeconds);
            return JsonItem.NewObject().Add("height", (int)BrushMath.HeightAt(model, x, z));
        });
    }

    private McpResult TerrainGenerateTool(JsonItem a)
    {
        _ = TerrainModel(a);
        var settings = new GenerateSettings(a.Int("seed"), Math.Clamp(a.OptFloat("roughness") ?? _generate.Roughness, 0f, 1f),
            a.OptFloat("lowest") ?? _generate.MinHeight, a.OptFloat("highest") ?? _generate.MaxHeight);
        if (!TerrainActions.Generate(settings))
        {
            return McpResult.Error("Generate: " + TerrainActions.Error);
        }

        return Edited(() => Said(TerrainActions.ApplyPending() ? "Generated and applied" : "Nothing pending"));
    }
}
