using System.Numerics;
using RetroEng.Render;
using SkyNet.Game;
using SkyNet.Game.DevTools;
using SkyNet.Game.Enemies;
using SkyNet.Game.Hud;
using SkyNet.Game.Net;
using SkyNet.Game.Vehicles;
using SkyNet.Game.Weapons;
using DroppedSprite = SkyNet.Game.Campaign.DroppedSprite;

/// <summary>
/// What a frame draws, caught by the simulation after its ticks (AppHost.CaptureFrame) and drawn from on
/// the frame-build thread (AppHost.BuildFrame) while the simulation runs the next ticks
/// (docs/plans/engine-threads.md B1). Pooled by the frame build's exchange: every list is cleared
/// and refilled, nothing is allocated per frame once the lists have grown.
/// <code>
///   simulation:  ticks ─► Capture(S) ─► ticks ─► ...
///   frame build:            └─► BuildFrame(S): level, robots, avatars, weapons, HUD, lights ─► Present
/// </code>
/// Interpolation: the camera and the cockpit are caught at the frame's alpha between the last two
/// ticks the simulation keeps (PlayerController.View, VehicleController.CockpitWorld), so the pair
/// travels inside the snapshot. A <see cref="Live"/> frame also draws the menus, the console, the
/// touch controls and the resource churn from live state while the simulation waits for it.
/// </summary>
internal sealed class FrameSnapshot
{
    /// <summary>Menus, the console or the touch controls are on screen: drawn live, the simulation waits.</summary>
    public bool Live;
    public long AnimationMs;
    public int Width, Height;

    // The views.
    public StereoOutput Stereo;
    public bool StereoGrid;
    public bool ReversedZ;
    /// <summary>The main view's style: DISPLAY's RENDERER, or POINT CLOUD for TERMINATOR VISION
    /// (<see cref="MachinePoints"/>, then in its machine colours).</summary>
    public RenderStyle Style;
    public bool MachinePoints;
    public Vector3 Eye;
    public FrameViews Views;
    public Vector4 Sky;
    public string? Shot;
    /// <summary>--capture's file for this frame; null: none.</summary>
    public string? CaptureShot;

    // The map: null when none is shown (or a full-screen menu hides it).
    public Level? Level;
    public float PixelAngle;
    public readonly LevelFrame LevelFrame = new();
    public EnemyRenderer? Robots;
    public readonly RobotFrame RobotFrame = new();
    public readonly DmFrame Bodies = new();

    // The cockpit.
    public bool Cockpit;
    public VehicleMode CockpitMode;
    public Matrix4x4 CockpitWorld;

    // The weapons, the drops and the panel (none while riding the torpedo).
    public bool Riding;
    /// <summary>The automap is up: it is drawn in place of the map.</summary>
    public bool Automap;
    public readonly WeaponFrame Weapons = new();
    public readonly List<DroppedSprite> Drops = [];
    public readonly List<DroppedSprite> DmItems = [];
    public ViewModelState ViewModel;
    public ViewModelPose ViewModelPose;
    public HudState Hud;
    /// <summary>The HUD's style this frame: the view centre, the gun's foot and the HUD follow it.</summary>
    public HudStyle HudStyle;
    /// <summary>The screen's pixels per millimetre for the UI's minimum text (UiMetrics.PxPerMm; 0: none).</summary>
    public float PxPerMm;
    public readonly HudText HudText = new();
    public bool Underwater;

    // The end banner: the art's name, else the text.
    public string? Banner;
    public string? BannerText;

    // The deathmatch's read-outs.
    public bool Deathmatch;
    public bool ScoreBoard;
    public readonly DmHudFrame DmHud = new();

    // The enhanced look's lights (none: the original look, or no map shown).
    public bool LightsOn;
    public readonly List<PointLight> Lights = [];
    public float LightReach;
    public int LightBudget;
    // The storm's flash and bolt (a mod's weather/storm.json, outdoors).
    public SkyNet.Game.Enhanced.StormFrame Storm;
    // The rain's clock and cover (a mod's weather/rain.json, outdoors).
    public readonly SkyNet.Game.Enhanced.RainView Rain = new();
    // A mod's clouds, haze, dust and wind (outdoors).
    public readonly SkyNet.Game.Enhanced.AirView Air = new();

    // The developer overlay.
    public bool OverlayOn;
    public bool OverlayDebug;
    public IReadOnlyList<string> OverlayLines = [];
    public float OverlayTop;
    public float ConsolePixel;
    public string? FpsText;
    public ObjectiveTarget? Pointer;
    public Matrix4x4 ViewProjection;
}
