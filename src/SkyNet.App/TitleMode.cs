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

/// <summary>The title and the menus off it: no map yet, or a menu that is not the game's (the title,
/// NEW GAME, LOAD, the settings from the title) over the last map left. Nothing ticks here (the
/// host's <see cref="AppHost.Frozen"/> always holds); the frame draws the menus (and, as before
/// the modes, the map left behind wherever the menu shows it).</summary>
internal sealed class TitleMode(AppHost host) : IAppMode
{
    public string Name => "title";

    public void Pump(float seconds)
    {
        host.SilenceEngineWhenFrozen();
        host.GameKeysOrMenu(seconds);
    }

    public void Tick(in TickInput input, int tick)
    {
        // Frozen: no map, or a menu that is not the game's.
    }

    public void Render(float alpha) => host.DrawFrame(alpha);
}

internal sealed partial class AppHost
{
    // The title's LOAD bar: both games keep ten slots of their own (saves and saves\shock), the
    // menu's tabs number Future Shock's after SkyNET's (13 = its slot 3).
    (GameKind Game, string Label, int Slot)? TitleSlot(int slot)
    {
        var (page, bar) = Math.DivRem(slot, SaveSlots.Count);
        if (slot < 0 || TitleSlotFolders().ElementAtOrDefault(page) is not (var kind, { } folder))
        {
            return null;
        }

        return new SaveSlots(folder).Label(bar) is { } label ? (kind, label, bar) : null;
    }

    IEnumerable<(GameKind, string)> TitleSlotFolders()
    {
        yield return (homeData.Game, SaveSlots.DefaultFolder(homeData.Game));
        if (futureShockDir != null)
        {
            yield return (GameKind.FutureShock, SaveSlots.DefaultFolder(GameKind.FutureShock));
        }
    }

    void PlayTitleMusic()
    {
        if (!options.NoMusic && !options.NoSound)
        {
            music.PlayTitle();
        }
    }
}
