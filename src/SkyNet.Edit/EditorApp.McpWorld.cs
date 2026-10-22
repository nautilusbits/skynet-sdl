using System.Numerics;
using RetroEng.Editor;
using RetroEng.Json;
using SkyNet.Formats;
using SkyNet.Game.Behaviour;
using SkyNet.Game.Campaign;
using SkyNet.Game.Mods;

namespace SkyNet.Edit;

/// <summary>The MCP tools of missions, maps and entities: what the Open Mission window, the Outliner, the
/// Details panel, the Place panel, Edit > Arrange and File > New do, each edit one undo step.
/// Positions: an entity's x, y, z are its DOS record's (the Details panel's); "world" is where it stands
/// in the view (x east, y up, z = -DOS z); entity_place takes a world floor point.</summary>
internal sealed partial class EditorApp
{
    private const int DefaultQueryLimit = 200, MaxChoices = 64;

    private static readonly McpArg InstanceArg = new("instance", "string", "the mission instance (default the shown one; another is shown first)");
    private static readonly McpArg KeysArg = new("keys", "integer[]", "entity keys (MAP offsets, as entity_query gives)");
    private static readonly McpArg KeyArg = new("key", "integer", "one entity key, in place of keys");

    private void AddMcpTools(McpServer s)
    {
        AddMissionTools(s);
        AddEntityTools(s);
        AddGraphTools(s);
        AddTableTools(s);
        AddTerrainTools(s);
        AddSessionTools(s);
    }

    // ---- missions and maps ----

    private void AddMissionTools(McpServer s)
    {
        McpArg game = new("game", "string", "skynet or shock (default the data set shown)");
        Tool(s, "mission_list", "The missions (number, briefing title) and deathmatch arenas that open, and which is open.", [game], MissionList);
        Tool(s, "mission_open", "Opens a mission (or an arena) at its start instance. Refused with unsaved edits unless discard.",
            [new("number", "integer", "mission number (an arena: its MAP number)", true), game, new("arena", "boolean", "number is a deathmatch arena's map"),
             new("instance", "string", "the instance shown first"), new("discard", "boolean", "drop unsaved edits")], MissionOpen);
        Tool(s, "mission_get", "The open mission: instances (id, map, layer), start, exit links, shown instance, camera, unsaved state, save mod, undo labels.",
            [], _ => McpResult.Json(MissionJson()));
        Tool(s, "map_list", "The open mission's instances with their maps: outdoor or not, entity count, which is shown.", [], _ => MapList());
        Tool(s, "map_open", "Shows an instance's map in the view (the entity tools then act on it).", [new("instance", "string", "instance id", true)],
            a => Show(a.Text("instance")) ? McpResult.Json(MissionJson()) : McpResult.Error($"No instance {a.Text("instance")} (map_list)"));
        Tool(s, "map_new", "File > New Map: a new map in the save mod, added to the open mission as an instance (one undo step; the map's files stay) and shown.",
            [new("number", "integer", "MAP number (default the first free of 720-799)"), new("setting", "string", "outdoor or interior (default outdoor)"),
             new("ground", "string", "flat or generated (default flat)"), new("mod", "string", "the save mod (default the current one)")], MapNew);
        Tool(s, "mission_new", "File > New Mission: a mission of new maps in the save mod, then opened.",
            [new("number", "integer", "mission number (default the first free of 20-99)"), new("maps", "string[]", "each map's setting in play order: outdoor or interior (default one outdoor)"),
             new("objective", "string", "the briefing's objective"), new("mod", "string", "the save mod (default the current one)"),
             new("discard", "boolean", "drop unsaved edits of the open mission")], MissionNew);
    }

    private McpResult MissionList(JsonItem a)
    {
        var game = GameOf(a);
        if (DataOf(game) is not { } data)
        {
            return McpResult.Error($"No {game} data beside {_data.Dir}");
        }

        var missions = MissionTable.Playable(game).Missions.Select(m => JsonItem.NewObject().Add("number", m.Number).Add("title", MissionLabel(game, m.Number).Trim()));
        var arenas = game == GameKind.SkyNet ? ArenaWorld.Levels(data).Select(l => JsonItem.NewObject().Add("map", l.Map).Add("name", l.Name)) : [];
        return McpResult.Json(JsonItem.NewObject().Add("game", GameName(game)).Add("open", Current is { } c ? OpenJson(c) : JsonItem.Null())
            .Add("missions", JsonItem.NewArray(missions)).Add("arenas", JsonItem.NewArray(arenas)));
    }

    private McpResult MissionOpen(JsonItem a)
    {
        if (IsDirty && !a.Flag("discard"))
        {
            return McpResult.Error("The open mission has unsaved edits: save first, or pass discard");
        }

        var game = GameOf(a);
        var number = a.Int("number");
        var opened = a.Flag("arena") ? Open(new OpenTarget(game, number, Arena: true)) : UseGame(game) && Open(number, a.OptText("instance"));
        return opened ? McpResult.Json(MissionJson()) : McpResult.Error($"{GameName(game)} {number} does not open (see the log)");
    }

    private JsonItem MissionJson()
    {
        if (_world == null || _doc == null)
        {
            return JsonItem.NewObject().Add("open", JsonItem.Null());
        }

        var world = _world.World;
        var instances = world.Instances.Select(i => JsonItem.NewObject().Add("id", i.Id).Add("map", EditorWorld.MapOf(i)).Add("layer", i.Layer).Add("group", i.Group));
        var links = world.Links.Select(l => JsonItem.NewObject().Add("from", l.From).Add("source", l.Source).Add("to", l.To).Add("back", l.Back).Add("arrival", l.Arrival));
        return JsonItem.NewObject().Add("open", OpenJson(Current!.Value)).Add("shown", _levelInstance)
            .Add("start", JsonItem.NewObject().Add("instance", world.Start.Instance).Add("arrival", world.Start.Arrival))
            .Add("instances", JsonItem.NewArray(instances)).Add("links", JsonItem.NewArray(links))
            .Add("camera", JsonItem.NewArray([(int)_eye.X, (int)_eye.Y, (int)_eye.Z]))
            .Add("dirty", IsDirty).Add("saveMod", _saveMod).Add("nextUndo", EditHistory.UndoLabel).Add("nextRedo", EditHistory.RedoLabel);
    }

    private McpResult MapList()
    {
        if (_world == null)
        {
            return McpResult.Error("Nothing open (mission_open)");
        }

        var maps = _world.World.Instances.Select(i => _world.Map(i.Id) is { } map
            ? JsonItem.NewObject().Add("instance", i.Id).Add("map", EditorWorld.MapOf(i)).Add("outdoor", map.IsOutdoor).Add("entities", map.Entities.Length).Add("shown", i.Id == _levelInstance)
            : JsonItem.NewObject().Add("instance", i.Id).Add("map", EditorWorld.MapOf(i)).Add("error", "does not load"));
        return McpResult.Json(JsonItem.NewObject().Add("instances", JsonItem.NewArray(maps)));
    }

    private McpResult MapNew(JsonItem a)
    {
        if (a.OptText("mod") is { } mod && UseSaveMod(mod, out var bad) == null)
        {
            return McpResult.Error(bad!);
        }

        var spec = _newMap with
        {
            Number = a.OptInt("number") ?? NewMaps.FirstFree(TakenMaps()),
            Setting = a.OptText("setting") is "interior" ? MapSetting.Interior : MapSetting.Outdoor,
            Terrain = a.OptText("ground") is "generated" ? TerrainStart.Generated : TerrainStart.Flat,
        };
        return Edited(() => CreateMap(spec) is { } error ? throw new InvalidOperationException(error) : Said(Inv($"MAP.{spec.Number:D3} made, shown as {_levelInstance}")));
    }

    private McpResult MissionNew(JsonItem a)
    {
        if (IsDirty && !a.Flag("discard"))
        {
            return McpResult.Error("The open mission has unsaved edits: save first, or pass discard");
        }

        if (a.OptText("mod") is { } mod && UseSaveMod(mod, out var bad) == null)
        {
            return McpResult.Error(bad!);
        }

        var maps = a.Words("maps").Select(m => m == "interior" ? MapSetting.Interior : MapSetting.Outdoor).DefaultIfEmpty(MapSetting.Outdoor).ToArray();
        var number = a.OptInt("number") ?? NewMissions.FirstFree(TakenMissions());
        if (CreateMission(number, maps, a.OptText("objective") ?? _newObjective) is { } error)
        {
            return McpResult.Error(error);
        }

        // Discarded edits: the open the unsaved-edits question waits for is done now.
        if (_askOpen is { } pending)
        {
            _askOpen = null;
            Open(pending);
        }

        return McpResult.Json(MissionJson());
    }

    private GameKind GameOf(JsonItem a) => a.OptText("game") switch
    {
        null => _data.Game,
        "shock" => GameKind.FutureShock,
        "skynet" => GameKind.SkyNet,
        var other => throw new ArgumentException($"game {other}: skynet or shock"),
    };

    private static string GameName(GameKind game) => game == GameKind.FutureShock ? "shock" : "skynet";

    private static JsonItem OpenJson(OpenTarget t) =>
        JsonItem.NewObject().Add("game", GameName(t.Game)).Add("number", t.Number).Add("arena", t.Arena);

    // ---- entities ----

    private void AddEntityTools(McpServer s)
    {
        Tool(s, "entity_query", "Finds the entities of an instance's map as the Outliner's filter does: kinds, search words, robot type, act, key, a DOS x/z area.",
            [InstanceArg, new("kinds", "string[]", "prop, actor, light, robot, pickup, sprite, marker (default every kind)"),
             new("text", "string", "search words, all must match: a part of the row label, kind or robot name (\"raptor\", \"door\")"),
             new("type", "integer", "robot type"), new("act", "integer", "act id"), new("key", "integer", "one key"),
             new("minX", "integer", "DOS x at least"), new("maxX", "integer", "DOS x at most"), new("minZ", "integer", "DOS z at least"),
             new("maxZ", "integer", "DOS z at most"), new("limit", "integer", "most entities listed (default 200)")], EntityQuery);
        Tool(s, "entity_get", "An entity's Details rows (sections Transform, its kind, Trigger: label, field, value, range, choices) and its raw properties.",
            [new("key", "integer", "entity key", true), InstanceArg], EntityGet);
        Tool(s, "entity_set", "Sets one Details row (by its label or field, e.g. x, yaw, robot type, act, next, dormant for) on one or more entities: one undo step.",
            [KeysArg, KeyArg, new("field", "string", "the row's label or field (entity_get lists them)", true),
             new("value", "integer", "the value (a choice row also takes its label as a string; a bool row 0 or 1)", true), InstanceArg], EntitySet);
        Tool(s, "entity_place", "Puts a Place panel item down (prop, robot, pickup, marker, light, sprite) at a world floor point or beside an entity: one undo step; it is selected.",
            [new("kind", "string", "prop, robot, pickup, marker, light or sprite", true),
             new("item", "string", "the palette item: a part of its label (\"endo\", \"Player start\"), a robot's type number", true),
             new("x", "number", "world x"), new("y", "number", "world y (the floor)"), new("z", "number", "world z"),
             new("near", "integer", "an entity key: put it where that one stands, in place of x, y, z"),
             new("dx", "integer", "with near: DOS x offset"), new("dz", "integer", "with near: DOS z offset"),
             new("floor", "boolean", "snap down onto the floor under the point (shown level)"), InstanceArg], EntityPlace);
        Tool(s, "entity_delete", "Deletes entities: one undo step.", [KeysArg, KeyArg, InstanceArg], EntityDelete);
        Tool(s, "entity_duplicate", "Duplicates entities, moved by dx, dy, dz DOS units (default 64, 0, 0): one undo step; the copies are selected.",
            [KeysArg, KeyArg, new("dx", "integer", "DOS x offset"), new("dy", "integer", "DOS y offset"), new("dz", "integer", "DOS z offset"), InstanceArg], EntityDuplicate);
        Tool(s, "entity_move", "Moves entities by dx, dy, dz DOS units, or (x, y, z given) to that DOS position: one undo step.",
            [KeysArg, KeyArg, new("dx", "integer", "DOS x offset"), new("dy", "integer", "DOS y offset"), new("dz", "integer", "DOS z offset"),
             new("x", "integer", "DOS x to put each at"), new("y", "integer", "DOS y"), new("z", "integer", "DOS z"), InstanceArg], EntityMove);
        Tool(s, "entity_drop", "Edit > Arrange > Drop to floor: puts entities of the shown level down onto the floor under them: one undo step.",
            [KeysArg, KeyArg, InstanceArg], EntityDrop);
        Tool(s, "entity_select", "Selects entities in the view (the first primary) and flies the camera to the first.",
            [KeysArg, KeyArg, new("fly", "boolean", "fly the camera there (default true)"), InstanceArg], EntitySelect);
    }

    /// <summary>The layer of the instance a call names (default the shown one), shown first so the view follows.</summary>
    private string McpLayer(JsonItem a)
    {
        var instance = a.OptText("instance") ?? _levelInstance ?? throw new InvalidOperationException("No level shown: mission_open first");
        if (instance != _levelInstance && !Show(instance))
        {
            throw new ArgumentException($"No instance {instance} in the mission (map_list)");
        }

        return LayerOf(instance) ?? throw new InvalidOperationException($"{instance} has no editable layer");
    }

    private static MapEntity EntityAt(MapFile map, int key) =>
        map.TryGetEntityAt(key, out var e) ? e : throw new MapLayerException($"@{key}: the map has no entity under that key");

    private static JsonItem EntityJson(MapFile map, MapEntity e)
    {
        var w = BehaviourModel.Position(e);
        var kind = EditorWorld.KindOf(e);
        var o = JsonItem.NewObject().Add("key", e.FileOffset).Add("kind", kind.ToString().ToLowerInvariant()).Add("label", EditorWorld.Label(map, e))
            .Add("x", e.X).Add("y", e.Y).Add("z", e.Z).Add("world", JsonItem.NewArray([(int)w.X, (int)w.Y, (int)w.Z]))
            .Add("act", e.ActType).Add("next", e.LinkNext);
        if (e.IsMarker)
        {
            o.Add("marker", e.MarkerType);
        }

        if (kind == EntityKind.Robot)
        {
            o.Add("robotType", e.EnemyType);
        }

        return o;
    }

    private McpResult EntityQuery(JsonItem a)
    {
        var layer = McpLayer(a);
        var map = _doc!.Map(layer);
        var words = new List<string>();
        if (a.OptText("text") is { } text)
        {
            words.Add(text);
        }

        // The Outliner's search words: type:N, act:N, @key.
        AddWord(words, "type:", a.OptInt("type"));
        AddWord(words, "act:", a.OptInt("act"));
        AddWord(words, "@", a.OptInt("key"));
        var filter = new OutlinerFilter { Text = string.Join(' ', words) };
        foreach (var kind in a.Words("kinds"))
        {
            filter.Kinds.Add(Enum.Parse<EntityKind>(kind, ignoreCase: true));
        }

        var (minX, maxX, minZ, maxZ) = (a.OptInt("minX") ?? int.MinValue, a.OptInt("maxX") ?? int.MaxValue, a.OptInt("minZ") ?? int.MinValue, a.OptInt("maxZ") ?? int.MaxValue);
        var found = map.Entities.Where(e => e.X >= minX && e.X <= maxX && e.Z >= minZ && e.Z <= maxZ && filter.Matches(map, e)).OrderBy(e => e.FileOffset).ToList();
        var limit = a.OptInt("limit") ?? DefaultQueryLimit;
        return McpResult.Json(JsonItem.NewObject().Add("instance", _levelInstance).Add("layer", layer).Add("of", map.Entities.Length)
            .Add("matched", found.Count).Add("truncated", found.Count > limit)
            .Add("entities", JsonItem.NewArray(found.Take(limit), e => EntityJson(map, e))));
    }

    private static void AddWord(List<string> words, string prefix, int? value)
    {
        if (value is { } v)
        {
            words.Add(prefix + v.ToString(System.Globalization.CultureInfo.InvariantCulture));
        }
    }

    /// <summary>The Details panel's sections of <paramref name="entities"/>, with the robot types the Tables grid has.</summary>
    private IReadOnlyList<DetailSection> DetailsOf(MapFile map, IReadOnlyList<MapEntity> entities) =>
        EntityDetails.Of(map, entities, RobotGrid() is { } robots ? RobotTypeEdits.Choices(robots) : []);

    private McpResult EntityGet(JsonItem a)
    {
        var layer = McpLayer(a);
        var map = _doc!.Map(layer);
        var e = EntityAt(map, a.Int("key"));
        var sections = DetailsOf(map, [e]).Select(s => JsonItem.NewObject().Add("title", s.Title).Add("rows", JsonItem.NewArray(s.Rows, RowJson)));
        var properties = _world!.Properties(new OutlinerItem(_levelInstance!, e.FileOffset)).Select(p => JsonItem.NewObject().Add("name", p.Name).Add("value", p.Value));
        return McpResult.Json(JsonItem.NewObject().Add("entity", EntityJson(map, e)).Add("sections", JsonItem.NewArray(sections))
            .Add("properties", JsonItem.NewArray(properties)));
    }

    private static JsonItem RowJson(DetailRow r)
    {
        var o = JsonItem.NewObject().Add("label", r.Label).Add("field", r.Field).Add("kind", r.Kind.ToString().ToLowerInvariant());
        if (r.Kind == DetailKind.Text)
        {
            return o.Add("text", r.Text);
        }

        o.Add("value", r.Value).Add("min", r.Min).Add("max", r.Max);
        if (r.Choices is { } choices)
        {
            o.Add("choices", choices.Count <= MaxChoices
                ? JsonItem.NewArray(choices, c => JsonItem.NewObject().Add("value", c.Value).Add("label", c.Label))
                : JsonItem.String($"{choices.Count} choices: pass the value, or a label as a string"));
        }

        return o;
    }

    private McpResult EntitySet(JsonItem a)
    {
        var layer = McpLayer(a);
        var map = _doc!.Map(layer);
        var keys = a.Keys();
        var name = a.Text("field");
        var rows = DetailsOf(map, [.. keys.Select(k => EntityAt(map, k))]).SelectMany(s => s.Rows).Where(r => !r.ReadOnly).ToList();
        var row = rows.FirstOrDefault(r => string.Equals(r.Label, name, StringComparison.OrdinalIgnoreCase))
            ?? rows.FirstOrDefault(r => string.Equals(r.Field, name, StringComparison.OrdinalIgnoreCase))
            ?? throw new ArgumentException($"No editable row {name}: {string.Join(", ", rows.Select(r => r.Label))}");
        var value = RowValue(row, a.Member("value"));
        return Edited(() =>
        {
            EntityDetails.Set(_doc, layer, keys, row, value);
            return JsonItem.NewObject().Add("entities", JsonItem.NewArray(keys, k => EntityJson(_doc.Map(layer), EntityAt(_doc.Map(layer), k))));
        });
    }

    // A number, a bool, or a choice's label.
    private static int RowValue(DetailRow row, JsonItem v)
    {
        if (v.Kind is JsonKind.True or JsonKind.False)
        {
            return v.AsBool() ? 1 : 0;
        }

        if (!v.TryString(out var text))
        {
            return v.AsInt();
        }

        var choices = row.Choices ?? [];
        var picked = choices.FirstOrDefault(c => string.Equals(c.Label, text, StringComparison.OrdinalIgnoreCase));
        picked = picked.Label != null ? picked : choices.FirstOrDefault(c => c.Label.Contains(text, StringComparison.OrdinalIgnoreCase));
        return picked.Label != null ? picked.Value : throw new ArgumentException($"{row.Label}: no choice \"{text}\"");
    }

    private McpResult EntityPlace(JsonItem a)
    {
        var layer = McpLayer(a);
        var map = _doc!.Map(layer);
        var kind = Enum.Parse<PlaceKind>(a.Text("kind"), ignoreCase: true);
        var want = a.Text("item").Trim();
        var items = PlacePalette.Items(kind, map, _arena != null);
        var item = items.FirstOrDefault(i => string.Equals(i.Label.Trim(), want, StringComparison.OrdinalIgnoreCase))
            ?? (kind == PlaceKind.Robot && int.TryParse(want, out var type) ? items.FirstOrDefault(i => i.Fields["enemy"] == type) : null)
            ?? items.FirstOrDefault(i => i.Label.Contains(want, StringComparison.OrdinalIgnoreCase))
            ?? throw new ArgumentException($"Nothing in the {kind} palette matches \"{want}\"");
        var feet = PlaceFeet(a, map);
        if (a.Flag("floor") && FloorAt(feet, -1) is { } floor)
        {
            feet.Y = floor;
        }

        var key = 0;
        var result = Edited(() =>
        {
            key = PlacePalette.Place(_doc, layer, item, feet);
            return JsonItem.NewObject().Add("placed", item.Label.Trim()).Add("entity", EntityJson(_doc.Map(layer), EntityAt(_doc.Map(layer), key)));
        });
        Select(new OutlinerItem(_levelInstance!, key));
        return result;
    }

    // The world floor point a place call names: x, y, z, or where entity "near" stands (DOS dx, dz on).
    private static Vector3 PlaceFeet(JsonItem a, MapFile map)
    {
        if (a.OptInt("near") is not { } near)
        {
            return new Vector3(a.Float("x"), a.Float("y"), a.Float("z"));
        }

        var e = EntityAt(map, near);
        var (dx, dz) = (a.OptInt("dx") ?? 0, a.OptInt("dz") ?? 0);
        return new Vector3(e.X + dx, -(e.Y + EntityArrange.Lift((int)e.Variant)), -(e.Z + dz));
    }

    private McpResult EntityDelete(JsonItem a)
    {
        var layer = McpLayer(a);
        var keys = a.Keys();
        var result = Edited(() =>
        {
            _doc!.Group("Delete entities", () => keys.ForEach(k => _doc.DeleteEntity(layer, k)));
            return JsonItem.NewObject().Add("deleted", JsonItem.NewArray(keys, k => k));
        });
        Select(new OutlinerItem(_levelInstance!));
        return result;
    }

    private McpResult EntityDuplicate(JsonItem a)
    {
        var layer = McpLayer(a);
        var keys = a.Keys();
        var move = (a.OptInt("dx") ?? PlacePalette.DuplicateOffset, a.OptInt("dy") ?? 0, a.OptInt("dz") ?? 0);
        IReadOnlyList<int> copies = [];
        var result = Edited(() =>
        {
            copies = EntityClipboard.Duplicate(_doc!, layer, keys, move);
            return JsonItem.NewObject().Add("copies", JsonItem.NewArray(copies, k => k));
        });
        SelectKeys(copies);
        return result;
    }

    private McpResult EntityMove(JsonItem a)
    {
        var layer = McpLayer(a);
        var map = _doc!.Map(layer);
        var (dx, dy, dz) = (a.OptInt("dx") ?? 0, a.OptInt("dy") ?? 0, a.OptInt("dz") ?? 0);
        (int X, int Y, int Z) Target(int key)
        {
            var e = EntityAt(map, key);
            return (a.OptInt("x") ?? e.X + dx, a.OptInt("y") ?? e.Y + dy, a.OptInt("z") ?? e.Z + dz);
        }

        var to = a.Keys().ToDictionary(k => k, Target);
        return Edited(() =>
        {
            EntityArrange.Move(_doc, layer, to);
            return JsonItem.NewObject().Add("entities", JsonItem.NewArray(to.Keys, k => EntityJson(_doc.Map(layer), EntityAt(_doc.Map(layer), k))));
        });
    }

    private McpResult EntityDrop(JsonItem a)
    {
        var layer = McpLayer(a);
        var keys = a.Keys();
        return Edited(() =>
        {
            var moved = EntityArrange.Drop(_doc!, layer, keys, (e, feet) => FloorAt(feet, e.FileOffset), FootDepth);
            return JsonItem.NewObject().Add("dropped", moved)
                .Add("entities", JsonItem.NewArray(keys, k => EntityJson(_doc!.Map(layer), EntityAt(_doc.Map(layer), k))));
        });
    }

    private McpResult EntitySelect(JsonItem a)
    {
        var layer = McpLayer(a);
        var keys = a.Keys();
        keys.ForEach(k => EntityAt(_doc!.Map(layer), k));
        SelectKeys(keys);
        if (a.Opt("fly")?.AsBool() != false)
        {
            FlyTo(new OutlinerItem(_levelInstance!, keys[0]));
        }

        return McpResult.Json(JsonItem.NewObject().Add("selected", JsonItem.NewArray(keys, k => k)).Add("instance", _levelInstance));
    }
}
