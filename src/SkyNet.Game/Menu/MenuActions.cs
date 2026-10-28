using SkyNet.Formats;

namespace SkyNet.Game.Menu;

/// <summary>What the menus ask of the game. The menus never touch the session themselves.</summary>
public interface IMenuActions
{
    /// <summary>NEW GAME → ONE PLAYER (SkyNET) or FUTURE SHOCK: start that game's campaign at
    /// <paramref name="mission"/>, played by <paramref name="pilot"/> (the new-game dialog's name and call sign).</summary>
    void StartGame(GameKind game, int mission, Campaign.Pilot pilot);

    /// <summary>BEGIN on the mission screen: the mission starts (the menu has closed).</summary>
    void BeginMission();

    /// <summary>RETURN or Esc on the in-game menu (the menu has closed).</summary>
    void Resume();

    /// <summary>YES in the RESTART MISSION box (the menu has closed).</summary>
    void RestartMission();

    /// <summary>YES in QUIT TO MAIN MENU?, NO in the RESTART box, Esc on the mission screen.</summary>
    void QuitToTitle();

    /// <summary>YES in QUIT TO DOS? on the title menu.</summary>
    void Quit();

    /// <summary>A LOAD slot was picked (the menu has closed): load it and resume play.</summary>
    void LoadGame(int slot);

    /// <summary>A SAVE slot was picked: true when the game was written to it (the screen then says
    /// GAME SAVED); false when it was not (the game says why).</summary>
    bool SaveGame(int slot);

    /// <summary>A setting changed (already saved when the store allows): apply it.</summary>
    void SettingsChanged(MenuSettings settings);

    /// <summary>A menu sound by its SOUNDS name (BUTTON1.RAW on a click).</summary>
    void Sound(string name);

    /// <summary>START on a network screen: host or join the game <paramref name="options"/> describe,
    /// as <c>--host</c> / <c>--join</c> do. Null when it started (a host's arena is up, a client
    /// is connecting), else why not (shown on the screen).</summary>
    string? StartNetGame(Net.DmRunOptions options) => "NETWORK GAMES ARE NOT AVAILABLE";

    /// <summary>Back on the join screen while it connects: the connection is given up.</summary>
    void StopNetGame()
    {
    }

    /// <summary>START on the host screen with DEDICATED: a server with no local player, run in this
    /// process behind the SERVER STATUS screen (which steps it). Null and why when it did not start.</summary>
    Net.DmDedicated? StartDedicated(Net.DmRunOptions options, out string? error)
    {
        error = "DEDICATED SERVERS ARE NOT AVAILABLE";
        return null;
    }

    /// <summary>The SERVER STATUS screen stopped its server.</summary>
    void StopDedicated()
    {
    }
}

/// <summary><see cref="IMenuActions"/> as closures, for the app's wiring.</summary>
public sealed class MenuEvents : IMenuActions
{
    public Action<GameKind, int, Campaign.Pilot>? OnStartGame;
    public Action? OnBeginMission, OnResume, OnRestartMission, OnQuitToTitle, OnQuit;
    public Action<int>? OnLoadGame;
    public Func<int, bool>? OnSaveGame;
    public Action<MenuSettings>? OnSettingsChanged;
    public Action<string>? OnSound;
    public Func<Net.DmRunOptions, string?>? OnStartNetGame;
    public Action? OnStopNetGame;
    public Func<Net.DmRunOptions, (Net.DmDedicated? Server, string? Error)>? OnStartDedicated;
    public Action? OnStopDedicated;

    public void StartGame(GameKind game, int mission, Campaign.Pilot pilot) => OnStartGame?.Invoke(game, mission, pilot);
    public void BeginMission() => OnBeginMission?.Invoke();
    public void Resume() => OnResume?.Invoke();
    public void RestartMission() => OnRestartMission?.Invoke();
    public void QuitToTitle() => OnQuitToTitle?.Invoke();
    public void Quit() => OnQuit?.Invoke();
    public void LoadGame(int slot) => OnLoadGame?.Invoke(slot);
    public bool SaveGame(int slot) => OnSaveGame?.Invoke(slot) ?? false;
    public void SettingsChanged(MenuSettings settings) => OnSettingsChanged?.Invoke(settings);
    public void Sound(string name) => OnSound?.Invoke(name);
    public void StopNetGame() => OnStopNetGame?.Invoke();
    public void StopDedicated() => OnStopDedicated?.Invoke();

    public Net.DmDedicated? StartDedicated(Net.DmRunOptions options, out string? error)
    {
        if (OnStartDedicated == null)
        {
            error = "DEDICATED SERVERS ARE NOT AVAILABLE";
            return null;
        }

        (var server, error) = OnStartDedicated(options);
        return server;
    }
    public string? StartNetGame(Net.DmRunOptions options) => OnStartNetGame != null ? OnStartNetGame(options) : "NETWORK GAMES ARE NOT AVAILABLE";
}

/// <summary>What the menus know of the world around them.</summary>
public sealed class MenuContext
{
    /// <summary>Which games have data (FUTURE SHOCK needs its folder).</summary>
    public bool SkyNetAvailable { get; init; } = true;
    public bool FutureShockAvailable { get; init; }

    /// <summary>The label of a save slot (0..9), null when empty. UI only: the save format is the caller's.</summary>
    public Func<int, string?> SlotName { get; init; } = _ => null;

    /// <summary>The key bindings the CONTROLS screen shows and changes; null hides them.</summary>
    public IBindings? Bindings { get; init; }

    /// <summary>The name and call sign an empty field of the new-game dialog takes, per game
    /// (STRINGS.PRS DefaultName / DefaultSign).</summary>
    public Func<GameKind, Campaign.Pilot> DefaultPilot { get; init; } = _ => Campaign.Pilot.Default;

    /// <summary>The display's sizes for the DISPLAY screen (asked when it opens); null: only the
    /// presets that fit a 1920 × 1080 desktop (tests, a display SDL cannot read).</summary>
    public Func<DisplayModes>? Display { get; init; }

    /// <summary>The window the game runs in: <see cref="WindowKind.FixedFullscreen"/> (Android) hides
    /// DISPLAY's WINDOW and RESOLUTION.</summary>
    public WindowKind Window { get; init; } = WindowKind.Choosable;

    /// <summary>How the player types: <see cref="KeyboardKind.OnScreen"/> (a touch screen) covers the
    /// menu, so the network screens open with no box in use.</summary>
    public KeyboardKind Keyboard { get; init; } = KeyboardKind.Physical;

    /// <summary>The on-screen controls and the window's size in pixels (a touch screen, or
    /// --touch); null hides CONTROLS' TOUCH LAYOUT.</summary>
    public Func<(RetroEng.Platform.TouchControls Controls, System.Numerics.Vector2 Screen)?> Touch { get; init; } = () => null;

    /// <summary>The render scale (%) the renderer used last frame (AUTO's pick); null: unknown.</summary>
    public Func<int>? RenderScaleInUse { get; init; }

    /// <summary>The ENHANCED look can be chosen (its pipeline is hooked up for this game).</summary>
    public bool EnhancedAvailable { get; init; }

    /// <summary>The connected gamepad's name for the GAMEPAD screen (asked as it draws); null: none.</summary>
    public Func<string?> GamepadName { get; init; } = () => null;

    /// <summary>Shakes the gamepad (motors 0..1, milliseconds): VIBRATION turned on tries it.</summary>
    public Action<float, float, int>? Rumble { get; init; }

    /// <summary>The deathmatch arenas (NETLEVEL.PRS) the host screen offers.</summary>
    public Func<IReadOnlyList<NetLevel>> NetLevels { get; init; } = () => NetLevelList.BuiltIn();

    /// <summary>The join screen's LAN browser, made when the screen opens (null: none, the
    /// screen says so); by default it probes the broadcast addresses and this machine.</summary>
    public Func<RetroEng.Net.DiscoveryBrowser?> LanBrowser { get; init; } = Net.DmLan.StartBrowser;

    /// <summary>The master server's address for this run: the settings' <c>net.master</c>, or
    /// <c>--master</c> over it; null: the settings'.</summary>
    public Func<string?> MasterAddress { get; init; } = () => null;

    /// <summary>The join screen's INTERNET list on a master server ("host:port"); null when the
    /// address cannot be read. By default SkyNET's game id and this build's protocol.</summary>
    public Func<string, RetroEng.Net.Master.MasterBrowser?> MasterBrowser { get; init; } =
        a => RetroEng.Net.Master.MasterBrowser.Start(a, "skynet", Net.DmProtocol.Version);

    /// <summary>A network game is connecting from the join screen (it waits for the arena).</summary>
    public Func<bool> NetConnecting { get; init; } = () => false;

    /// <summary>MODS: the mods folder's mods (read when the screen opens, <see cref="SkyNet.Game.Mods.ModCatalog"/>);
    /// null lists none.</summary>
    public Func<IReadOnlyList<SkyNet.Game.Mods.ModEntry>>? ModList { get; init; }

    /// <summary>This run's <c>--mod</c> list, which stands in for the settings' until the game quits;
    /// null: the settings' list is in use.</summary>
    public IReadOnlyList<string>? ModsOverride { get; init; }

    /// <summary>MODS → GENERATE HI-RES TEXTURES: starts making the hi-res texture mod in the
    /// background (<see cref="SkyNet.Game.Mods.HiresJob"/>); null: no button.</summary>
    public Func<IMenuJob>? GenerateHires { get; init; }

    /// <summary>A mod with textures is in use (no HI-RES TEXTURES notice then); null: the notice
    /// never comes up (tests, a build without the enhanced look).</summary>
    public Func<bool>? HiresTextures { get; init; }

    /// <summary>The CONTROLS screen's 17 boxes → action names in <see cref="Bindings"/>.</summary>
    public IReadOnlyDictionary<string, string> ControlActions { get; init; } = MenuLayout.DefaultControlActions;
}

/// <summary>Whether the player picks the window's mode and size (desktop) or the system fixes it
/// fullscreen (Android).</summary>
public enum WindowKind { Choosable, FixedFullscreen }

/// <summary>A keyboard beside the screen, or one the system draws over it (Android).</summary>
public enum KeyboardKind { Physical, OnScreen }

/// <summary>Where a menu's background job stands.</summary>
public enum MenuJobState { Running, Done, Failed, Cancelled }

/// <summary>Work a menu screen started on another thread (GENERATE HI-RES TEXTURES): its
/// progress line, its end and a way to stop it. Read from the menu's thread.</summary>
public interface IMenuJob
{
    /// <summary>The progress line, upper case.</summary>
    string Status { get; }
    MenuJobState State { get; }
    /// <summary>Why it failed (for the screen); null otherwise.</summary>
    string? Error { get; }
    /// <summary>The mod it makes, turned on when it is done.</summary>
    string ModName { get; }
    void Cancel();
}
