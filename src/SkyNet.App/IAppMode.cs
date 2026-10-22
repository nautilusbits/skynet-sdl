using RetroEng.Loop;

/// <summary>What the app is doing in its window: the title and its menus, a campaign's play, a network
/// deathmatch (an editor later). <see cref="AppHost"/> owns the window, the renderer, the sound, the
/// console and the menus, and hands each frame's input, ticks and draw to the mode of the moment
/// (<see cref="AppHost.Mode"/>). A dedicated server is no mode of the window: it runs headless before
/// any of this (Program.cs, SkyNet.Game.Net.DmDedicatedRunner).</summary>
internal interface IAppMode
{
    /// <summary>The mode's name (diagnostics).</summary>
    string Name { get; }

    /// <summary>Once a frame, after the shared keys (the menu script, F11 / F12, the console) and
    /// before the ticks: the mode's own keys, or the open menu fed this frame's input.</summary>
    void Pump(float seconds);

    /// <summary>One fixed tick (<see cref="AppTools.TicksPerSecond"/> a second).</summary>
    void Tick(in TickInput input, int tick);

    /// <summary>One frame drawn and presented, <paramref name="alpha"/> of the way to the next tick.</summary>
    void Render(float alpha);
}
