using RetroEng.Commands;

namespace SkyNet.Game.DevTools;

/// <summary>Which developer displays are on: the FPS readout in the corner (F11, <c>fps</c>) and the
/// debug overlay (F12, <c>debug</c>, <c>--debug-overlay</c>). Both are port-only aids, kept across
/// maps; the app saves them in the settings (<see cref="Changed"/>) and draws them.
/// <code>
///   F11 / fps [on|off]   ──► Fps   ──┐
///   F12 / debug [on|off] ──► Debug ──┴──► Changed ──► the settings, the zone timer
/// </code></summary>
public sealed class DevDisplays
{
    public bool Fps { get; private set; }

    public bool Debug { get; private set; }

    /// <summary>A display was switched (by a key or a command, not by <see cref="Restore"/>).</summary>
    public event Action<DevDisplays>? Changed;

    /// <summary>Sets both without <see cref="Changed"/> (the settings at start, the command line).</summary>
    public void Restore(bool fps, bool debug) => (Fps, Debug) = (fps, debug);

    public void SetFps(bool on)
    {
        if (Fps != on)
        {
            Fps = on;
            Changed?.Invoke(this);
        }
    }

    public void SetDebug(bool on)
    {
        if (Debug != on)
        {
            Debug = on;
            Changed?.Invoke(this);
        }
    }

    public void ToggleFps() => SetFps(!Fps);

    public void ToggleDebug() => SetDebug(!Debug);

    /// <summary>The console's <c>fps [on|off]</c> and <c>debug [on|off]</c> (no argument toggles).</summary>
    public static void Register(CommandRegistry registry, DevDisplays displays)
    {
        IEnumerable<string> OnOff(int index) => index == 0 ? ["on", "off"] : [];
        registry.Add(new Command("fps", "fps [on|off]", args =>
        {
            displays.SetFps(args.Switch(0, displays.Fps));
            return $"fps {(displays.Fps ? "on" : "off")}";
        })
        { Help = "the FPS readout in the corner (F11)", Arguments = OnOff });
        registry.Add(new Command("debug", "debug [on|off]", args =>
        {
            displays.SetDebug(args.Switch(0, displays.Debug));
            return $"debug overlay {(displays.Debug ? "on" : "off")}";
        })
        { Help = "the debug overlay: frame costs, renderer, position, objectives (F12)", Arguments = OnOff, Aliases = ["overlay"] });
    }
}
