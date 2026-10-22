namespace SkyNet.Edit;

/// <summary>
/// Help > Keys (F1) and the Keys part of --help: one row per key, as the handlers take them
/// (Shortcuts, PlaceKeys, ClipboardKeys, PlayKeys, TerrainKeys, GizmoKeys, Fly, the trigger graph canvas,
/// the viewers). EditorKeysTests checks it against the source both ways.
/// </summary>
internal static class EditorKeys
{
    private const int KeyColumn = 26;

    private static readonly (string Keys, string What)[] Rows =
    [
        ("Ctrl+O", "open a mission"),
        ("Ctrl+S", "save to the folder mod (File > Save As Mod picks one)"),
        ("Ctrl+Z", "undo"),
        ("Ctrl+Y / Ctrl+Shift+Z", "redo"),
        ("F1", "these keys"),
        ("F11", "fullscreen"),
        ("F5 / Shift+F5", "preview the shown level (Shift: from the camera)"),
        ("Ctrl+F5 / Ctrl+Shift+F5", "play-test in the game window (Shift: from the camera)"),
        ("F6", "fire the selected trigger"),
        ("Space", "play / pause the focused viewer (sound, music, sprite, model, robot, effect)"),
        ("", ""),
        ("", "Viewport (hovered or focused, no text field edited):"),
        ("Right mouse", "held: look"),
        ("W / A / S / D", "right mouse held: fly forward / left / back / right"),
        ("Q / E", "right mouse held: fly down / up"),
        ("Shift", "right mouse held: fly faster"),
        ("Left click", "select the entity under the pointer"),
        ("Ctrl+click", "add to the selection or take out (Terrain mode: pick)"),
        ("W / E", "move / rotate gizmo"),
        ("L", "gizmo local / world axes"),
        ("Left drag", "a handle: move or turn the selection (one undo step)"),
        ("Alt+drag", "duplicate the selection and drag the copies"),
        ("End", "drop the selection to the floor"),
        ("Del", "delete the selection"),
        ("Ctrl+D", "duplicate the selection"),
        ("Ctrl+C / Ctrl+X", "copy / cut the selection"),
        ("Ctrl+V / Ctrl+Shift+V", "paste at the pointer / in place"),
        ("Esc", "stop placing, link picking (Triggers panel, graph Pick) or an asset drag"),
        ("Home", "camera back to the player start"),
        ("Shift+2 / Shift+1", "Terrain mode on / off (back to selecting)"),
        ("", ""),
        ("", "Terrain mode:"),
        ("Left drag", "paint the tool (one undo step a stroke)"),
        ("Shift+drag", "invert: Raise lowers, Lower raises"),
        ("Ctrl+click", "pick: Flatten's height, Paint's tile"),
        ("[ / ]", "brush radius smaller / larger"),
        ("", ""),
        ("", "Triggers graph (focused):"),
        ("Left drag", "move nodes; on empty canvas a box selects (Shift adds)"),
        ("Shift+click / Ctrl+click", "add a node / toggle it"),
        ("Alt+click", "on a link: cut it"),
        ("Del", "delete the selection"),
        ("F", "frame the selection"),
        ("Home", "fit all"),
        ("Right drag / Middle drag", "pan (right click: add a node)"),
        ("Wheel", "zoom"),
        ("Left click after Pick", "an entity field (next): the entity clicked in the view sets it (Esc cancels)"),
        ("Outliner row drag", "dropped on an entity field: sets it"),
        ("", ""),
        ("", "Outliner: double-click a row flies there. Model and effect viewers: drag turns, wheel zooms, double-click resets."),
    ];

    /// <summary>The rows as text: key column, then what it does; a row without keys is a heading.</summary>
    public static string Text => string.Join('\n', Rows.Select(r => r.Keys.Length == 0 ? r.What : $"  {r.Keys.PadRight(KeyColumn)}{r.What}"));
}
