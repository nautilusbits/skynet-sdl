using System.Numerics;

namespace SkyNet.Game.Enemies;

/// <summary>A save's <see cref="PlayerTrail"/> and the robots' clock that stamps it.</summary>
public sealed record SavedTrail(double Clock, int Head, int[] Stamps, Vector3[] Points);

/// <summary>
/// Where the soldier went, as SKYNET.EXE v1.01 keeps it for its hovers: a ring of 150 points
/// {stamp, feet} at [0x90d3], filled with his place when he arrives (0x1229e4) and written a point
/// further each time he is more than 5 units from the newest (0x122a32). A hover holds one point
/// (its index, 0 none: the first slot can never be held) and the stamp it read there.
/// <code>
///   [0][1][2]...[Head]...[149]   Ahead: toward Head, stamps not older; Back: away from it
/// </code>
/// </summary>
public sealed class PlayerTrail
{
    public const int Length = 150;
    /// <summary>The stamps' rate: DOS's frame counter [0x8c00] counts 70 a second (0x124c94).</summary>
    public const double StampRate = 70.0;
    private const float Spacing = 5f;

    private readonly int[] _stamps = new int[Length];
    private readonly Vector3[] _points = new Vector3[Length];

    /// <summary>The newest point's index.</summary>
    public int Head { get; private set; }

    /// <summary>Filled since the map was entered.</summary>
    public bool Ready { get; private set; }

    public int Stamp(int index) => _stamps[index];

    public Vector3 At(int index) => _points[index];

    /// <summary>A new map: filled again with the soldier's place on his next tick.</summary>
    public void Clear() => Ready = false;

    /// <summary>The soldier's feet this tick: every slot his place at first (stamp 0), then a new
    /// point once he is more than 5 units from the newest.</summary>
    public void Record(Vector3 feet, int stamp)
    {
        if (!Ready)
        {
            Array.Fill(_stamps, 0);
            Array.Fill(_points, feet);
            (Head, Ready) = (0, true);
            return;
        }

        if (Vector3.Distance(feet, _points[Head]) <= Spacing)
        {
            return;
        }

        Head = (Head + 1) % Length;
        (_stamps[Head], _points[Head]) = (stamp, feet);
    }

    /// <summary>The point <paramref name="n"/> newer than <paramref name="node"/> while it still
    /// holds <paramref name="stamp"/> (0x13b817); -1 when none.</summary>
    public int Ahead(int node, int stamp, int n) => Holds(node, stamp) && Usable((node + n) % Length, stamp, Later.Ahead)
        ? (node + n) % Length : -1;

    /// <summary>The point <paramref name="n"/> older (0x13b7d8); -1 when none.</summary>
    public int Back(int node, int stamp, int n) => Holds(node, stamp) && Usable((node - n + Length) % Length, stamp, Later.Back)
        ? (node - n + Length) % Length : -1;

    private enum Later { Ahead, Back }

    private bool Holds(int node, int stamp) => stamp > 0 && _stamps[node] == stamp;

    private bool Usable(int j, int stamp, Later way) =>
        _stamps[j] > 0 && (way == Later.Ahead ? _stamps[j] >= stamp : _stamps[j] <= stamp);

    internal SavedTrail Save(double clock) => new(clock, Head, [.. _stamps], [.. _points]);

    internal void Restore(SavedTrail t)
    {
        if (t.Stamps.Length != Length || t.Points.Length != Length)
        {
            return;
        }

        t.Stamps.CopyTo(_stamps, 0);
        t.Points.CopyTo(_points, 0);
        (Head, Ready) = (t.Head, true);
    }
}
