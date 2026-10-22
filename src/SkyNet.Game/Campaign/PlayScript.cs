using System.Globalization;
using System.Numerics;
using RetroEng.Loop;
using SkyNet.Formats;
using SkyNet.Game.Behaviour;
using SkyNet.Game.Enemies;
using SkyNet.Game.Player;
using SkyNet.Game.Weapons;

namespace SkyNet.Game.Campaign;

/// <summary>
/// A scripted play of a <see cref="GameSession"/> (<c>--play-script</c>, tests): the soldier is
/// put where a step needs him, with real collision, and the use key and the crosshair go through
/// the session's tick as a player's would. One step per line, <c>#</c> comments; record ids are MAP
/// file offsets (hex with 0x), the ids of the Godot port's mission specs.
/// <code>
///   mission N            start mission N of the table (its briefing, its first map)
///   map N                start on MAP.N (the mission of its decade)
///   use 0xOFF            stand by the record, face it, press the use key for one tick, then 0.5 s
///   stand 0xOFF          stand on the record (a walk-on pad, a lift) for 1 s
///   goto x,y,z [yaw]     put the feet there (level axes) and settle 0.5 s
///   look YAW [PITCH]     turns the view (degrees), no tick (a screenshot's framing)
///   noclip on|off        the soldier flies and passes through walls (a screenshot's framing)
///   shoot 0xOFF N        N points of weapon damage on the record, through the weapons' targets
///   wait S               S seconds with no input
///   god on|off           the soldier takes no damage (on by default: a script plays the
///                        route, the robots shoot back); off lets a script test death
///   robots               lists the map's robots (name, feet, alive) in the log
///   meet NAME [D]        stands the feet D (default 400) in front of the nearest live robot
///                        whose name holds NAME, facing it, and settles 0.5 s
///   expect map|mission|left|phase|alive V    a check; a failed one is reported and the run goes on
///   until map|mission|left|phase|alive V S   plays up to S seconds until the check holds (a check)
/// </code>
/// </summary>
public sealed class PlayScript
{
    private static readonly PlayerActions Keys = new(Forward: 0, Back: 1, Left: 2, Right: 3, Run: 4, Jump: 5, Crouch: 6);
    private static readonly WeaponInput Idle = new(false, false, false);
    private static readonly float[] Rings = [40f, 70f, 110f], SpriteRings = [0f, 25f, 50f];
    private static readonly float[] StandLifts = [32f, 100f, 200f, 300f, 400f];

    private readonly List<(int Line, string[] Words)> _steps = [];

    public static PlayScript Parse(IEnumerable<string> lines)
    {
        var script = new PlayScript();
        var n = 0;
        foreach (var raw in lines)
        {
            n++;
            var line = raw.Split('#')[0].Trim();
            if (line.Length > 0)
            {
                script._steps.Add((n, line.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries)));
            }
        }

        return script;
    }

    public static PlayScript Load(string path) => Parse(File.ReadAllLines(path));

    public int Steps => _steps.Count;

    /// <summary>Plays the script; returns the failed checks (empty: every check held).</summary>
    public List<string> Run(GameSession session, Action<string>? log = null)
    {
        var failures = new List<string>();
        session.Soldier.God = true;
        foreach (var (line, w) in _steps)
        {
            log?.Invoke($"> {string.Join(' ', w)}");
            try
            {
                if (Step(session, w, log) is { } failure)
                {
                    failures.Add($"line {line}: {failure}");
                    log?.Invoke($"  FAIL {failure}");
                }
            }
            catch (Exception e) when (e is FormatException or IndexOutOfRangeException or InvalidOperationException)
            {
                failures.Add($"line {line}: {e.Message}");
                log?.Invoke($"  FAIL {e.Message}");
            }
        }

        return failures;
    }

    private static string? Step(GameSession s, string[] w, Action<string>? log)
    {
        var c = CultureInfo.InvariantCulture;
        switch (w[0])
        {
            case "mission":
                s.StartMission(s.Table.ByNumber(int.Parse(w[1], c)) ?? throw new InvalidOperationException($"no mission {w[1]}"));
                return null;
            case "map":
                s.StartAt(int.Parse(w[1], c));
                return null;
            case "use":
                Use(s, Offset(w[1]));
                return null;
            case "stand":
                Stand(s, Offset(w[1]));
                return null;
            case "goto":
                var p = w[1].Split(',').Select(v => float.Parse(v, c)).ToArray();
                s.Player.Spawn(new Vector3(p[0], p[1], p[2]), w.Length > 2 ? float.Parse(w[2], c) * MathF.PI / 180f : s.Player.Yaw);
                Ticks(s, 0.5f);
                return null;
            case "shoot":
                var off = Offset(w[1]);
                var target = s.Weapons.Targets.ByOwner(off) ?? throw new InvalidOperationException($"0x{off:x5} is no target");
                target.TakeDamage(new Damage(float.Parse(w[2], c), DamageKind.Direct, target.Centre, true));
                Ticks(s, 0.5f);
                return null;
            case "wait":
                Ticks(s, float.Parse(w[1], c));
                return null;
            case "look":
                s.Player.Yaw = float.Parse(w[1], c) * MathF.PI / 180f;
                s.Player.Pitch = w.Length > 2 ? float.Parse(w[2], c) * MathF.PI / 180f : 0f;
                return null;
            case "noclip":
                s.Player.Noclip = w[1] is "on" or "1" or "yes";
                return null;
            case "god":
                s.Soldier.God = w[1] is "on" or "1" or "yes";
                return null;
            case "robots":
                foreach (var r in s.Robots?.All ?? [])
                {
                    log?.Invoke(string.Create(c, $"  {r.Name} type {r.TypeIndex} hp {r.Hp:0} marker 0x{r.MarkerOffset:x5} feet {r.Position.X:0},{r.Position.Y:0},{r.Position.Z:0}{(r.Alive ? "" : " (gone)")}"));
                }

                return null;
            case "meet":
                Meet(s, w[1], w.Length > 2 ? float.Parse(w[2], c) : 400f);
                return null;
            case "until":
                var limit = (int)MathF.Round(float.Parse(w[3], c) * GameSession.TicksPerSecond);
                for (var i = 0; i < limit; i++)
                {
                    if (Check(s, w[1]) == w[2].ToLowerInvariant())
                    {
                        log?.Invoke(string.Create(c, $"  after {i / (float)GameSession.TicksPerSecond:0.00} s; health {s.Soldier.Health:0}"));
                        return null;
                    }

                    Ticks(s, 1f / GameSession.TicksPerSecond);
                }

                return Check(s, w[1]) == w[2].ToLowerInvariant() ? null : $"{w[1]} not {w[2]} within {w[3]} s, got {Check(s, w[1])}";
            case "expect":
                var got = Check(s, w[1]);
                return got == w[2].ToLowerInvariant() ? null : $"expected {w[1]} {w[2]}, got {got}";
            default:
                throw new FormatException($"unknown step {w[0]}");
        }
    }

    private static string Check(GameSession s, string what)
    {
        var c = CultureInfo.InvariantCulture;
        return what switch
        {
            "map" => s.MapNumber.ToString(c),
            "mission" => s.Mission?.Number.ToString(c) ?? "-",
            "left" => s.Progress.Left.ToString(c),
            "phase" => s.Phase.ToString().ToLowerInvariant(),
            "alive" => s.Soldier.Alive ? "yes" : "no",
            _ => throw new FormatException($"unknown check {what}"),
        };
    }

    private static int Offset(string s) =>
        s.StartsWith("0x", StringComparison.OrdinalIgnoreCase)
            ? int.Parse(s.AsSpan(2), NumberStyles.HexNumber, CultureInfo.InvariantCulture)
            : int.Parse(s, CultureInfo.InvariantCulture);

    private static void Ticks(GameSession s, float seconds, bool use = false)
    {
        var n = Math.Max(1, (int)MathF.Round(seconds * GameSession.TicksPerSecond));
        for (var i = 0; i < n; i++)
        {
            s.Tick(default, Keys, Idle, use && i == 0);
        }
    }

    private static MapEntity Record(GameSession s, int off) =>
        s.Map != null && s.Map.TryGetEntityAt(off, out var e) ? e : throw new InvalidOperationException($"no record 0x{off:x5} on MAP.{s.MapNumber:D3}");

    /// <summary>Stands the soldier by a record with his eye near it: a free spot on a ring around
    /// it from which the crosshair sees it (a mesh: the ray strikes its triangles), facing it; a
    /// sprite (a gate) with nothing free around takes the eye on it, as the behaviour tests do.</summary>
    private static void Use(GameSession s, int off)
    {
        var e = Record(s, off);
        var at = BehaviourModel.Position(e);
        var mesh = e.Variant == MapEntityVariant.Mesh;
        Vector3? best = null;
        foreach (var ring in mesh ? Rings : SpriteRings)
        {
            for (var i = 0; i < (ring > 0f ? 8 : 1) && best == null; i++)
            {
                var a = MathF.Tau * i / 8f;
                var spot = at + new Vector3(MathF.Cos(a) * ring, 0f, MathF.Sin(a) * ring);
                // On the floor under the spot (a button's eye level: its own height).
                var feet = mesh ? spot - new Vector3(0f, PlayerController.EyeHeight, 0f) : spot;
                if (s.World.Raycast(spot + new Vector3(0f, 60f, 0f), -Vector3.UnitY, 400f, out var floor))
                {
                    feet = mesh ? feet : floor.Point + Vector3.UnitY;
                }

                var eye = feet + new Vector3(0f, PlayerController.EyeHeight, 0f);
                if (s.World.Overlaps(s.Player.Body, feet + Vector3.UnitY))
                {
                    continue;
                }

                var to = at - eye;
                var reach = mesh
                    ? s.World.Raycast(eye, to, to.Length() + 1f, out var h) && h.Owner == off
                    : to.Length() <= ActRules.ProxGateRadius + ActRules.PlayerRadius;
                if (reach)
                {
                    best = feet;
                }
            }

            if (best != null)
            {
                break;
            }
        }

        var place = best ?? at - new Vector3(0f, PlayerController.EyeHeight, 0f);
        Face(s, place, at);
        Ticks(s, 1f / GameSession.TicksPerSecond, use: true);
        Ticks(s, 0.5f);
    }

    /// <summary>The feet <paramref name="distance"/> from the nearest live robot named
    /// <paramref name="name"/>, on open floor with a clear line to it (its facing side first, then
    /// round it), the soldier looking at it.</summary>
    private static void Meet(GameSession s, string name, float distance)
    {
        var robot = (s.Robots?.Active ?? []).Where(r => r.Name.Contains(name, StringComparison.OrdinalIgnoreCase))
            .OrderBy(r => Vector3.DistanceSquared(r.Position, s.Player.Position)).FirstOrDefault()
            ?? throw new InvalidOperationException($"no live robot named {name} on MAP.{s.MapNumber:D3}");
        var chest = robot.Position + new Vector3(0f, Enemy.AimHeight, 0f);
        Vector3? best = null;
        for (var i = 0; i < 16 && best == null; i++)
        {
            // The robot looks along -Z turned by its yaw (the soldier's convention); then round it.
            var dir = PlayerController.Direction(robot.Yaw + MathF.Tau * ((i + 1) / 2) / 16f * (i % 2 == 0 ? 1 : -1), 0f);
            var feet = robot.Position + new Vector3(dir.X, 0f, dir.Z) * distance;
            if (!s.World.Raycast(feet + new Vector3(0f, 200f, 0f), -Vector3.UnitY, 600f, out var floor))
            {
                continue;
            }

            feet = floor.Point + Vector3.UnitY;
            var eye = feet + new Vector3(0f, PlayerController.EyeHeight, 0f);
            var to = chest - eye;
            if (!s.World.Overlaps(s.Player.Body, feet) && !s.World.Raycast(eye, Vector3.Normalize(to), to.Length(), out _))
            {
                best = feet;
            }
        }

        Face(s, best ?? robot.Position + PlayerController.Direction(robot.Yaw, 0f) * distance, chest);
        Ticks(s, 0.5f);
    }

    private static void Face(GameSession s, Vector3 feet, Vector3 at)
    {
        var to = at - (feet + new Vector3(0f, PlayerController.EyeHeight, 0f));
        var yaw = new Vector2(to.X, to.Z).Length() > 0.1f ? MathF.Atan2(-to.X, -to.Z) : s.Player.Yaw;
        s.Player.Spawn(feet, yaw);
        if (to.Length() > 0.1f)
        {
            s.Player.Pitch = MathF.Asin(Math.Clamp(to.Y / to.Length(), -1f, 1f));
        }
    }

    /// <summary>Puts the feet on a record's mesh — the lowest of its faces under a ray down through
    /// its origin where the body fits (a corridor piece's floor, not its roof) — and lets him stand
    /// there for a second.</summary>
    private static void Stand(GameSession s, int off)
    {
        var at = BehaviourModel.Position(Record(s, off));
        var feet = at;
        foreach (var lift in StandLifts)
        {
            if (s.World.Raycast(at + new Vector3(0f, lift, 0f), -Vector3.UnitY, lift + 200f, out var h) && h.Owner == off
                && !s.World.Overlaps(s.Player.Body, h.Point + new Vector3(0f, 2f, 0f)))
            {
                feet = h.Point + new Vector3(0f, 2f, 0f);
                break;
            }
        }

        s.Player.Spawn(feet, s.Player.Yaw);
        Ticks(s, 1f);
    }
}
