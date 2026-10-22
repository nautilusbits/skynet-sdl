using System.Collections.Immutable;
using RetroEng.Json;
using SkyNet.Formats;

namespace SkyNet.Data;

/// <summary>
/// How one table file reads from and writes to JSON: the header (<see cref="TableFile{T}"/>) and its
/// rows, keys in camelCase in the records' declaration order, enums by name, nullable fields
/// written as null. Every key must be present when read; unknown keys are ignored.
/// <paramref name="merge"/> lays a mod's rows over the table under it (<see cref="Apply"/>); null:
/// the mod's rows replace the table whole.
/// </summary>
public sealed class TableSchema<T>(Func<JsonItem, T> read, Func<T, JsonItem> write, Func<T, T, T>? merge = null)
{
    public TableFile<T> Read(JsonItem file) =>
        new(Str(file, "table"), Str(file, "address"), Str(file, "source"), Str(file, "sourceMd5"), Str(file, "tool"), read(file.Member("rows")));

    /// <summary><paramref name="under"/> with a mod file's rows laid over it: a keyed table replaces the
    /// rows of the keys the file gives and adds new keys after the last; any other replaces the rows
    /// whole. The header stays <paramref name="under"/>'s (a mod file needs only "rows").
    /// <code>
    ///   under  rows 0 1 2 3      file  rows 1' 4      →  0 1' 2 3 4
    /// </code></summary>
    public TableFile<T> Apply(TableFile<T> under, JsonItem file)
    {
        var rows = read(file.Member("rows"));
        return under with { Rows = merge != null ? merge(under.Rows, rows) : rows };
    }

    public JsonItem Write(TableFile<T> file) => JsonItem.NewObject()
        .Add("table", file.Table).Add("address", file.Address).Add("source", file.Source).Add("sourceMd5", file.SourceMd5)
        .Add("tool", file.Tool).Add("rows", write(file.Rows));

    private static string Str(JsonItem o, string key) => o.Member(key).AsString();
}

/// <summary>The schema of each table file (<see cref="TableNames"/>).</summary>
public static class TableSchemas
{
    public static readonly TableSchema<ImmutableArray<AmmoRow>> Ammo = Rows(
        r => new AmmoRow(I(r, "index"), U(r, "callback"), S(r, "family"), S(r, "model"), I(r, "look"), I(r, "effect"), I(r, "damage"),
            I(r, "size"), I(r, "life"), I(r, "fireSound"), I(r, "impactSound"), I(r, "reach"), I(r, "flags"), I(r, "pieces"), I(r, "pieceType")),
        a => JsonItem.NewObject().Add("index", a.Index).Add("callback", a.Callback).Add("family", a.Family).Add("model", a.Model)
            .Add("look", a.Look).Add("effect", a.Effect).Add("damage", a.Damage).Add("size", a.Size).Add("life", a.Life)
            .Add("fireSound", a.FireSound).Add("impactSound", a.ImpactSound).Add("reach", a.Reach).Add("flags", a.Flags)
            .Add("pieces", a.Pieces).Add("pieceType", a.PieceType), a => a.Index);

    public static readonly TableSchema<ImmutableArray<WeaponRow>> Weapons = Rows(
        r => new WeaponRow(I(r, "index"), I(r, "viewX"), I(r, "ammo"), I(r, "speed"), I(r, "lift"), I(r, "rate"), I(r, "muzzleX"),
            I(r, "muzzleY"), I(r, "muzzleZ"), I(r, "fireSound"), I(r, "pool"), I(r, "cost"), I(r, "selectSound"), I(r, "drySound"), I(r, "flags")),
        w => JsonItem.NewObject().Add("index", w.Index).Add("viewX", w.ViewX).Add("ammo", w.Ammo).Add("speed", w.Speed)
            .Add("lift", w.Lift).Add("rate", w.Rate).Add("muzzleX", w.MuzzleX).Add("muzzleY", w.MuzzleY).Add("muzzleZ", w.MuzzleZ)
            .Add("fireSound", w.FireSound).Add("pool", w.Pool).Add("cost", w.Cost).Add("selectSound", w.SelectSound)
            .Add("drySound", w.DrySound).Add("flags", w.Flags), w => w.Index);

    public static readonly TableSchema<ImmutableArray<PoolRow>> Pools = Rows(
        r => new PoolRow(I(r, "index"), I(r, "current"), I(r, "initial"), I(r, "max")),
        p => JsonItem.NewObject().Add("index", p.Index).Add("current", p.Current).Add("initial", p.Initial).Add("max", p.Max), p => p.Index);

    public static readonly TableSchema<StartLists> StartLists = new(
        r => new StartLists(Ints(r, "skyNet"), Ints(r, "futureShock")),
        s => JsonItem.NewObject().Add("skyNet", Ints(s.SkyNet)).Add("futureShock", Ints(s.FutureShock)));

    public static readonly TableSchema<ImmutableArray<PieceRow>> Pieces = Rows(
        r => new PieceRow(I(r, "type"), I(r, "sprite"), I(r, "life"), I(r, "speed"), I(r, "spread"), I(r, "flags")),
        p => JsonItem.NewObject().Add("type", p.Type).Add("sprite", p.Sprite).Add("life", p.Life).Add("speed", p.Speed)
            .Add("spread", p.Spread).Add("flags", p.Flags), p => p.Type);

    public static readonly TableSchema<ImmutableArray<DestructionRow>> Destruction = Rows(
        r => new DestructionRow(I(r, "index"), Ints(r, "effects"), I(r, "spread"), I(r, "dropList"), I(r, "sound")),
        d => JsonItem.NewObject().Add("index", d.Index).Add("effects", Ints(d.Effects)).Add("spread", d.Spread)
            .Add("dropList", d.DropList).Add("sound", d.Sound), d => d.Index);

    public static readonly TableSchema<ImmutableArray<DropListRow>> DropLists = Rows(
        r => new DropListRow(I(r, "index"), Ints(r, "sprites")),
        d => JsonItem.NewObject().Add("index", d.Index).Add("sprites", Ints(d.Sprites)), d => d.Index);

    public static readonly TableSchema<LocationTable> Locations = new(
        r => new LocationTable(S(r, "prefix"), [.. r.Member("rows").ToArray(x => new LocationRow(I(x, "map"), S(x, "name")))]),
        t => JsonItem.NewObject().Add("prefix", t.Prefix)
            .Add("rows", JsonItem.NewArray(t.Rows, x => JsonItem.NewObject().Add("map", x.Map).Add("name", x.Name))));

    public static readonly TableSchema<MissionEnemies> MissionEnemies = new(
        r => new MissionEnemies(Ints(r, "skyNet"), Ints(r, "futureShock")),
        m => JsonItem.NewObject().Add("skyNet", Ints(m.SkyNet)).Add("futureShock", Ints(m.FutureShock)));

    public static readonly TableSchema<ImmutableArray<AisOp>> AisOps = Rows(
        r => new AisOp(U(r, "va"), r.Member("kind").AsEnum<AisOpKind>(), U(r, "handler"), I(r, "a"), I(r, "b"), I(r, "c"),
            r.Member("predicate").AsEnum<AisPredicate>(), U(r, "predicateHandler"), r.Member("compare").AsEnum<AisCompare>(),
            U(r, "target"), U(r, "animVa")),
        o => JsonItem.NewObject().Add("va", o.Va).Add("kind", o.Kind.ToString()).Add("handler", o.Handler).Add("a", o.A)
            .Add("b", o.B).Add("c", o.C).Add("predicate", o.Predicate.ToString()).Add("predicateHandler", o.PredicateHandler)
            .Add("compare", o.Compare.ToString()).Add("target", o.Target).Add("animVa", o.AnimVa), o => o.Va);

    public static readonly TableSchema<ImmutableArray<AisAnimBlock>> AisAnims = Rows(
        r => new AisAnimBlock(U(r, "va"), U(r, "type"), U(r, "count"), U(r, "rawFps"), (ushort)r.Member("flags").AsInt(0, ushort.MaxValue),
            [.. r.Member("frames").ToArray(f => new AisAnimFrame((ushort)f.Member("frame").AsInt(0, ushort.MaxValue),
                (ushort)f.Member("flags").AsInt(0, ushort.MaxValue)))]),
        a => JsonItem.NewObject().Add("va", a.Va).Add("type", a.Type).Add("count", a.Count).Add("rawFps", a.RawFps)
            .Add("flags", (int)a.Flags)
            .Add("frames", JsonItem.NewArray(a.Frames, f => JsonItem.NewObject().Add("frame", (int)f.Frame).Add("flags", (int)f.Flags))), a => a.Va);

    public static readonly TableSchema<ImmutableArray<ItemRow>> Items = Rows(
        r => new ItemRow(I(r, "sprite"), I(r, "pool"), I(r, "amount"), r.Member("heal").AsInt(0, 100), I(r, "armour"), I(r, "weapon"), S(r, "message")),
        i => JsonItem.NewObject().Add("sprite", i.Sprite).Add("pool", i.Pool).Add("amount", i.Amount).Add("heal", i.Heal)
            .Add("armour", i.Armour).Add("weapon", i.Weapon).Add("message", i.Message), i => i.Sprite);

    public static readonly TableSchema<ImmutableArray<DifficultyRow>> Difficulty = Rows(
        r => new DifficultyRow(Level(r), D(r, "enemyFire"), D(r, "damageToEnemy"), D(r, "damageToPlayer"), D(r, "armourRegen")),
        d => JsonItem.NewObject().Add("level", d.Level).Add("enemyFire", Number(d.EnemyFire)).Add("damageToEnemy", Number(d.DamageToEnemy))
            .Add("damageToPlayer", Number(d.DamageToPlayer)).Add("armourRegen", Number(d.ArmourRegen)), d => d.Level);

    public static readonly TableSchema<ImmutableArray<SpriteModelRow>> SpriteModels = Rows(
        r => new SpriteModelRow(S(r, "sprite"), S(r, "model"), D(r, "scale"), F(r, "yaw"), F(r, "spin"), D(r, "bob"), F(r, "offsetY")),
        m => JsonItem.NewObject().Add("sprite", m.Sprite).Add("model", m.Model).Add("scale", Number(m.Scale)).Add("yaw", Number(m.Yaw))
            .Add("spin", Number(m.Spin)).Add("bob", Number(m.Bob)).Add("offsetY", Number(m.OffsetY)), m => m.Sprite);

    public static readonly TableSchema<ImmutableArray<SoundEventRow>> Sounds = Rows(
        r => new SoundEventRow(S(r, "sample"), Between(r, "volume", 0, SoundEventRow.MaxVolume), D(r, "range"),
            Between(r, "pitch", SoundEventRow.MinPitch, SoundEventRow.MaxPitch), I(r, "priority"), r.Member("loop").AsBool()),
        e => JsonItem.NewObject().Add("sample", e.Sample).Add("volume", Number(e.Volume)).Add("range", Number(e.Range))
            .Add("pitch", Number(e.Pitch)).Add("priority", e.Priority).Add("loop", e.Loop), e => SoundEventRow.KeyOf(e.Sample));

    public static readonly TableSchema<ImmutableArray<EffectRow>> Effects = Rows(
        r => new EffectRow(S(r, "effect"), r.Member("low").AsInt(0, EffectRow.MaxCount), r.Member("high").AsInt(0, EffectRow.MaxCount),
            D(r, "life"), D(r, "size"), D(r, "speed"), D(r, "rise"), D(r, "spread"), D(r, "gravity"), D(r, "intensity"), D(r, "flicker"),
            Between(r, "r", 0, 1), Between(r, "g", 0, 1), Between(r, "b", 0, 1)),
        e => JsonItem.NewObject().Add("effect", e.Effect).Add("low", e.Low).Add("high", e.High).Add("life", Number(e.Life))
            .Add("size", Number(e.Size)).Add("speed", Number(e.Speed)).Add("rise", Number(e.Rise)).Add("spread", Number(e.Spread))
            .Add("gravity", Number(e.Gravity)).Add("intensity", Number(e.Intensity)).Add("flicker", Number(e.Flicker))
            .Add("r", Number(e.R)).Add("g", Number(e.G)).Add("b", Number(e.B)), e => e.Effect.ToLowerInvariant());

    public static readonly TableSchema<ImmutableArray<SkyRow>> Skies = Rows(
        r => new SkyRow(I(r, "map"), C(r, "horizonR"), C(r, "horizonG"), C(r, "horizonB"), C(r, "lowR"), C(r, "lowG"), C(r, "lowB"),
            C(r, "zenithR"), C(r, "zenithG"), C(r, "zenithB"), C(r, "glowR"), C(r, "glowG"), C(r, "glowB"), Between(r, "sunElevation", -5, 45),
            Between(r, "glowWidth", 1, 180), C(r, "cover"), C(r, "cloudR"), C(r, "cloudG"), C(r, "cloudB")),
        k => JsonItem.NewObject().Add("map", k.Map).Add("horizonR", Number(k.HorizonR)).Add("horizonG", Number(k.HorizonG))
            .Add("horizonB", Number(k.HorizonB)).Add("lowR", Number(k.LowR)).Add("lowG", Number(k.LowG)).Add("lowB", Number(k.LowB))
            .Add("zenithR", Number(k.ZenithR)).Add("zenithG", Number(k.ZenithG)).Add("zenithB", Number(k.ZenithB))
            .Add("glowR", Number(k.GlowR)).Add("glowG", Number(k.GlowG)).Add("glowB", Number(k.GlowB))
            .Add("sunElevation", Number(k.SunElevation)).Add("glowWidth", Number(k.GlowWidth)).Add("cover", Number(k.Cover))
            .Add("cloudR", Number(k.CloudR)).Add("cloudG", Number(k.CloudG)).Add("cloudB", Number(k.CloudB)), k => k.Map);

    public static readonly TableSchema<ImmutableArray<NightSkyRow>> NightSkies = Rows(
        r => new NightSkyRow(I(r, "map"), Between(r, "stars", 0, 4), Between(r, "starBrightness", 0, 4), Between(r, "milkyWay", 0, 4),
            C(r, "cover"), C(r, "cloudR"), C(r, "cloudG"), C(r, "cloudB"), Between(r, "moonGlow", 0, 4)),
        k => JsonItem.NewObject().Add("map", k.Map).Add("stars", Number(k.Stars)).Add("starBrightness", Number(k.StarBrightness))
            .Add("milkyWay", Number(k.MilkyWay)).Add("cover", Number(k.Cover)).Add("cloudR", Number(k.CloudR)).Add("cloudG", Number(k.CloudG))
            .Add("cloudB", Number(k.CloudB)).Add("moonGlow", Number(k.MoonGlow)), k => k.Map);

    /// <summary>The DIFFICULTY settings' names in the difficulty table, LOW to HIGH.</summary>
    public static readonly ImmutableArray<string> Levels = ["low", "medium", "high"];

    private static string Level(JsonItem r)
    {
        var level = r.Member("level");
        return Levels.Contains(level.AsString()) ? level.AsString() : throw level.Error($"expected one of {string.Join(", ", Levels)}");
    }

    public static readonly TableSchema<ImmutableArray<EnemyType>> EnemyTypes = Rows(ReadEnemy, WriteEnemy, e => e.Index);

    private static EnemyType ReadEnemy(JsonItem r) => new()
    {
        Index = I(r, "index"),
        Record = [.. r.Member("record").ToArray(x => x.AsUInt())],
        Name = S(r, "name"),
        ModelFile = S(r, "modelFile"),
        State = I(r, "state"),
        Params = Ints(r, "params"),
        HitPoints = I(r, "hitPoints"),
        Speed = OptI(r, "speed"),
        Near = OptI(r, "near"),
        Turn = OptI(r, "turn"),
        Axis = OptI(r, "axis"),
        AngleMin = OptI(r, "angleMin"),
        AngleMax = OptI(r, "angleMax"),
        Range = OptI(r, "range"),
        Altitude = OptI(r, "altitude"),
        VerticalAccel = OptI(r, "verticalAccel"),
        AvoidRadius = OptI(r, "avoidRadius"),
        ForwardSpeed = OptI(r, "forwardSpeed"),
        Fire = r.Member("fire") is { IsNull: false } f
            ? new AisFire(I(f, "muzzleX"), I(f, "muzzleY"), I(f, "muzzleZ"), I(f, "ammo"), I(f, "speed"), I(f, "rate"), I(f, "range"), I(f, "aimGate"))
            : null,
        ScriptVa = r.Member("scriptVa") is { IsNull: false } va ? va.AsUInt() : null,
        Events = [.. r.Member("events").ToArray(e => new AisFrameEvent(I(e, "frame"), I(e, "sound")))],
        EngineSound = OptI(r, "engineSound"),
        Death = [.. r.Member("death").ToArray(d => new EnemyDeathPart(I(d, "type"), I(d, "x"), I(d, "y"), I(d, "z")))],
        DeathKind = OptI(r, "deathKind"),
        Segments = [.. r.Member("segments").ToArray(s => new EnemySegment(I(s, "childType"), I(s, "x"), I(s, "y"), I(s, "z"),
            I(s, "pitch"), I(s, "yaw"), I(s, "roll")))],
    };

    private static JsonItem WriteEnemy(EnemyType e) => JsonItem.NewObject()
        .Add("index", e.Index).Add("record", JsonItem.NewArray(e.Record, x => x)).Add("name", e.Name).Add("modelFile", e.ModelFile)
        .Add("state", e.State).Add("params", Ints(e.Params)).Add("hitPoints", e.HitPoints).Add("speed", e.Speed).Add("near", e.Near)
        .Add("turn", e.Turn).Add("axis", e.Axis).Add("angleMin", e.AngleMin).Add("angleMax", e.AngleMax).Add("range", e.Range)
        .Add("altitude", e.Altitude).Add("verticalAccel", e.VerticalAccel).Add("avoidRadius", e.AvoidRadius)
        .Add("forwardSpeed", e.ForwardSpeed)
        .Add("fire", e.Fire is { } f
            ? JsonItem.NewObject().Add("muzzleX", f.MuzzleX).Add("muzzleY", f.MuzzleY).Add("muzzleZ", f.MuzzleZ).Add("ammo", f.Ammo)
                .Add("speed", f.Speed).Add("rate", f.Rate).Add("range", f.Range).Add("aimGate", f.AimGate)
            : JsonItem.Null())
        .Add("scriptVa", e.ScriptVa)
        .Add("events", JsonItem.NewArray(e.Events, v => JsonItem.NewObject().Add("frame", v.Frame).Add("sound", v.Sound)))
        .Add("engineSound", e.EngineSound)
        .Add("death", JsonItem.NewArray(e.Death, d => JsonItem.NewObject().Add("type", d.Type).Add("x", d.X).Add("y", d.Y).Add("z", d.Z)))
        .Add("deathKind", e.DeathKind)
        .Add("segments", JsonItem.NewArray(e.Segments, s => JsonItem.NewObject().Add("childType", s.ChildType).Add("x", s.X)
            .Add("y", s.Y).Add("z", s.Z).Add("pitch", s.Pitch).Add("yaw", s.Yaw).Add("roll", s.Roll)));

    /// <summary>A table whose rows are an array of <typeparamref name="TRow"/>, each named by
    /// <paramref name="key"/> (a mod's rows replace those of their keys).</summary>
    private static TableSchema<ImmutableArray<TRow>> Rows<TRow, TKey>(Func<JsonItem, TRow> read, Func<TRow, JsonItem> write, Func<TRow, TKey> key)
        where TKey : notnull =>
        new(rows => [.. rows.ToArray(read)], rows => JsonItem.NewArray(rows, write), (under, over) => Merge(under, over, key));

    // The rows of under, each replaced by over's row of its key; over's new keys appended in its order.
    private static ImmutableArray<TRow> Merge<TRow, TKey>(ImmutableArray<TRow> under, ImmutableArray<TRow> over, Func<TRow, TKey> key)
        where TKey : notnull
    {
        var rows = under.ToList();
        var at = new Dictionary<TKey, int>();
        for (var i = 0; i < rows.Count; i++)
        {
            at.TryAdd(key(rows[i]), i);
        }

        foreach (var row in over)
        {
            if (at.TryGetValue(key(row), out var i))
            {
                rows[i] = row;
                continue;
            }

            at[key(row)] = rows.Count;
            rows.Add(row);
        }

        return [.. rows];
    }

    private static int I(JsonItem o, string key) => o.Member(key).AsInt();
    private static int? OptI(JsonItem o, string key) => o.Member(key) is { IsNull: false } v ? v.AsInt() : null;
    private static uint U(JsonItem o, string key) => o.Member(key).AsUInt();
    private static double F(JsonItem o, string key) => o.Member(key) is var v && double.IsFinite(v.AsDouble())
        ? v.AsDouble() : throw o.Member(key).Error("expected a number");
    private static double D(JsonItem o, string key) => o.Member(key) is var v && double.IsFinite(v.AsDouble()) && v.AsDouble() >= 0
        ? v.AsDouble() : throw o.Member(key).Error("expected a number 0 or more");

    // A colour channel or a share: 0 to 1.
    private static double C(JsonItem o, string key) => Between(o, key, 0, 1);

    private static double Between(JsonItem o, string key, double min, double max) =>
        o.Member(key) is var v && v.AsDouble() >= min && v.AsDouble() <= max ? v.AsDouble() : throw v.Error($"expected a number {min} to {max}");

    /// <summary>A JSON number of <paramref name="value"/> (shortest text that reads back the same).</summary>
    public static JsonItem Number(double value) =>
        JsonItem.Parse(System.Text.Encoding.UTF8.GetBytes(value.ToString("R", System.Globalization.CultureInfo.InvariantCulture)));
    private static string S(JsonItem o, string key) => o.Member(key).AsString();
    private static ImmutableArray<int> Ints(JsonItem o, string key) => [.. o.Member(key).ToArray(x => x.AsInt())];
    private static JsonItem Ints(ImmutableArray<int> values) => JsonItem.NewArray(values, x => x);
}
