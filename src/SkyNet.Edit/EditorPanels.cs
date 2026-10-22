namespace SkyNet.Edit;

/// <summary>The docked panels the View menu ticks (their window titles are their names).</summary>
public enum EditorPanel
{
    Outliner,
    Details,
    Place,
    Triggers,
    Mission,
    Validation,
    Log,
}

/// <summary>Each panel's View-menu tick in the preferences: the panel's X clears it.</summary>
public static class EditorPanels
{
    public static string Title(EditorPanel panel) => panel.ToString();

    public static bool Shown(EditorPrefs p, EditorPanel panel) => panel switch
    {
        EditorPanel.Outliner => p.ShowOutliner,
        EditorPanel.Details => p.ShowProperties,
        EditorPanel.Place => p.ShowPlace,
        EditorPanel.Triggers => p.ShowTriggers,
        EditorPanel.Mission => p.ShowMission,
        EditorPanel.Validation => p.ShowValidation,
        EditorPanel.Log => p.ShowLog,
        _ => false,
    };

    /// <summary>The panel closed: its tick cleared.</summary>
    public static void Hide(EditorPrefs p, EditorPanel panel)
    {
        switch (panel)
        {
            case EditorPanel.Outliner: p.ShowOutliner = false; break;
            case EditorPanel.Details: p.ShowProperties = false; break;
            case EditorPanel.Place: p.ShowPlace = false; break;
            case EditorPanel.Triggers: p.ShowTriggers = false; break;
            case EditorPanel.Mission: p.ShowMission = false; break;
            case EditorPanel.Validation: p.ShowValidation = false; break;
            case EditorPanel.Log: p.ShowLog = false; break;
        }
    }
}
