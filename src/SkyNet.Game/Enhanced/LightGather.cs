using System.Numerics;
using RetroEng.Render;
using RetroEng.Render.Lights;

namespace SkyNet.Game.Enhanced;

/// <summary>
/// The frame's point lights for <see cref="Renderer.Lights"/>: everything the level and the weapons
/// give, those that can reach the view kept, the most important first, up to a budget. The cluster
/// grid lights the first <see cref="ClusterGrid.MaxLights"/> and keeps the first
/// <see cref="ClusterGrid.MaxPerCluster"/> of each froxel in input order, so the order is the choice:
/// <code>
///   importance = energy · max colour / max(distance to the eye, MinDistance)
///   dropped: no energy, or its sphere wholly past <c>reach</c> from the eye
/// </code>
/// Reuses its arrays: nothing is allocated once they have grown.
/// </summary>
public sealed class LightGather
{
    /// <summary>Nearer than this a light ranks as if this far (an explosion in the face doesn't
    /// outrank every lamp by a thousandfold).</summary>
    public const float MinDistance = 50f;

    private readonly List<PointLight> _all = [];
    private float[] _keys = new float[64];
    private PointLight[] _sorted = new PointLight[64];
    private int _count;

    /// <summary>The list the frame's lights are added to (cleared by <see cref="Begin"/>).</summary>
    public List<PointLight> All => _all;
    /// <summary>The lights handed to the renderer by the last <see cref="Finish"/>.</summary>
    public ReadOnlyMemory<PointLight> Lights => _sorted.AsMemory(0, _count);
    /// <summary>Lights the last <see cref="Finish"/> left out past the budget.</summary>
    public int OverBudget { get; private set; }

    public void Begin() => _all.Clear();

    /// <summary>A light's rank for an eye at <paramref name="eye"/>.</summary>
    public static float Importance(in PointLight light, Vector3 eye) =>
        light.Energy * MathF.Max(light.Colour.X, MathF.Max(light.Colour.Y, light.Colour.Z))
        / MathF.Max(Vector3.Distance(light.Position, eye), MinDistance);

    /// <summary>Sorts the gathered lights by importance for <paramref name="eye"/>, drops those that
    /// can't reach within <paramref name="reach"/> of it, keeps at most <paramref name="budget"/>
    /// (and <see cref="ClusterGrid.MaxLights"/>).</summary>
    public ReadOnlyMemory<PointLight> Finish(Vector3 eye, float reach, int budget = ClusterGrid.MaxLights)
    {
        if (_keys.Length < _all.Count)
        {
            var size = Math.Max(_all.Count, _keys.Length * 2);
            _keys = new float[size];
            _sorted = new PointLight[size];
        }

        var n = 0;
        foreach (var l in _all)
        {
            if (!(l.Energy > 0f) || !(l.Range > 0f) || Vector3.Distance(l.Position, eye) - l.Range > reach)
            {
                continue;
            }

            // Descending by importance: sort the negated key.
            _keys[n] = -Importance(l, eye);
            _sorted[n] = l;
            n++;
        }

        Array.Sort(_keys, _sorted, 0, n);
        _count = Math.Min(n, Math.Min(Math.Max(budget, 0), ClusterGrid.MaxLights));
        OverBudget = n - _count;
        return Lights;
    }
}
