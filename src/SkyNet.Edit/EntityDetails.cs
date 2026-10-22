using System.Globalization;
using SkyNet.Data;
using SkyNet.Formats;
using SkyNet.Game.Campaign;
using SkyNet.Game.Mods;

namespace SkyNet.Edit;

/// <summary>How a Details row is edited: a number, on/off, one of a list, or read-only text.</summary>
public enum DetailKind { Int, Bool, Choice, Text }

/// <summary>How an edit lands on the history: a step of its own, or merged with the drag it continues.</summary>
public enum DetailCommit { Step, Drag }

/// <summary>One value a <see cref="DetailKind.Choice"/> row may take.</summary>
public readonly record struct DetailChoice(int Value, string Label);

/// <summary>
/// One row of the Details panel: its label, how it is edited, its value (null: the selected entities differ,
/// shown <see cref="Mixed"/>), its range and the map layer field it writes (null: read-only; <see cref="Mask"/>: the state bit a
/// Bool stands for, 0 when the field itself is 0 or 1).
/// </summary>
public sealed record DetailRow(string Label, DetailKind Kind, int? Value, int Min, int Max, string? Field,
    int Mask = 0, IReadOnlyList<DetailChoice>? Choices = null, string Text = "")
{
    public bool ReadOnly => Field == null;

    public bool Mixed => Value == null && Kind != DetailKind.Text;

    /// <summary>What two entities' rows must share to be one row of a multi-selection.</summary>
    public string Key => Field == null ? "text " + Label : Field + "&" + Mask.ToString(CultureInfo.InvariantCulture);

    /// <summary>The field's raw value for <paramref name="value"/> on a record whose state byte is <paramref name="state"/>.</summary>
    public int Raw(int state, int value) => Mask == 0 ? value : value != 0 ? state | Mask : state & ~Mask;

    /// <summary>The value shown for <paramref name="value"/>: its choice's label, else the number.</summary>
    public string Show(int value) =>
        Choices?.FirstOrDefault(c => c.Value == value) is { Label: { } l } ? l : value.ToString(CultureInfo.InvariantCulture);
}

/// <summary>A titled group of rows ("Transform", "Robot", "Trigger").</summary>
public sealed record DetailSection(string Title, IReadOnlyList<DetailRow> Rows);

/// <summary>
/// The Details panel's model (Unreal's Details, kept to what the DOS record holds): the rows of the selected
/// entities of one layer, a multi-selection's rows being those all of them have (a value they differ in is
/// null), and the edits, each one undo step written into the layer.
/// <code>
///   Transform   x y z (pitch yaw roll: meshes)
///   &lt;kind&gt;      model / robot type, convoy / item / sprite / intensity, range
///   Trigger     act, next, the act's parameters (GraphParams: the trigger graph's own)
/// </code>
/// </summary>
public static class EntityDetails
{
    public const string Transform = "Transform", Trigger = "Trigger";
    /// <summary>A value the selected entities differ in (the UI font has no em dash).</summary>
    public const string Mixed = "--";
    private const int U16Max = 0xFFFF, ByteMax = 255;
    // A robot marker's words: param16 wakes a dormant robot after that long, param8 is the act its death fires (with state bit 4).
    private const int DeathBit = 4;

    private static readonly IReadOnlyList<DetailChoice> Acts = [.. ActCatalog.All.Select(a => new DetailChoice(a.Id, a.Name))];

    /// <summary>The sections of <paramref name="selection"/> (entities of <paramref name="map"/>); <paramref name="robotTypes"/>
    /// are the robot type picker's choices (the Tables grid's rows, mod robots included).</summary>
    public static IReadOnlyList<DetailSection> Of(MapFile map, IReadOnlyList<MapEntity> selection, IReadOnlyList<DetailChoice> robotTypes)
    {
        if (selection.Count == 0)
        {
            return [];
        }

        var each = selection.Select(e => Sections(map, e, robotTypes)).ToList();
        if (each.Count == 1)
        {
            return each[0];
        }

        // The sections and rows every entity has; a value they differ in is null.
        var merged = new List<DetailSection>();
        foreach (var section in each[0])
        {
            var others = each.Skip(1).Select(s => s.FirstOrDefault(o => o.Title == section.Title)).ToList();
            if (others.Any(o => o == null))
            {
                continue;
            }

            var rows = new List<DetailRow>();
            foreach (var row in section.Rows)
            {
                var same = others.Select(o => o!.Rows.FirstOrDefault(r => r.Key == row.Key)).ToList();
                if (same.Any(r => r == null))
                {
                    continue;
                }

                var value = same.All(r => r!.Value == row.Value) ? row.Value : null;
                var text = same.All(r => r!.Text == row.Text) ? row.Text : Mixed;
                rows.Add(row with { Value = value, Text = text });
            }

            merged.Add(section with { Rows = rows });
        }

        return merged;
    }

    /// <summary>Sets <paramref name="row"/> to <paramref name="value"/> on every entity of <paramref name="keys"/>
    /// (a state bit on each its own byte), one undo step; <see cref="DetailCommit.Drag"/> merges a single entity's
    /// run of sets until <see cref="EditDocument.EndDrag"/>. <see cref="MapLayerException"/> when it does not fit.</summary>
    public static void Set(EditDocument doc, string layer, IReadOnlyList<int> keys, DetailRow row, int value, DetailCommit commit = DetailCommit.Step)
    {
        if (row.ReadOnly)
        {
            throw new MapLayerException($"\"{row.Label}\" is not edited here");
        }

        if (value < row.Min || value > row.Max)
        {
            throw new MapLayerException(string.Create(CultureInfo.InvariantCulture, $"{row.Label} {value} is not {row.Min}-{row.Max}"));
        }

        var field = row.Field!;
        var label = "Set " + row.Label;

        // One entity's field: a plain set, which a drag merges.
        if (keys.Count == 1 && field != "act")
        {
            if (commit == DetailCommit.Step)
            {
                doc.EndDrag();
            }

            var e = Entity(doc.Map(layer), keys[0]);
            doc.SetFields(layer, keys[0], new Dictionary<string, int> { [field] = row.Raw(e.StateByte, value) }, label: label);
            if (commit == DetailCommit.Step)
            {
                doc.EndDrag();
            }

            return;
        }

        doc.EndDrag();
        doc.Group(label, () =>
        {
            foreach (var key in keys)
            {
                if (field == "act")
                {
                    GraphEdits.SetAct(doc, layer, key, value);
                    continue;
                }

                var e = Entity(doc.Map(layer), key);
                doc.SetFields(layer, key, new Dictionary<string, int> { [field] = row.Raw(e.StateByte, value) }, label: label);
            }
        });
        doc.EndDrag();
    }

    // ---- one entity's rows ----

    private static List<DetailSection> Sections(MapFile map, MapEntity e, IReadOnlyList<DetailChoice> robotTypes)
    {
        // Angles as DOS keeps them (-1536 is as good as 512): the panel wraps what it types.
        var turns = EntityDrag.Turns(e);
        List<DetailRow> transform =
        [
            Int("x", e.X, int.MinValue, int.MaxValue, "x"),
            Int("y", e.Y, int.MinValue, int.MaxValue, "y"),
            Int("z", e.Z, int.MinValue, int.MaxValue, "z"),
        ];
        if (turns)
        {
            transform.Add(Int("pitch", e.Pitch, int.MinValue, int.MaxValue, "pitch"));
            transform.Add(Int("yaw", e.Yaw, int.MinValue, int.MaxValue, "yaw"));
            transform.Add(Int("roll", e.Roll, int.MinValue, int.MaxValue, "roll"));
        }

        var kind = EditorWorld.KindOf(e);
        var own = KindRows(map, e, kind, robotTypes);
        List<DetailSection> sections = [new(Transform, transform), new(kind.ToString(), own)];

        // The trigger: act and next, then the act's parameters as the graph has them (a field shown above left out).
        var shown = own.Select(r => r.Key).ToHashSet();
        var trigger = new List<DetailRow>();
        if (e.MarkerType < 0)
        {
            trigger.Add(Choice("act", e.ActType, 0, ByteMax, "act", Acts));
        }

        trigger.Add(Int("next", e.LinkNext, int.MinValue, int.MaxValue, "next"));
        trigger.AddRange(GraphParams.Of(e).Select(RowOf).Where(r => !shown.Contains(r.Key)));
        sections.Add(new DetailSection(Trigger, trigger));
        return sections;
    }

    private static List<DetailRow> KindRows(MapFile map, MapEntity e, EntityKind kind, IReadOnlyList<DetailChoice> robotTypes)
    {
        switch (kind)
        {
            case EntityKind.Prop or EntityKind.Actor:
                var names = map.Names.Select((n, i) => new DetailChoice(i, n)).ToList();
                return
                [
                    Choice("model", e.NameIndex, 0, Math.Max(0, names.Count - 1), "nameIndex", names),
                    new DetailRow("actor", DetailKind.Bool, e.IsActor ? 1 : 0, 0, 1, "actor"),
                ];
            case EntityKind.Robot:
                var types = robotTypes.Any(c => c.Value == e.EnemyType)
                    ? robotTypes : [.. robotTypes, new DetailChoice(e.EnemyType, Inv($"{e.EnemyType} (not in the table)"))];
                return
                [
                    Choice("robot type", e.EnemyType, 0, ModRobots.MaxType, "enemy", types),
                    new DetailRow("convoy", DetailKind.Bool, e.Convoy ? 1 : 0, 0, 1, "convoy"),
                    Int("dormant for", e.Param16, 0, U16Max, "param16"),
                    new DetailRow("on death", DetailKind.Bool, (e.StateByte & DeathBit) != 0 ? 1 : 0, 0, 1, "state", DeathBit),
                    Int("death act", e.Param8, 0, ByteMax, "param8"),
                ];
            case EntityKind.Pickup:
                var items = PickupTable.Items.OrderBy(i => i.Key).Select(i => new DetailChoice(i.Key, Inv($"{i.Value.MessageKey}  ({SpriteModels.KeyOf(i.Key)})"))).ToList();
                return [Choice("item", e.SpriteIndex, 0, U16Max, "sprite", items)];
            case EntityKind.Light:
                return
                [
                    Int("intensity", e.LightIntensity, 0, U16Max, "light"),
                    Int("range (enable)", e.LightEnable, 0, U16Max, "lightEnable"),
                    new DetailRow("colour", DetailKind.Text, 0, 0, 0, null, Text: "white (the DOS record has none)"),
                ];
            case EntityKind.Sprite:
                return [Int("sprite", e.SpriteIndex, 0, U16Max, "sprite"), new DetailRow("bank/record", DetailKind.Text, 0, 0, 0, null, Text: SpriteModels.KeyOf(e.SpriteIndex))];
            default:
                return [new DetailRow("marker type", DetailKind.Text, 0, 0, 0, null, Text: e.MarkerType.ToString(CultureInfo.InvariantCulture))];
        }
    }

    // A graph parameter as a row: fixed ones as text, a state bit as on/off.
    private static DetailRow RowOf(GraphParam p) => p switch
    {
        { ReadOnly: true } => new DetailRow(p.Name, DetailKind.Text, p.Value, p.Value, p.Value, null,
            Text: p.Type == ParamType.Text ? p.Text : p.Value.ToString(CultureInfo.InvariantCulture)),
        { Type: ParamType.Bool } => new DetailRow(p.Name, DetailKind.Bool, p.Value, 0, 1, p.Field, p.Mask),
        _ => Int(p.Name, p.Value, p.Min, p.Max, p.Field),
    };

    private static DetailRow Int(string label, int value, int min, int max, string? field) => new(label, DetailKind.Int, value, min, max, field);

    private static DetailRow Choice(string label, int value, int min, int max, string field, IReadOnlyList<DetailChoice> choices) =>
        new(label, DetailKind.Choice, value, min, max, field, Choices: choices);

    private static MapEntity Entity(MapFile map, int key) =>
        map.TryGetEntityAt(key, out var e) ? e : throw new MapLayerException($"@{key}: the map has no entity under that key");

    private static string Inv(FormattableString s) => s.ToString(CultureInfo.InvariantCulture);
}
