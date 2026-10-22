using System.Numerics;
using SkyNet.Formats;
using SkyNet.Game.Behaviour;

namespace SkyNet.Verify;

/// <summary>
/// One map's behaviour driven headless the way a player would, with no physics: the eye placed at
/// a record, the key down for one tick, the feet on a pad, a hit. Effects come out in the Godot
/// trigger graph's vocabulary (EffectLog). Between activations the player waits far away and the
/// movers are allowed to arrive.
/// </summary>
public sealed class Driver
{
    public const float Dt = 1f / 35f;
    public static readonly Vector3 Away = new(-1e7f, -1e7f, -1e7f);
    /// <summary>The longest wait for movers to arrive; a rotator never does.</summary>
    public const float SettleMax = 30f;

    /// <summary>How long a fresh map runs before a check (its start cues and lifts settle).</summary>
    public const float SettleStart = 2f;

    public readonly MapFile Map;
    public readonly LevelBehaviour B;
    public readonly MissionProgress Mission;
    public readonly EffectLog Log = new();

    public Driver(MapFile map, TransfrmPrs transfrm, MissionProgress mission, Vector3? feet = null, Vector3? eye = null)
    {
        Map = map;
        Mission = mission;
        B = new LevelBehaviour(map, transfrm, mission, Log);
        B.Start(feet ?? Away, eye ?? Away);
    }

    /// <summary>A fresh map settled from its start (<see cref="SettleStart"/>), its start effects drained.</summary>
    public static Driver Ready(MapFile map, TransfrmPrs transfrm, MissionProgress mission)
    {
        var d = new Driver(map, transfrm, mission);
        d.Settle(SettleStart);
        d.Drain();
        return d;
    }

    /// <summary>A wall button: a mesh with the wall-button bit and a name, set off by the crosshair and the key.</summary>
    public static bool IsWallButton(MapEntity e) =>
        e.Variant == MapEntityVariant.Mesh && (e.StateByte & ActRules.WallButtonBit) != 0 && e.NameIndex >= 0;

    /// <summary>One press: a wall button by the crosshair, a gate by its own handler with the eye
    /// on it; then the movers arrive with the player away.</summary>
    public List<string> Press(MapEntity e, Vector3 eye)
    {
        if (IsWallButton(e))
        {
            Tick(new PlayerProbe(Away, Away, UsePressed: true, AimedOwner: e.FileOffset));
        }
        else
        {
            B.ProxUse(e.FileOffset, eye);
        }

        Settle();
        return Drain();
    }

    /// <summary>The feet on floor pad <paramref name="off"/> for a tick, then the movers arrive.</summary>
    public List<string> Walk(int off)
    {
        Tick(new PlayerProbe(Away, Away, FloorOwner: off));
        Settle();
        return Drain();
    }

    /// <summary>The feet at doorway <paramref name="feet"/> for a tick (it arms); what that did.</summary>
    public List<string> Touch(Vector3 feet)
    {
        Tick(new PlayerProbe(feet, Away));
        return Drain();
    }

    /// <summary>The key with the feet at doorway <paramref name="feet"/> (it takes the exit), the movers arriving.</summary>
    public List<string> TakeExit(Vector3 feet)
    {
        Tick(new PlayerProbe(feet, Away, UsePressed: true));
        Settle(1f, feet, Away);
        return Drain();
    }

    /// <summary>A hit of <paramref name="damage"/> on <paramref name="off"/>, then the movers arrive.</summary>
    public List<string> Shoot(int off, float damage)
    {
        B.Hit(off, damage);
        Settle();
        return Drain();
    }

    /// <summary>Relay <paramref name="off"/> switched on (when off), then one objective taken; the mission's
    /// counter must stand one above the relay's word (<see cref="ActRules.RelayAt"/> + 1).</summary>
    public List<string> Count(int off)
    {
        if (!B.Enabled(off))
        {
            B.Flip(off);
            Tick(new PlayerProbe(Away, Away));
            Drain();
        }

        Mission.Take(0);
        Settle();
        return Drain();
    }

    /// <summary>The chain from <paramref name="off"/> (a path's end, or a record only a chain sets off).</summary>
    public List<string> Chain(int off)
    {
        B.Flip(off);
        Settle();
        return Drain();
    }

    public Vector3 PositionOf(int off) => Map.TryGetEntityAt(off, out var e) ? BehaviourModel.Position(e) : Away;

    /// <summary>Ticks with the player at <paramref name="feet"/>/<paramref name="eye"/> until no mover
    /// runs (at least <paramref name="min"/> seconds, at most <see cref="SettleMax"/>).</summary>
    public void Settle(float min = 1f, Vector3? feet = null, Vector3? eye = null)
    {
        var probe = new PlayerProbe(feet ?? Away, eye ?? Away);
        for (var t = 0f; t < SettleMax; t += Dt)
        {
            B.Tick(Dt, probe);
            if (t >= min && !B.Movers.Any(B.MoverRunning))
            {
                break;
            }
        }
    }

    public List<string> Drain() =>
        [.. Log.Drain().Where(f => !f.StartsWith("destroyed@", StringComparison.Ordinal))];

    public void Tick(in PlayerProbe p) => B.Tick(Dt, p);
}
