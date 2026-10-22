using System.Drawing;
using System.Numerics;
using RetroEng.Platform;
using SkyNet.Game;
using SkyNet.Game.Automap;
using SkyNet.Game.Campaign;
using SkyNet.Game.Menu;
using SkyNet.Game.Player;

/// <summary>
/// The automap in the app (SKYNET.EXE v1.01 FUN_0013dc00, the CONTROLS AUTOMAP key): the soldier's
/// view marks what it shows seen every frame; the key opens a full screen that pauses the campaign
/// (none in a deathmatch, as DOS's network game has none); its own keys turn, tilt and zoom it;
/// Esc, the key again or EXIT closes it.
/// <code>
///   CaptureFrame ── MarkSeen ──► session.Seen ◄── save / load, map change, clearautomap
///   GameKeysOrMenu ── AutomapFrame ──► AutomapScreen (camera, bar)
///   BuildFrame ── DrawAutomap ──► AutomapView (art, then the 3D inset)
/// </code>
/// </summary>
internal sealed partial class AppHost
{
    private readonly AutomapScreen automap = new();
    private AutomapView? automapView;
    private AutomapIndex? automapIndex;
    // The level the index was built for (made again for each map).
    private Level? automapLevel;

    // The automap's key with the menus shut: opens it on a campaign map being played.
    bool OpenAutomap()
    {
        if (level == null || dm != null || session.Phase != SessionPhase.Playing || !soldier.Alive)
        {
            return false;
        }

        WaitForFrames();
        var (eye, yaw, pitch, _) = player.View(1f);
        automap.Open(eye, yaw, PlayerController.Direction(yaw, pitch));
        ReleaseMouse();
        return true;
    }

    // A frame of the open automap: its keys and bar; false when it is shut (the game's keys go on).
    bool AutomapFrame(float seconds)
    {
        if (!automap.IsOpen)
        {
            return false;
        }

        if (level == null)
        {
            automap.Close();
            return false;
        }

        var (_, height) = FrameSize();
        var frame = MenuFrame.ForCanvas(renderer.CanvasWidth, height);
        var canvas = renderer.CanvasPoint(input.PointerX, input.PointerY);
        var dos = frame.ToDos(canvas);
        automap.Update(new AutomapInput
        {
            Keys = controls.AutomapHeld(input.IsDown),
            Close = input.WasPressed(controls.Automap) || input.WasPressed(pause) || input.WasPressed(MenuKey.Back),
            // The bar lights under a pointer that moved (DOS's cursor), not at a still one.
            Pointer = input.PointerMoved || input.WasClicked(Pointer.Left) ? new PointF(dos.X, dos.Y) : null,
            Click = input.WasClicked(Pointer.Left),
            Held = input.IsHeld(Pointer.Left),
        }, seconds);
        if (!automap.IsOpen)
        {
            ResumeGame();
        }

        return true;
    }

    // DOS FUN_00136fb1 after each frame: what the soldier's view holds is seen.
    void MarkSeen(Matrix4x4 viewProjection, Vector3 eye)
    {
        if (level == null || dm != null || automap.IsOpen)
        {
            return;
        }

        if (!ReferenceEquals(level, automapLevel))
        {
            automapIndex = AutomapIndex.Build(level.Map, data.LoadModel);
            automapLevel = level;
        }

        // The game view's far clip (indoors none), as Level.Draw has it.
        var far = level.Outdoor || level.FarOverride != null ? level.Far : float.MaxValue;
        automapIndex!.Mark(session.Seen, viewProjection, eye, far);
    }

    // The automap's frame in place of the map's: the art on the canvas, the 3D over it.
    void DrawAutomap(Level map)
    {
        if (automapIndex == null || !ReferenceEquals(map, automapLevel))
        {
            automapIndex = AutomapIndex.Build(map.Map, data.LoadModel);
            automapLevel = map;
        }

        menuPainter.Use(null);
        AutomapView.DrawArt(menuPainter, automap);
        automapView ??= new AutomapView(renderer, data);
        var screen = menuPainter.Screen.Canvas(AutomapView.Window);
        automapView.DrawScene(map, automap, automapIndex, session.Seen, screen);
    }
}
