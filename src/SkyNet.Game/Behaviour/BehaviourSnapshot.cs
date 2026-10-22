using SkyNet.Game.Enemies;

namespace SkyNet.Game.Behaviour;

/// <summary>One record's live bytes: its state (bit 0 = enabled), act and link.</summary>
public readonly record struct RecordBytes(int Offset, int State, int Act, int Link);

/// <summary>A mover's travel so far, its next direction and whether it is under way.</summary>
public readonly record struct MoverSnapshot(int Offset, float Progress, float Dir, bool Running);

/// <summary>A trigger's latches.</summary>
public readonly record struct TriggerSnapshot(int Offset, bool Latched, bool EdgeDone, bool ExitWalked);

/// <summary>A TRANSFRM.PRS wreck's stage and its damage counter.</summary>
public readonly record struct WreckSnapshot(int Offset, int Stage, float Accum);

/// <summary>A light's enable word, intensity and flicker phase.</summary>
public readonly record struct LightSnapshot(int Offset, int Enable, int Intensity, bool FxShowing, bool FxOn, bool Announced);

/// <summary>
/// What a save keeps of one map's <see cref="LevelBehaviour"/> (<see cref="LevelBehaviour.Snapshot"/>):
/// every record's state, act and link, the hit points left, what is spent, hidden or out of the
/// collision, the movers, triggers, doorways, wrecks and lights, the path vehicles and the wall
/// switches turned to their ON face. Every list
/// is sorted by file offset, so one state always gives one snapshot.
/// </summary>
public sealed record BehaviourSnapshot(
    RecordBytes[] Records,
    (int Offset, float Hp)[] HitPoints,
    int[] Spent,
    int[] CueSpent,
    int[] Hidden,
    int[] NoCollide,
    MoverSnapshot[] Movers,
    TriggerSnapshot[] Triggers,
    (int Offset, bool Touching)[] Doorways,
    WreckSnapshot[] Wrecks,
    LightSnapshot[] Lights,
    float LightClock,
    PathVehicleSnapshot[] Paths,
    int[]? Turned = null);
