using System.Globalization;
using SkyNet.Formats;

namespace SkyNet.Edit;

/// <summary>
/// The outliner's search and kind chips. The text is words that must all match (case ignored):
/// <code>
///   @1234  @0x4d2     the entity's key
///   act:38 act:0x26   its act
///   type:37           a robot of enemy type 37
///   raptor  door      a part of its row label, kind or robot type name ("raptor" finds every raptor)
/// </code>
/// No chip on: every kind; chips on: only those kinds.
/// </summary>
public sealed class OutlinerFilter(Func<int, string>? robotName = null)
{
    private const string ActPrefix = "act:", TypePrefix = "type:", HexPrefix = "0x";
    private const char KeyPrefix = '@';

    private readonly Func<int, string> _robotName = robotName ?? RobotName;
    private string[] _words = [];
    private string _text = "";

    /// <summary>The search text.</summary>
    public string Text
    {
        get => _text;
        set => (_text, _words) = (value, value.Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries));
    }

    /// <summary>The kinds the chips let through (empty: every kind).</summary>
    public HashSet<EntityKind> Kinds { get; } = [];

    /// <summary>Whether anything is filtered out at all.</summary>
    public bool IsActive => _words.Length > 0 || Kinds.Count > 0;

    /// <summary>Whether entity <paramref name="e"/> of <paramref name="map"/> passes the chips and every word.</summary>
    public bool Matches(MapFile map, MapEntity e)
    {
        var kind = EditorWorld.KindOf(e);
        if (Kinds.Count > 0 && !Kinds.Contains(kind))
        {
            return false;
        }

        foreach (var word in _words)
        {
            if (!Word(map, e, kind, word))
            {
                return false;
            }
        }

        return true;
    }

    /// <summary>The entities of <paramref name="entities"/> that match, in their order.</summary>
    public List<MapEntity> Matching(MapFile map, IEnumerable<MapEntity> entities) => [.. entities.Where(e => Matches(map, e))];

    /// <summary>What "Select all matching" selects of <paramref name="entities"/>: the matching keys not in
    /// <paramref name="skip"/> (hidden or locked), in key order.</summary>
    public List<int> Selection(MapFile map, IEnumerable<MapEntity> entities, IReadOnlySet<int> skip) =>
        [.. entities.Where(e => !skip.Contains(e.FileOffset) && Matches(map, e)).Select(e => e.FileOffset).Order()];

    private bool Word(MapFile map, MapEntity e, EntityKind kind, string word)
    {
        if (word[0] == KeyPrefix)
        {
            return Number(word[1..]) == e.FileOffset;
        }

        if (word.StartsWith(ActPrefix, StringComparison.OrdinalIgnoreCase))
        {
            return Number(word[ActPrefix.Length..]) == e.ActType;
        }

        if (word.StartsWith(TypePrefix, StringComparison.OrdinalIgnoreCase) && Number(word[TypePrefix.Length..]) is { } type)
        {
            return kind == EntityKind.Robot && e.EnemyType == type;
        }

        // The row's label, its kind and, for a robot, its type's name.
        var robot = kind == EntityKind.Robot ? _robotName(e.EnemyType) : "";
        return EditorWorld.Label(map, e).Contains(word, StringComparison.OrdinalIgnoreCase)
            || kind.ToString().Contains(word, StringComparison.OrdinalIgnoreCase)
            || robot.Contains(word, StringComparison.OrdinalIgnoreCase);
    }

    // "1234" or "0x4d2"; null when neither.
    private static int? Number(string text)
    {
        if (text.StartsWith(HexPrefix, StringComparison.OrdinalIgnoreCase))
        {
            return int.TryParse(text.AsSpan(HexPrefix.Length), NumberStyles.AllowHexSpecifier, CultureInfo.InvariantCulture, out var h) ? h : null;
        }

        return int.TryParse(text, NumberStyles.Integer, CultureInfo.InvariantCulture, out var n) ? n : null;
    }

    // The enemy table's name of a type (mod robots included); "" when none.
    private static string RobotName(int type)
    {
        var types = SkyNet.Data.GameTables.Ais.Types;
        return type >= 0 && type < types.Length && types[type].Index == type ? types[type].Name : "";
    }
}
