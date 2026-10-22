using System.Numerics;
using RetroEng.Render;
using SkyNet.Formats;
using SkyNet.Game.Enhanced;

namespace SkyNet.Game;

/// <summary>The enhanced look's night sky (<see cref="StarrySky"/>), drawn in place of the DOS stars
/// and moon quad; the moon keeps the DOS way, size and fall (<see cref="NightMoon"/>).</summary>
public sealed partial class Level
{
    /// <summary>The near side's grey, square (data/sky, embedded).</summary>
    private const string MoonResource = "sky/moon.gray";

    private NightLook? _nightLook;
    private List<NightStar> _nightStars = [];
    private MeshHandle _nightStarMesh, _nightBand, _nightMoon;
    private int _nightStarIndices, _nightBandIndices;
    private ushort[] _nightMoonIndices = [];
    private Material _nightAdded;
    private float _nightPixel;
    private Vector2[] _moonPlaces = [];
    private uint[] _moonColours = [];
    private Vector3 _nightMoonWay;

    /// <summary>The enhanced look's night sky on a night map; null on the others.</summary>
    internal NightLook? NightLook => _nightLook;

    /// <summary>The enhanced night is drawn (not the DOS stars and moon quad).</summary>
    private bool NightShown => _enhanced && !_nightMoon.IsNone;

    /// <summary>The night's meshes: the stars (sized on the first draw), the Milky Way, the moon.</summary>
    private void BuildNight(Renderer renderer, Palette palette)
    {
        if (_moon.IsNone)
        {
            return;
        }

        var look = NightSkyTable.Current.For(Number);
        var dos = new (Vector3 Way, Vector3 Tint)[StarCount];
        for (var i = 0; i < StarCount; i++)
        {
            var t = _starTints[i];
            dos[i] = (_starDirections[i], new Vector3(Srgb.ToLinear((t & 0xff) / 255f), Srgb.ToLinear(((t >> 8) & 0xff) / 255f),
                Srgb.ToLinear(((t >> 16) & 0xff) / 255f)));
        }

        _nightStars = StarrySky.Stars(look, dos);
        var (stars, starIndices) = StarrySky.StarMesh(_nightStars, StarDistance, 1f);
        _nightStarMesh = renderer.CreateMesh(stars, starIndices);
        _nightStarIndices = starIndices.Length;
        _nightPixel = 0f;

        var (band, bandIndices) = StarrySky.BandMesh(look, StarDistance);
        _nightBand = renderer.CreateMesh(band, bandIndices);
        _nightBandIndices = bandIndices.Length;
        var added = new RenderState(Queues.Background, Cull.None, Blend.Add, Depth.Off, CameraRelative: true);
        _nightAdded = Material.Flat(Vector4.One, added) with { Shading = Shading.Unlit, Unfogged = true };

        var albedo = MoonAlbedo();
        var (places, colours, moonIndices) = StarrySky.MoonMesh(look, albedo, albedo == null ? 0 : (int)MathF.Sqrt(albedo.Length));
        (_moonPlaces, _moonColours) = (places, colours);
        _nightMoonWay = _moonDrawn;
        _nightMoon = renderer.CreateMesh(NightMoonVertices(_nightMoonWay), moonIndices);
        _nightMoonIndices = moonIndices;
        _nightLook = look;
    }

    /// <summary>Queues the band, the stars (resized when a pixel's angle changes) and the moon on its
    /// way now (the shot moon falls).</summary>
    private void DrawNight(LevelFrame frame, float pixelAngle)
    {
        _renderer.Draw(_nightBand, 0, _nightBandIndices, _nightAdded);
        if (Math.Abs(pixelAngle - _nightPixel) > 1e-7f)
        {
            _nightPixel = pixelAngle;
            var (stars, indices) = StarrySky.StarMesh(_nightStars, StarDistance, StarDistance * MathF.Tan(pixelAngle));
            _renderer.UpdateMesh(_nightStarMesh, stars, indices);
        }

        _renderer.Draw(_nightStarMesh, 0, _nightStarIndices, _nightAdded);

        var way = frame.MoonWay == Vector3.Zero ? MoonDirection() : frame.MoonWay;
        if (way != _nightMoonWay)
        {
            _nightMoonWay = way;
            _renderer.UpdateMesh(_nightMoon, NightMoonVertices(way), _nightMoonIndices);
        }

        _renderer.Draw(_nightMoon, 0, _nightMoonIndices.Length, _nightAdded);
    }

    /// <summary>The moon's disc and halo round <paramref name="d"/>: as wide and tall as the DOS quad
    /// (<see cref="MoonVertices"/>), facing the eye.</summary>
    private Vertex[] NightMoonVertices(Vector3 d)
    {
        var w = MoonDistance * _moonTexels.X / DosFocal / 2f;
        var h = MoonDistance * _moonTexels.Y * DosPixelAspect / DosFocal / 2f;
        var right = Vector3.Normalize(Vector3.Cross(Vector3.UnitY, -d));
        var up = Vector3.Normalize(Vector3.Cross(-d, right));
        var c = d * MoonDistance;
        var vertices = new Vertex[_moonPlaces.Length];
        for (var i = 0; i < vertices.Length; i++)
        {
            var p = _moonPlaces[i];
            vertices[i] = new Vertex(c + right * (p.X * w) + up * (p.Y * h), Vector2.Zero, _moonColours[i]);
        }

        return vertices;
    }

    private IEnumerable<Material> NightMaterials()
    {
        if (_nightMoon.IsNone)
        {
            yield break;
        }

        yield return _nightAdded;
    }

    /// <summary>The LRO near side's grey (NASA SVS 4720, public domain); null when not embedded.</summary>
    private static byte[]? MoonAlbedo()
    {
        using var s = typeof(Level).Assembly.GetManifestResourceStream(MoonResource);
        if (s == null)
        {
            return null;
        }

        using var m = new MemoryStream();
        s.CopyTo(m);
        return m.ToArray();
    }
}
