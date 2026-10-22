using RetroEng.Audio;
using RetroEng.Diagnostics;
using RetroEng.Render;
using SkyNet.Formats;
using SkyNet.Game.Audio;
using SkyNet.Game.Campaign;
using SkyNet.Game.Hud;
using SkyNet.Game.Menu;
using SkyNet.Game.Mods;
using SkyNet.Game.Vehicles;
using SkyNet.Game.Weapons;

/// <summary>
/// Everything that belongs to one game's data folder: the data, the sound bank and the score, the
/// session (soldier, weapons, campaign), the weapon art and view-model, the vehicles and cockpit,
/// the panel, the menu art and the save slots. NEW GAME → FUTURE SHOCK disposes SkyNET's and makes
/// Future Shock's (DOS SKYNET.EXE plays Future Shock inside SkyNET: its menus, fonts and hi-res set
/// still come from SkyNET's folder through <see cref="GameData.FrontArchive"/>); QUIT goes back.
/// The GPU resources made here live in their own renderer scope, released with the context; the
/// map's scope is the app's and must be released first.
/// </summary>
internal sealed class GameContext : IDisposable
{
    private readonly Renderer _renderer;
    private readonly Renderer.Scope _scope;

    public GameData Data { get; }
    public SoundBank? Sounds { get; }
    public MusicPlayer Music { get; }
    public GameSession Session { get; }
    public TextureHandle Palette { get; }
    /// <summary>A mod's surfaces on the GPU across maps (<see cref="KeptSurfaces"/>), in this context's scope.</summary>
    public KeptSurfaces Kept { get; }
    public WeaponRenderer WeaponView { get; }
    public ViewModel ViewModel { get; }
    public VehicleRig Vehicles { get; }
    public CockpitRenderer CockpitView { get; }
    public DosHud Hud { get; }
    public BitmapFont? BannerFont { get; }
    public MenuPainter MenuPainter { get; }
    public SaveSlots Slots { get; }
    /// <summary>The LOAD/SAVE bars' names of <see cref="Slots"/>, refreshed on every save.</summary>
    public string?[] SlotNames { get; }

    private GameContext(GameData data, Renderer renderer, Mixer mixer, HudArtSet art)
    {
        _renderer = renderer;
        _scope = renderer.Mark();
        Data = data;
        Sounds = SoundBank.Open(mixer, data);
        Music = new MusicPlayer(mixer, data);
        // A failed mission waits for RESTART MISSION? instead of restarting by itself.
        Session = new GameSession(data, Sounds) { Logger = line => Log.Info("Session", line), AutoRestart = false };
        Palette = CreatePalette(renderer, data);
        Kept = new KeptSurfaces(renderer, Palette);
        WeaponView = new WeaponRenderer(renderer, data, Palette, Kept);
        Session.Weapons.EffectFrames = WeaponView.FramesOf;
        renderer.WarmUp([.. WeaponView.Preload(Session.Weapons.Table)]);
        // The gun in the soldier's hands, drawn under the panel.
        ViewModel = new ViewModel(renderer, data, Session.Weapons.Table, art, Palette, Kept);
        ViewModel.Attach(Session.Weapons);
        // The jeep and the HK: the mission table's mode on every map, the cockpit drawn from the seat.
        Vehicles = new VehicleRig(Session);
        CockpitView = new CockpitRenderer(renderer, data, Palette);
        renderer.WarmUp([.. CockpitView.Materials()]);
        // The DOS panel, compass, crosshair and message box on the canvas.
        Hud = new DosHud(renderer, data, art);
        BannerFont = HudFont.Load(data, "FONT0000.FNT") ?? Hud.Font;
        MenuPainter = new MenuPainter(renderer, data);
        Slots = new SaveSlots(SaveSlots.DefaultFolder(data.Game));
        SlotNames = [.. Enumerable.Range(0, SaveSlots.Count).Select(Slots.Label)];
    }

    /// <summary>The context of <paramref name="data"/>'s game.</summary>
    public static GameContext Create(GameData data, Renderer renderer, Mixer mixer, HudArtSet art)
    {
        var context = new GameContext(data, renderer, mixer, art);
        var w = context.Session.Weapons;
        Log.Info("Weapons", $"{data.Game}: table {w.Table.Source}; start list {string.Join(',', w.Table.StartList)}; " +
            $"in hand {w.Table.Weapons[w.Inventory.Primary].Name}, thrown {w.Table.Weapons[w.Inventory.Thrown].Name}");
        return context;
    }

    public void Dispose()
    {
        Music.Stop();
        Music.Dispose();
        Sounds?.Dispose();
        ViewModel.Dispose();
        Hud.Dispose();
        MenuPainter.Dispose();
        _renderer.Release(_scope);
    }

    // The game palette (SKYNET.COL) as a texture, for what is drawn across maps (the weapon art).
    private static TextureHandle CreatePalette(Renderer renderer, GameData data)
    {
        var palette = data.LoadPalette() ?? throw new InvalidDataException("No game palette (SKYNET.COL) in the data folder.");
        var rgba = new byte[256 * 4];
        for (var i = 0; i < 256; i++)
        {
            var (r, g, b) = palette[i];
            (rgba[i * 4], rgba[i * 4 + 1], rgba[i * 4 + 2], rgba[i * 4 + 3]) = (r, g, b, 255);
        }

        return renderer.CreatePalette(rgba);
    }
}
