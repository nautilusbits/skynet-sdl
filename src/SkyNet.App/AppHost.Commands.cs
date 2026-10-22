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
    // The app's console commands: what the session does not own (the menus, saves, sound, the HUD, the
    // window). The session's are SessionCommands.
    void RegisterAppCommands()
    {
        var c = CultureInfo.InvariantCulture;
        var levels = new Dictionary<string, int> { ["low"] = 0, ["med"] = 1, ["medium"] = 1, ["high"] = 2 };
        IEnumerable<string> Levels(int i) => i == 0 ? ["low", "med", "high"] : [];
        commands.Add("version", "version", _ =>
            $"SkyNET C# port {typeof(Options).Assembly.GetName().Version}; {data.Game} data; {GpuBackends.Name(renderer.Gpu.Backend)} on {renderer.Gpu.Device}",
            "the build, the data and the GPU");
        commands.Add("quit", "quit", _ =>
        {
            quitting = true;
            return "";
        }, "leaves the game", "exit");
        commands.Add("workers", "workers [N|auto]", args =>
        {
            var running = RetroEng.Jobs.JobSystem.Shared.Workers;
            if (args.Count == 0)
            {
                var saved = settings.Workers < 0 ? $"auto ({RetroEng.Jobs.JobSystem.DefaultWorkerCount})" : settings.Workers.ToString(c);
                return $"{running} job workers running; settings: {saved}";
            }

            var word = args[0];
            int value;
            if (word.Equals("auto", StringComparison.OrdinalIgnoreCase))
            {
                value = MenuSettings.AutoWorkers;
            }
            else if (!int.TryParse(word, NumberStyles.None, c, out value) || value > RetroEng.Jobs.JobSystem.MaxWorkers)
            {
                return $"workers: 0..{RetroEng.Jobs.JobSystem.MaxWorkers} or auto";
            }

            // The pool cannot resize while jobs run: the settings take it, the next start uses it.
            settings.Workers = value;
            if (interactive)
            {
                var stored = SettingsStore.Load(settingsPath);
                stored.Workers = value;
                SettingsStore.Save(stored, settingsPath);
            }

            var wanted = value < 0 ? RetroEng.Jobs.JobSystem.DefaultWorkerCount : value;
            return wanted == running ? $"{running} job workers (as running)" : $"{wanted} job workers from the next start ({running} now)";
        }, "the job pool's worker threads (auto: cores - 1, 2..8); takes effect at the next start", "jobs");
        commands.Add("menu", "menu", _ =>
        {
            menuEvents.OnQuitToTitle?.Invoke();
            return "Title";
        }, "back to the title");
        commands.Add("automap", "automap", _ => OpenAutomap() ? "Automap" : "No automap here (a campaign map being played)",
            "the automap (key Tab, pad R3): arrows turn and tilt, + and - zoom, Esc or Tab close; clearautomap forgets it");
        commands.Add("save", $"save [1-{SaveSlots.Count}]", args =>
        {
            var slot = (args.OptionalInt(0, 1, SaveSlots.Count) ?? SaveSlots.QuickSlot + 1) - 1;
            return SaveGame(slot) ? $"Saved to slot {slot + 1}" : "Cannot save now";
        }, "saves (no slot: the F6 slot)");
        commands.Add("load", $"load [1-{SaveSlots.Count}]", args =>
        {
            var slot = (args.OptionalInt(0, 1, SaveSlots.Count) ?? SaveSlots.QuickSlot + 1) - 1;
            if (slots.Label(slot) == null)
            {
                return $"Slot {slot + 1} is empty";
            }

            LoadGame(slot);
            return $"Loaded slot {slot + 1}";
        }, "loads (no slot: the F7 slot)");
        commands.Add(new Command("difficulty", "difficulty <low|med|high>", args =>
        {
            settings.Difficulty = (DifficultyLevel)args.Choice(0, levels);
            ApplySettings(settings);
            return $"difficulty {settings.Difficulty} (this run)";
        })
        { Help = "the robots' fire rate and the damage both ways", Arguments = Levels });
        commands.Add(new Command("robotai", "robotai [classic|improved]", args =>
        {
            if (args.Count == 0)
            {
                return $"robotai {RobotAis.Word(settings.RobotAi)}";
            }

            settings.RobotAi = args.Choice(0, RobotAis.Words);
            ApplySettings(settings);
            if (interactive)
            {
                // Kept in the settings file; the rest of the file as it is on disk.
                var stored = SettingsStore.Load(settingsPath);
                stored.RobotAi = settings.RobotAi;
                SettingsStore.Save(stored, settingsPath);
            }

            return $"robotai {RobotAis.Word(settings.RobotAi)}";
        })
        { Help = "ROBOT AI: classic (DOS's robots) or improved (round obstacles, memory, no shots into walls)", Arguments = i => i == 0 ? ["classic", "improved"] : [] });
        commands.Add(new Command("detail", "detail <low|med|high>", args =>
        {
            settings.Detail = (Detail)args.Choice(0, levels);
            ApplySettings(settings);
            return $"detail {settings.Detail} (this run)";
        })
        { Help = "RENDER DETAIL: the far clip, the depth cue and (from the next map) the HD textures' size", Arguments = Levels });
        commands.Add(new Command("far", "far [units]", args =>
        {
            farOverride = args.OptionalFloat(0, 1f, 1e6f);
            ApplySettings(settings);
            var detailFar = Level.DetailFar[(int)settings.Detail].ToString(c);
            return farOverride is { } f
                ? string.Create(c, $"far {f} (this run; RENDER DETAIL's is {detailFar}), cue from {level?.CueStart ?? Math.Min(Level.DetailCueStart[(int)settings.Detail], f)}")
                : $"far {detailFar} (RENDER DETAIL)";
        })
        { Help = "the far clip for this run, any distance (the depth cue keeps RENDER DETAIL's start); no argument: back to RENDER DETAIL's" });
        commands.Add(new Command("hud", "hud <dos|hires>", args =>
        {
            settings.HudArt = args.Choice(0, new Dictionary<string, HudArtSet> { ["dos"] = HudArtSet.LowRes, ["hires"] = HudArtSet.HiRes });
            ApplySettings(settings);
            return $"hud {(settings.HudArt == HudArtSet.HiRes ? "hires" : "dos")} (this run)";
        })
        { Help = "the 320 x 200 panel or the 640 x 480 set", Arguments = i => i == 0 ? ["dos", "hires"] : [] });
        commands.Add(new Command("hudstyle", "hudstyle <classic|modern>", args =>
        {
            settings.HudStyle = args.Choice(0, new Dictionary<string, HudStyle> { ["classic"] = HudStyle.Classic, ["modern"] = HudStyle.Modern });
            ApplySettings(settings);
            return $"hudstyle {settings.HudStyle.ToString().ToLowerInvariant()} (this run)";
        })
        { Help = "DOS's panel or the corner widgets over a full-frame view", Arguments = i => i == 0 ? ["classic", "modern"] : [] });
        commands.Add(new Command("relief", "relief [off|low|medium|high]", args =>
        {
            settings.Relief = args.OptionalWord(0) is null
                ? EnhancedOptions.StepRelief(settings.Relief, 1)
                : args.Choice(0, new Dictionary<string, ReliefLevel>
                {
                    ["off"] = ReliefLevel.Off, ["low"] = ReliefLevel.Low, ["medium"] = ReliefLevel.Medium, ["high"] = ReliefLevel.High,
                });
            ApplySettings(settings);
            return $"relief {settings.Relief.ToString().ToLowerInvariant()} (this run)";
        })
        { Help = "ENHANCED OPTIONS' SURFACE RELIEF: how strongly the hi-res textures' normal and roughness maps light them (no argument: the next level)", Arguments = i => i == 0 ? ["off", "low", "medium", "high"] : [] });
        commands.Add(new Command("renderer", "renderer [default|ascii|pointcloud] [source|green|amber]", args =>
        {
            settings.Style = args.OptionalWord(0) is null ? ViewStyles.Step(settings.Style, 1) : args.Choice(0, ViewStyles.Words);
            if (args.OptionalWord(1) is not null)
            {
                settings.AsciiColour = args.Choice(1, ViewStyles.ColourWords);
            }

            ApplySettings(settings);
            return $"renderer {ViewStyles.Name(settings.Style).ToLowerInvariant()}, ascii {settings.AsciiColour.ToString().ToLowerInvariant()} (this run)";
        })
        { Help = "DISPLAY's RENDERER: the world as is, as ASCII glyphs or as a point cloud (no argument: the next); the ASCII colour", Arguments = i => i switch { 0 => ViewStyles.Words.Keys, 1 => ViewStyles.ColourWords.Keys, _ => [] } });
        commands.Add(new Command("music", "music [off | title | <song> | 0-100]", args =>
        {
            if (args.OptionalWord(0) is not { } word)
            {
                return string.Create(c, $"music {music.Current ?? "off"}, volume {music.Volume / MusicPlayer.DefaultVolume * 100f:0} %");
            }

            switch (word.ToLowerInvariant())
            {
                case "off" or "stop":
                    music.Stop();
                    return "music off";
                case "title":
                    music.PlayTitle();
                    return $"music {music.Current}";
            }

            if (CommandArgs.TryInt(word, out _))
            {
                var percent = args.Int(0, 0, 100);
                music.Volume = MusicPlayer.DefaultVolume * percent / 100f;
                return string.Create(c, $"music volume {percent} % (this run)");
            }

            var song = word.Contains('.', StringComparison.Ordinal) ? word.ToUpperInvariant() : word.ToUpperInvariant() + ".HMI";
            if (data.ReadLoose(song) == null)
            {
                return $"No {song} in the data";
            }

            music.Play(song);
            return $"music {song}";
        })
        { Help = "the score: a song (T200, TITLE.HMI...), the title's, off, or the volume", Arguments = i => i == 0 ? ["off", "title"] : [] });
        RegisterStereoCommand();
        commands.Add("hint", "hint <text>", args =>
        {
            hud.ShowHint(args.Rest().Length != 0 ? args.Rest() : throw new CommandUsageException());
            return "";
        }, "a line in the panel's message window");
        commands.Add("status", "status <text>", args =>
        {
            hud.ShowStatus(args.Rest().Length != 0 ? args.Rest() : throw new CommandUsageException());
            return "";
        }, "a status line over the view");
        commands.Add("screenshot", "screenshot <file.bmp>", args =>
        {
            pendingShot = args.Rest().Length != 0 ? args.Rest() : throw new CommandUsageException();
            return $"Screenshot after this frame: {pendingShot}";
        }, "saves the next frame");
        commands.Add("record", "record <name> [mission <n> | map <n>]", args =>
        {
            var name = args.Word(0);
            var kind = args.OptionalWord(1)?.ToLowerInvariant();
            if (dm is { Replay: null } && kind == null)
            {
                return StartNetRecording(name) ?? $"Recording the network game to {name} (stop ends it; leaving the game too)";
            }

            if (kind == null)
            {
                return StartRecording(name, null, null) ?? $"Recording {name} from here (stop ends it)";
            }

            var n = args.Int(2, 0, 999);
            var (number, side) = kind switch
            {
                "mission" => (n, (int?)null),
                "map" => session.Table.ForMap(n) is { } m ? (m.Number, m.StartMap == n ? null : n) : (0, n),
                _ => throw new CommandUsageException(),
            };
            return StartRecording(name, number, side) ?? $"Recording {name} from the start of {(number > 0 ? $"mission {number}" : $"MAP.{n:D3}")} (stop ends it)";
        }, "records a demo to demos/<name>.sndemo: from here (a save of the game now), or from a mission's start; in a network game, what the server sends");
        commands.Add("stop", "stop", _ => StopDemos("stop") ?? "No demo is recording or playing", "ends the recording (writes it) or the playback");
        commands.Add("playdemo", "playdemo <name>", args => StartPlayback(args.Word(0), timedemo: false) ?? $"Playing {args.Word(0)}",
            "plays a demo back (Esc pauses, demospeed, demopause; stop ends it)");
        commands.Add("timedemo", "timedemo <name>", args => StartPlayback(args.Word(0), timedemo: true) ?? $"Timing {args.Word(0)}",
            "plays a demo as fast as the frames render and reports the frame rate");
        commands.Add("demospeed", "demospeed [0.25-8]", args =>
        {
            demoSpeed = args.OptionalFloat(0, 0.25f, 8f) ?? 1f;
            ApplyDemoClock();
            return string.Create(c, $"demo speed x{demoSpeed:0.##}");
        }, "the playback's speed (no argument: x1)");
        commands.Add("demopause", "demopause", _ =>
        {
            var playing = playback != null || dm?.Replay != null;
            demoPaused = playing && !demoPaused;
            return demoPaused ? "demo paused" : playing ? "demo playing" : "No demo is playing";
        }, "holds or resumes the playback");
        commands.Add("demoseek", "demoseek <seconds | +seconds | -seconds>", args =>
        {
            if (dm?.Replay is not { } replay)
            {
                return "No network demo is playing";
            }

            var word = args.Word(0);
            var relative = word.StartsWith('+') || word.StartsWith('-');
            if (!double.TryParse(word, NumberStyles.Float, c, out var value))
            {
                throw new CommandUsageException();
            }

            var at = replay.Seek(relative ? replay.Time + value : value);
            dm.Frame(0f);
            demoOver = false;
            return string.Create(c, $"demo at {at:0.0} s (the keyframe at or before {(relative ? replay.Time + value : value):0.0} s) of {replay.Length:0.0} s");
        }, "network demos: jumps to the keyframe at or before a time (keyframes every 10 s)");
        commands.Add(new Command("democam", "democam [first|chase|free]", args =>
        {
            if (dm?.Replay is null)
            {
                return "No network demo is playing";
            }

            dm.Camera = args.OptionalWord(0)?.ToLowerInvariant() switch
            {
                null => (SkyNet.Game.Net.DmGame.DemoCamera)(((int)dm.Camera + 1) % 3),
                "first" => SkyNet.Game.Net.DmGame.DemoCamera.FirstPerson,
                "chase" => SkyNet.Game.Net.DmGame.DemoCamera.Chase,
                "free" => SkyNet.Game.Net.DmGame.DemoCamera.Free,
                _ => throw new CommandUsageException(),
            };
            return $"demo camera: {dm.Camera.ToString().ToLowerInvariant()}";
        })
        { Help = "network demos: the followed player's eye, behind them, or a free camera (no argument: the next; End in the game)", Arguments = i => i == 0 ? ["first", "chase", "free"] : [] });
        commands.Add(new Command("demofollow", "demofollow [next|prev|<id>]", args =>
        {
            if (dm?.Replay is null)
            {
                return "No network demo is playing";
            }

            var word = args.OptionalWord(0)?.ToLowerInvariant() ?? "next";
            if (word is "next" or "prev")
            {
                return $"following {dm.FollowNext(word == "next" ? 1 : -1)}";
            }

            return int.TryParse(word, NumberStyles.Integer, c, out var id) && dm.FollowPlayer(id) ? $"following player {id}" : $"No player {word} in the demo";
        })
        { Help = "network demos: the player the camera follows (PageDown / PageUp in the game)", Arguments = i => i == 0 ? ["next", "prev"] : [] });
        commands.Add(new Command("storm", "storm [strike]", args =>
        {
            var table = Weather.Current.Storm;
            if (table == null)
            {
                return $"No storm: no mod in use has a storm in {Weather.Folder}";
            }

            if (args.OptionalWord(0) is not { } word)
            {
                return string.Create(c, $"storm: a strike every {table.Interval} s on average, {table.NearMetres}-{table.FarMetres} m; outdoors, ENHANCED look");
            }

            if (!word.Equals("strike", StringComparison.OrdinalIgnoreCase))
            {
                throw new CommandUsageException();
            }

            effects.Storm.Strike(PlayerController.Direction(player.Yaw, 0f));
            return "Lightning ahead at the next frame (outdoors, ENHANCED look)";
        })
        { Help = "a mod's storm (weather/storm.json): its settings, or a strike ahead now", Arguments = i => i == 0 ? ["strike"] : [] });
        commands.Add(new Command("weather", "weather [gust]", args =>
        {
            if (args.OptionalWord(0) is not { } word)
            {
                return Weather.Describe(Weather.Current) + "; outdoors, ENHANCED look";
            }

            if (!word.Equals("gust", StringComparison.OrdinalIgnoreCase))
            {
                throw new CommandUsageException();
            }

            if (Weather.Current.Dust == null)
            {
                return $"No dust: no mod in use has dust in {Weather.Folder}";
            }

            effects.Air.Gust();
            return "A dust cloud rises upwind (outdoors, ENHANCED look)";
        })
        { Help = "the mods' weather (weather/*.json): what is on, or a dust cloud raised upwind now", Arguments = i => i == 0 ? ["gust"] : [] });
        commands.Add("stats", "stats", _ =>
        {
            var recent = frameTimes.Count == 0 ? 0 : frameTimes.TakeLast(60).Average();
            var memory = renderer.Memory;
            return string.Create(c, $"{renderer.Stats.DrawCalls} draw calls, {renderer.Stats.Triangles} triangles, {recent:0.00} ms a frame (last 60); ") +
                string.Create(c, $"{session.Robots?.All.Count(r => r.Alive) ?? 0} robots alive; window {window.Size.Width} x {window.Size.Height}; ") +
                string.Create(c, $"GPU {memory.TextureBytes >> 20} MB textures + {memory.MeshBytes >> 20} MB meshes (DETAIL {settings.Detail})");
        }, "what the last frames cost, and the GPU memory held");
    }

    // --menu-script: one step a frame (a wait holds the queue).
    void StepMenuScript()
    {
        if (menuScript == null || scriptWait-- > 0 || Stopwatch.GetTimestamp() < scriptSleepUntil)
        {
            return;
        }

        if (!menuScript.TryDequeue(out var step))
        {
            menuScript = null;
            return;
        }

        var space = step.IndexOf(' ', StringComparison.Ordinal);
        var (verb, arg) = space > 0 ? (step[..space], step[(space + 1)..].Trim()) : (step, "");
        Log.Info("Menu", $"Menu script: {step}");
        switch (verb)
        {
            case "up": input.Press(MenuKey.Up); break;
            case "down": input.Press(MenuKey.Down); break;
            case "left": input.Press(MenuKey.Left); break;
            case "right": input.Press(MenuKey.Right); break;
            case "accept": input.Press(MenuKey.Accept); break;
            case "back": input.Press(MenuKey.Back); break;
            case "esc":
                input.Press(pause);
                input.Press(MenuKey.Back);
                break;
            case "select": menu.Current?.Select(arg); break;
            case "type": input.Type(arg); break;
            case "run":
                // A command may change what a frame build reads (the level, the look).
                WaitForFrames();
                devConsole.Run(arg);
                break;
            case "wait": scriptWait = int.Parse(arg, CultureInfo.InvariantCulture); break;
            case "sleep":
                // Wall-clock milliseconds (a network peer's pace, which frames do not keep offscreen).
                scriptSleepUntil = Stopwatch.GetTimestamp() + int.Parse(arg, CultureInfo.InvariantCulture) * Stopwatch.Frequency / 1000;
                break;
            case "shot": pendingShot = arg; break;
            case "quit": quitting = true; break;
            case "die": soldier.Hurt(1e6f); break;
            case "key":
                // A game key by its action name (F6 "Quick save", F7 "Quick load").
                if (actions.Find(arg) is var key and >= 0)
                {
                    input.Press(key);
                }

                break;
            case "pad":
                // A gamepad button pressed and let go by its name ("A", "RT", "D-pad up").
                if (GamepadMap.Parse(arg) is { } button)
                {
                    input.HoldPad(button, Input.State.Down);
                    input.HoldPad(button, Input.State.Up);
                }

                break;
            case "hold" or "release":
                // A game key held down (or let go) across frames, by its action name ("Turn left").
                if (actions.Find(arg) is var held and >= 0)
                {
                    input.Hold(held, verb == "hold" ? Input.State.Down : Input.State.Up);
                }

                break;
            case "print":
                Log.Info("Menu", $"Menu: [{string.Join(" > ", menu.Stack.Select(m => m.Name))}] highlight {menu.Current?.Highlighted?.Id}; " +
                    $"MAP.{session.MapNumber:D3} {session.Phase}; detail {settings.Detail}; far {level?.Far.ToString(CultureInfo.InvariantCulture) ?? "-"} cue {level?.CueStart.ToString(CultureInfo.InvariantCulture) ?? "-"}");
                break;
            default: Log.Warn("Menu", $"Menu script: unknown step '{step}'"); break;
        }
    }
}
