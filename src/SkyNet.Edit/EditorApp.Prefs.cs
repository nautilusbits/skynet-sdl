using System.Diagnostics;
using System.Globalization;
using Hexa.NET.ImGui;
using RetroEng.Commands;
using RetroEng.Diagnostics;
using SkyNet.Formats;
using SkyNet.Game.Menu;

namespace SkyNet.Edit;

/// <summary>What the user sets, kept in the preferences: the view's toggles, the gizmo, the Place tab, the
/// Assets filters, the look, the save mod and where the editor was. The editor's own state is copied into
/// the preferences once a frame and written a moment after the last change, and on exit. A value the
/// command line overrode stays out of the file until the user changes it.
/// <code>
///   file ──Load──► EditorPrefs ──ViewFromPrefs──► fields (_showRobots, _gizmoMode, ...)
///                       ▲                                │ menus, keys, panels change them
///                       └──────────CapturePrefs──────────┘ (each frame) ──► Save after PrefsDelay
/// </code></summary>
internal sealed partial class EditorApp
{
    // The pause after the last change before the preferences are written.
    private const double PrefsDelay = 0.5;

    private long _prefsDue;
    // The look and save mod the file holds for this run (the command line's when it set them): only a
    // change from these is written.
    private Look _lookKept;
    private string? _saveModKept;
    // Where the editor is goes to the file: not while the command line chose the mission and camera,
    // until the user opens or shows another.
    private bool _placeKept, _looping;

    /// <summary>The command line names the mission, instance or camera: the place kept is not restored.</summary>
    private bool PlaceOverridden => _options.Mission != null || _options.Arena != null || _options.Instance != null
        || _options.Game != null || _options.Pos != null || _options.Yaw != null || _options.Pitch != null;

    /// <summary>The fields the preferences stand for set from them (start, reset, the pref command).</summary>
    private void ViewFromPrefs()
    {
        var p = _prefs;
        (_showLinks, _showMarkers, _showLights) = (p.ShowLinks, p.ShowMarkers, p.ShowLights);
        (_showSprites, _showRobots, _showEffects) = (p.ShowSprites, p.ShowRobots, p.ShowEffects);
        _gizmoMode = p.Gizmo;
        if (_placeKind != p.PlaceTab)
        {
            (_placeKind, _placeTabForced, _armed) = (p.PlaceTab, true, null);
        }

        AssetsFromPrefs();
    }

    /// <summary>The Assets panel's filters from the preferences (the panel made, or the preferences replaced).</summary>
    private void AssetsFromPrefs()
    {
        if (_assetsPanel?.Browser is not { } b)
        {
            return;
        }

        var p = _prefs;
        if (p.AssetKind != null && b.Kinds.Contains(p.AssetKind))
        {
            b.Kind = p.AssetKind;
        }

        (b.Origin, b.ScopeOnly, b.Filter) = (p.AssetOrigin, p.AssetMapOnly, p.AssetSearch);
    }

    /// <summary>The editor's state into the preferences; a change marks them for writing.</summary>
    private void CapturePrefs()
    {
        var p = _prefs;
        var view = (_showLinks, _showMarkers, _showLights, _showSprites, _showRobots, _showEffects, _gizmoMode, _placeKind);
        if (view != (p.ShowLinks, p.ShowMarkers, p.ShowLights, p.ShowSprites, p.ShowRobots, p.ShowEffects, p.Gizmo, p.PlaceTab))
        {
            (p.ShowLinks, p.ShowMarkers, p.ShowLights, p.ShowSprites, p.ShowRobots, p.ShowEffects, p.Gizmo, p.PlaceTab) = view;
            _prefsChanged = true;
        }

        // View > Look: the look the editor opens in next time.
        if (_settings.Look != _lookKept)
        {
            (p.DefaultLook, _lookKept) = (_settings.Look, _settings.Look);
            _prefsChanged = true;
        }

        if (_saveMod != _saveModKept)
        {
            (p.SaveMod, _saveModKept) = (_saveMod, _saveMod);
            _prefsChanged = true;
        }

        if (_assetsPanel?.Browser is { } b && (b.Kind, b.Origin, b.ScopeOnly, b.Filter) != (p.AssetKind, p.AssetOrigin, p.AssetMapOnly, p.AssetSearch))
        {
            (p.AssetKind, p.AssetOrigin, p.AssetMapOnly, p.AssetSearch) = (b.Kind, b.Origin, b.ScopeOnly, b.Filter);
            _prefsChanged = true;
        }
    }

    /// <summary>Where the editor is (the open mission or arena, the shown instance, the camera) into the
    /// preferences, unless the command line chose it.</summary>
    private void CapturePlace()
    {
        if (!_placeKept || Current is not { } target || _levelInstance == null)
        {
            return;
        }

        var place = new EditorPlace(target.Game, target.Number, target.Arena, _levelInstance, _eye, _yaw, _pitch);
        if (place != _prefs.Place)
        {
            _prefs.Place = place;
            _prefsChanged = true;
        }
    }

    /// <summary>A level shown (not a rebuild where the camera is): once the editor runs it is the user's
    /// choice, kept.</summary>
    private void PlaceShown()
    {
        _placeKept |= _looping;
        CapturePlace();
    }

    /// <summary>Opens where the editor was when it closed (its camera too); mission 1 when nothing is kept
    /// or it does not open.</summary>
    private bool RestorePlace()
    {
        _placeKept = true;
        if (_prefs.Place is not { } place)
        {
            return Open(1);
        }

        // Not kept while it opens: the start camera would replace the one kept.
        _placeKept = false;

        var opened = UseGame(place.Game) && (place.Arena ? OpenArena(place.Number) : Open(place.Number, place.Instance));
        if (!opened)
        {
            _placeKept = true;
            Log.Warn("Edit", $"The last {(place.Arena ? "arena" : "mission")} ({place.Game} {place.Number}) does not open: mission 1");
            return UseGame(GameKind.SkyNet) && Open(1);
        }

        if (place.Eye is { } eye && place.Instance == _levelInstance)
        {
            (_eye, _yaw, _pitch) = (eye, place.Yaw, place.Pitch);
        }

        _placeKept = true;
        return true;
    }

    /// <summary>Writes the preferences a moment after the last change (not while a widget is held).</summary>
    private void SaveWhenDue()
    {
        var now = Stopwatch.GetTimestamp();
        if (_prefsChanged)
        {
            _prefsChanged = false;
            _prefsDue = now + (long)(PrefsDelay * Stopwatch.Frequency);
        }

        if (_prefsDue == 0 || now < _prefsDue || ImGui.IsAnyItemActive())
        {
            return;
        }

        _prefsDue = 0;
        SavePrefs();
    }

    /// <summary>Replaces the preferences (Reset settings, the pref command) and applies them to the view.</summary>
    private void UsePrefs(EditorPrefs next)
    {
        var rebuild = next.ShowRobots != _showRobots || next.ShowEffects != _showEffects || next.DefaultLook != _settings.Look;
        var sprites = next.ShowSprites != _showSprites;
        next.Window = _prefs.Window;
        _prefs = next;
        ViewFromPrefs();
        (_settings.Look, _lookKept) = (next.DefaultLook, next.DefaultLook);
        next.ApplyTo(_settings);
        if (next.SaveMod != null && SaveTarget.Folder(_modsDir, next.SaveMod, _systemMods, out _) != null)
        {
            _saveMod = next.SaveMod;
        }

        _saveModKept = next.SaveMod;
        next.FollowSystemScale(_imgui.SystemScale);
        (_gizmosStale, _linksStale, _prefsChanged) = (true, true, true);
        if (sprites)
        {
            ApplyVisibility();
        }

        if (rebuild)
        {
            Reload();
            return;
        }

        // ENHANCED OPTIONS: no rebuild, as the game's screen.
        ApplyLook();
    }

    /// <summary>Settings > Reset settings: every setting back to its default (the panels' layout is View > Reset layout's).</summary>
    private void ResetSettings()
    {
        UsePrefs(_prefs.Reset());
        Log.Info("Edit", "Settings reset");
    }

    // "pref": lists the preferences, prints one, or sets one as the file names it ("pref showRobots false");
    // "pref.reset": Settings > Reset settings.
    private void RegisterPrefCommands()
    {
        _commands.Add("pref", "pref [name [value]]", a => PrefCommand(a.OptionalWord(0), a.Rest(1)),
            "lists, prints or sets a preference (its name in the preferences file)");
        _commands.Add("pref.reset", "pref.reset", _ =>
        {
            ResetSettings();
            return "Settings reset";
        }, "Settings > Reset settings");
        _commands.Add("view_distance", "view_distance [units|0]", a => ViewDistanceCmd(a.OptionalWord(0)),
            "prints or sets how far the viewport draws, world units (0: the map's own)");
    }

    private string ViewDistanceCmd(string? units)
    {
        if (units != null)
        {
            if (!float.TryParse(units, NumberStyles.Float, CultureInfo.InvariantCulture, out var far) || !float.IsFinite(far))
            {
                throw new CommandUsageException($"view_distance takes world units, not \"{units}\"");
            }

            _prefs.DrawDistance = EditorPrefs.ViewDistance(far);
            _prefsChanged = true;
        }

        return _prefs.DrawDistance > 0f ? Inv($"view_distance = {_prefs.DrawDistance:0}") : "view_distance = 0 (the map's own)";
    }

    private string PrefCommand(string? name, string value)
    {
        if (name == null)
        {
            return System.Text.Encoding.UTF8.GetString(_prefs.ToJson());
        }

        if (value.Length == 0)
        {
            return _prefs.Get(name) is { } now ? $"{name} = {now}" : throw new CommandUsageException($"no preference \"{name}\"");
        }

        if (_prefs.With(name, value, out var error) is not { } next)
        {
            throw new CommandUsageException(error);
        }

        UsePrefs(next);
        return $"{name} = {_prefs.Get(name)}";
    }
}
