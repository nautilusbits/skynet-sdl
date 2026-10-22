namespace SkyNet.Game.Automap;

/// <summary>
/// What the automap may show: the current map's records the player has seen, by file offset.
/// Wiped on a map change and by the clear cheat; kept in a save (format 9).
/// </summary>
public sealed class AutomapSeen
{
    private readonly HashSet<int> _offsets = [];

    internal int Count => _offsets.Count;

    internal bool Contains(int offset) => _offsets.Contains(offset);

    /// <summary>Marks a record seen; true when it was not before.</summary>
    internal bool Mark(int offset) => _offsets.Add(offset);

    internal void Clear() => _offsets.Clear();

    /// <summary>The offsets, sorted (a save's list: the same game saves to the same bytes).</summary>
    internal int[] Save() => [.. _offsets.Order()];

    /// <summary>A save's list in place of what is marked.</summary>
    internal void Restore(IEnumerable<int> offsets)
    {
        _offsets.Clear();
        _offsets.UnionWith(offsets);
    }
}
