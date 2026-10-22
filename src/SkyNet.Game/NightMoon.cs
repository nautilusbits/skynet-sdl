using System.Globalization;
using System.Numerics;
using SkyNet.Formats;

namespace SkyNet.Game;

/// <summary>
/// The night moon's joke (SKYNET.EXE v1.01): shoot it and it says "OW!"; after enough hits it
/// falls out of the sky.
/// <code>
///   FUN_0013a132 (night maps, [0x54c96] == 1): draws TEXTURE.359 record 2 at the projection of
///       (0x64000, −0x3e800 + [0x5bd14], 0x32000) and keeps its screen centre in [0x5bd04/08]
///   FUN_001261b1 (the trigger: the gun, the jeep/HK gun; not the thrown items, FUN_001263cc)
///       nothing under the crosshair ([0x44604] == 1, FUN_00125bef) and the crosshair
///       (the projection centre) within 15 px of that centre on both axes →
///       FUN_0012f8f7("OW!" at 0x44608, colour 8), [0x5bd18] += 1; ≥ 25 → [0x5bd10] = 0x18800
///   FUN_0013a210 (every frame, dt = [0x43200] in 16.16 s):
///       [0x5bd0c] += (accel / 2)·dt, [0x5bd14] += [0x5bd0c]·dt;
///       past 0x7d000 → accel, speed and hits 0 (the drop stays: the moon lies below the horizon)
///   FUN_0013a1e7 (every map load, FUN_00121bdf): all of it 0
/// </code>
/// No sound, nothing saved (a load enters the map afresh), nothing sent over the network.
/// </summary>
public sealed class NightMoon
{
    /// <summary>The moon's rest vector before the camera's rotation (DOS x, y down, z).</summary>
    public const int RestX = 0x64000, RestY = -0x3e800, RestZ = 0x32000;
    /// <summary>The fall's acceleration the 25th hit sets ([0x5bd10], 16.16 DOS units a second²; halved as used).</summary>
    public const int FallAcceleration = 0x18800;
    /// <summary>The hit that starts the fall (cmp [0x5bd18], 0x19).</summary>
    public const int HitsToFall = 25;
    /// <summary>The drop at which the fall ends (FUN_0013a210: cmp 0x7d000): the moon mirrored below the horizon.</summary>
    public const int Landed = 0x7d000;
    /// <summary>The crosshair's reach round the moon's centre, DOS screen pixels on each axis.</summary>
    public const int HitPixels = 15;
    /// <summary>DOS's 320 × 200 projection (x 200, y 180 pixels per unit of x/z, y/z).</summary>
    public const float FocalX = 200f, FocalY = 180f;
    /// <summary>The line (string at 0x44608) and its colour (FUN_0012f8f7's DL).</summary>
    public const string Text = "OW!";
    public const byte Colour = 8;

    /// <summary>The map draws the moon: a night outdoor map (not the dusk ones, not indoors).</summary>
    public bool Shown { get; private set; }
    /// <summary>Hits since the map's load or the last landing ([0x5bd18]).</summary>
    public int Hits { get; private set; }
    /// <summary>The fall's acceleration ([0x5bd10]), its speed ([0x5bd0c]) and the drop so far ([0x5bd14]), DOS 16.16 units.</summary>
    public int Acceleration { get; private set; }
    public int Speed { get; private set; }
    public int Drop { get; private set; }
    public bool Falling => Acceleration != 0 || Speed != 0;
    /// <summary>The fall ran its course on this map (the moon lies below the horizon).</summary>
    public bool Down => Drop > Landed;

    /// <summary>A shot on the moon: the line to show (<see cref="Text"/>, <see cref="Colour"/>).</summary>
    public event Action<string, byte>? Said;

    /// <summary>A map's load (FUN_0013a1e7): no hits, no fall.</summary>
    public void Reset(MoonSky sky)
    {
        Shown = sky == MoonSky.Shown;
        (Hits, Acceleration, Speed, Drop) = (0, 0, 0, 0);
    }

    /// <summary>The moon shows on the night outdoor maps: not the dusk ones (<see cref="Level.IsDusk"/>), not indoors.</summary>
    public static MoonSky SkyOf(int map, MapFile file) => file.IsOutdoor && !Level.IsDusk(map) ? MoonSky.Shown : MoonSky.None;

    /// <summary>One frame's fall (FUN_0013a210, the DOS 16.16 fixed-point steps).</summary>
    public void Tick(float dt)
    {
        var step = (int)MathF.Round(dt * 65536f);
        Speed += Mul(Acceleration >> 1, step);
        Drop += Mul(Speed, step);
        if (Drop > Landed)
        {
            (Acceleration, Speed, Hits) = (0, 0, 0);
        }
    }

    private static int Mul(int a, int b) => (int)(((long)a * b + 0x8000) >> 16);

    /// <summary>The moon's vector now in DOS axes (y down).</summary>
    public Vector3 DosVector => new(RestX, RestY + Drop, RestZ);

    /// <summary>The way to the moon now, world axes: DOS (x, y, z) → (x, −y, −z).</summary>
    public Vector3 Direction
    {
        get
        {
            var d = DosVector;
            return Vector3.Normalize(new Vector3(d.X, -d.Y, -d.Z));
        }
    }

    /// <summary>The moon's centre from the crosshair in DOS screen pixels (x right, y up) for a view
    /// along <paramref name="forward"/>; null when it is behind the eye (DOS does not draw it).</summary>
    public Vector2? Offset(Vector3 forward, Vector3 right, Vector3 up)
    {
        var d = Direction;
        var z = Vector3.Dot(d, forward);
        if (z <= 1e-3f)
        {
            return null;
        }

        return new Vector2(FocalX * Vector3.Dot(d, right) / z, FocalY * Vector3.Dot(d, up) / z);
    }

    /// <summary>The crosshair is within <see cref="HitPixels"/> of the moon's centre on both axes.</summary>
    public bool OnCrosshair(Vector3 forward, Vector3 right, Vector3 up) =>
        Shown && Offset(forward, right, up) is { } o && MathF.Max(MathF.Abs(o.X), MathF.Abs(o.Y)) < HitPixels;

    /// <summary>A trigger pull with nothing under the crosshair: a hit when the moon is on it.</summary>
    public bool Shoot(Vector3 forward, Vector3 right, Vector3 up)
    {
        if (!OnCrosshair(forward, right, up))
        {
            return false;
        }

        Hit();
        return true;
    }

    /// <summary>One hit: "OW!", the count, and from the 25th the fall (the console's <c>moon hit</c>).</summary>
    public void Hit()
    {
        Said?.Invoke(Text, Colour);
        Hits++;
        if (Hits >= HitsToFall)
        {
            Acceleration = FallAcceleration;
        }
    }

    /// <summary>Starts the fall now, as the 25th hit does (the console's <c>moon fall</c>).</summary>
    public void Fall()
    {
        Hits = Math.Max(Hits, HitsToFall);
        Acceleration = FallAcceleration;
    }

    /// <summary>The state for the console.</summary>
    public string Describe() => !Shown
        ? "No moon on this map (indoors or a dusk map)"
        : string.Create(CultureInfo.InvariantCulture,
            $"moon: {Hits} hits ({HitsToFall} to fall), {(Down ? "down below the horizon" : Falling ? "falling" : "in the sky")}, " +
            $"drop 0x{Drop:x} of 0x{Landed:x}, speed 0x{Speed:x}, elevation {MathF.Asin(Direction.Y) * 180f / MathF.PI:0.0}°");
}

/// <summary>Whether a map has the moon (<see cref="NightMoon.SkyOf"/>).</summary>
public enum MoonSky { None, Shown }
