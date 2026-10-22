using SkyNet.Formats;

namespace SkyNet.Game.Water;

/// <summary>
/// A map's water surface (SKYNET.EXE v1.01). FUN_00120f85 at map load: marker type 103, else 104,
/// gives the surface at the marker's Y − 0x10 (DOS Y down: 16 units above it) and picks the look —
/// 103: HAZE.005 row 8, span routine 0x1587a0, glide 0x1000 (16 u/s); 104: HAZE.006 row 3, span
/// routine 0x15dc00, glide 0x800 (8 u/s). The fine level is [0x38908] (24.8), its target [0x3890c].
/// Acts 0xD6-0xDA (handler 0x121160) move the target: 0xD6 to the entity's own Y, the others by a
/// delta; FUN_00121083 glides the level towards it every frame.
/// World axes are the port's: Y up (world Y = −DOS Y).
/// </summary>
public sealed class WaterLevel
{
    public const int MarkerClear = 103, MarkerMurky = 104;
    /// <summary>The surface is this far above the marker (DOS Y − 0x10).</summary>
    public const float MarkerLift = 0x10;
    /// <summary>The glide, units per second: [0x38910] 0x1000 / 0x800 in 24.8.</summary>
    public const float GlideClear = 16f, GlideMurky = 8f;

    public WaterLevel(int marker, float surface)
    {
        Marker = marker;
        Surface = Target = PreviousSurface = surface;
    }

    /// <summary>103 or 104.</summary>
    public int Marker { get; }
    /// <summary>The surface now (world Y).</summary>
    public float Surface { get; private set; }
    /// <summary>The surface at the previous tick's end (render interpolation; never simulated).</summary>
    public float PreviousSurface { get; private set; }
    /// <summary>The surface drawn between the last two ticks.</summary>
    public float SurfaceAt(float alpha) => alpha >= 1f ? Surface : float.Lerp(PreviousSurface, Surface, Math.Max(alpha, 0f));
    /// <summary>Where the surface glides to (world Y).</summary>
    public float Target { get; private set; }
    public float Glide => Marker == MarkerClear ? GlideClear : GlideMurky;
    /// <summary>The HAZE file and row the post-process remaps through.</summary>
    public string HazeFile => Marker == MarkerClear ? "HAZE.005" : "HAZE.006";
    public int HazeRow => Marker == MarkerClear ? 8 : 3;

    /// <summary>The map's water: its first marker 103 (else 104), or null for a dry map.</summary>
    public static WaterLevel? Find(MapFile map)
    {
        foreach (var marker in (ReadOnlySpan<int>)[MarkerClear, MarkerMurky])
        {
            foreach (var e in map.Entities)
            {
                if (e.Variant == MapEntityVariant.Sprite && e.IsMarker && e.MarkerType == marker)
                {
                    return new WaterLevel(marker, -(e.Y - MarkerLift));
                }
            }
        }

        return null;
    }

    /// <summary>A water act: <paramref name="absolute"/> sends the target to <paramref name="value"/>
    /// (world Y), else moves it by <paramref name="value"/> (up positive).</summary>
    public void Move(float value, bool absolute) => Target = absolute ? value : Target + value;

    /// <summary>Puts the surface and its target at <paramref name="surface"/> (a saved state).</summary>
    public void Set(float surface, float target)
    {
        Surface = PreviousSurface = surface;
        Target = target;
    }

    /// <summary>A tick's start: the surface now becomes the one the frames glide from.</summary>
    public void KeepPose() => PreviousSurface = Surface;

    /// <summary>One frame of the glide (FUN_00121083): at most <see cref="Glide"/> × dt towards the target.</summary>
    public void Tick(float dt)
    {
        var gap = Target - Surface;
        var step = Glide * dt;
        Surface = MathF.Abs(gap) <= step ? Target : Surface + MathF.Sign(gap) * step;
    }
}
