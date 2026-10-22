using System.Text;

namespace SkyNet.Formats.Tests;

/// <summary>
/// The enemy AI tables shipped in data/tables (SKYNET.EXE v1.01's, see ExeTablesTests) compared with the
/// Godot port's generated enemy_ai_data.gd (GodotReference/godot_dump_ais.txt: MD5s only). The port's
/// data came from v1.00; scripts are compared in a build-independent order (see dump_ais.gd.txt).
/// </summary>
public class AisRealDataTests
{
    static readonly string[] Reference = File.ReadAllLines(Path.Combine(AppContext.BaseDirectory, "GodotReference", "godot_dump_ais.txt"));

    static string Md5(string s) => TestData.Md5(Encoding.UTF8.GetBytes(s));
    static string N(int? v) => v?.ToString() ?? "-";

    static string TypeLine(EnemyType t)
    {
        var parts = new List<string> { t.Name, t.State.ToString(), t.HitPoints.ToString() };
        parts.AddRange(new[] { t.Speed, t.Near, t.Turn, t.Axis, t.AngleMin, t.AngleMax, t.Range, t.Altitude,
            t.VerticalAccel, t.AvoidRadius, t.ForwardSpeed, t.EngineSound }.Select(N));
        parts.Add(t.Fire is { } f ? string.Join(',', f.MuzzleX, f.MuzzleY, f.MuzzleZ, f.Ammo, f.Speed, f.Rate, f.Range, f.AimGate) : "-");
        parts.Add(string.Join(',', t.Events.SelectMany(e => new[] { e.Frame, e.Sound })));
        parts.Add(string.Join('|', t.Death.Select(d => $"{d.Type},{d.X},{d.Y},{d.Z}")));
        return string.Join(' ', parts);
    }

    static string Pred(AisPredicate p) => p switch
    {
        AisPredicate.See => "see", AisPredicate.Distance => "dist", AisPredicate.Bearing => "bearing",
        AisPredicate.BearingAbs => "bearing_abs", AisPredicate.Random => "rand", AisPredicate.AngleToPlayer => "angle_to_player",
        AisPredicate.AnimFlags => "animflags", AisPredicate.Flags => "flags", _ => "unknown",
    };

    static string Script(AisData ais, uint start)
    {
        var ops = ais.ScriptOps(start);
        var index = ops.Select((o, i) => (o.Va, i)).ToDictionary(x => x.Va, x => x.i);
        return string.Join('\n', ops.Select(o => o.Kind switch
        {
            AisOpKind.If => $"if {Pred(o.Predicate)} {o.Compare.ToString().ToLowerInvariant()} {o.B} {index[o.Target]}",
            AisOpKind.Goto => $"goto {index[o.Target]}",
            AisOpKind.Wait => $"wait {o.A} {(o.C != 0 ? 1 : 0)}",
            AisOpKind.Anim => ais.Anims[o.AnimVa] is var a ? $"anim {a.Fps} {a.Flags} {string.Join(',', a.Frames.Select(f => f.Frame))}" : "",
            AisOpKind.Sound => $"sound {o.A}",
            AisOpKind.Set => $"set {o.A} {o.B}",
            AisOpKind.Add => $"add {o.A} {o.B}",
            AisOpKind.SetFn => $"setfn {o.A} {Pred(o.Predicate)}",
            _ => "end",
        }));
    }

    [Fact]
    public void Skynet_exe_tables_match_the_godot_port()
    {
        var ais = SkyNet.Data.GameTables.Ais;
        Assert.Equal("1.01", ais.Tables.Version);

        var count = Reference.Single(l => l.StartsWith("COUNT ", StringComparison.Ordinal)).Split(' ');
        Assert.Equal(int.Parse(count[1]), ais.Types.Length);
        Assert.Equal(int.Parse(count[2]), ais.Ops.Count);
        Assert.Equal(int.Parse(count[3]), ais.Anims.Count);

        var mismatched = new List<string>();
        foreach (var l in Reference.Where(l => l.StartsWith("AIS ", StringComparison.Ordinal)).Select(l => l.Split(' ')))
        {
            var t = ais.Types[int.Parse(l[1])];
            if (Md5(TypeLine(t)) != l[2])
                mismatched.Add($"type {t.Index} {t.Name}");
            if ((t.ScriptVa is { } s ? Md5(Script(ais, s)) : "-") != l[3])
                mismatched.Add($"script {t.Index} {t.Name}");
        }
        foreach (var l in Reference.Where(l => l.StartsWith("AMMO ", StringComparison.Ordinal)).Select(l => l.Split(' ')))
        {
            var a = ais.Ammo[int.Parse(l[1])];
            string line = string.Join(' ', a.Family, a.Model, a.ImpactBank, a.Damage, a.BlastRadius, a.LifeTicks, a.FireSound, a.ImpactSound);
            if (Md5(line) != l[2])
                mismatched.Add($"ammo {a.Index}");
        }
        Assert.Empty(mismatched);

        // No script reaches an unknown handler or predicate.
        Assert.DoesNotContain(ais.Ops.Values, o => o.Kind == AisOpKind.End && o.Handler != 0 && o.Handler < 0x140000 && o.Handler > 0x120000);
        Assert.DoesNotContain(ais.Ops.Values, o => o.Kind is AisOpKind.If or AisOpKind.SetFn && o.Predicate == AisPredicate.Unknown);
        // Multi-segment types (turrets, T-rex, boss) and path vehicles (state 11) exist.
        Assert.True(ais.Types.Count(t => t.Segments.Length > 0) >= 30, "segments");
        Assert.Contains(ais.Types, t => t.State == 11);
    }
}
