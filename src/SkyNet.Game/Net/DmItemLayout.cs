namespace SkyNet.Game.Net;

/// <summary>
/// Where a deathmatch's items lie, as SKYNET.EXE v1.01 lays them out at a level start
/// (0x11c200 → 0x11c34d / 0x11c3b0 / 0x11c407). No random choice anywhere:
/// <list type="bullet">
/// <item>The spots are the arena's <b>marker 100</b> spots only, in marker order (0x11c34d; the
/// vehicles' markers 101 / 102 hold no item).</item>
/// <item>0x11c3b0 moves the spot at index 7 to index 1, then the one now at index 4 to index 1
/// (0x11c3c7, each only when the list is that long).</item>
/// <item>0x11c407 walks <see cref="DmProtocol.DosItems"/> in its order. A category of n items with
/// k kinds first lays one of each of its first min(n, k) kinds, then spreads the rest over the
/// weights: step = 0x10000 / rest, the pick starts at step / 2 and moves on by step, the kind
/// cursor kept.</item>
/// <item>Each item takes the next free spot (0x11c484); when none is left the layout stops, and
/// the remaining items (the later categories) are not placed.</item>
/// </list>
/// </summary>
public static class DmItemLayout
{
    /// <summary>The spots' order after 0x11c3b0: indices into the marker-order list.</summary>
    public static List<int> SpotOrder(int count)
    {
        var order = Enumerable.Range(0, count).ToList();
        foreach (var at in (ReadOnlySpan<int>)[7, 4])
        {
            if (order.Count > at)
            {
                var moved = order[at];
                order.RemoveAt(at);
                order.Insert(1, moved);
            }
        }

        return order;
    }

    /// <summary>The items (spot index, sprite) for the arena's item spots and the per-category
    /// counts (NETLEVEL keys).</summary>
    public static List<(int Spot, int Sprite)> Lay(int spots, Func<string, int> countOf)
    {
        var order = SpotOrder(spots);
        var placed = new List<(int, int)>();
        foreach (var (category, kinds) in DmProtocol.DosItems)
        {
            var n = countOf(category);
            if (n <= 0 || kinds.Length == 0)
            {
                continue;
            }

            var first = Math.Min(n, kinds.Length);
            for (var i = 0; i < first; i++)
            {
                if (!Place(kinds[i].Sprite))
                {
                    return placed;
                }
            }

            var rest = n - first;
            if (rest == 0)
            {
                continue;
            }

            var step = 0x10000 / rest;
            var pick = step >> 1;
            var k = 0;
            for (var i = 0; i < rest; i++)
            {
                while (pick > kinds[k].Weight && k < kinds.Length - 1)
                {
                    pick -= kinds[k].Weight;
                    k++;
                }

                if (!Place(kinds[k].Sprite))
                {
                    return placed;
                }

                pick += step;
            }
        }

        return placed;

        bool Place(int sprite)
        {
            if (placed.Count >= order.Count)
            {
                return false;
            }

            placed.Add((order[placed.Count], sprite));
            return true;
        }
    }
}
