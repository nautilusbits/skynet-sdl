
// SkyNET: the campaign in the original palette look, walked by the soldier (DOS movement and
// collision), with the level behaviour, map changes through the doorways and the mission flow.
// Without --mission or --map it starts at the DOS title menu; the settings (DETAIL, volumes,
// difficulty, keys), the saves (saves\) and the console history live beside the exe
// (SettingsStore.UserFolder; the app-data folder where the game's folder cannot be written).
// The command line is Options.Help (--help prints it).
// The game writes its log to logs\skynet.log beside the exe (the last five runs kept; where the
// game's folder cannot be written, SettingsStore.LogFolder in the app-data folder instead); the
// headless tools (--walk-test, --fire-test, --play-script, --music-wav) print to stdout instead.
// A crash is logged with its stack, then a message box points at the log.
// Keys: WASD walk, Shift runs, Space jumps, C crouches, arrows turn (Alt + arrows sidestep),
// PgUp/PgDn look, the mouse looks, E uses (doors, buttons, doorways), Esc opens the game menu. Noclip
// (fly: Space up, C down) has no key: the console's noclip, or one bound (bind f noclip). Enter or E skips the end-of-mission screen. F6 saves to the
// first LOAD slot and F7 loads it (the Godot port's quicksave keys; the slots live in the app-data
// folder, SaveSlots.DefaultFolder, Future Shock's under saves\shock). NEW GAME → FUTURE SHOCK plays
// Future Shock's data (found beside SkyNET's, GameDataLocator.OtherGameDir) inside the same window,
// as DOS SKYNET.EXE does; QUIT goes back to SkyNET's title, whose LOAD lists both games' slots.
// Weapons: left mouse / Ctrl fires, right mouse throws, 1-9 pick records 0-8, ; and ' cycle,
// F1-F5 pick the thrown item (molotov, pipe bomb, grenade, canister, satchel); RUN held when a
// rocket leaves the barrel rides the rocket eye.
// ` (the key left of 1, ~ with Shift) drops the developer console: the log as it comes, commands
// with Tab completion and a history (console-history.txt beside the settings); `help` lists them.
// The game is paused while it is down, as the Godot port's. bind <key> <command...> makes a key run
// a console line in play, as Quake's (bind <key> +action adds it to a game action; unbind, bindlist);
// the binds and every action's keys are in the settings file ("commandBinds", "bindings"), written
// with all its defaults on the first run.
// F11 (or `fps`) shows the FPS readout at the top right; F12 (or `debug`) the debug overlay: frame
// costs by part and by profiler zone, memory, the renderer, the GPU, where the soldier is, the robots
// and the mission's objectives with a pointer to the next one. Both stay on across maps and runs
// (the settings) and are drawn only while on, so screenshots show them only then.
// The window's game is AppHost (its modes: TitleMode, PlayMode, DeathmatchMode); the headless tools
// and helpers are AppTools; the command line is Options.
return GameMain.Run(args);
