/// <summary>What a platform's own code plugs into the game before <see cref="GameMain.Run"/> (GameActivity on
/// Android); unset on the desktop, where each is a no-op.</summary>
internal static class PlatformHooks
{
    /// <summary>The window's brightness: 0..1, or <see cref="SkyNet.Game.Menu.ScreenBrightnessLevels.System"/>
    /// for the system's. Called from the game's thread.</summary>
    public static Action<float>? WindowBrightness { get; set; }

    /// <summary>Called once, after the first frame is presented: Android's backdrop covers the
    /// black window until then. Called from the main thread.</summary>
    public static Action? FirstFrame { get; set; }
}
