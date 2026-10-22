using System.Globalization;
using System.Numerics;
using Hexa.NET.ImGui;
using RetroEng.Diagnostics;
using RetroEng.Editor;
using RetroEng.Render;
using RetroEng.World;
using SkyNet.Formats;
using SkyNet.Game.Behaviour;
using SkyNet.Game.Campaign;
using SkyNet.Game.Mods;

namespace SkyNet.Edit;

/// <summary>The Triggers and Mission panels and the 3D link lines (editor plan step E8): the selected
/// record's link (act by the handler table's name, next, destroy type and parameter, state), the chain
/// it is in, its exit link (target instance, arrival set); the mission's instances, groups, start and
/// game properties. Every change is one undo step, written by File > Save.</summary>
internal sealed partial class EditorApp
{
    private static readonly string[] VehicleNames = ["-", "foot", "jeep", "hk"];
    private static readonly LinkFamily[] Families = Enum.GetValues<LinkFamily>();
    private static readonly Vector4 ChainColour = new(1f, 1f, 1f, 1f);

    private readonly MeshHandle _linkLines;
    private readonly List<Vector3> _linkPoints = [];
    private readonly List<Vertex> _linkVertices = [];
    private readonly List<(int Start, int Count, Vector4 Colour)> _linkRanges = [];
    private (MapFile? Map, OutlinerItem? Selected) _linksOf;
    private bool _showLinks = true, _linksStale = true;
    // Pick in view: the next click in the view sets this record's next link.
    private (string Layer, int Key)? _pickNext;
    private string _actFilter = "";
    private string _newInstanceId = "";
    private int _newInstanceMap = 210;

    // ---- the link lines ----

    /// <summary>The shown level's links as arrows, coloured by the act family of the record they leave;
    /// the selected record's chain once more in white.</summary>
    private void DrawLinks()
    {
        if (!_showLinks || _level == null || _levelInstance == null)
        {
            return;
        }

        var map = LayerOf(_levelInstance) is { } layer ? _doc!.Map(layer) : _level.Map;
        if (_linksStale || !ReferenceEquals(_linksOf.Map, map) || _linksOf.Selected != _selected)
        {
            _linksStale = false;
            _linksOf = (map, _selected);
            BuildLinks(map);
        }

        foreach (var (start, count, colour) in _linkRanges)
        {
            _renderer.DrawLines(_linkLines, start, count, Material.Flat(colour, SelectionOutline.State) with { Unfogged = true });
        }
    }

    private void BuildLinks(MapFile map)
    {
        _linkVertices.Clear();
        _linkRanges.Clear();
        var links = TriggerChains.Links(map).ToList();
        foreach (var family in Families)
        {
            _linkPoints.Clear();
            foreach (var (from, to, f) in links)
            {
                if (f == family)
                {
                    TriggerChains.Arrow(_linkPoints, Centre(map, from), Centre(map, to));
                }
            }

            AddRange(TriggerChains.Colour(family));
        }

        if (_selected is { IsInstance: false } item && item.Instance == _levelInstance)
        {
            _linkPoints.Clear();
            var chain = TriggerChains.Chain(map, item.Entity, out _);
            for (var i = 0; i + 1 < chain.Count; i++)
            {
                TriggerChains.Arrow(_linkPoints, Centre(map, chain[i]) + Vector3.UnitY * 4f, Centre(map, chain[i + 1]) + Vector3.UnitY * 4f, 64f);
            }

            AddRange(ChainColour);
        }

        if (_linkVertices.Count > 0)
        {
            _renderer.UpdateMesh(_linkLines, System.Runtime.InteropServices.CollectionsMarshal.AsSpan(_linkVertices));
        }
    }

    private void AddRange(Vector4 colour)
    {
        if (_linkPoints.Count == 0)
        {
            return;
        }

        var start = _linkVertices.Count;
        foreach (var p in _linkPoints)
        {
            _linkVertices.Add(new Vertex(p, Vector2.Zero, 0xFFFFFFFFu));
        }

        _linkRanges.Add((start, _linkPoints.Count, colour));
    }

    /// <summary>Where a link line meets an entity: the middle of its pick box, else its position.</summary>
    private Vector3 Centre(MapFile map, int key)
    {
        if (_picking != null && _picking.TryGet(key, out var target))
        {
            return (target.Min + target.Max) * 0.5f;
        }

        return map.TryGetEntityAt(key, out var e) ? BehaviourModel.Position(e) : Vector3.Zero;
    }

    // ---- the link commands ----

    private void SetNext(string layer, int key, int next)
    {
        _doc!.EndDrag();
        Try(() => _doc.EditLink(layer, key, next: next));
        Log.Info("Edit", string.Create(CultureInfo.InvariantCulture, $"{layer} @{key}: next {next}"));
    }

    /// <summary>--select: entity <paramref name="key"/> of the shown level selected, the camera before it.</summary>
    private bool SelectAndFly(int key)
    {
        if (_levelInstance == null || _world?.Map(_levelInstance) is not { } map || !map.TryGetEntityAt(key, out _))
        {
            Log.Error("Edit", $"--select: the shown level has no entity {key}");
            return false;
        }

        Select(new OutlinerItem(_levelInstance, key));
        FlyTo(new OutlinerItem(_levelInstance, key));
        return true;
    }

    // ---- the Triggers panel ----

    private void TriggersPanel(Vector2 at, Vector2 size)
    {
        PanelAt(at, size);
        if (BeginPanel(EditorPanel.Triggers))
        {
            if (!SelectedEntity(out var layer, out var e))
            {
                ImGui.TextDisabled("Select an entity of the shown level: its link, chain and exit show here.");
            }
            else
            {
                LinkFields(layer, e);
                ImGui.Separator();
                ChainList(layer, e);
                ImGui.Separator();
                ExitFields(e);
            }
        }

        ImGui.End();
    }

    private void LinkFields(string layer, MapEntity e)
    {
        var map = _doc!.Map(layer);
        ImGui.TextUnformatted(EditorWorld.Label(map, e));
        var marker = e.MarkerType >= 0;
        var mesh = e.Variant == MapEntityVariant.Mesh;
        if (mesh && e.Link == null)
        {
            ImGui.TextDisabled("No link record.");
            ImGui.SameLine();
            if (ImGui.Button("Add link record"))
            {
                _doc.EndDrag();
                Try(() => _doc.EditLink(layer, e.FileOffset, act: e.ActType, next: e.LinkNext));
            }

            return;
        }

        // The act: a marker's act byte is its robot type (the Place panel's), so it is not offered here.
        if (marker)
        {
            ImGui.TextDisabled("A marker's act byte is its robot type; its next link leads its path.");
        }
        else
        {
            ImGui.SetNextItemWidth(-1f);
            if (ImGui.BeginCombo("##act", "act " + TriggerChains.ActName(e.ActType), ImGuiComboFlags.HeightLarge))
            {
                ImGui.SetNextItemWidth(-1f);
                ImGui.InputTextWithHint("##actFilter", "filter", ref _actFilter, 32);
                foreach (var act in TriggerChains.KnownActs)
                {
                    var name = TriggerChains.ActName(act);
                    if (_actFilter.Length > 0 && !name.Contains(_actFilter, StringComparison.OrdinalIgnoreCase))
                    {
                        continue;
                    }

                    if (ImGui.Selectable(name, act == e.ActType) && act != e.ActType)
                    {
                        _doc.EndDrag();
                        Try(() => _doc.EditLink(layer, e.FileOffset, act: act));
                    }
                }

                ImGui.EndCombo();
            }
        }

        var next = e.LinkNext;
        ImGui.SetNextItemWidth(ImGui.GetFontSize() * 7f);
        if (IntField("next", ref next) && next != e.LinkNext)
        {
            SetNext(layer, e.FileOffset, Math.Max(0, next));
        }

        ImGui.SameLine();
        var picking = _pickNext is { } p && p.Layer == layer && p.Key == e.FileOffset;
        if (ImGui.Button(picking ? "Click an entity... (Esc)" : "Pick in view"))
        {
            _pickNext = picking ? null : (layer, e.FileOffset);
            (_armed, _drag) = (null, null);
        }

        ImGui.SameLine();
        ImGui.BeginDisabled(e.LinkNext == 0);
        if (ImGui.Button("Clear"))
        {
            SetNext(layer, e.FileOffset, 0);
        }

        ImGui.EndDisabled();
        if (e.LinkNext > 0 && !map.TryGetEntityAt(e.LinkNext, out _))
        {
            ImGui.TextColored(new Vector4(1f, 0.8f, 0.3f, 1f), "next leads to no entity of the map");
        }

        if (mesh)
        {
            var link = e.Link!.Value;
            var destroyType = link.DestroyType;
            var destroyParam = link.DestroyParam;
            ImGui.SetNextItemWidth(ImGui.GetFontSize() * 7f);
            if (IntField("destroy type", ref destroyType) && destroyType != link.DestroyType)
            {
                _doc.EndDrag();
                Try(() => _doc.EditLink(layer, e.FileOffset, destroyType: destroyType));
            }

            ImGui.SetNextItemWidth(ImGui.GetFontSize() * 7f);
            if (IntField("destroy param", ref destroyParam) && destroyParam != link.DestroyParam)
            {
                _doc.EndDrag();
                Try(() => _doc.EditLink(layer, e.FileOffset, destroyParam: destroyParam));
            }
        }

        var state = e.StateByte;
        ImGui.SetNextItemWidth(ImGui.GetFontSize() * 7f);
        if (IntField("state", ref state) && state != e.StateByte)
        {
            _doc.EndDrag();
            Try(() => _doc.SetField(layer, e.FileOffset, "state", state));
        }

        ImGui.SameLine();
        ImGui.TextDisabled(StateText(e.StateByte));
    }

    /// <summary>The state byte's bits in words: armed (bit 0), shot (2), death (4), wall button (8), walk-on pad (0x10).</summary>
    private static string StateText(int state)
    {
        List<string> bits = [(state & 1) != 0 ? "armed" : "off"];
        if ((state & 2) != 0)
        {
            bits.Add("shot");
        }

        if ((state & 4) != 0)
        {
            bits.Add("death");
        }

        if ((state & ActRules.WallButtonBit) != 0)
        {
            bits.Add("button");
        }

        if ((state & ActRules.PadBit) != 0)
        {
            bits.Add("pad");
        }

        return string.Join(", ", bits);
    }

    private void ChainList(string layer, MapEntity e)
    {
        var map = _doc!.Map(layer);
        var chain = TriggerChains.Chain(map, e.FileOffset, out var at);
        var incoming = TriggerChains.Incoming(map, e.FileOffset);
        ImGui.TextUnformatted(chain.Count <= 1 ? "Chain: not linked" : string.Create(CultureInfo.InvariantCulture, $"Chain: {chain.Count} records"));
        if (incoming.Count > 1)
        {
            ImGui.SameLine();
            ImGui.TextDisabled(string.Create(CultureInfo.InvariantCulture, $"({incoming.Count} lead in; the first is shown)"));
        }

        if (chain.Count <= 1)
        {
            return;
        }

        var last = map.TryGetEntityAt(chain[^1], out var tail) && TriggerChains.NextOf(map, tail) >= 0;
        if (ImGui.BeginChild("chain", new Vector2(0f, ImGui.GetTextLineHeightWithSpacing() * Math.Min(chain.Count + 1, 9)), ImGuiChildFlags.Borders))
        {
            for (var i = 0; i < chain.Count; i++)
            {
                if (!map.TryGetEntityAt(chain[i], out var c))
                {
                    continue;
                }

                var label = (i == 0 ? "   " : "-> ") + TriggerChains.Label(map, c) + "###chain" + chain[i].ToString(CultureInfo.InvariantCulture);
                if (ImGui.Selectable(label, i == at) && i != at)
                {
                    Select(new OutlinerItem(_levelInstance!, chain[i]));
                }
            }

            if (last)
            {
                ImGui.TextDisabled("-> (back into the chain: a loop)");
            }
        }

        ImGui.EndChild();
    }

    private void ExitFields(MapEntity e)
    {
        if (_world == null || _levelInstance == null)
        {
            return;
        }

        var link = _world.World.Link(_levelInstance, e.FileOffset);
        var exitAct = e.MarkerType < 0 && e.ActType == ActRules.Exit;
        if (link == null && !exitAct)
        {
            ImGui.TextDisabled("Not an exit (act 0xF0).");
            return;
        }

        var editable = _doc?.MissionPath != null;
        ImGui.TextUnformatted(link == null ? string.Create(CultureInfo.InvariantCulture, $"Exit: no mission link (the record's own MAP.{e.Param16:D3}, set {e.Param8})") : "Exit");
        ImGui.BeginDisabled(!editable);
        if (link == null)
        {
            if (ImGui.Button("Add exit link"))
            {
                var to = _world.World.Instances.FirstOrDefault(i => MissionFile.LayerMap(i) == e.Param16)?.Id ?? _world.World.Instances[0].Id;
                EditMission("Add exit", m => MissionEdits.SetExit(m, _levelInstance, e.FileOffset, to, false, e.Param8));
            }
        }
        else
        {
            var target = link.Back ? "(back)" : link.To ?? "(closed)";
            ImGui.SetNextItemWidth(ImGui.GetFontSize() * 10f);
            if (ImGui.BeginCombo("to", target))
            {
                foreach (var choice in new[] { "(back)", "(closed)" }.Concat(_world.World.Instances.Select(i => i.Id)))
                {
                    if (ImGui.Selectable(choice, choice == target) && choice != target)
                    {
                        var back = choice == "(back)";
                        var to = back || choice == "(closed)" ? null : choice;
                        EditMission("Exit target", m => MissionEdits.SetExit(m, link.From, link.Source, to, back, link.Arrival));
                    }
                }

                ImGui.EndCombo();
            }

            var arrival = link.Arrival;
            ImGui.SetNextItemWidth(ImGui.GetFontSize() * 7f);
            if (IntField("arrival set", ref arrival) && arrival != link.Arrival)
            {
                EditMission("Exit arrival", m => MissionEdits.SetExit(m, link.From, link.Source, link.To, link.Back, arrival));
            }

            if (ImGui.Button("Remove exit link"))
            {
                EditMission("Remove exit", m => MissionEdits.RemoveExit(m, link.From, link.Source));
            }
        }

        ImGui.EndDisabled();
    }

    // ---- the Mission panel ----

    /// <summary>One mission command (a <see cref="MissionEdits"/> step), its new instances' layers added
    /// first; a result that is not a valid mission is logged, not applied.</summary>
    private void EditMission(string label, Func<MissionFile, MissionFile> edit)
    {
        if (_doc?.Mission is not { } m || _doc.MissionPath == null)
        {
            return;
        }

        try
        {
            var after = edit(m);
            foreach (var i in after.World.Instances)
            {
                _doc.EnsureLayer(_data, i, ModContent.Files);
            }

            _doc.EndDrag();
            _doc.SetMission(after, label);
            Log.Info("Edit", "Mission: " + label);
        }
        catch (WorldFormatException ex)
        {
            Log.Warn("Edit", "Mission not changed: " + ex.Message);
        }
    }

    // The mission changed (a command or its undo): the outliner's world follows; a shown instance that
    // is gone gives way to the start.
    private void MissionEdited()
    {
        if (_doc?.Mission is not { } m)
        {
            return;
        }

        _world = new EditorWorld(m, EditedMap);
        _linksStale = true;
        if (_selected is { } s && m.World.Instance(s.Instance) == null)
        {
            _selected = new OutlinerItem(m.World.Start.Instance);
        }

        if (_levelInstance != null && m.World.Instance(_levelInstance) == null)
        {
            Show(m.World.Start.Instance);
        }
    }

    private void MissionPanel(Vector2 at, Vector2 size)
    {
        PanelAt(at, size);
        if (BeginPanel(EditorPanel.Mission))
        {
            if (_doc?.Mission is not { } m)
            {
                ImGui.TextDisabled("Open a mission.");
            }
            else if (_doc.MissionPath == null)
            {
                ImGui.TextDisabled("A deathmatch arena has no mission file.");
            }
            else
            {
                MissionFields(m);
            }
        }

        ImGui.End();
    }

    private void MissionFields(MissionFile m)
    {
        ImGui.TextUnformatted(string.Create(CultureInfo.InvariantCulture, $"Mission {m.Number} ({m.Game}), saved as {_doc!.MissionPath}"));
        var mode = Array.IndexOf(VehicleNames, MissionFile.ModeName(m.Mode)) - 1;
        ImGui.SetNextItemWidth(ImGui.GetFontSize() * 7f);
        if (ImGui.Combo("vehicle (mission table)", ref mode, "foot\0jeep\0hk\0") && VehicleOf(mode + 1) is { } newMode && newMode != m.Mode)
        {
            EditMission("Mission vehicle", x => MissionEdits.SetMode(x, newMode));
        }

        var start = m.World.Start;
        ImGui.SetNextItemWidth(ImGui.GetFontSize() * 8f);
        if (ImGui.BeginCombo("start", start.Instance))
        {
            foreach (var i in m.World.Instances)
            {
                if (ImGui.Selectable(i.Id, i.Id == start.Instance) && i.Id != start.Instance)
                {
                    EditMission("Start instance", x => MissionEdits.SetStart(x, i.Id, start.Arrival));
                }
            }

            ImGui.EndCombo();
        }

        ImGui.SameLine();
        var arrival = start.Arrival;
        ImGui.SetNextItemWidth(ImGui.GetFontSize() * 5f);
        if (IntField("arrival", ref arrival) && arrival != start.Arrival)
        {
            EditMission("Start arrival", x => MissionEdits.SetStart(x, start.Instance, arrival));
        }

        const ImGuiTableFlags flags = ImGuiTableFlags.RowBg | ImGuiTableFlags.BordersInnerV | ImGuiTableFlags.Resizable | ImGuiTableFlags.ScrollY;
        var tableHeight = MathF.Max(80f, ImGui.GetContentRegionAvail().Y - ImGui.GetFrameHeightWithSpacing() * 1.2f);
        if (ImGui.BeginTable("instances", 6, flags, new Vector2(0f, tableHeight)))
        {
            ImGui.TableSetupScrollFreeze(0, 1);
            foreach (var h in new[] { "instance", "map", "group", "music", "vehicle", "" })
            {
                ImGui.TableSetupColumn(h);
            }

            ImGui.TableHeadersRow();
            foreach (var i in m.World.Instances)
            {
                InstanceRow(m, i);
            }

            ImGui.EndTable();
        }

        ImGui.SetNextItemWidth(ImGui.GetFontSize() * 6f);
        ImGui.InputTextWithHint("##newId", "new id", ref _newInstanceId, 32);
        ImGui.SameLine();
        ImGui.SetNextItemWidth(ImGui.GetFontSize() * 5f);
        ImGui.InputInt("##newMap", ref _newInstanceMap, 0, 0);
        ImGui.SameLine();
        ImGui.BeginDisabled(_newInstanceId.Trim().Length == 0 || _data.LoadMap(_newInstanceMap) == null);
        if (ImGui.Button("Add instance"))
        {
            var (id, map) = (_newInstanceId.Trim(), _newInstanceMap);
            EditMission("Add instance", x => MissionEdits.AddInstance(x, id, map));
            _newInstanceId = "";
        }

        ImGui.EndDisabled();
    }

    private void InstanceRow(MissionFile m, WorldInstance i)
    {
        ImGui.PushID(i.Id);
        ImGui.TableNextRow();
        ImGui.TableSetColumnIndex(0);
        if (ImGui.Selectable(i.Id, i.Id == _levelInstance, ImGuiSelectableFlags.SpanAllColumns | ImGuiSelectableFlags.AllowOverlap) && i.Id != _levelInstance)
        {
            _selected = new OutlinerItem(i.Id);
            Show(i.Id);
        }

        ImGui.TableSetColumnIndex(1);
        ImGui.TextUnformatted(i.Layer);
        ImGui.TableSetColumnIndex(2);
        var group = i.Group ?? "";
        ImGui.SetNextItemWidth(-1f);
        if (ImGui.InputText("##group", ref group, 32, ImGuiInputTextFlags.EnterReturnsTrue) && group != (i.Group ?? ""))
        {
            EditMission("Instance group", x => MissionEdits.SetGroup(x, i.Id, group));
        }

        ImGui.TableSetColumnIndex(3);
        var music = MissionFile.Music(i) ?? "";
        ImGui.SetNextItemWidth(-1f);
        if (ImGui.InputText("##music", ref music, 32, ImGuiInputTextFlags.EnterReturnsTrue) && music != (MissionFile.Music(i) ?? ""))
        {
            EditMission("Instance music", x => MissionEdits.SetProperty(x, i.Id, MissionFile.MusicProperty, music));
        }

        ImGui.TableSetColumnIndex(4);
        var vehicle = i.Property(MissionFile.VehicleProperty) is { } v ? Math.Max(0, Array.IndexOf(VehicleNames, v)) : 0;
        ImGui.SetNextItemWidth(-1f);
        if (ImGui.Combo("##vehicle", ref vehicle, "-\0foot\0jeep\0hk\0"))
        {
            var value = vehicle == 0 ? null : VehicleNames[vehicle];
            if (value != i.Property(MissionFile.VehicleProperty))
            {
                EditMission("Instance vehicle", x => MissionEdits.SetProperty(x, i.Id, MissionFile.VehicleProperty, value));
            }
        }

        ImGui.TableSetColumnIndex(5);
        ImGui.BeginDisabled(m.World.Start.Instance == i.Id);
        if (ImGui.SmallButton("x"))
        {
            EditMission("Remove instance", x => MissionEdits.RemoveInstance(x, i.Id));
        }

        ImGui.EndDisabled();
        ImGui.PopID();
    }

    /// <summary>A whole number typed and taken on Enter (ImGui's InputInt has no Enter-only mode); true
    /// with the new <paramref name="value"/> when one was entered.</summary>
    private static bool IntField(string label, ref int value)
    {
        var text = value.ToString(CultureInfo.InvariantCulture);
        if (ImGui.InputText(label, ref text, 16, ImGuiInputTextFlags.EnterReturnsTrue | ImGuiInputTextFlags.CharsDecimal)
            && int.TryParse(text, NumberStyles.Integer, CultureInfo.InvariantCulture, out var typed))
        {
            value = typed;
            return true;
        }

        return false;
    }

    private static PlayerMode? VehicleOf(int index) => index switch
    {
        1 => PlayerMode.Foot,
        2 => PlayerMode.Jeep,
        3 => PlayerMode.Hk,
        _ => null,
    };
}
