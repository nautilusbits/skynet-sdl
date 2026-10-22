using SkyNet.Formats;

namespace SkyNet.Game.Enemies;

/// <summary>One AIS animation block a robot's script starts, named by what its flags make it.</summary>
public sealed record RobotAnim(string Name, AisAnimBlock Block);

/// <summary>A robot type's AIS blocks by name, as the Model Viewer lists them and a robot's glTF
/// template names its clips (docs/modding.md, Robot template).</summary>
public static class RobotAnims
{
    private const int WalkerState = 7;

    /// <summary>The blocks <paramref name="type"/>'s script can start, in the order it reaches them,
    /// named by their flags: "fire" (firing pose), "walk" (a walker's loop), "loop", "once" (one-shot),
    /// "pose"; repeats numbered.</summary>
    public static IReadOnlyList<RobotAnim> Of(AisData ais, EnemyType? type)
    {
        var anims = new List<RobotAnim>();
        if (type?.ScriptVa is not { } start)
        {
            return anims;
        }

        var seen = new HashSet<uint>();
        var counts = new Dictionary<string, int>();
        var todo = new Queue<uint>();
        todo.Enqueue(start);
        while (todo.TryDequeue(out var pc))
        {
            if (!seen.Add(pc) || !ais.Ops.TryGetValue(pc, out var op) || op.Kind == AisOpKind.End)
            {
                continue;
            }

            if (op.Kind is AisOpKind.Goto or AisOpKind.If)
            {
                todo.Enqueue(op.Target);
            }

            if (op.Kind != AisOpKind.Goto)
            {
                todo.Enqueue(op.Next);
            }

            if (op.Kind != AisOpKind.Anim || !ais.Anims.TryGetValue(op.AnimVa, out var block) || anims.Any(a => a.Block.Va == block.Va))
            {
                continue;
            }

            var role = RoleOf(block, type.State);
            counts[role] = counts.GetValueOrDefault(role) + 1;
            anims.Add(new RobotAnim(role, block));
        }

        // A role that appears more than once is numbered in order: "loop 1", "loop 2".
        var numbered = new Dictionary<string, int>();
        return [.. anims.Select(a => counts[a.Name] == 1 ? a : a with { Name = $"{a.Name} {numbered[a.Name] = numbered.GetValueOrDefault(a.Name) + 1}" })];
    }

    // A walker's (state 7) looping block is its walk cycle. No block is a death: a robot explodes.
    private static string RoleOf(AisAnimBlock block, int state) =>
        (block.Flags & AisAnimBlock.FiringPose) != 0 ? "fire"
        : (block.Flags & AisAnimBlock.Loop) != 0 ? state == WalkerState ? "walk" : "loop"
        : (block.Flags & AisAnimBlock.OneShot) != 0 ? "once"
        : "pose";
}
