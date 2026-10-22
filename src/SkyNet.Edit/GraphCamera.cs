using System.Numerics;

namespace SkyNet.Edit;

/// <summary>
/// The node canvas's pan and zoom: graph units to screen pixels and back.
/// <code>
///   screen = Origin + Offset + graph * Zoom
///   (Origin: the canvas's top left on screen; Offset: the pan, pixels)
/// </code>
/// </summary>
public sealed class GraphCamera
{
    /// <summary>Zoom limits: far enough to see a big map's graph, near enough to read a node.</summary>
    public const float MinZoom = 0.15f, MaxZoom = 2.5f;

    // Fit leaves this share of the canvas around the framed nodes; never zooms past 1:1 for it.
    private const float FitMargin = 0.9f, FitMaxZoom = 1f;

    /// <summary>The canvas's top left corner on screen.</summary>
    public Vector2 Origin { get; set; }

    /// <summary>The canvas's size, pixels.</summary>
    public Vector2 Size { get; set; }

    /// <summary>The pan, pixels from <see cref="Origin"/> to graph point (0, 0).</summary>
    public Vector2 Offset { get; set; }

    public float Zoom { get; private set; } = 1f;

    public Vector2 ToScreen(Vector2 graph) => Origin + Offset + graph * Zoom;

    public Vector2 ToGraph(Vector2 screen) => (screen - Origin - Offset) / Zoom;

    /// <summary>Pans by <paramref name="pixels"/>.</summary>
    public void Pan(Vector2 pixels) => Offset += pixels;

    /// <summary>Zooms by <paramref name="factor"/>, the graph point under <paramref name="screen"/> staying put.</summary>
    public void ZoomAt(Vector2 screen, float factor)
    {
        var anchor = ToGraph(screen);
        Zoom = Math.Clamp(Zoom * factor, MinZoom, MaxZoom);
        Offset = screen - Origin - anchor * Zoom;
    }

    /// <summary>Zooms and pans so <paramref name="min"/>..<paramref name="max"/> (graph units) fills the canvas, centred.</summary>
    public void Fit(Vector2 min, Vector2 max)
    {
        var extent = Vector2.Max(max - min, Vector2.One);
        var zoom = MathF.Min(Size.X / extent.X, Size.Y / extent.Y) * FitMargin;
        Zoom = Math.Clamp(zoom, MinZoom, FitMaxZoom);
        CentreOn((min + max) * 0.5f);
    }

    /// <summary>Pans so graph point <paramref name="graph"/> is in the canvas's middle.</summary>
    public void CentreOn(Vector2 graph) => Offset = Size * 0.5f - graph * Zoom;
}
