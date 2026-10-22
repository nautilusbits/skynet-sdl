using System.Globalization;
using SkyNet.Formats;
using SkyNet.Game.Behaviour;
using SkyNet.Verify;

namespace SkyNet.Edit;

/// <summary>What one firing of a record did: the way it was set off (the verifier's mode), the effects in
/// the trigger graph's vocabulary, and the records they touched (the fired one first).</summary>
public sealed record FireResult(int Key, string Mode, IReadOnlyList<string> Effects, IReadOnlyList<int> Touched);

/// <summary>
/// Play > Fire trigger (editor plan step E9): a record's chain run once on a fresh, headless copy of the
/// edited map's behaviour by tools/SkyNet.Verify's <see cref="Driver"/>, the way its checks drive a node:
/// the map settles from its start with the player far away, then the node's primary mode (a floor pad by
/// walking on it; else use, prox, exit, shot, death, counter; else the chain itself) sets it off and the
/// movers arrive. Nothing of the document or of a running level changes.
/// </summary>
public static class TriggerFire
{
    /// <summary>Fires record <paramref name="key"/> of <paramref name="map"/>; null when the map has no such record.</summary>
    public static FireResult? Fire(MapFile map, TransfrmPrs transfrm, int key)
    {
        if (!map.TryGetEntityAt(key, out var e))
        {
            return null;
        }

        var mode = Mode(e);
        var d = Driver.Ready(map, transfrm, new MissionProgress(mode == "counter" ? ActRules.RelayAt + 1 : 5));
        var p = d.PositionOf(key);
        var effects = mode switch
        {
            "walk" => d.Walk(key),
            "use" or "prox" => d.Press(e, p),
            "exit" => [.. d.Touch(p), .. d.TakeExit(p)],
            "shot_each" => d.Shoot(key, 1f),
            "shot_death" => d.Shoot(key, Math.Max(1f, d.B.Hp(key))),
            "counter" => d.Count(key),
            _ => d.Chain(key),
        };

        var touched = new List<int> { key };
        foreach (var f in effects)
        {
            if (f.IndexOf('@', StringComparison.Ordinal) is var at and >= 0 && at + 6 <= f.Length
                && int.TryParse(f.AsSpan(at + 1, 5), NumberStyles.HexNumber, CultureInfo.InvariantCulture, out var off) && !touched.Contains(off))
            {
                touched.Add(off);
            }
        }

        return new FireResult(key, mode, effects, touched);
    }

    /// <summary>The log's lines for a firing: the record, its mode and effects, each touched record with its act's name.</summary>
    public static IEnumerable<string> Describe(MapFile map, FireResult r)
    {
        string Name(int off) => map.TryGetEntityAt(off, out var e)
            ? string.Create(CultureInfo.InvariantCulture, $"{off:x5} {TriggerChains.ActName(e.MarkerType >= 0 ? 0 : e.ActType)}")
            : string.Create(CultureInfo.InvariantCulture, $"{off:x5}");
        yield return $"Fire {Name(r.Key)} by {r.Mode}: {(r.Effects.Count == 0 ? "nothing happened" : string.Join(' ', r.Effects))}";
        foreach (var off in r.Touched.Skip(1))
        {
            yield return "  touched " + Name(off);
        }
    }

    /// <summary>The mode the verifier drives a record by (<see cref="TriggerVerifier.Modes"/>, a floor pad walked
    /// on first, else <see cref="LockNode.PrimaryOf"/>); "chain" when only a chain sets it off.</summary>
    public static string Mode(MapEntity e)
    {
        var modes = TriggerVerifier.Modes(e, TriggerVerifier.KindName(e), incoming: false);
        return modes.Contains("walk") ? "walk" : LockNode.PrimaryOf(modes) is { Length: > 0 } primary ? primary : "chain";
    }
}
