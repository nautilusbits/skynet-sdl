using System.ComponentModel;
using System.Diagnostics;
using RetroEng.Diagnostics;
using SkyNet.Game.Campaign;
using SkyNet.Game.DevTools;

namespace SkyNet.Edit;

/// <summary>Play > Play-test in game window (Ctrl+F5, Ctrl+Shift+F5 from the camera): the game exe in a
/// second window on the edited map; while it runs, the same key writes the edits again and the game
/// reloads its map over the editor link, the soldier kept where he stands.</summary>
internal sealed partial class EditorApp
{
    private Process? _game;
    private EditorLinkHost? _link;

    private bool GameRunning => _game is { HasExited: false };

    /// <summary>Writes the edits to the play-test mod, then starts the game on the shown instance's map, or
    /// asks the running one to reload. False when nothing was started or sent.</summary>
    public bool PlayTestInGame(bool fromCamera)
    {
        if (_doc?.Mission is not { } mission || _levelInstance == null || mission.World.Instance(_levelInstance) is not { } instance)
        {
            Log.Warn("Play-test", "Open a mission's level first");
            return false;
        }

        var exe = Path.Combine(AppContext.BaseDirectory, PlayTest.GameExe);
        if (!GameRunning && !File.Exists(exe))
        {
            Log.Error("Play-test", $"{PlayTest.GameExe} is not beside the editor ({AppContext.BaseDirectory}): it cannot play-test");
            return false;
        }

        try
        {
            PlayTest.Write(_doc, _modsDir);
            WriteTerrainCopies(PlayTest.Folder(_modsDir));
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            Log.Error("Play-test", $"{PlayTest.Folder(_modsDir)} cannot be written: {e.Message}");
            return false;
        }

        _link ??= new EditorLinkHost();
        PlayTestFrame();
        if (GameRunning && _link.SendReload())
        {
            Log.Info("Play-test", "Edits written: the game reloads its map");
            return true;
        }

        if (GameRunning)
        {
            // Started but not linked yet (or no longer): the old window goes, a new one starts.
            StopGame();
        }

        var map = MissionFile.LayerMap(instance);
        var args = PlayTest.Arguments(_data.Dir, _modsDir, SkyNet.Game.Mods.ModContent.Enabled.Except(_systemMods, StringComparer.OrdinalIgnoreCase),
            map, _link.Port, fromCamera ? _eye : null, fromCamera ? _yaw * 180f / MathF.PI : null);
        if (_options.Offscreen)
        {
            args.Add("--offscreen");
        }

        var start = new ProcessStartInfo(exe) { UseShellExecute = false, WorkingDirectory = AppContext.BaseDirectory };
        foreach (var a in args)
        {
            start.ArgumentList.Add(a);
        }

        try
        {
            _game = Process.Start(start);
        }
        catch (Exception e) when (e is Win32Exception or InvalidOperationException)
        {
            Log.Error("Play-test", $"{PlayTest.GameExe} does not start: {e.Message}");
            return false;
        }

        Log.Info("Play-test", $"{PlayTest.GameExe} {string.Join(' ', args)}");
        return _game != null;
    }

    // Once a frame: the game's answers to the log.
    private void PlayTestFrame()
    {
        foreach (var line in _link?.Poll() ?? [])
        {
            Log.Info("Play-test", "Game: " + line);
        }
    }

    private void StopGame()
    {
        try
        {
            if (GameRunning)
            {
                _game!.Kill();
                _game.WaitForExit(2000);
            }
        }
        catch (Exception e) when (e is Win32Exception or InvalidOperationException)
        {
            Log.Warn("Play-test", $"The game window does not close: {e.Message}");
        }

        _game?.Dispose();
        _game = null;
    }

    // On exit: the game window closed, the link shut, the play-test mod removed.
    private void EndPlayTest()
    {
        StopGame();
        _link?.Dispose();
        _link = null;
        try
        {
            PlayTest.Delete(_modsDir);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            Log.Warn("Play-test", $"{PlayTest.Folder(_modsDir)} is not removed: {e.Message}");
        }
    }
}
