using System.Globalization;
using System.Numerics;
using RetroEng.Commands;
using SkyNet.Game.Campaign;
using SkyNet.Game.Weapons;

namespace SkyNet.Game.DevTools;

/// <summary>
/// The console's commands on the <see cref="GameSession"/>: maps and missions, where the soldier
/// is, the cheats (DOS's CHEAT.PRS codes as other names: the codes DOS took after Alt+\ work as
/// typed), the kit and the robots. The Godot port's console commands, those the session can do;
/// the app adds its own (save, load, music, quit...). The session is asked for on every command
/// (NEW GAME → FUTURE SHOCK replaces it).
/// <code>
///   registry ──► SessionCommands ──► session(): Player, Soldier, Weapons.Inventory, Robots, Table
/// </code>
/// </summary>
public static class SessionCommands
{
    public const string NotInMap = "Not in a map";

    /// <summary>"arnold": records 0..11 (DOS's list at 0x4350c, not the super UZI).</summary>
    private const int ArnoldLast = 11;
    /// <summary>A kill that no robot outlives.</summary>
    private const float KillPoints = 1e7f;

    private static readonly CultureInfo C = CultureInfo.InvariantCulture;

    /// <summary>The whoami cheat's line (SKYNET.EXE v1.01 0x142892, the literal at 0x6177e).</summary>
    public const string WhoAmI = "You are #N.";

    /// <summary>The clearautomap cheat's line, DOS's typo kept (v1.01 0x1429cc, the literal at 0x612d1).</summary>
    internal const string AutomapCleared = "Automap cleaered.";

    private static readonly string[] CheatsText =
    [
        "DOS cheat codes (CHEAT.PRS; DOS took them after Alt+\\), each also a command:",
        "  willnotstop (csej) god  -  arnold (cskydere) every weapon  -  superuzi (cskyder)",
        "  slugs (ckugler) ammo  -  surgery (cfalck) health and armour  -  illbeback (cbane) next mission",
        "  win (cslut) mission complete  -  showspawns (cfyr) the robots  -  whoami (chvem) your name",
        "  clearautomap (ckort) the automap forgets what was seen",
    ];

    /// <param name="session">The running session.</param>
    /// <param name="started">Called after a command started a map or mission (the app closes its
    /// menus and resumes play).</param>
    public static void Register(CommandRegistry registry, Func<GameSession> session, Action? started = null)
    {
        GameSession S() => session();
        void Started() => started?.Invoke();
        IEnumerable<string> Weapons(int index) => index == 0
            ? ["all", "ammo", "super", .. Enumerable.Range(0, WeaponTable.SuperUzi + 1).Select(i => i.ToString(C))]
            : [];
        IEnumerable<string> OnOff(int index) => index == 0 ? ["on", "off"] : [];

        registry.Add("cheats", "cheats", _ => string.Join('\n', CheatsText), "the DOS cheat codes");
        registry.Add("maps", "maps", _ => Maps(S()), "the maps in the data");
        registry.Add(new Command("map", "map <NNN | MAP.NNN>", args => Map(S(), args, Started))
        {
            Help = "plays the map (in the mission of its decade)",
            Arguments = i => i == 0 ? MapNumbers(S()).Select(n => n.ToString(C)) : [],
        });
        registry.Add(new Command("mission", "mission <n>", args => StartMission(S(), args, Started))
        {
            Help = "starts mission n from its first map",
            Arguments = i => i == 0 ? S().Table.Missions.Select(m => m.Number.ToString(C)) : [],
        });
        registry.Add("restart", "restart", _ => InMap(S(), s =>
        {
            s.RestartMission();
            Started();
            return "Mission restarted";
        }), "RESTART MISSION");
        registry.Add("nextlevel", "nextlevel", _ => NextMission(S(), Started), "the next mission", "illbeback", "cbane");
        registry.Add("win", "win", _ => InMap(S(), s => s.CompleteMission() ? "Mission complete" : "Not now"),
            "MISSION COMPLETE", "cslut");
        registry.Add("pos", "pos", _ => InMap(S(), Pos), "where the soldier stands, and the options that start there", "where");
        registry.Add("tp", "tp <x> <y> <z> [yaw]", args => InMap(S(), s => Teleport(s, args)), "puts the feet there (world units)", "teleport");
        registry.Add("look", "look <yaw> [pitch]", args => InMap(S(), s => Look(s, args)), "turns the view (degrees)");
        registry.Add(new Command("god", "god [on|off]", args => InMap(S(), s =>
        {
            s.Soldier.God = args.Switch(0, s.Soldier.God);
            return $"god {Name(s.Soldier.God)}";
        }))
        { Help = "no damage", Aliases = ["willnotstop", "csej"], Arguments = OnOff });
        registry.Add(new Command("noclip", "noclip [on|off]", args => InMap(S(), s =>
        {
            s.Player.Noclip = args.Switch(0, s.Player.Noclip);
            s.Player.Velocity = Vector3.Zero;
            return $"noclip {Name(s.Player.Noclip)}";
        }))
        { Help = "flies through walls", Aliases = ["fly"], Arguments = OnOff });
        registry.Add(new Command("give", "give [all | ammo | super | <record> | <name>]", args => InMap(S(), s => Give(s, args)))
        { Help = "a weapon or ammo (no argument: what is owned)", Arguments = Weapons });
        registry.Add("arnold", "arnold", _ => InMap(S(), s => $"Every weapon ({Arnold(s.Weapons.Inventory)} owned)"), "every weapon", "cskydere");
        registry.Add("whoami", "whoami", _ => S().Pilot.Expand(WhoAmI), "the player's name (DOS's \"You are #N.\")", "chvem");
        registry.Add("clearautomap", "clearautomap", _ => InMap(S(), s =>
        {
            s.Seen.Clear();
            return AutomapCleared;
        }), "the automap forgets what was seen", "ckort");
        registry.Add(new Command("navmesh", "navmesh [on|off]", args =>
        {
            var s = S();
            s.ShowRobotNav = args.Switch(0, s.ShowRobotNav);
            var mesh = s.Robots?.Nav == null ? "no mesh: ROBOT AI CLASSIC or no map" : "the walkers' meshes and paths";
            return $"navmesh {Name(s.ShowRobotNav)} ({mesh})";
        })
        { Help = "draws the walking robots' nav meshes and paths (ROBOT AI IMPROVED)", Arguments = OnOff });
        registry.Add("superuzi", "superuzi", _ => InMap(S(), s => SuperUzi(s.Weapons.Inventory)), "the super UZI in hand", "cskyder");
        registry.Add("slugs", "slugs", _ => InMap(S(), s => $"Every ammo pool full ({FillAmmo(s.Weapons.Inventory)} rounds added)"),
            "fills every ammo pool", "ammo", "ckugler");
        registry.Add("surgery", "surgery", _ => InMap(S(), s =>
        {
            (s.Soldier.Health, s.Soldier.Armour) = (Soldier.MaxHealth, 1f);
            return Body(s.Soldier);
        }), "full health and armour", "heal", "cfalck");
        registry.Add("health", "health [points]", args => InMap(S(), s =>
        {
            if (args.OptionalFloat(0, 0f, Soldier.MaxHealth) is { } points)
            {
                s.Soldier.Health = points;
            }

            return Body(s.Soldier);
        }), $"reads or sets the health (0..{Soldier.MaxHealth:0})", "hp");
        registry.Add("armor", "armor [0-100]", args => InMap(S(), s =>
        {
            if (args.OptionalFloat(0, 0f, 100f) is { } percent)
            {
                s.Soldier.Armour = percent / 100f;
            }

            return Body(s.Soldier);
        }), "reads or sets the armour (percent)", "armour");
        registry.Add(new Command("weapon", "weapon <record>", args => InMap(S(), s => Weapon(s.Weapons.Inventory, args.Int(0, 0, WeaponTable.SuperUzi))))
        { Help = "takes and holds a weapon", Arguments = i => i == 0 ? Enumerable.Range(0, WeaponTable.SuperUzi + 1).Select(n => n.ToString(C)) : [] });
        registry.Add("throw", "throw [1-5]", args => InMap(S(), s => Thrown(s.Weapons.Inventory, args)), "picks the thrown item (as F1-F5)", "secondary");
        registry.Add("killall", "killall", _ => InMap(S(), KillAll), "every robot on the map dies", "kill");
        registry.Add("enemies", "enemies", _ => InMap(S(), Enemies), "the map's robots", "showspawns", "cfyr");
        registry.Add("objectives", "objectives", _ => Objectives(S()), "the mission's counter", "obj");
        registry.Add(new Command("moon", "moon [hit | fall | reset]", args => InMap(S(), s => Moon(s.Moon, args.OptionalWord(0))))
        { Help = "the night moon: its hits and fall; one hit (OW!), the fall now, or back in the sky", Arguments = i => i == 0 ? ["hit", "fall", "reset"] : [] });
    }

    /// <summary>"moon": the state, or a hit, the fall or the moon back at rest (the map's load does that too).</summary>
    private static string Moon(NightMoon moon, string? what)
    {
        if (!moon.Shown)
        {
            return moon.Describe();
        }

        switch (what?.ToLowerInvariant())
        {
            case null:
                break;
            case "hit":
                moon.Hit();
                break;
            case "fall":
                moon.Fall();
                break;
            case "reset":
                moon.Reset(MoonSky.Shown);
                break;
            default:
                throw new CommandUsageException();
        }

        return moon.Describe();
    }

    // ---------------------------------------------------------------- maps and missions

    /// <summary>The MAP.NNN numbers in the map archive and the ones the mods add, ascending.</summary>
    public static IReadOnlyList<int> MapNumbers(GameSession s)
    {
        using var archive = s.Data.OpenMapArchive();
        return [.. archive.Entries
            .Select(e => e.Name.StartsWith("MAP.", StringComparison.OrdinalIgnoreCase) && int.TryParse(e.Name.AsSpan(4), NumberStyles.None, C, out var n) ? n : -1)
            .Concat(Mods.ModMaps.Numbers(Mods.ModContent.Files))
            .Where(n => n >= 0).Distinct().Order()];
    }

    private static string Maps(GameSession s)
    {
        var maps = MapNumbers(s);
        return $"{maps.Count} maps: {string.Join(' ', maps.Select(n => n.ToString("D3", C)))}";
    }

    private static string Map(GameSession s, CommandArgs args, Action started)
    {
        if (args.IsEmpty)
        {
            return s.Map != null ? $"MAP.{s.MapNumber:D3}, mission {MissionName(s)}" : NotInMap;
        }

        var word = args[0];
        word = word.StartsWith("MAP.", StringComparison.OrdinalIgnoreCase) ? word[4..] : word;
        if (!int.TryParse(word, NumberStyles.None, C, out var number))
        {
            throw new CommandUsageException();
        }

        if (!MapNumbers(s).Contains(number))
        {
            return $"No MAP.{number:D3} in {s.Data.MapArchive}";
        }

        s.StartAt(number);
        started();
        return $"MAP.{number:D3}, mission {MissionName(s)}";
    }

    private static string StartMission(GameSession s, CommandArgs args, Action started)
    {
        var number = args.Int(0, 0);
        if (s.Table.ByNumber(number) is not { } mission)
        {
            return $"No mission {number} (missions {string.Join(' ', s.Table.Missions.Select(m => m.Number))})";
        }

        s.StartMission(mission);
        started();
        return $"Mission {number}: MAP.{mission.StartMap:D3}";
    }

    private static string NextMission(GameSession s, Action started)
    {
        if (s.Mission is not { } mission)
        {
            return "Not in a mission";
        }

        if (s.Table.After(mission) is not { } next)
        {
            return "The last mission";
        }

        s.StartMission(next);
        started();
        return $"Mission {next.Number}: MAP.{next.StartMap:D3}";
    }

    private static string MissionName(GameSession s) => s.Mission?.Number.ToString(C) ?? "-";

    private static string Objectives(GameSession s)
    {
        if (s.Mission == null)
        {
            return "No mission";
        }

        var p = s.Progress;
        return $"Mission {s.Mission.Value.Number}: {p.Left} of {p.Total} objectives left{(p.Complete ? ", complete" : "")}{(p.Failed ? ", failed" : "")}; phase {s.Phase}";
    }

    // ---------------------------------------------------------------- the soldier

    /// <summary>Where the soldier is, and the command line that starts there.
    /// <code>
    ///   MAP.210 mission 1 feet 1200 64 -3400 yaw 90 pitch 0
    ///   --map 210 --pos 1200,139,-3400 --yaw 90
    /// </code></summary>
    private static string Pos(GameSession s)
    {
        var p = s.Player;
        var eye = p.Position + new Vector3(0f, p.Eye, 0f);
        return string.Create(C, $"MAP.{s.MapNumber:D3} mission {MissionName(s)} feet {p.Position.X:0} {p.Position.Y:0} {p.Position.Z:0} ") +
            string.Create(C, $"yaw {Degrees(p.Yaw):0} pitch {Degrees(p.Pitch):0}{(p.Noclip ? " (noclip)" : "")}\n") +
            string.Create(C, $"--map {s.MapNumber} --pos {eye.X:0},{eye.Y:0},{eye.Z:0} --yaw {Degrees(p.Yaw):0}");
    }

    private static string Teleport(GameSession s, CommandArgs args)
    {
        var feet = new Vector3(args.Float(0), args.Float(1), args.Float(2));
        var yaw = args.OptionalFloat(3) is { } degrees ? Radians(degrees) : s.Player.Yaw;
        var pitch = s.Player.Pitch;
        s.Player.Spawn(feet, yaw);
        s.Player.Pitch = pitch;
        return string.Create(C, $"feet {feet.X:0} {feet.Y:0} {feet.Z:0} yaw {Degrees(yaw):0}");
    }

    private static string Look(GameSession s, CommandArgs args)
    {
        s.Player.Yaw = Radians(args.Float(0));
        if (args.OptionalFloat(1, -90f, 90f) is { } pitch)
        {
            s.Player.Pitch = Radians(pitch);
        }

        return string.Create(C, $"yaw {Degrees(s.Player.Yaw):0} pitch {Degrees(s.Player.Pitch):0}");
    }

    private static string Body(Soldier soldier) =>
        string.Create(C, $"health {soldier.Health:0} / {Soldier.MaxHealth:0}, armour {soldier.Armour * 100f:0} %");

    // ---------------------------------------------------------------- the kit

    private static string Give(GameSession s, CommandArgs args)
    {
        var inventory = s.Weapons.Inventory;
        if (args.IsEmpty)
        {
            var owned = Enumerable.Range(0, inventory.Table.Weapons.Length).Where(inventory.Owns).Select(r => NameOf(inventory, r));
            return $"Owned: {string.Join(", ", owned)}";
        }

        var what = args.Rest();
        switch (what.ToLowerInvariant())
        {
            case "all":
                Arnold(inventory);
                inventory.Grant(WeaponTable.SuperUzi);
                FillAmmo(inventory);
                return "Every weapon, every pool full";
            case "ammo":
                return $"Every ammo pool full ({FillAmmo(inventory)} rounds added)";
            case "super" or "superuzi":
                return SuperUzi(inventory);
        }

        var record = CommandArgs.TryInt(what, out var n) ? n
            : Enumerable.Range(0, Math.Min(inventory.Table.Weapons.Length, WeaponTable.SuperUzi + 1))
                .FirstOrDefault(r => NameOf(inventory, r).Equals(what, StringComparison.OrdinalIgnoreCase), -1);
        if (record is < 0 or > WeaponTable.SuperUzi || record >= inventory.Table.Weapons.Length)
        {
            return $"Unknown weapon '{what}' (a record 0-{WeaponTable.SuperUzi} or a name)";
        }

        inventory.Grant(record);
        if (inventory.Table.Weapons[record].Pool is var pool and >= 0)
        {
            inventory.AddAmmo(pool, inventory.Table.Pools[pool].Max);
        }

        return $"{NameOf(inventory, record)}";
    }

    private static int Arnold(Inventory inventory)
    {
        for (var r = 0; r <= Math.Min(ArnoldLast, inventory.Table.Weapons.Length - 1); r++)
        {
            inventory.Grant(r);
        }

        return Enumerable.Range(0, inventory.Table.Weapons.Length).Count(inventory.Owns);
    }

    /// <summary>DOS's handler: record 12 owned and selected, its pool filled.</summary>
    private static string SuperUzi(Inventory inventory)
    {
        inventory.Grant(WeaponTable.SuperUzi);
        if (inventory.Table.Weapons[WeaponTable.SuperUzi].Pool is var pool and >= 0)
        {
            inventory.AddAmmo(pool, inventory.Table.Pools[pool].Max);
        }

        inventory.Select(WeaponTable.SuperUzi);
        return $"{NameOf(inventory, WeaponTable.SuperUzi)}: {inventory.AmmoFor(WeaponTable.SuperUzi)} rounds";
    }

    private static int FillAmmo(Inventory inventory)
    {
        var added = 0;
        for (var p = 0; p < inventory.Table.Pools.Length; p++)
        {
            added += inventory.AddAmmo(p, inventory.Table.Pools[p].Max);
        }

        return added;
    }

    private static string Weapon(Inventory inventory, int record)
    {
        inventory.Grant(record);
        inventory.Select(record);
        return $"Weapon {record}: {NameOf(inventory, record)}";
    }

    private static string Thrown(Inventory inventory, CommandArgs args)
    {
        if (args.OptionalInt(0, 1, Inventory.ThrowKeys.Length) is { } key)
        {
            inventory.SelectThrown(Inventory.ThrowKeys[key - 1]);
        }

        return $"Thrown: {NameOf(inventory, inventory.Thrown)} x{inventory.AmmoFor(inventory.Thrown)}";
    }

    private static string NameOf(Inventory inventory, int record) =>
        record >= 0 && record < inventory.Table.Weapons.Length ? inventory.Table.Weapons[record].Name : $"record {record}";

    // ---------------------------------------------------------------- the robots

    private static string KillAll(GameSession s)
    {
        var robots = s.Robots?.Active.ToList() ?? [];
        foreach (var robot in robots)
        {
            robot.TakeDamage(new Damage(KillPoints, DamageKind.Direct, robot.Position, Scaled: false));
        }

        return $"Killed {robots.Count(r => !r.Alive)} of {robots.Count} robots";
    }

    private static string Enemies(GameSession s)
    {
        var all = s.Robots?.All ?? [];
        var lines = all.Select(r => string.Create(C,
            $"  {r.Name,-12} marker 0x{r.MarkerOffset:x5} at {r.Position.X:0} {r.Position.Y:0} {r.Position.Z:0} hp {r.Hp:0}{(r.Alive ? "" : " (gone)")}"));
        return $"{all.Count(r => r.Alive)} of {all.Count} robots alive\n{string.Join('\n', lines)}";
    }

    // ---------------------------------------------------------------- helpers

    /// <summary>A command of the map: run on the session, or <see cref="NotInMap"/>.</summary>
    private static string InMap(GameSession s, Func<GameSession, string> run) => s.Map != null ? run(s) : NotInMap;

    private static string Name(bool on) => on ? "on" : "off";

    private static float Degrees(float radians) => radians * 180f / MathF.PI;

    private static float Radians(float degrees) => degrees * MathF.PI / 180f;
}
