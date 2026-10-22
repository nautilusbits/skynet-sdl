using System.Diagnostics;
using System.Numerics;
using Hexa.NET.ImGui;
using RetroEng.Diagnostics;
using SkyNet.Formats;

namespace SkyNet.Edit;

/// <summary>The Validation panel (View > Validation, editor plan step E10): <see cref="Validator"/>'s rows for
/// the open document, run again a moment after the last edit; a row selects its entity and flies to it.</summary>
internal sealed partial class EditorApp
{
    // How long the document must stay unchanged before the rows are run again.
    private const double ValidationDelay = 0.4;

    private Validator? _validator;
    private GameData? _validatorData;
    private List<ValidationRow> _validation = [];
    private object[] _validatedState = [];
    private long _validationDue = -1;

    /// <summary>The document as it is now, by reference: the document, its mission and each layer's edits
    /// (all immutable values a command replaces), so any edit, undo or redo changes it.</summary>
    private object[] ValidationState() => _doc == null ? []
        : [_doc, (object?)_doc.Mission ?? _doc, .. _doc.Layers.Select(l => (object)_doc.Layer(l))];

    private void ValidationPanel(Vector2 at, Vector2 size)
    {
        PanelAt(at, size);
        if (BeginPanel(EditorPanel.Validation))
        {
            RefreshValidation();
            if (_doc == null)
            {
                ImGui.TextDisabled("Open a mission to validate it.");
            }
            else
            {
                ValidationRows();
            }
        }

        ImGui.End();
    }

    private void RefreshValidation()
    {
        var state = ValidationState();
        var now = Stopwatch.GetTimestamp();
        if (!state.SequenceEqual(_validatedState, ReferenceEqualityComparer.Instance))
        {
            // Changed: wait for a quiet moment (a drag changes the layer every frame).
            _validatedState = state;
            _validationDue = now + (long)(ValidationDelay * Stopwatch.Frequency);
            if (_validation.Count == 0 && _doc != null)
            {
                _validationDue = now;
            }
        }

        if (_validationDue < 0 || now < _validationDue || _doc == null)
        {
            return;
        }

        _validationDue = -1;
        if (_validator == null || !ReferenceEquals(_validatorData, _data))
        {
            (_validator, _validatorData) = (new Validator(_data), _data);
        }

        try
        {
            _validation = _validator.Run(_doc);
        }
        catch (Exception e) when (e is IOException or InvalidDataException or KeyNotFoundException or InvalidOperationException)
        {
            Log.Warn("Edit", $"Validation: {e.Message}");
            _validation = [];
        }
    }

    private void ValidationRows()
    {
        var errors = _validation.Count(r => r.Level == ValidationLevel.Error);
        var warnings = _validation.Count(r => r.Level == ValidationLevel.Warning);
        var chains = _validation.Count(r => r.Code == "graph");
        ImGui.TextUnformatted(FormattableString.Invariant($"{errors} errors, {warnings} warnings; this edit changes {chains} trigger chains"));
        ImGui.Separator();
        if (!ImGui.BeginChild("rows"))
        {
            ImGui.EndChild();
            return;
        }

        for (var i = 0; i < _validation.Count; i++)
        {
            var r = _validation[i];
            var colour = r.Level switch
            {
                ValidationLevel.Error => new Vector4(1f, 0.4f, 0.35f, 1f),
                ValidationLevel.Warning => new Vector4(1f, 0.8f, 0.3f, 1f),
                _ => new Vector4(0.6f, 0.8f, 1f, 1f),
            };
            ImGui.PushStyleColor(ImGuiCol.Text, colour);
            var label = FormattableString.Invariant($"{r.Instance}{(r.Entity >= 0 ? FormattableString.Invariant($" @{r.Entity}") : "")}  {r.Text}##v{i}");
            if (ImGui.Selectable(label, _selected == r.Item))
            {
                Select(r.Item);
                if (r.Entity >= 0)
                {
                    FlyTo(r.Item);
                }
                else if (r.Instance != _levelInstance && _world?.World.Instance(r.Instance) != null)
                {
                    Show(r.Instance);
                }
            }

            ImGui.PopStyleColor();
        }

        ImGui.EndChild();
    }
}
