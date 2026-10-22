using System.Globalization;
using System.Numerics;
using Hexa.NET.ImGui;
using RetroEng.Editor;
using SkyNet.Formats;

namespace SkyNet.Edit;

/// <summary>What a robot's part is to it.</summary>
public enum RobotPartKind
{
    /// <summary>A child segment, assembled on the robot (a head, a turret, a gun).</summary>
    Segment,
    /// <summary>A wreck part thrown when it dies.</summary>
    Wreck,
}

/// <summary>A part of a robot type: what it is, its enemy type and its .3D name ("T-REXHED").</summary>
public readonly record struct RobotPart(RobotPartKind Kind, int Type, string Model);

/// <summary>An AIS block of a robot and the clip of its whole-robot model the game plays for it (null: the rest pose).</summary>
public readonly record struct RobotClipRow(string Block, string? Clip);

/// <summary>The Assets panel's "Robots" (editor-tools plan, T2): one entry per whole robot (an enemy type with a
/// model that is no other type's segment or wreck part), grouped by AI state, previewed by the
/// <see cref="ModelViewer"/> assembled as in game, its parts listed under it (<see cref="Parts"/>); replaced
/// whole in a mod through the viewer (<see cref="RobotModelEdits"/>).</summary>
public sealed class RobotAssets(AisData ais, ModelViewer? viewer = null) : IAssetSource
{
    public const string Robots = "Robots";
    private const string NoRobotPath = "A mod replaces a robot with models/NAME.robot.glb: the viewer's Replace with glTF... writes it (Export as glTF gives a start).";
    // Segments nest as deep as the game assembles them (Enemy.AddSegments); a table that loops stops there.
    private const int MaxDepth = 4;

    public IReadOnlyList<string> Kinds { get; } = [Robots];

    public IEnumerable<AssetEntry> Enumerate(string kind)
    {
        if (kind != Robots)
        {
            yield break;
        }

        var parts = ais.Types.SelectMany(t => t.Segments.Select(seg => seg.ChildType).Concat(t.Death.Select(d => d.Type))).ToHashSet();
        foreach (var t in ais.Types.Where(t => t.ModelFile.Length > 0 && !parts.Contains(t.Index)))
        {
            var state = ModelSubject.StateNames.TryGetValue(t.State, out var s) ? s : $"state {t.State}";
            var detail = t.Segments.Length > 0 ? $"type {t.Index}, {t.Segments.Length} segment(s)" : $"type {t.Index}";
            yield return new AssetEntry(Robots, Id(t.Index), t.Name, state, AssetOrigin.Base, null, detail);
        }
    }

    /// <summary>A type's entry id: "037".</summary>
    public static string Id(int type) => type.ToString("D3", CultureInfo.InvariantCulture);

    /// <summary>Robot <paramref name="type"/>'s parts: its segments depth first as the game assembles them (the root
    /// not included), then its wreck parts once each.</summary>
    public static IReadOnlyList<RobotPart> Parts(AisData ais, int type)
    {
        var parts = new List<RobotPart>();
        if (type < 0 || type >= ais.Types.Length)
        {
            return parts;
        }

        Segments(ais, ais.Types[type], 0, parts);
        foreach (var wreck in ais.Types[type].Death.Select(d => d.Type).Distinct().Where(w => w >= 0 && w < ais.Types.Length))
        {
            parts.Add(new RobotPart(RobotPartKind.Wreck, wreck, ModelOf(ais.Types[wreck])));
        }

        return parts;
    }

    /// <summary>Each block in <paramref name="anims"/> and the clip of <paramref name="clips"/> the game plays for it.</summary>
    public static IReadOnlyList<RobotClipRow> ClipMap(IReadOnlyList<SkyNet.Game.Enemies.RobotAnim> anims, IReadOnlyList<string> clips) =>
        [.. anims.Select(a => SkyNet.Game.Enemies.RobotClips.Match(a.Name, clips) is var i and >= 0 ? new RobotClipRow(a.Name, clips[i]) : new RobotClipRow(a.Name, null))];

    private static void Segments(AisData ais, EnemyType type, int depth, List<RobotPart> parts)
    {
        if (depth >= MaxDepth)
        {
            return;
        }

        foreach (var child in type.Segments.Select(seg => seg.ChildType).Where(c => c >= 0 && c < ais.Types.Length))
        {
            parts.Add(new RobotPart(RobotPartKind.Segment, child, ModelOf(ais.Types[child])));
            Segments(ais, ais.Types[child], depth + 1, parts);
        }
    }

    private static string ModelOf(EnemyType t) => ModelReplace.NameOf(t.ModelFile.Length > 0 ? t.ModelFile : t.Name).ToUpperInvariant();

    public void DrawPreview(AssetEntry entry, Vector2 size)
    {
        if (viewer == null || !int.TryParse(entry.Id, NumberStyles.None, CultureInfo.InvariantCulture, out var type))
        {
            ImGui.TextDisabled("No preview.");
            return;
        }

        viewer.RobotControls(ais, type);
    }

    /// <summary>Called with each row as the panel draws it, its row the last item: a drag of it begins there (a drop into the view).</summary>
    public Action<AssetEntry>? Dragging { get; set; }

    public bool DrawThumbnail(AssetEntry entry, Vector2 size)
    {
        Dragging?.Invoke(entry);
        return false;
    }

    public string? PathOf(AssetEntry entry) => null;

    public string? OverrideBlocker(AssetEntry entry) => NoRobotPath;

    public IReadOnlyList<string> Override(AssetEntry entry, string modFolder) => throw new InvalidOperationException(NoRobotPath);
}

/// <summary>One field of a robot's property table: its section, name and value as shown.</summary>
public readonly record struct RobotField(string Section, string Name, string Value);

/// <summary>A robot's enemy-table row and AI parameters as read-only property tables (section,
/// field, value), one row per field so a value cell can become an editor later.</summary>
public static class RobotTables
{
    public const string Row = "Enemy table row", Ai = "AI parameters", Segments = "Segments", Block = "Parameter block";
    private static readonly string[] RecordNames = ["model", "+0x04", "state", "segments", "death spec", "parameter block", "stand height"];
    private const ImGuiTableFlags TableFlags = ImGuiTableFlags.Borders | ImGuiTableFlags.RowBg | ImGuiTableFlags.SizingStretchProp;

    /// <summary>Every field of <paramref name="t"/>, by section.</summary>
    public static IReadOnlyList<RobotField> Fields(EnemyType t)
    {
        var fields = new List<RobotField>
        {
            new(Row, "type", Int(t.Index)),
            new(Row, "name", t.Name),
            new(Row, "model", t.ModelFile),
        };
        for (var i = 0; i < t.Record.Length; i++)
        {
            fields.Add(new(Row, $"dw{i} ({(i < RecordNames.Length ? RecordNames[i] : "?")})", Hex(t.Record[i])));
        }

        var state = ModelSubject.StateNames.TryGetValue(t.State, out var s) ? $"{t.State} ({s})" : Int(t.State);
        fields.Add(new(Ai, "state", state));
        fields.Add(new(Ai, "hit points", Int(t.HitPoints)));
        Optional(fields, "speed", t.Speed);
        Optional(fields, "near", t.Near);
        Optional(fields, "turn", t.Turn);
        Optional(fields, "axis", t.Axis);
        Optional(fields, "angle min", t.AngleMin);
        Optional(fields, "angle max", t.AngleMax);
        Optional(fields, "range", t.Range);
        Optional(fields, "altitude", t.Altitude);
        Optional(fields, "vertical accel", t.VerticalAccel);
        Optional(fields, "avoid radius", t.AvoidRadius);
        Optional(fields, "forward speed", t.ForwardSpeed);
        if (t.Fire is { } f)
        {
            fields.Add(new(Ai, "fire muzzle", $"{f.MuzzleX}, {f.MuzzleY}, {f.MuzzleZ}"));
            fields.Add(new(Ai, "fire ammo", Int(f.Ammo)));
            fields.Add(new(Ai, "fire speed", Int(f.Speed)));
            fields.Add(new(Ai, "fire rate", Int(f.Rate)));
            fields.Add(new(Ai, "fire range", Int(f.Range)));
            fields.Add(new(Ai, "aim gate", Int(f.AimGate)));
        }

        fields.Add(new(Ai, "script", t.ScriptVa is { } va ? Hex(va) : "none"));
        Optional(fields, "engine sound", t.EngineSound);
        fields.Add(new(Ai, "frame events", t.Events.Length == 0 ? "none" : string.Join(", ", t.Events.Select(e => $"{e.Frame}: sound {e.Sound}"))));
        fields.Add(new(Ai, "death parts", t.Death.Length == 0 ? "none" : string.Join(", ", t.Death.Select(d => $"type {d.Type} at {d.X},{d.Y},{d.Z}"))));
        foreach (var (seg, i) in t.Segments.Select((seg, i) => (seg, i)))
        {
            fields.Add(new(Segments, $"segment {i}", $"type {seg.ChildType} at {seg.X},{seg.Y},{seg.Z}; pitch {seg.Pitch}, yaw {seg.Yaw}, roll {seg.Roll}"));
        }

        for (var i = 0; i < t.Params.Length; i++)
        {
            fields.Add(new(Block, $"param {i}", Int(t.Params[i])));
        }

        return fields;
    }

    /// <summary>The tables, one collapsing section each (the row and the AI open).</summary>
    public static void Draw(EnemyType t)
    {
        foreach (var section in Fields(t).GroupBy(f => f.Section))
        {
            var open = section.Key is Row or Ai ? ImGuiTreeNodeFlags.DefaultOpen : ImGuiTreeNodeFlags.None;
            if (!ImGui.CollapsingHeader(section.Key, open) || !ImGui.BeginTable("##" + section.Key, 2, TableFlags))
            {
                continue;
            }

            ImGui.TableSetupColumn("field", ImGuiTableColumnFlags.WidthStretch, 0.35f);
            ImGui.TableSetupColumn("value", ImGuiTableColumnFlags.WidthStretch, 0.65f);
            foreach (var f in section)
            {
                ImGui.TableNextRow();
                ImGui.TableSetColumnIndex(0);
                ImGui.TextUnformatted(f.Name);
                ImGui.TableSetColumnIndex(1);
                ImGui.TextUnformatted(f.Value);
            }

            ImGui.EndTable();
        }
    }

    private static void Optional(List<RobotField> fields, string name, int? value)
    {
        if (value is { } v)
        {
            fields.Add(new(Ai, name, Int(v)));
        }
    }

    private static string Int(int v) => v.ToString(CultureInfo.InvariantCulture);
    private static string Hex(uint v) => string.Create(CultureInfo.InvariantCulture, $"0x{v:X}");
}
