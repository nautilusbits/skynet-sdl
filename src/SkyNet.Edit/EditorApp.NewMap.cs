using System.Globalization;
using System.Numerics;
using Hexa.NET.ImGui;
using RetroEng.Content;
using RetroEng.Diagnostics;
using SkyNet.Formats;
using SkyNet.Game.Campaign;
using SkyNet.Game.Mods;

namespace SkyNet.Edit;

/// <summary>
/// File > New Map… and New Mission… (editor plan step T7): the wizards over <see cref="NewMaps"/> and
/// <see cref="NewMissions"/>, writing into the save mod, which is then mounted on top. A new map joins the
/// open mission as an instance (its layer edited as any map's); a new mission opens. The commands
/// <c>map.new</c> and <c>mission.new</c> do the same without the windows.
/// <code>
///   New Map:     number (720-799, first free) · outdoor / interior · size · flat / generated ground
///                · height · ground tile · maptype (song) · interior: the map whose walls it copies
///   New Mission: number (20-99) · objective · its maps in play order (each outdoor or interior)
/// </code>
/// </summary>
internal sealed partial class EditorApp
{
    private static readonly string[] Settings = ["outdoor", "interior"];
    private static readonly string[] Starts = ["flat", "generated"];
    private const int MaxMissionMaps = 8;

    /// <summary>Which rows the map form shows: New Map's (with the setting) or the ones every map of a
    /// new mission shares (outdoor and interior alike).</summary>
    private enum MapFormRows { OneMap, EveryMap }

    private bool _showNewMap, _showNewMission;
    private NewMapSpec _newMap = new(ModMaps.FirstNew, MapSetting.Outdoor);
    private int _newMission = ModMissions.FirstNew;
    private string _newObjective = NewMissions.DefaultObjective;
    private readonly List<MapSetting> _newMissionMaps = [MapSetting.Outdoor];
    private string? _newError;

    private void RegisterNewCommands()
    {
        _commands.Add("map.new", "map.new [number] [outdoor|interior] [flat|generated]", a =>
        {
            var taken = TakenMaps();
            var spec = _newMap with
            {
                Number = a.OptionalInt(0) ?? NewMaps.FirstFree(taken),
                Setting = a.OptionalWord(1) == Settings[1] ? MapSetting.Interior : MapSetting.Outdoor,
                Terrain = a.OptionalWord(2) == Starts[1] ? TerrainStart.Generated : TerrainStart.Flat,
            };
            return CreateMap(spec) ?? $"MAP.{spec.Number:D3} made";
        }, "File > New Map: a new map in the save mod, added to the open mission");
        _commands.Add("mission.new", "mission.new [number] [outdoor|interior ...]", a =>
        {
            var settings = Enumerable.Range(1, Math.Max(a.Count - 1, 1))
                .Select(i => a.OptionalWord(i) == Settings[1] ? MapSetting.Interior : MapSetting.Outdoor).ToArray();
            var number = a.OptionalInt(0) ?? NewMissions.FirstFree(TakenMissions());
            return CreateMission(number, settings, _newObjective) ?? $"Mission {number} made";
        }, "File > New Mission: a new mission of new maps in the save mod, opened");
    }

    private void OpenNewMap()
    {
        _newMap = _newMap with { Number = NewMaps.FirstFree(TakenMaps()) };
        (_newError, _showNewMap) = (null, true);
    }

    private void OpenNewMission()
    {
        _newMission = NewMissions.FirstFree(TakenMissions());
        (_newError, _showNewMission) = (null, true);
    }

    private void NewMapWindow()
    {
        if (!_showNewMap)
        {
            return;
        }

        ImGui.SetNextWindowSize(new Vector2(420f, 0f), ImGuiCond.FirstUseEver);
        if (ImGui.Begin("New Map", ref _showNewMap))
        {
            var number = _newMap.Number;
            ImGui.InputInt("MAP number", ref number);
            _newMap = _newMap with { Number = number };
            MapForm(ref _newMap, MapFormRows.OneMap);
            Target();
            if (ImGui.Button("Create"))
            {
                _newError = CreateMap(_newMap);
                _showNewMap = _newError != null;
            }
        }

        ImGui.End();
    }

    private void NewMissionWindow()
    {
        if (!_showNewMission)
        {
            return;
        }

        ImGui.SetNextWindowSize(new Vector2(440f, 0f), ImGuiCond.FirstUseEver);
        if (ImGui.Begin("New Mission", ref _showNewMission))
        {
            ImGui.InputInt("Mission number", ref _newMission);
            ImGui.InputText("Objective", ref _newObjective, 200);
            ImGui.SeparatorText("Maps, in play order (each ends in a doorway to the next; the last in the objective)");
            var first = NewMaps.FirstFree(TakenMaps());
            for (var i = 0; i < _newMissionMaps.Count; i++)
            {
                ImGui.PushID(i);
                var setting = (int)_newMissionMaps[i];
                ImGui.SetNextItemWidth(ImGui.GetFontSize() * 8f);
                if (ImGui.Combo(string.Create(CultureInfo.InvariantCulture, $"MAP.{first + i:D3}"), ref setting, Settings, Settings.Length))
                {
                    _newMissionMaps[i] = (MapSetting)setting;
                }

                ImGui.SameLine();
                if (_newMissionMaps.Count > 1 && ImGui.SmallButton("Remove"))
                {
                    _newMissionMaps.RemoveAt(i);
                }

                ImGui.PopID();
            }

            if (_newMissionMaps.Count < MaxMissionMaps && ImGui.SmallButton("Add map"))
            {
                _newMissionMaps.Add(MapSetting.Outdoor);
            }

            ImGui.SeparatorText("Every map");
            MapForm(ref _newMap, MapFormRows.EveryMap);
            Target();
            if (ImGui.Button("Create"))
            {
                _newError = CreateMission(_newMission, [.. _newMissionMaps], _newObjective);
                _showNewMission = _newError != null;
            }
        }

        ImGui.End();
    }

    // The map choices shared by both windows.
    private void MapForm(ref NewMapSpec spec, MapFormRows rows)
    {
        var setting = rows == MapFormRows.OneMap;
        var s = (int)spec.Setting;
        if (setting && ImGui.Combo("Setting", ref s, Settings, Settings.Length))
        {
            spec = spec with { Setting = (MapSetting)s };
        }

        var (size, terrain, height, tile, mapType, room, seed) = (spec.Size, (int)spec.Terrain, spec.Height, spec.Tile, spec.MapType, spec.Room, spec.Seed);
        if (!setting || spec.Setting == MapSetting.Outdoor)
        {
            ImGui.InputInt("Size (corners a side)", ref size, WldFile.ChunkCells);
            ImGui.Combo("Ground", ref terrain, Starts, Starts.Length);
            ImGui.SliderInt("Height (curve index)", ref height, 0, WldTerrain.HeightCurve.Length - 1);
            ImGui.SliderInt("Ground tile (TEXTURE.302)", ref tile, 0, NewMaps.TileCount - 1);
            if (terrain == (int)TerrainStart.Generated)
            {
                ImGui.InputInt("Seed", ref seed);
            }
        }

        if (!setting || spec.Setting == MapSetting.Interior)
        {
            if (room == 0)
            {
                room = NewMaps.DefaultRoom(_data.Game);
            }

            ImGui.InputInt("Interior walls from MAP", ref room);
        }

        ImGui.SliderInt("Maptype (song)", ref mapType, 0, SkyNet.Game.Audio.MusicTracks.ByMapType.Count - 1);
        ImGui.TextDisabled(SkyNet.Game.Audio.MusicTracks.ForMapType(mapType));
        spec = spec with { Size = size, Terrain = (TerrainStart)terrain, Height = height, Tile = tile, MapType = mapType, Room = room, Seed = seed };
    }

    // Where Create writes, or why it cannot; the last error.
    private void Target()
    {
        ImGui.TextWrapped(_saveMod != null ? $"Written to the mod {_saveMod} ({_data.Game})" : "No save mod: Create asks for one (File > Save As Mod).");
        if (_newError != null)
        {
            ImGui.TextColored(new Vector4(1f, 0.8f, 0.3f, 1f), _newError);
        }
    }

    /// <summary>Makes <paramref name="spec"/> in the save mod and adds it to the open mission; the error, null when made.</summary>
    private string? CreateMap(NewMapSpec spec)
    {
        if (SaveFolderOrAsk() is not { } folder)
        {
            return "No save mod: choose one (File > Save As Mod)";
        }

        if (NewMaps.Refuse(spec, _data, TakenMaps()) is { } refused)
        {
            return refused;
        }

        var written = NewMaps.Write(folder, NewMaps.Build(_data, spec));
        Log.Info("Edit", $"New map: {string.Join(", ", written)} in {_saveMod}");
        Mounted();
        if (_world != null && _arena == null)
        {
            var id = MissionFile.DefaultId(spec.Number);
            EditMission($"New map MAP.{spec.Number:D3}", m => MissionEdits.AddInstance(m, id, spec.Number));
            Show(id);
        }

        return null;
    }

    /// <summary>Makes a mission of new maps (<paramref name="settings"/> in play order) in the save mod and
    /// opens it; the error, null when made.</summary>
    private string? CreateMission(int number, IReadOnlyList<MapSetting> settings, string objective)
    {
        if (SaveFolderOrAsk() is not { } folder)
        {
            return "No save mod: choose one (File > Save As Mod)";
        }

        var maps = TakenMaps();
        var free = Enumerable.Range(ModMaps.FirstNew, ModMaps.LastNew - ModMaps.FirstNew + 1).Where(n => !maps.Contains(n)).Take(settings.Count).ToArray();
        if (free.Length < settings.Count)
        {
            return $"{settings.Count} maps: the range {ModMaps.FirstNew}-{ModMaps.LastNew} has {free.Length} free";
        }

        var spec = new NewMissionSpec(number, [.. settings.Select((s, i) => _newMap with { Number = free[i], Setting = s })], objective);
        if (NewMissions.Refuse(spec, _data, TakenMissions(), maps) is { } refused)
        {
            return refused;
        }

        var written = NewMissions.Write(_data, folder, spec);
        Log.Info("Edit", $"New mission {number}: {string.Join(", ", written)} in {_saveMod}");
        Mounted();
        RequestOpen(new OpenTarget(_data.Game, number));
        return null;
    }

    // The save mod's folder; none: Save As asks for one.
    private string? SaveFolderOrAsk()
    {
        if (SaveFolder() is { } folder)
        {
            return folder;
        }

        AskSaveAs();
        return null;
    }

    // The save mod mounted on top and the block store's mods again: the new files resolve from now on.
    private void Mounted()
    {
        MountOnTop(_saveMod!);
        _blocks?.Mount();
        _missionNames.Clear();
    }

    private SortedSet<int> TakenMaps()
    {
        using var mods = AllMods();
        return NewMaps.Taken(_data, mods);
    }

    private SortedSet<int> TakenMissions()
    {
        using var mods = AllMods();
        return NewMissions.Taken(_data.Game, mods);
    }

    // Every user mod in the folder, on or off: a number one of them has is taken.
    private VirtualFileSystem AllMods()
    {
        var root = new ContentRoot(_modsDir);
        return root.Mount([], root.Discover().Select(m => m.Name).Where(n => !_systemMods.Contains(n, StringComparer.OrdinalIgnoreCase)));
    }
}
