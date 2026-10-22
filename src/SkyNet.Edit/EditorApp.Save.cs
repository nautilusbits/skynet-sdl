using System.Numerics;
using Hexa.NET.ImGui;
using RetroEng.Diagnostics;
using RetroEng.Platform;
using SkyNet.Game.Mods;

namespace SkyNet.Edit;

/// <summary>File > Save and Save As Mod (editor plan step E6): the document's layers written to a folder
/// mod, which is then mounted (on top when it was off) so the game and the editor read them from there.</summary>
internal sealed partial class EditorApp
{
    private const string SaveAsPopup = "Save As Mod";

    private string? _saveMod;
    private string _saveAsName = "";
    private string? _saveAsError;
    private bool _askSaveAs;

    /// <summary>Saves to the current mod; without one it asks for a name. True when written.</summary>
    private bool Save()
    {
        if (_tables?.Dirty == true && _saveMod != null)
        {
            Log.Info("Edit", _tables.Save());
        }

        if (_doc == null)
        {
            return SaveRobotsOnly();
        }

        if (_saveMod == null)
        {
            AskSaveAs();
            return false;
        }

        return SaveTo(_saveMod, all: false);
    }

    /// <summary>Writes the document to the folder mod <paramref name="name"/> (with <paramref name="all"/>
    /// every non-empty layer, not only the changed ones) and makes it the mod a Save goes to.</summary>
    public bool SaveTo(string name, bool all)
    {
        name = name.Trim();
        if (_doc == null)
        {
            return false;
        }

        if (SaveTarget.Folder(_modsDir, name, _systemMods, out var error) is not { } folder)
        {
            Log.Error("Edit", "Not saved: " + error);
            return false;
        }

        IReadOnlyList<string> written;
        try
        {
            // The terrains first: the document's save marks the shared history saved.
            written = [.. SaveTerrains(folder), .. _doc.Save(folder, all), .. SaveRobotModels(folder)];
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            Log.Error("Edit", $"Not saved to {folder}: {e.Message}");
            return false;
        }

        _saveMod = name;
        Log.Info("Edit", written.Count == 0 ? $"Saved to {name}: nothing had changed" : $"Saved to {folder}: {string.Join(", ", written)}");
        MountOnTop(name);
        return true;
    }

    /// <summary>The folder mod <paramref name="name"/> mounted again (on top when it was off): what was
    /// written to it now resolves from its files.</summary>
    private void MountOnTop(string name)
    {
        var enabled = ModContent.Enabled.ToList();
        if (!enabled.Contains(name, StringComparer.OrdinalIgnoreCase))
        {
            ModOrder.Set(enabled, name, true);
            if (_options.Mod == null)
            {
                _prefs.Mods = [.. enabled];
                SavePrefs();
            }
        }

        MountMods(enabled);
    }

    // File > Save with no mission open: the unsaved robot models alone.
    private bool SaveRobotsOnly()
    {
        if (!_robotModels.Dirty || SaveFolder() is not { } folder)
        {
            return false;
        }

        try
        {
            Log.Info("Edit", $"Saved to {folder}: {string.Join(", ", SaveRobotModels(folder))}");
            return true;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            Log.Error("Edit", $"Not saved to {folder}: {e.Message}");
            return false;
        }
    }

    private void AskSaveAs()
    {
        (_saveAsName, _saveAsError, _askSaveAs) = (_saveMod ?? "", null, true);
    }

    private void SaveAsModal()
    {
        if (_askSaveAs)
        {
            ImGui.OpenPopup(SaveAsPopup);
            _askSaveAs = false;
        }

        if (!ImGui.BeginPopupModal(SaveAsPopup, ImGuiWindowFlags.AlwaysAutoResize))
        {
            return;
        }

        ImGui.TextUnformatted($"The folder mod the mission's layers go to, in {_modsDir}:");
        var enter = ImGui.InputText("Mod name", ref _saveAsName, 128, ImGuiInputTextFlags.EnterReturnsTrue);
        ImGui.SameLine();
        if (ImGui.Button("Browse...##saveAs"))
        {
            var start = _saveAsName.Trim() is { Length: > 0 } n && Directory.Exists(Path.Combine(_modsDir, n)) ? Path.Combine(_modsDir, n) : _modsDir;
            _dialogs.OpenFolder(_window, start, picked =>
            {
                if (picked != null && SaveTarget.Browsed(_modsDir, picked, _saveAsName, _systemMods, out _saveAsError) is { } name)
                {
                    _saveAsName = name;
                }
            }, "Save as mod: the mods folder or a folder mod in it");
        }

        if (_saveAsError != null)
        {
            ImGui.TextColored(new Vector4(1f, 0.8f, 0.3f, 1f), _saveAsError);
        }

        if (ImGui.Button("Save") | enter)
        {
            if (SaveTarget.Folder(_modsDir, _saveAsName, _systemMods, out _saveAsError) != null && SaveTo(_saveAsName, all: true))
            {
                ImGui.CloseCurrentPopup();
            }
            else
            {
                _saveAsError ??= "Not saved: see the log.";
            }
        }

        ImGui.SameLine();
        if (ImGui.Button("Cancel"))
        {
            ImGui.CloseCurrentPopup();
        }

        ImGui.EndPopup();
    }
}
