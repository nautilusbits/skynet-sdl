namespace SkyNet.Game.Enemies;

/// <summary>
/// ROBOT AI. CLASSIC moves the robots exactly as DOS does. IMPROVED keeps DOS's AI states, scripts,
/// attacks, weapons and damage and changes only how robots move and when they fire
/// (<see cref="Enemy"/>'s Improved part; docs/game-logic/enemies-ai.md "Improved robot AI").
/// </summary>
public enum RobotAi { Classic, Improved }

/// <summary>The setting's words (the console's <c>robotai</c>, <c>--robot-ai</c>).</summary>
public static class RobotAis
{
    public static readonly IReadOnlyDictionary<string, RobotAi> Words = new Dictionary<string, RobotAi>(StringComparer.OrdinalIgnoreCase)
    {
        ["classic"] = RobotAi.Classic,
        ["improved"] = RobotAi.Improved,
    };

    /// <summary>The word of <paramref name="ai"/> (lower case).</summary>
    public static string Word(RobotAi ai) => ai == RobotAi.Improved ? "improved" : "classic";

    /// <summary>The value of <paramref name="word"/>; null when it names none.</summary>
    public static RobotAi? Parse(string? word) => word != null && Words.TryGetValue(word, out var ai) ? ai : null;
}
