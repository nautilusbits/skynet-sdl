namespace SkyNet.Game.Mods;

/// <summary>
/// GPU objects a map's renderer scope owns, by key: built on first use, forgotten when the map's
/// scope is released (<see cref="Release"/>), so a drawer that outlives the map (the drops' models,
/// the cockpit) builds them again instead of drawing destroyed handles.
/// <code>
///   cache.Get(model, Build)   // MAP.210: built
///   cache.Release()           // MAP.210's scope released
///   cache.Get(model, Build)   // MAP.211: built again
/// </code>
/// </summary>
public sealed class ScopedCache<TKey, TValue>(IEqualityComparer<TKey>? comparer = null) where TKey : notnull
{
    private readonly Dictionary<TKey, TValue> _items = new(comparer);

    /// <summary>What was built since the last release.</summary>
    public IEnumerable<TValue> Values => _items.Values;

    /// <summary><paramref name="key"/>'s object, built now if not yet in this scope.</summary>
    public TValue Get(TKey key, Func<TKey, TValue> build)
    {
        if (_items.TryGetValue(key, out var value))
        {
            return value;
        }

        value = build(key);
        _items[key] = value;
        return value;
    }

    /// <summary>Whether <paramref name="key"/>'s object is built in this scope.</summary>
    public bool Contains(TKey key) => _items.ContainsKey(key);

    /// <summary>The map's scope went: everything is built again on its next use.</summary>
    public void Release() => _items.Clear();
}
