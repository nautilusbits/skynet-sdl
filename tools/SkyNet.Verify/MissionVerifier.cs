using System.Globalization;
using System.Numerics;
using SkyNet.Formats;
using SkyNet.Game.Behaviour;
using SkyNet.Game.Campaign;
using SkyNet.Game.Enemies;
using SkyNet.Game.Player;

namespace SkyNet.Verify;

/// <summary>One step of a mission spec (tests/rules/skynet.missions.txt of the Godot port).</summary>
public sealed record MissionStep(string Kind, string Verb, int Map, int Id, IReadOnlyList<string> Want, int Counter, string Xfail);

public sealed record MissionSpec(int Key, IReadOnlyList<MissionStep> Steps, string Xfail);

/// <summary>A step row, written as the Godot mission runner's line:
/// <c>mission n verb map id result tag why</c>.</summary>
public sealed record StepRow(int Mission, int N, string Verb, int Map, int Id, string Result, string Tag, string Why)
{
    public override string ToString() =>
        string.Create(CultureInfo.InvariantCulture, $"{Mission} {N} {Verb} {Map} {Id:x5} {Result} {(Tag.Length > 0 ? Tag : "-")} {Why}");
}

/// <summary>
/// The Godot port's mission spec runner (scripts/triggers/mission_verifier.gd) over the C# level
/// behaviour, headless: the steps in order, nothing reset between them, exits TAKEN (the next map
/// comes up at the exit's marker set, a map seen before keeps its state), one objective counter for
/// the mission. <c>expect</c> means "these happened" (extras are written into the row);
/// <c>must_not</c> means none of them did.
/// </summary>
public sealed class MissionVerifier(GameData data)
{
    private readonly TransfrmPrs _transfrm = BehaviourModel.LoadTransfrm(data);

    public static List<MissionSpec> Parse(IEnumerable<string> lines)
    {
        var specs = new List<MissionSpec>();
        int key = -1;
        var steps = new List<MissionStep>();
        var xfail = "";
        void Close()
        {
            if (key >= 0)
            {
                specs.Add(new MissionSpec(key, steps, xfail));
            }
        }

        foreach (var raw in lines)
        {
            var line = raw.Trim();
            if (line.Length == 0 || line.StartsWith('#'))
            {
                continue;
            }

            var t = line.Split(' ', StringSplitOptions.RemoveEmptyEntries);
            switch (t[0])
            {
                case "mission":
                    Close();
                    key = int.Parse(t[1], CultureInfo.InvariantCulture);
                    steps = [];
                    xfail = "";
                    continue;
                case "xfail":
                    xfail = t[1];
                    continue;
                case "counter":
                    steps.Add(new MissionStep("counter", "counter", key, 0, [], int.Parse(t[1], CultureInfo.InvariantCulture), ""));
                    continue;
            }

            var kind = t[0] == "must_not" ? "must_not" : "do";
            var i = kind == "must_not" ? 1 : 0;
            var verb = t[i];
            var map = int.Parse(t[i + 1], CultureInfo.InvariantCulture);
            var id = int.Parse(t[i + 2], NumberStyles.HexNumber, CultureInfo.InvariantCulture);
            var rest = t.Skip(i + 3).ToList();
            var tag = "";
            var x = rest.IndexOf("xfail");
            if (x >= 0)
            {
                tag = rest[x + 1];
                rest = rest[..x];
            }

            var want = rest.Count > 0 && rest[0] == "expect" ? rest.Skip(1).ToList() : [];
            steps.Add(new MissionStep(kind, verb, map, id, want, 0, tag));
        }

        Close();
        return specs;
    }

    private sealed class Run
    {
        public required MissionProgress Mission;
        public readonly Dictionary<int, Driver> Maps = [];
        public readonly Stack<int> Back = new();
        public int Current, Previous;
        public Vector3 Feet, Eye;
        /// <summary>Mission 5's torpedo ride, under way on MAP.250 after MAP.253 (main.gd).</summary>
        public TorpedoRide? Ride;
        public bool RideExit;
    }

    public List<StepRow> RunAll(IEnumerable<MissionSpec> specs)
    {
        var rows = new List<StepRow>();
        foreach (var spec in specs)
        {
            rows.AddRange(RunMission(spec));
        }

        return rows;
    }

    public List<StepRow> RunMission(MissionSpec spec)
    {
        var rows = new List<StepRow>();
        var run = new Run { Mission = MissionProgress.Load(data, spec.Key) };
        if (!Enter(run, spec.Key, -1))
        {
            rows.Add(new StepRow(spec.Key, 0, "mission", spec.Key, 0, Row.Fail, "", "the start map would not load"));
            return rows;
        }

        var n = 0;
        foreach (var step in spec.Steps)
        {
            n++;
            var tag = step.Xfail.Length > 0 ? step.Xfail : spec.Xfail;
            rows.Add(Final(RunStep(run, spec.Key, n, step), tag));
        }

        return rows;
    }

    private static StepRow Final(StepRow r, string tag) =>
        tag.Length == 0 ? r : r with { Tag = tag, Result = r.Result == Row.Fail ? "XFAIL" : r.Result == Row.Pass ? "XPASS" : r.Result };

    private StepRow RunStep(Run run, int key, int n, MissionStep step)
    {
        if (step.Kind == "counter")
        {
            var left = run.Mission.Left;
            return new StepRow(key, n, "counter", key, 0, left == step.Counter ? Row.Pass : Row.Fail, "",
                left == step.Counter ? "" : $"the counter reads {left}, not {step.Counter}");
        }

        var forced = "";
        if (run.Current != step.Map)
        {
            forced = $"the run stood on MAP.{run.Current:D3}";
            if (!Enter(run, step.Map, -1))
            {
                return new StepRow(key, n, step.Verb, step.Map, step.Id, Row.Fail, "", Equiv.Join(forced, "and that map would not load"));
            }
        }

        var d = run.Maps[run.Current];
        if (!d.Map.TryGetEntityAt(step.Id, out var e))
        {
            return new StepRow(key, n, step.Verb, step.Map, step.Id, Row.Fail, "", "no such record");
        }

        var tokens = Perform(run, d, step.Verb, e);
        var (missing, extra) = Equiv.Compare(step.Want, tokens);
        var negative = step.Kind == "must_not";
        bool ok;
        var why = "";
        if (negative)
        {
            var did = step.Want.Where(t => !missing.Contains(t)).ToList();
            ok = did.Count == 0;
            if (!ok)
            {
                why = "it did " + string.Join(' ', did);
            }
        }
        else
        {
            ok = missing.Count == 0;
            if (!ok)
            {
                why = "missing " + string.Join(' ', missing);
            }

            if (extra.Count > 0)
            {
                why = Equiv.Join(why, "also " + string.Join(' ', extra));
            }
        }

        TakeExit(run, d);
        if (forced.Length > 0)
        {
            ok = false;
            why = Equiv.Join(why, forced);
        }

        return new StepRow(key, n, step.Verb, step.Map, step.Id, ok ? Row.Pass : Row.Fail, "", why);
    }

    private const float StepSeconds = 3f;

    private static List<string> Perform(Run run, Driver d, string verb, MapEntity e)
    {
        var off = e.FileOffset;
        var p = d.PositionOf(off);
        var wallButton = e.Variant == MapEntityVariant.Mesh && (e.StateByte & ActRules.WallButtonBit) != 0 && e.NameIndex >= 0;
        switch (verb)
        {
            case "use":
            case "exit" when e.ActType == ActRules.ProxGate:
                run.Feet = Driver.Away;
                run.Eye = p;
                d.Tick(new PlayerProbe(run.Feet, run.Eye, UsePressed: true, AimedOwner: wallButton ? off : -1));
                break;
            case "use_below":
                // The key from under the record: inside its horizontal radius, outside its 3D reach.
                run.Eye = p - new Vector3(0f, ActRules.ProxGateRadius + ActRules.PlayerRadius + 40f, 0f);
                run.Feet = run.Eye - new Vector3(0f, PlayerController.EyeHeight, 0f);
                d.Tick(new PlayerProbe(run.Feet, run.Eye, UsePressed: true));
                break;
            case "prox":
                run.Eye = p;
                run.Feet = Driver.Away;
                break;
            case "exit":
                run.Feet = p;
                run.Eye = Driver.Away;
                d.Tick(new PlayerProbe(run.Feet, run.Eye));
                if (!d.B.ExitTaken)
                {
                    d.Tick(new PlayerProbe(run.Feet, run.Eye, UsePressed: true));
                }

                break;
            case "shoot":
                d.B.Hit(off, 1f);
                break;
            case "walk":
                d.Tick(new PlayerProbe(Driver.Away, Driver.Away, FloorOwner: off));
                break;
            case "wait" when d.B.Paths.Get(off) is { } vehicle:
                return WaitForPath(run, d, vehicle);
            case "wait":
                run.Feet = p;
                run.Eye = p + new Vector3(0f, PlayerController.EyeHeight, 0f);
                break;
            case "stay":
                break;
        }

        // The game runs on with the player where the step put him, until the movers arrive.
        // A taken exit leaves the level running until the map change lands (main.gd's fade), so
        // what the same chain started on the way still announces itself.
        // …and for as long after that as the torpedo ride has the view (main.gd _do_stay).
        for (var t = 0f; t < StepSeconds || (run.Ride is { Riding: true } && t < PathWaitMax); t += Driver.Dt)
        {
            d.Tick(new PlayerProbe(run.Feet, run.Eye));
            RideStep(run, d);
        }

        return d.Drain();
    }

    /// <summary>One frame of the torpedo ride; its exit is announced as a doorway's would be.</summary>
    private static void RideStep(Run run, Driver d)
    {
        if (run.Ride is not { Riding: true } ride || !ride.Step(Driver.Dt, run.Feet, run.Eye, 0f, out _))
        {
            return;
        }

        d.Log.Exit(new ExitRequest(-1, TorpedoRide.ToMap, TorpedoRide.ToSet));
        run.RideExit = true;
    }

    /// <summary>A path vehicle's longest wait: its own drive × <see cref="PathWaitSlack"/> divided
    /// by its ticks a frame, plus <see cref="StepSeconds"/>, at most this (mission_verifier.gd).</summary>
    private const float PathWaitMax = 60f, PathWaitSlack = 1.5f;

    /// <summary>mission_verifier.gd _do_wait on a path vehicle: a machine already ticked from where
    /// the player stands is watched from there (MAP.234's HK flies out of his window if he is put on
    /// its spawn); otherwise the player stands beside it. The step ends when the path runs out.</summary>
    private static List<string> WaitForPath(Run run, Driver d, PathVehicle vehicle)
    {
        if (vehicle.TicksFrom(run.Feet) == 0)
        {
            run.Feet = vehicle.Position + new Vector3(0f, 0f, 256f);
            run.Eye = run.Feet + new Vector3(0f, PlayerController.EyeHeight, 0f);
        }

        var per = Math.Max(vehicle.TicksFrom(run.Feet), 1);
        var limit = Math.Clamp(vehicle.DriveSeconds() * PathWaitSlack / per + StepSeconds, StepSeconds, PathWaitMax);
        for (var t = 0f; t < limit && !vehicle.Finished; t += Driver.Dt)
        {
            d.Tick(new PlayerProbe(run.Feet, run.Eye));
            RideStep(run, d);
        }

        d.Tick(new PlayerProbe(run.Feet, run.Eye));
        return d.Drain();
    }

    private void TakeExit(Run run, Driver d)
    {
        if (run.RideExit)
        {
            run.RideExit = false;
            d.B.ExitRefused();
            run.Back.Push(run.Current);
            Enter(run, TorpedoRide.ToMap, TorpedoRide.ToSet);
            return;
        }

        if (d.B.PendingExit is not { } exit)
        {
            return;
        }

        d.B.ExitRefused();
        if (exit.Back)
        {
            if (run.Back.Count > 0)
            {
                Enter(run, run.Back.Pop(), exit.MarkerSet);
            }

            return;
        }

        run.Back.Push(run.Current);
        Enter(run, exit.TargetMap, exit.MarkerSet);
    }

    /// <summary>Brings a map up (or back) with the player at marker set <paramref name="set"/>
    /// (main.gd place_at_marker: the first marker of that type), else at the start marker.</summary>
    private bool Enter(Run run, int num, int set)
    {
        if (!run.Maps.TryGetValue(num, out var d))
        {
            if (data.LoadMap(num) is not { } map)
            {
                return false;
            }

            Spot(map, set, out run.Feet, out run.Eye);
            d = new Driver(map, _transfrm, run.Mission, run.Feet, run.Eye);
            run.Maps[num] = d;
        }
        else
        {
            Spot(d.Map, set, out run.Feet, out run.Eye);
            d.B.Arm(run.Feet, run.Eye);
        }

        run.Previous = run.Current;
        run.Current = num;
        run.Ride = TorpedoRide.Due(num, run.Previous) ? new TorpedoRide() : null;
        d.Tick(new PlayerProbe(run.Feet, run.Eye));
        d.Drain();
        return true;
    }

    private static void Spot(MapFile map, int set, out Vector3 feet, out Vector3 eye)
    {
        var want = set >= 0 ? set : 0;
        var m = map.Entities.FirstOrDefault(x => x.Variant == MapEntityVariant.Sprite && x.MarkerType == want)
                ?? map.Entities.FirstOrDefault(x => x.Variant == MapEntityVariant.Sprite && x.MarkerType == 0);
        feet = m != null ? new Vector3(m.X, -(m.Y + 0x10), -m.Z) : Driver.Away;
        eye = feet + new Vector3(0f, PlayerController.EyeHeight, 0f);
    }
}
