using System.Numerics;
using SkyNet.Formats;
using SkyNet.Game.Behaviour;

namespace SkyNet.Game.Tests.Behaviour;

/// <summary>The player's game data through SKYNET_DATA; the behaviour tests that need it skip without it.</summary>
static class BehaviourData
{
    public static string? Dir
    {
        get
        {
            var d = Environment.GetEnvironmentVariable("SKYNET_DATA");
            return !string.IsNullOrWhiteSpace(d) && GameDataLocator.GameOf(d) == GameKind.SkyNet ? d : null;
        }
    }

    private static GameData? _data;
    private static TransfrmPrs? _transfrm;

    public static GameData Data => _data ??= GameData.Open(Dir!) ?? throw new InvalidOperationException("no game data");
    public static TransfrmPrs Transfrm => _transfrm ??= BehaviourModel.LoadTransfrm(Data);
}

/// <summary>A fact skipped unless SKYNET_DATA points at SkyNET's data folder.</summary>
sealed class BehaviourDataFactAttribute : FactAttribute
{
    public BehaviourDataFactAttribute()
    {
        if (BehaviourData.Dir == null)
        {
            Skip = "SKYNET_DATA is not set to a SkyNET data folder";
        }
    }
}

/// <summary>A theory skipped unless SKYNET_DATA points at SkyNET's data folder.</summary>
sealed class BehaviourDataTheoryAttribute : TheoryAttribute
{
    public BehaviourDataTheoryAttribute()
    {
        if (BehaviourData.Dir == null)
        {
            Skip = "SKYNET_DATA is not set to a SkyNET data folder";
        }
    }
}

/// <summary>
/// Drives one map's behaviour headless the way a player would: the eye placed at a record, the use
/// key pressed for one tick, the feet on a pad, a shot. Effects come out in the Godot trigger
/// graph's vocabulary. The player waits far away between activations.
/// </summary>
sealed class Harness
{
    public const float Dt = 1f / 35f;
    public static readonly Vector3 Away = new(-1e7f, -1e7f, -1e7f);

    public readonly MapFile Map;
    public readonly LevelBehaviour B;
    public readonly EffectLog Log = new();

    public Harness(int map, MissionProgress? mission = null)
    {
        Map = BehaviourData.Data.LoadMap(map) ?? throw new InvalidOperationException($"MAP.{map} missing");
        B = new LevelBehaviour(Map, BehaviourData.Transfrm, mission ?? new MissionProgress(5), Log);
        B.Start(Away, Away);
        Settle();
        Log.Drain();
    }

    public Vector3 PositionOf(int off) => Map.TryGetEntityAt(off, out var e) ? BehaviourModel.Position(e) : Away;

    /// <summary>Runs <paramref name="seconds"/> of ticks with the player away.</summary>
    public void Settle(float seconds = 30f)
    {
        for (var t = 0f; t < seconds; t += Dt)
        {
            B.Tick(Dt, new PlayerProbe(Away, Away));
        }
    }

    /// <summary>The use key at a record: eye on it, the crosshair on it for a wall button.</summary>
    public List<string> Use(int off, bool aim = false)
    {
        var p = PositionOf(off);
        // A wall button is reached by the crosshair from afar: the feet stay away from doorways.
        B.Tick(Dt, new PlayerProbe(aim ? Away : p, p, UsePressed: true, AimedOwner: aim ? off : -1));
        Settle();
        return Log.Drain();
    }

    /// <summary>The eye walks into a proximity trigger for one tick, then leaves.</summary>
    public List<string> Prox(int off)
    {
        var p = PositionOf(off);
        B.Tick(Dt, new PlayerProbe(p, p));
        Settle();
        return Log.Drain();
    }

    /// <summary>One proximity trigger alone, as the verifier activates a node: its own handler
    /// with the eye inside it (overlapping triggers stay out of it).</summary>
    public List<string> ProxOnly(int off)
    {
        B.ProxUse(off, PositionOf(off));
        Settle();
        return Log.Drain();
    }

    /// <summary>The feet stand on a walk-on pad.</summary>
    public List<string> Walk(int off)
    {
        B.Tick(Dt, new PlayerProbe(Away, Away, FloorOwner: off));
        Settle();
        return Log.Drain();
    }

    public List<string> Shoot(int off, float damage)
    {
        B.Hit(off, damage);
        Settle();
        return Log.Drain();
    }

    /// <summary>The effects the trigger lock lists (destruction itself is the caller's, not a graph effect).</summary>
    public static List<string> Comparable(IEnumerable<string> fx) =>
        [.. fx.Where(f => !f.StartsWith("destroyed@", StringComparison.Ordinal)).Order(StringComparer.Ordinal)];
}
