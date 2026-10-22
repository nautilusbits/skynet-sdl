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

/// <summary>A network deathmatch in the window (--host / --join, or START on a network screen), or a
/// network demo played back: the chat line's and the demo camera's keys, the session's ticks with the
/// soldier's keys withheld while the menus, the console, the chat or the server hold them.</summary>
internal sealed class DeathmatchMode(AppHost host) : IAppMode
{
    public string Name => "deathmatch";

    public void Pump(float seconds)
    {
        host.SilenceEngineWhenFrozen();
        if (!host.menu.IsOpen && host.level != null && host.NetKeys())
        {
            return;
        }

        host.GameKeysOrMenu(seconds);
    }

    public void Tick(in TickInput input, int tick)
    {
        if (host.Frozen() || host.dm is not { } dm)
        {
            return;
        }

        // A deathmatch goes on under the Esc menu and the console (dm_game.gd: no local pause in a
        // network game), but the soldier gets no keys then, nor while the chat line is typed, before
        // the server's spawn or after the round.
        var latched = host.menu.IsOpen || host.devConsole.IsOpen || dm.InputBlocked ? default : input;
        // A network demo: the free camera flies on the keys; nothing else of the player's reaches the game.
        host.Simulate(latched, watching: dm.Replay != null);
    }

    public void Render(float alpha) => host.DrawFrame(alpha);
}

internal sealed partial class AppHost
{
    // A network game started (--host / --join, or START on a network screen): null, or why not. A
    // host is in its arena at once (the menus close); a client joining from the menu waits on the join
    // screen until the welcome brings the arena (NetFrame).
    string? StartNet(SkyNet.Game.Net.DmRunOptions o, bool fromMenu)
    {
        StopDemos("a network game started");
        if (dm != null)
        {
            dm.Dispose();
            dm = null;
        }

        if (data.Game != homeData.Game && !SwitchGame(homeData.Game))
        {
            return "SKYNET DATA DOES NOT OPEN";
        }

        netEnded = null;
        if (fromMenu)
        {
            // A fresh kit, as a new game's.
            weapons.Inventory.Reset();
        }

        var started = o.HostMap != null
            ? SkyNet.Game.Net.DmGame.StartHost(session, o, out var startError)
            : SkyNet.Game.Net.DmGame.StartClient(session, o, out startError);
        if (started == null)
        {
            return startError ?? "The network game did not start.";
        }

        dm = started;
        // TERMINATOR VISION: this machine's choice, never sent (the frame's style follows dm.PointCloudView).
        dm.PointCloudVision = o.PointCloudVision;
        dm.Status += (text, colour) => hud.ShowStatus(text, colour);
        dm.Ended += reason => netEnded = reason;
        dm.Register(commands);
        if (fromMenu && dm.IsHost)
        {
            hud.ClearMessages();
            ResumeGame();
        }

        return null;
    }

    // The deathmatch's frame (under the menus too: the wire and the server's clock go on). A client
    // that joined from the join screen goes in once the arena is up; a game the server ended (or a
    // join that failed) goes back to the network screen it came from, saying why.
    void NetFrame(float seconds)
    {
        if (dm?.Replay is { } replay)
        {
            // A network demo: its clock at the playback's speed, held while paused or over.
            dm.Frame(demoPaused || demoOver ? 0f : (float)(seconds * demoSpeed));
            if (replay.Done && replay.Time >= replay.Length && !demoOver)
            {
                demoOver = true;
                Log.Info("Demo", string.Create(CultureInfo.InvariantCulture, $"{playbackName}: {replay.Length:0.0} s played"));
                hud.ShowStatus("DEMO OVER");
                if (timedemoQuits)
                {
                    quitting = true;
                }
            }

            return;
        }

        dm?.Frame(seconds);
        if (dm is { IsHost: false, LevelUp: true } && netEnded == null && menu.Current is NetJoinScreen)
        {
            menu.Close();
            hud.ClearMessages();
            ResumeGame();
        }

        if (netEnded is { } reason && dm != null)
        {
            StopDemos("the network game ended");
            var joined = !dm.IsHost;
            dm.Dispose();
            dm = null;
            netEnded = null;
            menu.OpenNetwork(joined);
            menu.ShowToast(reason.ToUpperInvariant());
            ReleaseMouse();
            PlayTitleMusic();
        }
    }

    // The deathmatch's keys (dm_game.gd _unhandled_input), the menus shut: T or ENTER opens the chat
    // line, which then takes the typing (ENTER sends, Esc drops it); on the end-of-round screen the
    // host's ENTER starts a new round. True when they had this frame's input.
    internal bool NetKeys()
    {
        if (dm == null)
        {
            return false;
        }

        if (dm.Replay != null)
        {
            // A network demo: PageDown / PageUp follow the next / previous player, End changes the camera.
            if (input.WasPressed(RawKey.PageDown) || input.WasPressed(RawKey.PageUp))
            {
                hud.ShowStatus($"FOLLOWING {dm.FollowNext(input.WasPressed(RawKey.PageDown) ? 1 : -1)}");
            }
            else if (input.WasPressed(RawKey.End))
            {
                dm.Camera = (SkyNet.Game.Net.DmGame.DemoCamera)(((int)dm.Camera + 1) % 3);
                hud.ShowStatus($"{dm.Camera.ToString().ToUpperInvariant()} CAMERA");
            }
            else
            {
                return false;
            }

            input.Withhold();
            return true;
        }

        if (dm.ChatOpen)
        {
            dm.TypeChat(input.Typed);
            if (input.WasPressed(RawKey.Backspace))
            {
                dm.EraseChat();
            }

            if (input.WasPressed(RawKey.Enter))
            {
                dm.SubmitChat();
            }
            else if (input.WasPressed(RawKey.Escape))
            {
                dm.CloseChat();
            }

            input.Withhold();
            return true;
        }

        if (dm.RoundOver != null)
        {
            if (!input.WasPressed(RawKey.Enter))
            {
                return false;
            }

            dm.Restart();
            input.Withhold();
            return true;
        }

        if (!input.WasPressed(RawKey.T) && !input.WasPressed(RawKey.Enter))
        {
            return false;
        }

        dm.OpenChat();
        input.Withhold();
        return true;
    }
}
