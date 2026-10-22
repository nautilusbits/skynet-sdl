using RetroEng.Diagnostics;
using SkyNet.Game.DevTools;

// SkyNetEdit's play-test (--editor-link PORT, docs/plans/editor-vr-menus.md section 5): the editor writes
// the edited layers to its play-test mod again and asks for a reload; the map in play is rebuilt from
// them with the soldier where he stands.
internal sealed partial class AppHost
{
    private EditorLinkClient? editorLink;

    // Once a frame: the editor's reload requests (several since the last frame are one reload).
    void EditorLinkFrame()
    {
        if (editorLink == null && options.EditorLink is { } port)
        {
            editorLink = new EditorLinkClient(port);
            Log.Info("Edit", $"Play-test: linked to the editor on 127.0.0.1:{port}");
        }

        if (editorLink == null || editorLink.PollReloads() == 0)
        {
            return;
        }

        var map = FormattableString.Invariant($"MAP.{session.MapNumber:D3}");
        if (level == null || dm != null)
        {
            editorLink.Send($"{EditorLink.Failed} no map in play");
            return;
        }

        // The mods mounted again (the play-test mod's files changed), then the map entered afresh.
        SkyNet.Game.Mods.ModContent.Mount(modsDir, modsMounted, tables: false);
        var ok = session.ReloadMap();
        Log.Info("Edit", ok ? $"Play-test: {map} reloaded from the editor" : $"Play-test: {map} does not reload");
        editorLink.Send(ok ? $"{EditorLink.Reloaded} {map}" : $"{EditorLink.Failed} {map}");
    }
}
