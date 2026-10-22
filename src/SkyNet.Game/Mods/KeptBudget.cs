namespace SkyNet.Game.Mods;

/// <summary>
/// Which kept items (a mod's surfaces on the GPU, <see cref="KeptSurfaces"/>) stay across maps: each
/// with its size and the map it was last used on. Everything the current map and the one just left
/// use stays (an interior and back); of the rest, the least recently used go once they pass
/// <see cref="Budget"/> bytes.
/// <code>
///   budget.Use(surface, bytes)     // drawn on this map
///   budget.NextMap()               // a map is entered
///   budget.Over()                  // the map is built: what to free (unused here, oldest first)
/// </code>
/// </summary>
public sealed class KeptBudget<T>(long budget, IEqualityComparer<T>? comparer = null) where T : notnull
{
    private readonly Dictionary<T, (long Bytes, int Map)> _items = new(comparer);
    private int _map;

    /// <summary>Bytes kept that neither the current map nor the one just left use.</summary>
    public long Budget { get; } = budget;

    /// <summary>Bytes of everything kept.</summary>
    public long Bytes { get; private set; }

    /// <summary>Items kept.</summary>
    public int Count => _items.Count;

    public bool Contains(T item) => _items.ContainsKey(item);

    /// <summary>The current map uses <paramref name="item"/> (kept from now on if new).</summary>
    public void Use(T item, long bytes)
    {
        if (_items.TryGetValue(item, out var kept))
        {
            bytes = kept.Bytes;
        }
        else
        {
            Bytes += bytes;
        }

        _items[item] = (bytes, _map);
    }

    /// <summary>A new map: what was used so far belongs to the earlier ones.</summary>
    public void NextMap() => _map++;

    /// <summary>The items to free (forgotten here): used on neither the current map nor the one
    /// just left, least recently used first, until the rest fits <see cref="Budget"/>.</summary>
    public List<T> Over()
    {
        var unused = _items.Where(i => i.Value.Map < _map - 1).OrderBy(i => i.Value.Map).ToList();
        var spare = unused.Sum(i => i.Value.Bytes);
        var freed = new List<T>();
        foreach (var (item, (bytes, _)) in unused)
        {
            if (spare <= Budget)
            {
                break;
            }

            _items.Remove(item);
            (Bytes, spare) = (Bytes - bytes, spare - bytes);
            freed.Add(item);
        }

        return freed;
    }
}
