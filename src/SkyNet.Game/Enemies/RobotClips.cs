namespace SkyNet.Game.Enemies;

/// <summary>
/// Which clip of a whole-robot model (<c>models/NAME.robot.glb</c>) plays for an AIS block
/// (docs/modding.md, Robot template): the block's name as <see cref="RobotAnims"/> gives it
/// ("walk", "fire 2"), matched in this order:
/// <code>
///   1. exactly                        "fire 2"
///   2. loosely: case, an exporter's   "Armature|Fire_2", "FIRE.2", "fire2"
///      "object|" prefix and the
///      separators ' ' _ - . ignored
///   3. by its role, a numbered block  "fire 2" ─► "fire" (one clip for every fire block)
///      taking the clip of its role
/// </code>
/// No match: the robot holds its rest pose while the block plays.
/// </summary>
public static class RobotClips
{
    /// <summary>The index in <paramref name="clips"/> of the clip block <paramref name="anim"/> plays; −1: none.</summary>
    public static int Match(string anim, IReadOnlyList<string> clips)
    {
        for (var i = 0; i < clips.Count; i++)
        {
            if (string.Equals(clips[i], anim, StringComparison.Ordinal))
            {
                return i;
            }
        }

        var key = Key(anim);
        for (var i = 0; i < clips.Count; i++)
        {
            if (Key(clips[i]) == key)
            {
                return i;
            }
        }

        if (Role(anim) is not { } role)
        {
            return -1;
        }

        var roleKey = Key(role);
        for (var i = 0; i < clips.Count; i++)
        {
            if (Key(clips[i]) == roleKey)
            {
                return i;
            }
        }

        return -1;
    }

    /// <summary>A name compared loosely: lower case, without an exporter's "object|" prefix and the separators.</summary>
    public static string Key(string name)
    {
        var bar = name.LastIndexOf('|');
        var rest = bar >= 0 ? name[(bar + 1)..] : name;
        return string.Concat(rest.Where(c => c is not (' ' or '_' or '-' or '.')).Select(char.ToLowerInvariant));
    }

    /// <summary>A numbered block's role ("fire 2" ─► "fire"); null when not numbered.</summary>
    public static string? Role(string anim)
    {
        var space = anim.LastIndexOf(' ');
        return space > 0 && anim[(space + 1)..] is { Length: > 0 } n && n.All(char.IsAsciiDigit) ? anim[..space] : null;
    }
}
