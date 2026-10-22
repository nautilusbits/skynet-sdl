using System.Numerics;
using SkyNet.Formats;

namespace SkyNet.Game;

/// <summary>
/// The interior light model of the Godot port (main.gd _light_level): an ambient of
/// <see cref="Level.IndoorAmbient"/> plus every variant-2 light that starts on, as Godot lights a
/// vertex: energy × |N·L| × (1 − (d / range)⁴)² / d. The brightness becomes the HAZE.000 row
/// whose mean brightness over the palette is nearest, so it stays palette-exact. Pure CPU: the
/// level reads it at run time (<see cref="Level.ShadeAt"/>), the map baker shades the vertices with it.
/// </summary>
public sealed class IndoorShading
{
    private const float LightRangePerUnit = 10f, LightEnergyDiv = 14f;

    private readonly float[] _rowBrightness = new float[HazeTable.Steps];
    private readonly List<(Vector3 Position, float Range, float Energy)> _lights = [];

    public IndoorShading(MapFile map, HazeTable haze, Palette palette)
    {
        float Lum(int i)
        {
            var (r, g, b) = palette[i];
            return 0.299f * r + 0.587f * g + 0.114f * b;
        }

        var full = 0f;
        for (var i = 1; i < 256; i++) full += Lum(i);
        for (var s = 0; s < HazeTable.Steps; s++)
        {
            var sum = 0f;
            for (var i = 1; i < 256; i++) sum += Lum(haze.Map(s, i));
            _rowBrightness[s] = full > 0f ? sum / full : s / (HazeTable.Steps - 1f);
        }

        AmbientLevel = LevelOf(Ambient);
        foreach (var e in map.Entities)
        {
            if (e.Variant == MapEntityVariant.Light && e.LightEnable > 0)
            {
                _lights.Add((new Vector3(e.X, -e.Y, -e.Z),
                    Math.Clamp(Math.Abs(e.LightEnable) * LightRangePerUnit, 400f, 6000f),
                    Math.Clamp(e.LightIntensity / LightEnergyDiv, 0.4f, 3.5f)));
            }
        }
    }

    /// <summary>The shade level (HAZE.000 row) of the ambient alone.</summary>
    public int AmbientLevel { get; }

    public int LightCount => _lights.Count;

    private static float Ambient => (Level.IndoorAmbient.X + Level.IndoorAmbient.Y + Level.IndoorAmbient.Z) / 3f;

    /// <summary>The shade level at a point with a normal (zero normal: as if facing every light).</summary>
    public byte VertexShade(Vector3 position, Vector3 normal)
    {
        var light = Ambient;
        foreach (var (at, range, energy) in _lights)
        {
            var d = Vector3.Distance(at, position);
            if (d >= range || d < 1e-3f)
            {
                continue;
            }

            var nd = d / range;
            nd *= nd;
            nd *= nd;
            var window = (1f - nd) * (1f - nd);
            var ndotl = normal == Vector3.Zero ? 1f : MathF.Abs(Vector3.Dot(normal, (at - position) / d));
            light += energy * ndotl * window / d;
        }

        return (byte)LevelOf(light);
    }

    /// <summary>The HAZE.000 row nearest to a brightness (1: the palette as drawn).</summary>
    private int LevelOf(float brightness)
    {
        var best = HazeTable.Steps - 1;
        var bestError = float.MaxValue;
        for (var s = 0; s < _rowBrightness.Length; s++)
        {
            var error = MathF.Abs(_rowBrightness[s] - brightness);
            if (error < bestError)
            {
                (best, bestError) = (s, error);
            }
        }

        return best;
    }
}
