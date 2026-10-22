using System.Diagnostics;
using System.Globalization;
using System.Numerics;
using Hexa.NET.ImGui;
using RetroEng.Commands;
using SkyNet.Formats;

namespace SkyNet.Edit;

/// <summary>
/// The Outliner (Unreal's, kept simple): the mission's instances, each map's entities grouped by kind, a search box
/// and kind chips (<see cref="OutlinerFilter"/>) with counts, "Select all matching" (the Details panel then edits
/// them together), a group's click selecting its members, and an eye and a lock per entity and group
/// (<see cref="OutlinerStates"/>: hidden ones not drawn, hidden or locked ones not picked in the view).
/// <code>
///   [search ________________]
///   (Prop) (Robot) (Pickup) ...      3 of 214 match   [Select all matching]
///   ▾ i1  MAP.210 (shown)
///     ◉ 🔒 ▾ Robot (3/12)
///     ◉ 🔒     robot type 37  @1234
/// </code>
/// </summary>
internal sealed partial class EditorApp
{
    private static readonly Vector4 ChipOn = new(0.26f, 0.59f, 0.98f, 0.8f);
    // An eye or lock icon's width, in text lines.
    private const float IconWidth = 1.4f;
    private readonly OutlinerFilter _outlinerFilter = new();
    private string _outlinerText = "";
    // The search or chips changed: the shown instance and its groups open once, so the matches show.
    private bool _outlinerOpen;

    private void RegisterOutlinerCommands()
    {
        _commands.Add("outliner.filter", "outliner.filter [words]", a => FilterCmd(a.Rest()),
            "sets the outliner's search (@key, act:N, type:N, names); no words clears it");
        _commands.Add("outliner.kinds", "outliner.kinds [kind,kind...]", a => KindsCmd(a.OptionalWord(0)),
            "sets the outliner's kind chips (prop, actor, light, robot, pickup, sprite, marker); none: every kind");
        _commands.Add("outliner.select", "outliner.select", _ => Inv($"{SelectMatching()} selected"),
            "selects every entity of the shown level the outliner's filter matches (Select all matching)");
        _commands.Add("outliner.hide", "outliner.hide <key> [on|off]", a => FlagCmd(a, EntityFlag.Hidden),
            "hides (or shows) entity key of the shown level: not drawn, not picked");
        _commands.Add("outliner.lock", "outliner.lock <key> [on|off]", a => FlagCmd(a, EntityFlag.Locked),
            "locks (or unlocks) entity key of the shown level: not picked in the view");
        _commands.Add("place.drag", "place.drag <kind:text> <x> <y>", a => DragCmd(a.Word(0), a.Float(1), a.Float(2)),
            "drags the first Place palette item matching kind:text over pixel x,y of the view (its ghost shown; Esc or place.cancel stops)");
        _commands.Add("place.drop", "place.drop", _ => DropCmd(), "drops the item place.drag holds (one undo step)");
        _commands.Add("place.cancel", "place.cancel", _ => { CancelDrag(); return "Drag cancelled"; }, "drops nothing");
    }

    private void Outliner(Vector2 at, Vector2 size)
    {
        PanelAt(at, size);
        if (BeginPanel(EditorPanel.Outliner) && _world != null)
        {
            ImGui.TextUnformatted(_arena != null ? $"Arena {_arena.MapName}: {_arena.Name}"
                : string.Create(CultureInfo.InvariantCulture, $"Mission {_world.Mission.Number} ({_world.Mission.Game})"));
            OutlinerFilterBar();
            ImGui.Separator();
            if (ImGui.BeginChild("outlinerTree"))
            {
                OutlinerTree();
            }

            ImGui.EndChild();
        }

        ImGui.End();
    }

    // The search box, the kind chips, the shown level's count and "Select all matching".
    private void OutlinerFilterBar()
    {
        ImGui.SetNextItemWidth(-1f);
        if (ImGui.InputTextWithHint("##outlinerFilter", "search: name, type, act:N, @key", ref _outlinerText, 128))
        {
            (_outlinerFilter.Text, _outlinerOpen) = (_outlinerText, true);
        }

        var right = ImGui.GetCursorScreenPos().X + ImGui.GetContentRegionAvail().X;
        var first = true;
        foreach (var kind in EditorWorld.Kinds)
        {
            var label = kind.ToString();
            var width = ImGui.CalcTextSize(label).X + ImGui.GetStyle().FramePadding.X * 2f;
            if (!first && ImGui.GetItemRectMax().X + ImGui.GetStyle().ItemSpacing.X + width < right)
            {
                ImGui.SameLine();
            }

            first = false;
            var on = _outlinerFilter.Kinds.Contains(kind);
            if (on)
            {
                ImGui.PushStyleColor(ImGuiCol.Button, ChipOn);
            }

            if (ImGui.SmallButton(label + "##chip"))
            {
                if (!_outlinerFilter.Kinds.Remove(kind))
                {
                    _outlinerFilter.Kinds.Add(kind);
                }

                _outlinerOpen = true;
            }

            if (on)
            {
                ImGui.PopStyleColor();
            }
        }

        if (_levelInstance == null || _world!.Map(_levelInstance) is not { } map)
        {
            return;
        }

        var all = map.Entities.Length;
        var matching = _outlinerFilter.IsActive ? map.Entities.Count(e => _outlinerFilter.Matches(map, e)) : all;
        ImGui.TextDisabled(Inv($"{matching} of {all} on {_levelInstance}"));
        ImGui.SameLine();
        ImGui.BeginDisabled(matching == 0);
        if (ImGui.SmallButton("Select all matching"))
        {
            SelectMatching();
        }

        ImGui.EndDisabled();
    }

    private void OutlinerTree()
    {
        string? show = null;
        foreach (var instance in _world!.World.Instances)
        {
            var item = new OutlinerItem(instance.Id);
            var revealHere = _reveal && _selected is { IsInstance: false } sel && sel.Instance == instance.Id;
            if (revealHere || (_outlinerOpen && _outlinerFilter.IsActive && instance.Id == _levelInstance))
            {
                ImGui.SetNextItemOpen(true);
            }

            var shown = instance.Id == _levelInstance;
            var flags = ImGuiTreeNodeFlags.OpenOnArrow | ImGuiTreeNodeFlags.SpanAvailWidth
                | (_selected == item ? ImGuiTreeNodeFlags.Selected : 0) | (shown ? ImGuiTreeNodeFlags.DefaultOpen : 0);
            var label = string.Create(CultureInfo.InvariantCulture,
                $"{instance.Id}  MAP.{EditorWorld.MapOf(instance):D3}{(shown ? "  (shown)" : "")}###i{instance.Id}");
            var open = ImGui.TreeNodeEx(label, flags);
            if (ImGui.IsItemClicked() && !ImGui.IsItemToggledOpen())
            {
                _selected = item;
            }

            if (ImGui.IsItemHovered() && ImGui.IsMouseDoubleClicked(ImGuiMouseButton.Left))
            {
                show = instance.Id;
            }

            if (!open)
            {
                continue;
            }

            if (_world.Map(instance.Id) is { } map)
            {
                InstanceGroups(instance.Id, instance.Layer, map, revealHere);
            }
            else
            {
                ImGui.TextDisabled("the map does not load");
            }

            ImGui.TreePop();
        }

        (_reveal, _outlinerOpen) = (false, false);
        if (show != null && show != _levelInstance)
        {
            Show(show);
        }
    }

    // An instance's kind groups: the members the filter lets through, an eye and a lock before each.
    private void InstanceGroups(string instance, string layer, MapFile map, bool revealHere)
    {
        var states = _doc?.Layers.Contains(layer) == true ? _doc.Outliner : null;
        var revealKind = revealHere && map.TryGetEntityAt(_selected!.Value.Entity, out var picked) ? EditorWorld.KindOf(picked) : (EntityKind?)null;
        foreach (var kind in EditorWorld.Kinds)
        {
            var entities = _world!.Entities(instance, kind);
            IReadOnlyList<MapEntity> members = _outlinerFilter.IsActive ? _outlinerFilter.Matching(map, entities) : entities;
            if (members.Count == 0)
            {
                continue;
            }

            var id = Inv($"{instance}.{kind}");
            if (states != null)
            {
                GroupFlags(id, layer, states, members);
            }

            var count = members.Count == entities.Count ? Inv($"{entities.Count}") : Inv($"{members.Count}/{entities.Count}");

            // After the icons: SetNextItemOpen is for the next item.
            if (revealKind == kind || (_outlinerOpen && _outlinerFilter.IsActive && instance == _levelInstance))
            {
                ImGui.SetNextItemOpen(true);
            }

            var open = ImGui.TreeNodeEx(Inv($"{kind} ({count})###k{id}"), ImGuiTreeNodeFlags.OpenOnArrow | ImGuiTreeNodeFlags.SpanAvailWidth);
            if (ImGui.IsItemClicked() && !ImGui.IsItemToggledOpen())
            {
                SelectAll(instance, layer, members);
            }

            if (!open)
            {
                continue;
            }

            foreach (var e in members)
            {
                EntityRow(instance, layer, states, map, e, revealKind == kind);
            }

            ImGui.TreePop();
        }
    }

    private void EntityRow(string instance, string layer, OutlinerStates? states, MapFile map, MapEntity e, bool revealHere)
    {
        var row = new OutlinerItem(instance, e.FileOffset);
        var key = e.FileOffset.ToString(CultureInfo.InvariantCulture);
        var hidden = states?.Has(layer, e.FileOffset, EntityFlag.Hidden) == true;
        if (states != null)
        {
            Flags(Inv($"{instance}.{key}"), layer, states, [e.FileOffset], hidden, states.Has(layer, e.FileOffset, EntityFlag.Locked));
        }

        if (hidden)
        {
            ImGui.PushStyleColor(ImGuiCol.Text, ImGui.GetStyle().Colors[(int)ImGuiCol.TextDisabled]);
        }

        if (ImGui.Selectable(EditorWorld.Label(map, e) + "###e" + instance + "." + key, _selected == row || _extra.Contains(row))
            && !AddToSelection(row))
        {
            _selected = row;
            _extra.Clear();
        }

        EntityDragSource(instance, map, e);
        if (hidden)
        {
            ImGui.PopStyleColor();
        }

        if (revealHere && _selected == row)
        {
            ImGui.SetScrollHereY(0.5f);
            _reveal = false;
        }

        if (ImGui.IsItemHovered() && ImGui.IsMouseDoubleClicked(ImGuiMouseButton.Left))
        {
            FlyTo(row);
        }
    }

    // A group's eye and lock: on when every member has it; a click sets every member.
    private void GroupFlags(string id, string layer, OutlinerStates states, IReadOnlyList<MapEntity> members)
    {
        var keys = members.Select(m => m.FileOffset).ToList();
        var hidden = states.Of(layer, EntityFlag.Hidden);
        var locked = states.Of(layer, EntityFlag.Locked);
        Flags("g" + id, layer, states, keys, keys.TrueForAll(hidden.Contains), keys.TrueForAll(locked.Contains));
    }

    // The eye and the lock of a row, each a click to toggle (one undo step).
    private static void Flags(string id, string layer, OutlinerStates states, List<int> keys, bool hidden, bool locked)
    {
        if (FlagIcon("##h" + id, EntityFlag.Hidden, hidden))
        {
            states.Set(layer, keys, EntityFlag.Hidden, hidden ? FlagValue.Off : FlagValue.On);
        }

        ImGui.SameLine(0f, 2f);
        if (FlagIcon("##l" + id, EntityFlag.Locked, locked))
        {
            states.Set(layer, keys, EntityFlag.Locked, locked ? FlagValue.Off : FlagValue.On);
        }

        ImGui.SameLine();
    }

    // An eye (struck through when hidden) or a padlock (closed when locked), drawn; true when clicked.
    private static bool FlagIcon(string id, EntityFlag flag, bool on)
    {
        var h = ImGui.GetTextLineHeight();
        var clicked = ImGui.InvisibleButton(id, new Vector2(h * IconWidth, h));
        var (min, max) = (ImGui.GetItemRectMin(), ImGui.GetItemRectMax());
        var c = (min + max) * 0.5f;
        var r = h * 0.3f;
        var lit = flag == EntityFlag.Hidden ? !on : on;
        var colour = ImGui.GetColorU32(lit || ImGui.IsItemHovered() ? ImGuiCol.Text : ImGuiCol.TextDisabled);
        var draw = ImGui.GetWindowDrawList();
        if (flag == EntityFlag.Hidden)
        {
            // The eye: an almond of two arcs, the pupil; a stroke across when hidden.
            draw.PathArcTo(c + new Vector2(0f, r * 1.5f), r * 2.5f, -MathF.PI * 0.78f, -MathF.PI * 0.22f);
            draw.PathArcTo(c - new Vector2(0f, r * 1.5f), r * 2.5f, MathF.PI * 0.22f, MathF.PI * 0.78f);
            draw.PathStroke(colour, ImDrawFlags.Closed, 1.2f);
            draw.AddCircleFilled(c, r * 0.55f, colour);
            if (on)
            {
                draw.AddLine(new Vector2(min.X + 1f, max.Y - 1f), new Vector2(max.X - 1f, min.Y + 1f), colour, 1.5f);
            }
        }
        else
        {
            // The padlock: a body and its shackle, lifted open when unlocked.
            var body = new Vector2(r, r * 0.8f);
            draw.AddRectFilled(c - new Vector2(body.X, 0f), c + new Vector2(body.X, body.Y * 1.4f), colour, 1f);
            var lift = on ? 0f : r * 0.5f;
            draw.PathArcTo(c - new Vector2(0f, lift), r * 0.65f, MathF.PI, MathF.Tau);
            draw.PathStroke(colour, ImDrawFlags.None, 1.5f);
        }

        if (ImGui.IsItemHovered())
        {
            ImGui.SetTooltip(flag == EntityFlag.Hidden ? (on ? "Show" : "Hide") : (on ? "Unlock" : "Lock"));
        }

        return clicked;
    }

    // ---- selecting what the filter lets through ----

    /// <summary>Selects the shown level's entities the filter matches (not hidden or locked): the count.</summary>
    private int SelectMatching()
    {
        if (_levelInstance == null || LayerOf(_levelInstance) is not { } layer || _world!.Map(_levelInstance) is not { } map)
        {
            return 0;
        }

        return SelectAll(_levelInstance, layer, map.Entities);
    }

    /// <summary>Selects the <paramref name="entities"/> of <paramref name="instance"/> the filter matches (the first
    /// primary), the hidden and locked ones left out: the count selected. The selection stays when none is left.</summary>
    private int SelectAll(string instance, string layer, IEnumerable<MapEntity> entities)
    {
        if (_world?.Map(instance) is not { } map)
        {
            return 0;
        }

        var skip = _doc?.Layers.Contains(layer) == true ? _doc.Outliner.Unpickable(layer) : new HashSet<int>();
        var items = _outlinerFilter.Selection(map, entities, skip).Select(k => new OutlinerItem(instance, k)).ToList();
        if (items.Count == 0)
        {
            return 0;
        }

        _selected = items[0];
        _extra.Clear();
        _extra.UnionWith(items.Skip(1));
        return items.Count;
    }

    // A hidden entity's toggle: the shown level baked again without (or with) it, at once.
    private void OutlinerChanged(string layer)
    {
        if (LayerOf(_levelInstance) == layer)
        {
            _rebuildDue = Stopwatch.GetTimestamp();
        }
    }

    /// <summary>The map the view bakes for <paramref name="instance"/>: <see cref="ShownEdits"/> without the entities the
    /// outliner hides (play-tests and previews keep them).</summary>
    private MapFile? ViewEdits(RetroEng.World.WorldInstance instance) =>
        _doc != null && _doc.Layers.Contains(instance.Layer) && _doc.Outliner.Of(instance.Layer, EntityFlag.Hidden) is { Count: > 0 } hidden
            ? OutlinerStates.Without(_doc, instance.Layer, hidden)
            : ShownEdits(instance);

    /// <summary>What a click in the shown level goes through: its hidden and locked entities.</summary>
    private IReadOnlySet<int> Unpickable() =>
        LayerOf(_levelInstance) is { } layer ? _doc!.Outliner.Unpickable(layer) : new HashSet<int>();

    // ---- the commands ----

    private string FilterCmd(string text)
    {
        (_outlinerText, _outlinerFilter.Text, _outlinerOpen) = (text, text, true);
        return OutlinerCount();
    }

    private string KindsCmd(string? list)
    {
        _outlinerFilter.Kinds.Clear();
        foreach (var word in (list ?? "").Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            if (!Enum.TryParse<EntityKind>(word, ignoreCase: true, out var kind))
            {
                throw new CommandUsageException($"no kind {word} (prop, actor, light, robot, pickup, sprite, marker)");
            }

            _outlinerFilter.Kinds.Add(kind);
        }

        _outlinerOpen = true;
        return OutlinerCount();
    }

    private string OutlinerCount() =>
        _levelInstance != null && _world?.Map(_levelInstance) is { } map
            ? Inv($"{map.Entities.Count(e => _outlinerFilter.Matches(map, e))} of {map.Entities.Length} match")
            : "No level shown";

    private string FlagCmd(CommandArgs a, EntityFlag flag)
    {
        var key = a.Int(0);
        if (LayerOf(_levelInstance) is not { } layer || !_doc!.Map(layer).TryGetEntityAt(key, out _))
        {
            return Inv($"The shown level has no entity {key}");
        }

        var value = a.OptionalWord(1) is "off" ? FlagValue.Off : FlagValue.On;
        _doc.Outliner.Set(layer, [key], flag, value);
        return Inv($"@{key}: {flag} {value}");
    }
}
