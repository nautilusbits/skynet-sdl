using System.Diagnostics;
using System.Drawing;
using System.Globalization;
using System.Numerics;
using RetroEng.Audio;
using RetroEng.Commands;
using RetroEng.Diagnostics;
using RetroEng.Loop;
using RetroEng.Physics;
using RetroEng.Platform;
using RetroEng.Render;
using SkyNet.Data;
using SkyNet.Formats;
using SkyNet.Game;
using SkyNet.Game.Audio;
using SkyNet.Game.Baking;
using SkyNet.Game.Campaign;
using SkyNet.Game.Collision;
using SkyNet.Game.Demos;
using SkyNet.Game.DevTools;
using SkyNet.Game.Enhanced;
using SkyNet.Game.Enemies;
using SkyNet.Game.Hud;
using SkyNet.Game.Menu;
using SkyNet.Game.Player;
using SkyNet.Game.Vehicles;
using SkyNet.Game.Weapons;

internal sealed partial class AppHost
{
    // A fresh session of the running game (a new context: nothing of the last game's left in the
    // soldier, the weapons or the robots), so a recording and its playback start alike.
    void FreshSession()
    {
        var pilot = session.Pilot;
        SwitchGame(data.Game);
        session.Pilot = pilot;
    }

    // Starts recording: from the game as it stands (mission null: a save of it, loaded into a fresh
    // session) or from mission N's start (on side map, when given). Null, or why not.
    string? StartRecording(string name, int? mission, int? side)
    {
        if (dm != null)
        {
            return "A network game records with plain 'record <name>' (what the server sends)";
        }

        StopDemos("a new recording");
        var difficulty = settings.Difficulty;
        SaveGame? save = null;
        if (mission == null)
        {
            if (!session.CanSave)
            {
                return "Cannot record from here (record <name> mission <n> starts a mission)";
            }

            save = session.Capture(DateTime.Now);
        }

        FreshSession();
        session.RobotAi = settings.RobotAi;
        var demo = CampaignDemo.Create(session, controls, difficulty, modsDir, blocks != null);
        if (save != null)
        {
            CampaignDemo.SetSaveStart(demo, save);
        }
        else
        {
            weapons.Inventory.Reset();
            CampaignDemo.SetMissionStart(demo, session, mission!.Value, side);
        }

        recording = new CampaignRecorder(demo, CampaignDemo.PathOf(name));
        CampaignDemo.Begin(session, demo);
        if (level == null)
        {
            recording = null;
            return "The demo's map did not start";
        }

        ResumeGame();
        Log.Info("Demo", $"Recording {recording.Path}: {demo.Field(CampaignDemo.KeyStart)} start, mission {demo.Field(CampaignDemo.KeyMission)}, MAP.{demo.Field(CampaignDemo.KeyMap)}");
        return null;
    }

    // Plays a demo (timedemo: one tick a frame, unpaced). Null, or why not.
    string? StartPlayback(string name, bool timedemo)
    {
        if (dm is { Replay: null })
        {
            return "No demos in a network game";
        }

        StopDemos("a new playback");
        var path = CampaignDemo.PathOf(name);
        if (NetDemoFile.IsNetDemo(path))
        {
            return StartNetPlayback(name, path);
        }
        DemoData demo;
        try
        {
            demo = DemoFile.Read(path);
        }
        catch (Exception e) when (e is DemoFormatException or IOException or UnauthorizedAccessException)
        {
            return $"{name}: {e.Message}";
        }

        var kind = Enum.TryParse<GameKind>(demo.Field(CampaignDemo.KeyGame), out var k) ? k : GameKind.None;
        if (kind != data.Game && (kind == GameKind.None || !SwitchGame(kind)))
        {
            return $"{name}: the demo is of {demo.Field(CampaignDemo.KeyGame)}, whose data does not open";
        }

        var warnings = new List<string>();
        if (CampaignDemo.Refusal(demo, data, modsDir, blocks != null, warnings) is { } why)
        {
            return $"{name}: {why}";
        }

        foreach (var w in warnings)
        {
            Log.Warn("Demo", $"{name}: {w} (a desync may follow)");
        }

        FreshSession();
        try
        {
            CampaignDemo.Begin(session, demo);
        }
        catch (Exception e) when (e is SaveFormatException or DemoFormatException or EndOfStreamException)
        {
            return $"{name}: the start does not load ({e.Message})";
        }

        menu.Close();
        playback = new CampaignPlayback(demo, controls);
        playbackName = name;
        demoPaused = false;
        if (timedemo && loop != null)
        {
            loop.OneTickPerFrame = true;
            renderer.VSync = VSync.Off;
            timedemoStart = Stopwatch.GetTimestamp();
            timedemoFrames = 0;
        }

        ApplyDemoClock();
        ResumeGame();
        Log.Info("Demo", string.Create(CultureInfo.InvariantCulture,
            $"Playing {path}: {demo.Ticks} ticks ({demo.Ticks / (double)GameSession.TicksPerSecond:0.0} s), {demo.Field(CampaignDemo.KeyStart)} start, mission {demo.Field(CampaignDemo.KeyMission)}, MAP.{demo.Field(CampaignDemo.KeyMap)}{(timedemo ? ", timed" : "")}"));
        return null;
    }

    // The playback's speed on the loop (several ticks a frame at the fast speeds).
    void ApplyDemoClock()
    {
        if (loop == null)
        {
            return;
        }

        var scale = playback != null ? demoSpeed : 1.0;
        loop.TimeScale = scale;
        loop.MaxStepsPerFrame = Math.Max(8, (int)Math.Ceiling(8 * scale));
    }

    // The demo ran to its end: the result in the log and on the status line (a timedemo's frame rate).
    void FinishPlayback()
    {
        if (playback == null)
        {
            return;
        }

        var p = playback;
        var line = p.Desync is { } d ? $"{playbackName}: {p.Cursor.Tick} ticks, {d}"
            : string.Create(CultureInfo.InvariantCulture, $"{playbackName}: {p.Cursor.Tick} ticks, {p.Cursor.Checked} checks, no desync");
        if (loop is { OneTickPerFrame: true })
        {
            var seconds = Stopwatch.GetElapsedTime(timedemoStart).TotalSeconds;
            line += string.Create(CultureInfo.InvariantCulture,
                $"; timedemo {timedemoFrames} frames in {seconds:0.00} s = {timedemoFrames / Math.Max(seconds, 1e-6):0.0} fps ({p.Cursor.Tick / Math.Max(seconds, 1e-6):0} ticks/s)");
        }

        Log.Info("Demo", line);
        hud.ShowStatus(p.Desync != null ? "DEMO DESYNC" : "DEMO OVER");
        StopDemos(null);
        if (timedemoQuits)
        {
            quitting = true;
        }
    }

    // Ends a recording (written to its file) and a playback. The line for the console, null when
    // neither ran.
    string? StopDemos(string? why)
    {
        string? said = null;
        if (dm?.StopRecording() is { } netRecording)
        {
            try
            {
                var path = netRecording.Save();
                said = string.Create(CultureInfo.InvariantCulture, $"Recorded {netRecording.Seconds:0.0} s of the network game to {path}");
                Log.Info("Demo", $"{said}{(why != null ? $" ({why})" : "")}");
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException)
            {
                said = $"The demo was not written: {e.Message}";
                Log.Error("Demo", said);
            }
        }

        if (dm?.Replay is { } netPlayback)
        {
            said = string.Create(CultureInfo.InvariantCulture, $"{playbackName}: stopped at {netPlayback.Time:0.0} of {netPlayback.Length:0.0} s");
            if (why != null)
            {
                Log.Info("Demo", $"{said} ({why})");
            }

            dm.Dispose();
            dm = null;
            demoPaused = false;
            demoOver = false;
            session.Player.Noclip = false;
            if (why is "stop")
            {
                menu.OpenTitle();
                ReleaseMouse();
                PlayTitleMusic();
            }
        }

        if (recording is { } r)
        {
            recording = null;
            try
            {
                var path = r.Save();
                said = $"Recorded {r.Ticks} ticks to {path}";
                Log.Info("Demo", $"{said}{(why != null ? $" ({why})" : "")}");
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException)
            {
                said = $"The demo was not written: {e.Message}";
                Log.Error("Demo", said);
            }
        }

        if (playback is { } p)
        {
            playback = null;
            said = $"{playbackName}: stopped after {p.Cursor.Tick} of {p.Demo.Ticks} ticks{(p.Desync is { } d ? $", {d}" : "")}";
            if (why != null)
            {
                Log.Info("Demo", $"{said} ({why})");
            }

            demoPaused = false;
            if (loop != null)
            {
                loop.OneTickPerFrame = false;
            }

            renderer.VSync = options.NoVsync ? VSync.Off : settings.VSync;
            session.Difficulty = Difficulty.Of(settings.Difficulty);
            session.RobotAi = settings.RobotAi;
            ApplyDemoClock();
        }

        return said;
    }

    // Records the network game under way (docs/demos.md "Network demos"): what the server sends this
    // machine's player (the host: what a client as player 1 would get), its own poses, shots and hits,
    // and a keyframe every 10 s. Written by stop, or when the game is left. Null, or why not.
    string? StartNetRecording(string name)
    {
        if (dm == null)
        {
            return "No network game to record";
        }

        var path = CampaignDemo.PathOf(name);
        if (dm.StartRecording(path, data) is { } why)
        {
            return why;
        }

        Log.Info("Demo", $"Recording the network game to {path}");
        return null;
    }

    // Plays a network demo: a client on its recorded messages, no server and no simulation. Null, or why not.
    string? StartNetPlayback(string name, string path)
    {
        NetDemoData demo;
        try
        {
            demo = NetDemoFile.Read(path);
        }
        catch (Exception e) when (e is DemoFormatException or IOException or UnauthorizedAccessException)
        {
            return $"{name}: {e.Message}";
        }

        var kind = Enum.TryParse<GameKind>(demo.Field(SkyNet.Game.Demos.NetDemo.KeyGame), out var k) ? k : GameKind.None;
        if (kind != data.Game && (kind == GameKind.None || !SwitchGame(kind)))
        {
            return $"{name}: the demo is of {demo.Field(SkyNet.Game.Demos.NetDemo.KeyGame)}, whose data does not open";
        }

        var warnings = new List<string>();
        if (SkyNet.Game.Demos.NetDemo.Refusal(demo, data, warnings) is { } why)
        {
            return $"{name}: {why}";
        }

        foreach (var w in warnings)
        {
            Log.Warn("Demo", $"{name}: {w}");
        }

        dm?.Dispose();
        dm = null;
        FreshSession();
        weapons.Inventory.Reset();
        var replay = new SkyNet.Game.Demos.NetDemoPlayer(demo);
        dm = SkyNet.Game.Net.DmGame.StartReplay(session, replay);
        dm.Status += (text, colour) => hud.ShowStatus(text, colour);
        dm.Register(commands);
        netEnded = null;
        playbackName = name;
        demoPaused = false;
        demoOver = false;
        menu.Close();
        hud.ClearMessages();
        // The first keyframe loads the arena at once.
        dm.Frame(0f);
        if (level == null)
        {
            dm.Dispose();
            dm = null;
            return $"{name}: the arena MAP.{demo.Field(SkyNet.Game.Demos.NetDemo.KeyMap)} does not load";
        }

        ResumeGame();
        Log.Info("Demo", string.Create(CultureInfo.InvariantCulture,
            $"Playing the network demo {path}: {replay.Length:0.0} s, {replay.KeyframeTimes.Count()} keyframes, MAP.{demo.Field(SkyNet.Game.Demos.NetDemo.KeyMap)} '{demo.Field(SkyNet.Game.Demos.NetDemo.KeyArena)}', " +
            $"recorded by {demo.Field(SkyNet.Game.Demos.NetDemo.KeyRole)} {demo.Field(SkyNet.Game.Demos.NetDemo.KeyLocalName)} (player {replay.LocalId})"));
        return null;
    }
}
