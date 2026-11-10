using System.Numerics;
using SkyNet.Formats;

namespace SkyNet.Game.Campaign;

/// <summary>
/// A map's radiation (SKYNET.EXE v1.01). At map load FUN_0013b949 lists every marker 4 with a
/// strength (sub+2 &gt; 0) and FUN_0013b99f takes the first marker 8 as a floor: Y and dose (sub+2).
/// Every frame FUN_0013ba9a measures the dose at the eye lowered by half the eye height
/// (FUN_0013b9e0), hurts the soldier by it (FUN_00122b52, no grunt: the hit flash stays red) and
/// plays sound 19, or 20 from <see cref="Danger"/> on. Per source, in DOS integer units:
/// <code>
///   skip when 3/4 × rough distance (0x14d794: max + others / 4) &gt; strength
///   r = strength − exact distance (0x14d775); skip when r ≤ 0
///   sum += min(r × 50, 0x3200)            dose = (sum + 0x80) &gt;&gt; 8   (≤ 50 a source)
///   at or above the floor marker: dose += its dose
///   HP a frame = max(1, round(dose × seconds))
/// </code>
/// </summary>
public sealed class Radiation
{
    public const int SourceMarker = 4, FloorMarker = 8;
    /// <summary>The Geiger sounds (FUN_0012ef06 0x13 / 0x14).</summary>
    public const int SoundLow = 0x13, SoundHigh = 0x14;
    /// <summary>From this dose on: the high sound and RADCOUNT's DANGER frame.</summary>
    public const int Danger = 35;

    private const int PerUnit = 50, SourceCap = 0x3200;

    // DOS axes (Y down).
    private readonly (Vector3 At, int Strength)[] _sources;
    private readonly float _floorY;
    private readonly int _floorDose;

    private Radiation((Vector3, int)[] sources, float floorY, int floorDose)
    {
        _sources = sources;
        _floorY = floorY;
        _floorDose = floorDose;
    }

    /// <summary>The radiation of <paramref name="map"/>; null when it has none.</summary>
    public static Radiation? Find(MapFile map)
    {
        var markers = map.Entities.Where(e => e.Variant == MapEntityVariant.Sprite && e.IsMarker).ToList();
        var sources = markers
            .Where(e => e.MarkerType == SourceMarker && e.Param16 > 0)
            .Select(e => (new Vector3(e.X, e.Y, e.Z), e.Param16))
            .ToArray();
        var floor = markers.FirstOrDefault(e => e.MarkerType == FloorMarker);
        var floorDose = floor?.Param16 ?? 0;
        if (sources.Length == 0 && floorDose == 0)
        {
            return null;
        }

        return new Radiation(sources, floor?.Y ?? 0f, floorDose);
    }

    /// <summary>The dose a second at <paramref name="eye"/> (world axes, Y up) of a soldier whose
    /// eye stands <paramref name="eyeHeight"/> over his feet.</summary>
    public int Dose(Vector3 eye, float eyeHeight)
    {
        var p = new Vector3(eye.X, -eye.Y + eyeHeight / 2f, -eye.Z);
        var sum = 0;
        foreach (var (at, strength) in _sources)
        {
            var d = at - p;
            if (Rough(d) * 3 >> 2 > strength)
            {
                continue;
            }

            var r = strength - (int)d.Length();
            if (r <= 0)
            {
                continue;
            }

            sum += Math.Min(r * PerUnit, SourceCap);
        }

        var dose = (sum + 0x80) >> 8;
        if (_floorDose != 0 && p.Y <= _floorY)
        {
            dose += _floorDose;
        }

        return dose;
    }

    /// <summary>The hit points <paramref name="dose"/> takes in a frame of <paramref name="seconds"/>.</summary>
    public static float Points(int dose, float seconds) => dose <= 0 ? 0f : MathF.Max(1f, MathF.Round(dose * seconds));

    // 0x14d794: the largest axis plus a quarter of the other two.
    private static int Rough(Vector3 d)
    {
        int x = Math.Abs((int)d.X), y = Math.Abs((int)d.Y), z = Math.Abs((int)d.Z);
        var max = Math.Max(x, Math.Max(y, z));
        return max + (x >> 2) + (y >> 2) + (z >> 2) - (max >> 2);
    }
}
