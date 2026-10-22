using System.Numerics;
using SkyNet.Formats;

namespace SkyNet.Game.Behaviour;

/// <summary>Which behaviour node an entity becomes (the Godot port's level_behaviour.gd kind_of).</summary>
public enum NodeKind
{
    /// <summary>Static geometry, a plain sprite or a placement marker.</summary>
    None,
    Mover, Destructible, Damageable, Trigger, Exit, SoundLoop, Voice, SoundCue, Message, Objective,
    /// <summary>Lights, water, relays, spawns, and relays a chain passes through.</summary>
    Raw,
}

/// <summary>Classification of the MAP records, shared by the runtime, the level's draw split and
/// the collision split. Pure functions of the records and TRANSFRM.PRS.</summary>
public static class BehaviourModel
{
    /// <summary>The TRANSFRM.PRS of the data (in MDMDBRIF.BSA); empty when it is missing.</summary>
    public static TransfrmPrs LoadTransfrm(GameData data)
    {
        if (!data.Exists("MDMDBRIF.BSA"))
        {
            return TransfrmPrs.Parse([]);
        }

        using var brif = data.OpenArchive("MDMDBRIF.BSA");
        return TransfrmPrs.Parse(brif.Read("TRANSFRM.PRS") ?? []);
    }

    /// <summary>The level-axes position of a record (DOS (x, y, z) → (x, −y, −z)).</summary>
    public static Vector3 Position(MapEntity e) => new(e.X, -e.Y, -e.Z);

    /// <summary>A variant-1 record's placement (Level.Rotation then the translation), from 11-bit
    /// Euler units that may be fractional (a swinging door).</summary>
    public static Matrix4x4 Placement(Vector3 euler, Vector3 position) =>
        Matrix4x4.CreateRotationY(euler.Y * MathF.Tau / 2048f)
        * Matrix4x4.CreateRotationX(euler.X * MathF.Tau / 2048f)
        * Matrix4x4.CreateRotationZ(-euler.Z * MathF.Tau / 2048f)
        * Matrix4x4.CreateTranslation(position);

    /// <summary>(pitch, yaw, roll) in 11-bit units.</summary>
    public static Vector3 Euler(MapEntity e) => new(e.Pitch & 0x7FF, e.Yaw & 0x7FF, e.Roll & 0x7FF);

    /// <summary>The entity a record's chain link points at (null: end, self loop, garbage).</summary>
    public static MapEntity? LinkOf(MapFile map, MapEntity e) =>
        e.LinkNext <= 0 || e.LinkNext == e.FileOffset || !map.TryGetEntityAt(e.LinkNext, out var t) ? null : t;

    /// <summary>Every offset another (non-marker) record's chain points at.</summary>
    public static HashSet<int> ChainTargets(MapFile map)
    {
        var set = new HashSet<int>();
        foreach (var e in map.Entities)
        {
            if (e.MarkerType < 0 && LinkOf(map, e) != null)
            {
                set.Add(e.LinkNext);
            }
        }

        return set;
    }

    public static bool HasStages(MapFile map, MapEntity e, TransfrmPrs transfrm) =>
        e.Variant == MapEntityVariant.Mesh && transfrm.StagesOf(map.EntityName(e)).Length > 0;

    /// <summary>The node kind of a record.</summary>
    public static NodeKind KindOf(MapFile map, MapEntity e, TransfrmPrs transfrm, HashSet<int> targeted)
    {
        if (e.MarkerType >= 0)
        {
            return NodeKind.None;
        }

        var mesh = e.Variant == MapEntityVariant.Mesh;
        var act = e.ActType;
        if (ActRules.IsMover(act))
        {
            return mesh ? NodeKind.Mover : NodeKind.Raw;
        }

        if (ActRules.IsDestructible(act) || HasStages(map, e, transfrm))
        {
            return mesh ? NodeKind.Destructible : NodeKind.Raw;
        }

        if (ActRules.IsTrigger(act))
        {
            return NodeKind.Trigger;
        }

        switch (act)
        {
            case ActRules.Exit: return NodeKind.Exit;
            case ActRules.SoundLoop: return NodeKind.SoundLoop;
            case ActRules.Voice: return NodeKind.Voice;
        }

        if (ActRules.SoundOneShot.ContainsKey(act))
        {
            return NodeKind.SoundCue;
        }

        if (act is >= ActRules.HintFirst and <= ActRules.HintLast)
        {
            return NodeKind.Message;
        }

        if (act is >= ActRules.ObjectiveFirst and <= ActRules.Fail)
        {
            return NodeKind.Objective;
        }

        if (mesh && (e.HitPoints > 0 || (e.StateByte & 6) != 0 || act == ActRules.Demolish))
        {
            return NodeKind.Damageable;
        }

        if (act is > 0 and < ActRules.SpentFirst)
        {
            return NodeKind.Raw;
        }

        return LinkOf(map, e) != null || targeted.Contains(e.FileOffset) ? NodeKind.Raw : NodeKind.None;
    }

    /// <summary>
    /// Does this variant-1 mesh move, change stage or disappear in play, so that it cannot live
    /// in the merged static mesh and the static collision grid? Movers, TRANSFRM.PRS wrecks,
    /// demolished records and everything with hit points or hit/death bits.
    /// </summary>
    public static bool IsDynamic(MapFile map, MapEntity e, TransfrmPrs transfrm)
    {
        if (e.Variant != MapEntityVariant.Mesh)
        {
            return false;
        }

        var act = e.ActType;
        return ActRules.IsMover(act) || ActRules.IsDestructible(act) || act == ActRules.Demolish
               || HasStages(map, e, transfrm) || (e.StateByte & 6) != 0 || e.HitPoints > 0 || IsSwitch(e);
    }

    /// <summary>
    /// A wall switch: a variant-1 0xEF gate with state bit 3. The gate handler (v1.01 0x13868d)
    /// ends, after its ObjFlipLink, with `if (sub+0x12 &amp; 8) sub+4 = (sub+4 + 0x400) &amp; 0x7FF`
    /// (0x13873e): every press turns the record's yaw half round. The switch models (BUTTON01-04,
    /// 232BUTTO, 232KYPD1, BTNA ...) are one quad with the OFF art on the front face and the ON art
    /// on the back, so the turn is what lights the white panel, presses the purple one in and flips
    /// the lever; the next press turns it back.
    /// </summary>
    public static bool IsSwitch(MapEntity e) =>
        e.Variant == MapEntityVariant.Mesh && e.ActType == ActRules.ProxGate && (e.StateByte & ActRules.WallButtonBit) != 0;

    /// <summary>The 11-bit yaw a switch's press adds (0x400 = 180°).</summary>
    public const int SwitchTurn = 0x400;

    /// <summary>The model names a dynamic entity shows, by stage: its own mesh, or its TRANSFRM.PRS frames.</summary>
    public static IReadOnlyList<string> StageMeshes(MapFile map, MapEntity e, TransfrmPrs transfrm)
    {
        var name = map.EntityName(e);
        var stages = transfrm.StagesOf(name);
        return stages.Length > 0 ? stages.Select(s => s.ToUpperInvariant()).ToArray() : [name];
    }
}
