using SkyNet.Formats;

namespace SkyNet.Game.Behaviour;

/// <summary>What a save keeps of the counter: what is left, the end flags and each section's next entry.</summary>
public sealed record ProgressSnapshot(int Left, bool Complete, bool Failed, int[] Cursor);

/// <summary>
/// The objective counter of one mission (docs/game-logic/objectives.md): it starts at the number
/// of <c>####</c>-terminated [M1]..[M5] entries of the start map's briefing, blank entries
/// included, and belongs to the mission, not the map: every level of the mission shares one.
/// Objective acts 0x26+n take one off and show the next entry of section [M(n+1)].
/// </summary>
public sealed class MissionProgress
{
    private readonly Briefing? _briefing;
    private readonly int[] _cursor = new int[Briefing.MissionSections];

    public MissionProgress(int total, Briefing? briefing = null)
    {
        Total = total;
        Left = total;
        _briefing = briefing;
    }

    /// <summary>The counter from a briefing (<see cref="Briefing.ObjectiveCount"/>).</summary>
    public static MissionProgress From(Briefing briefing) => new(briefing.ObjectiveCount, briefing);

    /// <summary>The counter of the mission starting on MAP.<paramref name="startMap"/>, from its
    /// briefing NNN.TXT in MDMDBRIF.BSA; 0 objectives when there is none.</summary>
    public static MissionProgress Load(GameData data, int startMap)
    {
        if (!data.Exists("MDMDBRIF.BSA"))
        {
            return new MissionProgress(0);
        }

        using var brif = data.OpenArchive("MDMDBRIF.BSA");
        return brif.Read($"{startMap:D3}.TXT") is { } bytes ? From(Briefing.Parse(bytes)) : new MissionProgress(0);
    }

    public int Total { get; }
    /// <summary>The counter: objectives still to do.</summary>
    public int Left { get; private set; }
    public bool Complete { get; private set; }
    public bool Failed { get; private set; }

    /// <summary>One objective of section <paramref name="index"/> done; returns that section's next entry ("" when none).
    /// Once the counter is spent the act does nothing at all (handler 0x137fd0; main.gd _on_objective_complete).</summary>
    public string Take(int index)
    {
        if (Left <= 0)
        {
            return "";
        }

        var text = "";
        if (_briefing != null && index >= 0 && index < _briefing.MissionTexts.Length)
        {
            var section = _briefing.MissionTexts[index];
            if (_cursor[index] < section.Length)
            {
                text = section[_cursor[index]];
            }

            _cursor[index]++;
        }

        Left--;
        if (Left <= 0 && Total > 0)
        {
            Complete = true;
        }

        return text;
    }

    /// <summary>The text of hint [G<paramref name="index"/>+1], "" when unknown.</summary>
    public string HintText(int index) =>
        _briefing != null && index >= 0 && index < _briefing.Hints.Length ? _briefing.Hints[index] : "";

    public void Fail() => Failed = true;

    /// <summary>The counter as it stands (a save).</summary>
    public ProgressSnapshot Snapshot() => new(Left, Complete, Failed, [.. _cursor]);

    /// <summary>Puts a <see cref="Snapshot"/> back (the total and the briefing are the mission's own).</summary>
    public void Restore(ProgressSnapshot s)
    {
        Left = s.Left;
        Complete = s.Complete;
        Failed = s.Failed;
        Array.Clear(_cursor);
        s.Cursor.AsSpan(0, Math.Min(s.Cursor.Length, _cursor.Length)).CopyTo(_cursor);
    }

    /// <summary>Back to the mission start (RESTART MISSION).</summary>
    public void Reset()
    {
        Left = Total;
        Complete = Failed = false;
        Array.Clear(_cursor);
    }
}
