using System.Text.Json;
using RetroEng.Json;
using RetroEng.World;
using SkyNet.Game.Campaign;

namespace SkyNet.Edit;

/// <summary>
/// The mission file's edits (editor plan step E8): each returns a new, validated
/// <see cref="MissionFile"/> (<see cref="WorldFormatException"/> when the result is not a valid one),
/// for <see cref="EditDocument.SetMission"/> to make one undo step of. Everything an edit does not touch
/// carries over as it was: the world's id, source hash and inputs, and the game section member by
/// member (fields this build does not know included).
/// </summary>
public static class MissionEdits
{
    /// <summary>The mission with some of its parts replaced, the rest as it was.</summary>
    public static MissionFile With(MissionFile m, WorldStart? start = null, IReadOnlyList<WorldInstance>? instances = null,
        IReadOnlyList<WorldLink>? links = null, PlayerMode? mode = null)
    {
        var w = m.World;
        var game = mode is { } v && w.Game != null ? WithMember(w.Game, "vehicle", MissionFile.ModeName(v)) : w.Game;
        return MissionFile.From(new WorldDefinition(w.Id, start ?? w.Start, instances ?? w.Instances, links ?? w.Links, game, w.SourceHash, w.Inputs));
    }

    /// <summary>A copy of object <paramref name="o"/> with member <paramref name="name"/> set (in its place, or
    /// added at the end); <paramref name="o"/> itself is left as it was (the undo step keeps it).</summary>
    private static JsonItem WithMember(JsonItem o, string name, JsonItem value)
    {
        using var stream = new MemoryStream();
        using (var writer = new Utf8JsonWriter(stream))
        {
            o.WriteTo(writer);
        }

        var copy = JsonItem.Parse(stream.ToArray());
        var result = JsonItem.NewObject();
        var set = false;
        foreach (var (k, item) in copy.Members.ToArray())
        {
            set |= k == name;
            result.Add(k, k == name ? value : item);
        }

        return set ? result : result.Add(name, value);
    }

    /// <summary>The exit link from entity <paramref name="source"/> of instance <paramref name="from"/> set
    /// (added when there is none): to instance <paramref name="to"/>, or back (to the instance the player
    /// came from), or closed (neither), arriving at marker set <paramref name="arrival"/>.</summary>
    public static MissionFile SetExit(MissionFile m, string from, int source, string? to, bool back, int arrival)
    {
        var link = new WorldLink(from, source, back ? null : to, back, arrival);
        var links = m.World.Links.ToList();
        var i = links.FindIndex(l => l.From == from && l.Source == source);
        if (i >= 0)
        {
            links[i] = link;
        }
        else
        {
            links.Add(link);
        }

        return With(m, links: links);
    }

    /// <summary>The exit link from entity <paramref name="source"/> of <paramref name="from"/> removed
    /// (the game then follows the record's own target map).</summary>
    public static MissionFile RemoveExit(MissionFile m, string from, int source) =>
        With(m, links: [.. m.World.Links.Where(l => !(l.From == from && l.Source == source))]);

    /// <summary>Play begins on <paramref name="instance"/> at marker set <paramref name="arrival"/> (−1: its own start).</summary>
    public static MissionFile SetStart(MissionFile m, string instance, int arrival) => With(m, start: new WorldStart(instance, arrival));

    /// <summary>The mission table's vehicle mode (the game section's "vehicle").</summary>
    public static MissionFile SetMode(MissionFile m, PlayerMode mode) => With(m, mode: mode);

    /// <summary>A new instance <paramref name="id"/> of MAP.<paramref name="map"/> (block and layer that map).</summary>
    public static MissionFile AddInstance(MissionFile m, string id, int map, string? group = null)
    {
        var name = MissionFile.MapName(map);
        var instance = new WorldInstance(id, name, name, string.IsNullOrWhiteSpace(group) ? null : group, new Dictionary<string, string>(StringComparer.Ordinal));
        return With(m, instances: [.. m.World.Instances, instance]);
    }

    /// <summary>Instance <paramref name="id"/> removed with its exit links; links into it close. Not the
    /// start instance, nor one the torpedo ride names.</summary>
    public static MissionFile RemoveInstance(MissionFile m, string id)
    {
        if (m.World.Start.Instance == id)
        {
            throw new WorldFormatException($"\"{id}\" is the start instance: make another the start first");
        }

        if (m.Torpedo is { } t && (t.At == id || t.After == id || t.To == id))
        {
            throw new WorldFormatException($"\"{id}\" is part of the torpedo ride");
        }

        var links = m.World.Links.Where(l => l.From != id).Select(l => l.To == id ? l with { To = null } : l).ToList();
        return With(m, instances: [.. m.World.Instances.Where(i => i.Id != id)], links: links);
    }

    /// <summary>Instance <paramref name="id"/>'s group (null or blank: none).</summary>
    public static MissionFile SetGroup(MissionFile m, string id, string? group) =>
        Replace(m, id, i => i with { Group = string.IsNullOrWhiteSpace(group) ? null : group.Trim() });

    /// <summary>One of instance <paramref name="id"/>'s game properties (music, vehicle); null or blank removes it.</summary>
    public static MissionFile SetProperty(MissionFile m, string id, string name, string? value) =>
        Replace(m, id, i =>
        {
            var props = new Dictionary<string, string>(i.Properties, StringComparer.Ordinal);
            if (string.IsNullOrWhiteSpace(value))
            {
                props.Remove(name);
            }
            else
            {
                props[name] = value.Trim();
            }

            return i with { Properties = props };
        });

    private static MissionFile Replace(MissionFile m, string id, Func<WorldInstance, WorldInstance> change)
    {
        if (m.World.Instance(id) == null)
        {
            throw new WorldFormatException($"the mission has no instance \"{id}\"");
        }

        return With(m, instances: [.. m.World.Instances.Select(i => i.Id == id ? change(i) : i)]);
    }
}
