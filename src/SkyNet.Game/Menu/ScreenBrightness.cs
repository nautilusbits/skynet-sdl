using RetroEng.Render;

namespace SkyNet.Game.Menu;

/// <summary>3D VIEW's BACKLIGHT: the phone's screen while the game shows (Android's window
/// brightness; the desktop has none). Inside a closed headset adaptive brightness and the auto-dim
/// see a dark room and dim the picture, so <see cref="Auto"/> is full in the phone viewer.</summary>
public enum ScreenBrightness
{
    /// <summary><see cref="Max"/> in the phone viewer, <see cref="System"/> otherwise.</summary>
    Auto,
    /// <summary>The system's brightness (adaptive or the user's slider).</summary>
    System,
    Percent50,
    Percent75,
    Max,
}

/// <summary>BACKLIGHT's steps, names and the level the window gets, without the platform.</summary>
public static class ScreenBrightnessLevels
{
    /// <summary>The system's brightness: Android's BRIGHTNESS_OVERRIDE_NONE.</summary>
    public const float System = -1f;

    private static readonly ScreenBrightness[] Order =
        [ScreenBrightness.Auto, ScreenBrightness.System, ScreenBrightness.Percent50, ScreenBrightness.Percent75, ScreenBrightness.Max];

    /// <summary>The window's brightness: 0..1, or <see cref="System"/>.</summary>
    public static float Level(ScreenBrightness setting, StereoLayout layout) => setting switch
    {
        ScreenBrightness.Auto => layout == StereoLayout.Viewer ? 1f : System,
        ScreenBrightness.Percent50 => 0.5f,
        ScreenBrightness.Percent75 => 0.75f,
        ScreenBrightness.Max => 1f,
        _ => System,
    };

    public static ScreenBrightness Step(ScreenBrightness setting, int step)
    {
        var i = Array.IndexOf(Order, setting);
        return Order[i < 0 ? 0 : ((i + step) % Order.Length + Order.Length) % Order.Length];
    }

    public static string Name(ScreenBrightness setting) => setting switch
    {
        ScreenBrightness.System => "SYSTEM",
        ScreenBrightness.Percent50 => "50%",
        ScreenBrightness.Percent75 => "75%",
        ScreenBrightness.Max => "MAX",
        _ => "AUTO",
    };
}

/// <summary>Hands the window's brightness to the platform's hook when it changes (GameActivity on
/// Android); without a hook (the desktop) a no-op.</summary>
public sealed class WindowBrightness(Action<float>? hook)
{
    private float? _applied;

    /// <summary>The level last handed on, null before the first.</summary>
    public float? Applied => _applied;

    /// <summary>Each frame: the setting and the layout's level, handed on once per change.</summary>
    public void Update(ScreenBrightness setting, StereoLayout layout)
    {
        if (hook == null)
        {
            return;
        }

        var level = ScreenBrightnessLevels.Level(setting, layout);
        if (_applied == level)
        {
            return;
        }

        _applied = level;
        hook(level);
    }
}
