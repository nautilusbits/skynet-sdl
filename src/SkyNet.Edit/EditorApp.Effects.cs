using System.Numerics;
using Hexa.NET.ImGui;
using SkyNet.Data;
using SkyNet.Game.Enhanced;

namespace SkyNet.Edit;

/// <summary>View > Effect editor (editor-tools plan, T5): the <c>effects</c> table's rows previewed and edited
/// (<see cref="EffectsWindow"/>) on the Tables window's grid, so both show one set of edits and one Save.</summary>
internal sealed partial class EditorApp
{
    // The Effects window's first size at most.
    private const float EffectsWidth = 640f, EffectsHeight = 600f, EffectsMinWidth = 400f;
    private EffectsWindow? _effectsWindow;
    private EffectStage? _effectStage;
    private bool _showEffectsWindow;

    // The window over the Tables window's effects grid; its effect.* commands registered.
    private void EffectsFor(TablesWindow tables)
    {
        _effectStage = new EffectStage(_renderer);
        _effectsWindow = new EffectsWindow(() => tables.Grid(TableNames.Effects), () => EditHistory) { Stage = _effectStage, Reads = EffectTable.Reads };
        _effectsWindow.Shown += () => _showEffectsWindow = true;
        _effectsWindow.Saved += () =>
        {
            if (_saveMod != null)
            {
                MountOnTop(_saveMod);
            }
        };
        _effectsWindow.Register(_commands);
    }

    private void EffectsFrame(Vector2 at, float width, float height)
    {
        if (!_showEffectsWindow || _effectsWindow == null)
        {
            return;
        }

        var main = ImGui.GetMainViewport();
        ImGui.SetNextWindowPos(main.Pos + at, ImGuiCond.FirstUseEver);
        ImGui.SetNextWindowSize(new Vector2(Math.Clamp(width, EffectsMinWidth, EffectsWidth), MathF.Min(height, EffectsHeight)), ImGuiCond.FirstUseEver);
        _effectsWindow.Draw(ref _showEffectsWindow);
    }

    // The preview into the frame (before Present): the window's own view target.
    private void EffectsRender()
    {
        if (_showEffectsWindow)
        {
            _effectStage?.Render();
        }
    }
}
