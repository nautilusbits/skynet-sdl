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

/// <summary>A campaign's play (SkyNET's or Future Shock's): the soldier, the map, the mission, a demo
/// recording or playing. The game's keys (Esc, F6, F7) with the menus shut, the session's ticks, the
/// world drawn under the HUD and the menus.</summary>
internal sealed class PlayMode(AppHost host) : IAppMode
{
    public string Name => "play";

    public void Pump(float seconds)
    {
        host.SilenceEngineWhenFrozen();
        host.GameKeysOrMenu(seconds);
    }

    public void Tick(in TickInput input, int tick) => host.Simulate(input, watching: false);

    public void Render(float alpha) => host.DrawFrame(alpha);
}

internal sealed partial class AppHost
{
    // One tick of the game under way: the input as the mode gives it (a deathmatch withholds the
    // soldier's keys at times), watching a network demo (only the free camera flies on the keys).
    internal void Simulate(in TickInput latched, bool watching)
    {
        // The game stands still under a menu that pauses it, under the title, and before any map.
        if (Frozen())
        {
            return;
        }

        var tick = latched;

        // A demo playing: its events, then its input instead of the player's.
        if (playback != null)
        {
            if (playback.Done)
            {
                FinishPlayback();
                return;
            }

            tick = playback.BeforeTick(text => CampaignDemo.Apply(session, text, commands));
        }

        // --fire-demo counts the ticks played, not the loop's: a mission screen or a slow load before
        // the first playing tick must not swallow the shots.
        var demo = options.FireDemo && session.Phase == SessionPhase.Playing ? demoTicks++ : -1;
        var weaponInput = watching ? default : controls.WeaponInput(tick);
        if (demo is >= 20 and < 80)
        {
            weaponInput = weaponInput with { Fire = true };
        }

        if (demo == 30)
        {
            weaponInput = weaponInput with { Throw = true, SelectThrown = 14 };
        }

        recording?.BeforeTick(HeadlessCampaign.LevelOf(session.Difficulty), session.RobotAi);
        var started = Stopwatch.GetTimestamp();
        using (Prof.Zone(AppZones.Session))
        {
            controls.Tick(session, tick, weaponInput);
        }

        recording?.AfterTick(session, tick);
        if (playback?.AfterTick(session) is { } desync)
        {
            Log.Warn("Demo", $"{playbackName}: {desync}");
            hud.ShowStatus(string.Create(CultureInfo.InvariantCulture, $"DEMO DESYNC AFTER TICK {desync.Tick}"));
        }

        var ticked = Stopwatch.GetTimestamp();
        sections.Add(Section.Scripts, ticked - started);
        (perfTicks, perfTickTime) = (perfTicks + 1, perfTickTime + ticked - started);
        using (Prof.Zone(AppZones.AudioUpdate))
        {
            EngineLoop();
            var ears = weapons.RocketEye is { } rocketEar ? rocketEar.Eye : player.EyePosition;
            sounds?.SetListener(ears, PlayerController.Direction(player.Yaw, player.Pitch), Vector3.UnitY);
            if (soundCapture == null)
            {
                audio.Update(1f / TicksPerSecond);
            }
        }

        sections.Add(Section.Audio, Stopwatch.GetTimestamp() - ticked);
        using (Prof.Zone(AppZones.HudTick))
        {
            hud.Tick(1f / TicksPerSecond);
        }
    }

    // The engine's loop while riding (careng1 for the jeep, hk2 for the HK), riding with the eye.
    void EngineLoop()
    {
        var want = vehicles.Riding && session.Phase == SessionPhase.Playing ? VehicleController.EngineSound(vehicles.Mode) : -1;
        if (want != engineSound)
        {
            engineLoop?.Stop();
            engineLoop = want >= 0 ? sounds?.PlayLoop3D(want, player.EyePosition) : null;
            engineSound = want;
        }

        if (engineLoop != null)
        {
            engineLoop.Position = player.EyePosition;
        }
    }
}
