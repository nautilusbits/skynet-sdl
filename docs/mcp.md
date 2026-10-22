# SkyNetEdit over MCP

AI agents (Claude Code, any MCP client) drive the running editor through MCP: Streamable HTTP,
JSON-RPC by POST on `http://127.0.0.1:PORT/mcp`. Localhost only, off by default.

## Enable

- Preferences > AI agents (MCP): "Serve the editor's tools on localhost", port (default 47800,
  0 = any free one). Takes effect at once.
- Or the command line: `SkyNetEdit --mcp` (the preferences' port) or `--mcp 0` / `--mcp 47801`.

The log and the status bar show the endpoint: `MCP: http://127.0.0.1:47800/mcp`.

## Connect Claude Code

```
claude mcp add --transport http skynet-edit http://127.0.0.1:47800/mcp
```

Calls run on the editor's main thread between frames; the editor must be running.

## Rules

- Every edit is one step of Edit > Undo's stack; `undo` / `redo` walk it (the user's Ctrl+Z too).
  An edit's result carries `changed` and the step's `undo` label.
- Edits change the open document; nothing is written until `save`. Saves go to a folder mod
  (`mods/<name>/`), never the game's data. Pick it first with `save_target` when tables are edited.
- Entity keys are MAP offsets (`entity_query` lists them). An entity's `x, y, z` are its DOS
  record's; `world` is the view's (y up, z = -DOS z). `entity_place` takes a world floor point or
  `near` an entity.
- Tools taking `instance` act on that mission instance and show it; default the shown one.

## Tools

| Tool | Does |
|---|---|
| `mission_list` | missions and arenas that open, the open one |
| `mission_open` | opens a mission or arena (`discard` drops unsaved edits) |
| `mission_get` | open mission: instances, start, links, shown, camera, dirty, save mod |
| `map_list`, `map_open` | instances with their maps; shows one |
| `map_new`, `mission_new` | File > New Map / New Mission into the save mod |
| `entity_query` | Outliner filter: kinds, text, type, act, key, DOS x/z area |
| `entity_get`, `entity_set` | Details rows; set one row (label or field) on one or more entities |
| `entity_place` | Place panel item (prop, robot, pickup, marker, light, sprite) |
| `entity_delete`, `entity_duplicate`, `entity_move`, `entity_drop`, `entity_select` | the selection edits |
| `graph_get` | trigger graph: nodes, edges, issues |
| `graph_connect`, `graph_disconnect`, `graph_set_act`, `graph_set_param`, `graph_add`, `graph_delete` | graph edits |
| `graph_verify` | Verify: edited chains fired by the game's verifier, PASS/FAIL, issues |
| `table_list`, `table_get`, `table_set`, `table_add`, `table_revert` | game tables (Tables window) |
| `terrain_brush`, `terrain_generate` | Terrain mode stroke; generated height field |
| `save_target`, `save` | the save mod; File > Save |
| `playtest` | plays the shown instance headless (`seconds`: default 3, at most 120) while the editor goes on; reports phase, health, robots, objectives, log |
| `undo`, `redo` | Edit > Undo / Redo, one step a call: `Undid <label>` / `Nothing to undo` (the engine's tools) |
| `screenshot` | PNG of the editor window |
| every console command | `asset_list`, `table_open`, `edit_paste`, `pref`, ... with one string argument `args` |

`tools/list` gives each tool's JSON schema.
