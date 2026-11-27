namespace SkyNet.Game.Net;

/// <summary>
/// A deathmatch bot's character (Quake III's bot characteristics, ideas only): each 0..1, 0.5 the
/// average. Skill (<see cref="DmSettings.BotSkill"/> 0-2) sets the base numbers
/// (<see cref="BotBrain.Skills"/>); the traits bend them per bot name.
/// <code>
///   Accuracy     spread of each shot          Caution     retreat earlier, value health more
///   Reaction     delay before the first shot  VehicleLike how much a free jeep/HK is worth
///   AimSkill     error settles fast, leads    ItemFocus   how much items are worth
///   Aggression   chases longer, fights closer Strafe      how hard it strafes in a fight
///   Camp         waits at a reached goal      Jump        hops while strafing
/// </code>
/// </summary>
internal readonly record struct BotTraits(
    float Accuracy, float Reaction, float AimSkill, float Aggression, float Caution,
    float VehicleLike, float ItemFocus, float Strafe, float Camp, float Jump)
{
    /// <summary>Every trait average: the bot plays its skill's base numbers.</summary>
    public static readonly BotTraits Average = new(0.5f, 0.5f, 0.5f, 0.5f, 0.5f, 0.5f, 0.5f, 0.5f, 0.5f, 0.5f);

    /// <summary>Per <see cref="DmServer.BotNames"/> entry: who they are.</summary>
    private static readonly Dictionary<string, BotTraits> ByName = new()
    {
        //                     acc    react  aim    aggr   caut   veh    item   straf  camp   jump
        ["T-800"] = new(0.70f, 0.40f, 0.60f, 0.90f, 0.10f, 0.50f, 0.30f, 0.20f, 0.10f, 0.10f),
        ["T-600"] = new(0.40f, 0.30f, 0.30f, 0.80f, 0.20f, 0.30f, 0.30f, 0.20f, 0.20f, 0.10f),
        ["HK-UNIT"] = new(0.60f, 0.60f, 0.70f, 0.60f, 0.40f, 1.00f, 0.30f, 0.50f, 0.10f, 0.20f),
        ["SKYNET"] = new(0.80f, 0.70f, 0.90f, 0.50f, 0.60f, 0.70f, 0.70f, 0.60f, 0.30f, 0.30f),
        ["CYBERDYNE"] = new(0.50f, 0.50f, 0.50f, 0.40f, 0.70f, 0.60f, 0.90f, 0.50f, 0.40f, 0.30f),
        ["ENDO"] = new(0.60f, 0.50f, 0.40f, 0.70f, 0.30f, 0.40f, 0.40f, 0.40f, 0.20f, 0.20f),
        ["T-1000"] = new(0.70f, 0.80f, 0.70f, 0.80f, 0.30f, 0.40f, 0.50f, 0.90f, 0.10f, 0.70f),
        ["SERIES 800"] = new(0.60f, 0.50f, 0.60f, 0.60f, 0.40f, 0.50f, 0.50f, 0.40f, 0.30f, 0.20f),
        ["DYSON"] = new(0.40f, 0.50f, 0.50f, 0.20f, 0.90f, 0.60f, 0.80f, 0.60f, 0.70f, 0.30f),
        ["REESE"] = new(0.60f, 0.70f, 0.60f, 0.70f, 0.50f, 0.80f, 0.60f, 0.80f, 0.10f, 0.60f),
        ["CONNOR"] = new(0.70f, 0.60f, 0.80f, 0.50f, 0.70f, 0.90f, 0.70f, 0.70f, 0.30f, 0.50f),
        ["MARCUS"] = new(0.50f, 0.60f, 0.50f, 0.90f, 0.20f, 0.70f, 0.40f, 0.60f, 0.10f, 0.40f),
    };

    /// <summary>The traits of the bot called <paramref name="name"/> (a numbered duplicate,
    /// "T-800-13", is the T-800); <see cref="Average"/> for a name not in the table.</summary>
    public static BotTraits For(string name)
    {
        if (ByName.TryGetValue(name, out var t))
        {
            return t;
        }

        var dash = name.LastIndexOf('-');
        return dash > 0 && ByName.TryGetValue(name[..dash], out t) ? t : Average;
    }

    /// <summary>From <paramref name="low"/> at trait 0 to <paramref name="high"/> at trait 1.</summary>
    public static float Lerp(float low, float high, float trait) => low + (high - low) * Math.Clamp(trait, 0f, 1f);
}
