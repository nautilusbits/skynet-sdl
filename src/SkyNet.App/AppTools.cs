using System.Diagnostics;
using System.Drawing;
using System.Globalization;
using System.Numerics;
using RetroEng.Audio;
using RetroEng.Commands;
using RetroEng.Diagnostics;
using RetroEng.Loop;
using RetroEng.Physics;
using RetroEng.Platform;
using RetroEng.Render;
using SkyNet.Data;
using SkyNet.Formats;
using SkyNet.Game;
using SkyNet.Game.Audio;
using SkyNet.Game.Baking;
using SkyNet.Game.Campaign;
using SkyNet.Game.Collision;
using SkyNet.Game.Demos;
using SkyNet.Game.DevTools;
using SkyNet.Game.Enhanced;
using SkyNet.Game.Enemies;
using SkyNet.Game.Hud;
using SkyNet.Game.Menu;
using SkyNet.Game.Player;
using SkyNet.Game.Vehicles;
using SkyNet.Game.Weapons;

/// <summary>What the app runs before (or instead of) a window: the log's sinks, the fatal error, the
/// headless tools (--bake, --gen-hires, --walk-test, --fire-test, --music-wav, --play-script) and the
/// small helpers the app modes share.</summary>
internal static class AppTools
{
    /// <summary>The game's tick rate.</summary>
    internal const int TicksPerSecond = 60;

    // The log's sinks. The tools print to stdout alone (their output is what they are for); the game
    // writes the file, the ring (the console's, and the crash report's last lines), stdout when someone
    // reads it, logcat on Android. The file starts with the program, the OS and the command line.
    internal static string? StartLog(Options? options, bool tool, LogRing ring, string[] args)
    {
        if (tool)
        {
            // A GUI-subsystem exe has no console: a tool run from cmd prints into the caller's.
            TextLogSink.AttachParentConsole();
            Log.AddSink(TextLogSink.Console(plain: true));
            return null;
        }

        Log.AddSink(ring);
        if (LogcatSink.ForAndroid("SkyNET") is { } logcat)
        {
            Log.AddSink(logcat);
        }

        // The game writes its log to the file and the in-game console; stdout only on request.
        if (options is { ConsoleLog: true } && TextLogSink.AttachParentConsole())
        {
            Log.AddSink(TextLogSink.Console(plain: true));
        }

        // logs\ beside the game; where that cannot be written (Program Files), the app-data folder.
        var file = FileLogSink.Open(options?.LogDir ?? Path.Combine(AppContext.BaseDirectory, "logs"), "skynet", keep: 5, out var error);
        var fallback = file == null;
        file ??= FileLogSink.Open(SettingsStore.LogFolder, "skynet", keep: 5, out _);
        if (file != null)
        {
            Log.AddSink(file);
        }

        LogHeader.Write("SkyNET", typeof(Options).Assembly, args, "--data");
        Log.Info("App", fallback
            ? file != null
                ? $"Log: the game's folder cannot be written ({error}), so this log is in the app-data folder ({SettingsStore.Folder}/logs)"
                : $"Log: no log file ({error})"
            : options?.LogDir is { } dir ? $"Log: {dir}" : "Log: logs/skynet.log beside the game");

        return file?.Path;
    }

    // An error that stops the program before the game starts: logged, and shown to a player.
    internal static int Fatal(string message, bool show)
    {
        Log.Error("App", message);
        if (show)
        {
            SystemDialog.Error("SkyNET", message);
        }

        return 1;
    }

    internal static float Wrap(float degrees) => ((degrees % 360f) + 360f) % 360f;

    // console-history.txt beside the settings file (beside the game).
    // The window of a settings file: windowed at its size, borderless over the desktop, or exclusive
    // fullscreen in its mode (none: the desktop's).
    internal static DisplaySetup DisplayOf(MenuSettings s) => new(s.WindowMode switch
    {
        WindowMode.Borderless => Fullscreen.Desktop,
        WindowMode.Fullscreen => Fullscreen.Exclusive,
        _ => Fullscreen.Off,
    }, s.WindowSize, s.FullscreenSize);

    internal static string ConsoleHistoryPath(string settingsPath) =>
        Path.Combine(Path.GetDirectoryName(settingsPath) ?? ".", "console-history.txt");

    // --bake / --rebake: the map blocks of SkyNET's data and of Future Shock's beside it, then exit.
    internal static int GenHires(GameData data, string modsDir, string? banks)
    {
        var only = banks?.Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries)
            .Select(b => int.Parse(b, System.Globalization.CultureInfo.InvariantCulture)).ToHashSet();
        var job = SkyNet.Game.Mods.HiresJob.Start(data, Path.GetFullPath(modsDir), Path.Combine(AppContext.BaseDirectory, "cache"),
            SkyNet.Upscale.UpscalerSource.Default, only);
        job.Wait();
        Console.WriteLine(job.Status);
        return job.State == SkyNet.Game.Menu.MenuJobState.Done ? 0 : 1;
    }

    // --gen-relief: the relief maps of an existing hi-res mod folder, from its own PNGs, then exit.
    internal static int GenRelief(GameData data, string modDir) =>
        SkyNet.Game.Mods.HiresTextures.GenerateRelief(data, Path.GetFullPath(modDir), RetroEng.Platform.PngImage.Decode) > 0 ? 0 : 1;

    // --gen-materials: the shipped materials.json drafted from the data's colours (MaterialGuess), a
    // line per record on the console, then exit.
    internal static int GenMaterials(GameData data, string file)
    {
        var table = SkyNet.Game.Mods.MaterialGuess.Generate(data, Console.WriteLine);
        File.WriteAllBytes(Path.GetFullPath(file), table.ToJson("Made by SkyNet --gen-materials (MaterialGuess), then picked by hand; docs/modding.md, Materials."));
        return 0;
    }

    internal static int BakeAll(GameData data, string modsDir, bool all)
    {
        var sets = new List<GameData> { data };
        if (data.Game == GameKind.SkyNet && GameDataLocator.OtherGameDir(data.Dir) is { } fs && GameData.Open(fs, data.Dir) is { } other)
        {
            sets.Add(other);
        }

        foreach (var d in sets)
        {
            using var store = new BlockStore(modsDir, d);
            var clock = Stopwatch.StartNew();
            var stale = store.Stale(all);
            Log.Info("Bake", $"{Path.GetFileName(store.ZipPath)}: {stale.Count} map blocks to bake (checked in {clock.Elapsed.TotalMilliseconds:0} ms)");
            var last = -1;
            store.Bake(stale, (done, total) =>
            {
                if (done * 10 / Math.Max(total, 1) != last)
                {
                    last = done * 10 / Math.Max(total, 1);
                    Log.Info("Bake", $"PREPARING GAME DATA {done}/{total}");
                }
            });
            var missions = store.StaleMissions(all);
            Log.Info("Bake", $"{Path.GetFileName(store.ZipPath)}: {missions.Count} mission files to generate");
            store.BakeMissions(missions);
            Log.Info("Bake", $"{d.Game}: done in {clock.Elapsed.TotalMilliseconds:0} ms");
        }

        return 0;
    }

    // --play-script without a window: the session alone, every step and map change printed.
    internal static int PlayHeadless(GameData data, string path, DifficultyLevel difficulty)
    {
        var clock = Stopwatch.StartNew();
        var session = new GameSession(data) { Logger = line => Log.Info("Session", line), Difficulty = Difficulty.Of(difficulty) };
        var failures = PlayScript.Load(path).Run(session, line => Log.Info("Script", line));
        Log.Info("Script", $"Script {Path.GetFileName(path)}: {(failures.Count == 0 ? "every check held" : $"{failures.Count} failed")}; " +
            $"{session.MapsEntered} maps entered, mission {session.Mission?.Number.ToString(CultureInfo.InvariantCulture) ?? "-"} on MAP.{session.MapNumber:D3}, " +
            $"{clock.Elapsed.TotalMilliseconds:0} ms");
        foreach (var f in failures)
        {
            Log.Info("Script", $"  {f}");
        }

        return failures.Count == 0 ? 0 : 1;
    }

    // Puts the soldier at the map's start (or at --pos, an eye position, in noclip), facing --yaw/--pitch.
    internal static void SpawnPlayer(PlayerController player, CollisionWorld world, MapFile map, WldTerrain? wld, Options options)
    {
        var (feet, yaw, found) = PlayerStart.Find(map, wld);
        if (!found)
        {
            Log.Info("App", "No player-start marker: starting above the middle of the map.");
        }

        if (options.Position is { } eye)
        {
            feet = eye - new Vector3(0f, PlayerController.EyeHeight, 0f);
        }
        else
        {
            feet = PlayerStart.Clear(world, player.Body, feet);
        }

        player.Spawn(feet, options.Yaw is { } y ? y * MathF.PI / 180f : yaw);
        player.Pitch = (options.Pitch ?? 0f) * MathF.PI / 180f;
        player.Noclip = options.Position != null && options.WalkTest == null;
    }

    // --walk-test: the collision world without a window; the soldier settles, then walks forward (or
    // towards --walk-to) for the ticks asked, his feet printed every --walk-every ticks.
    internal static int WalkTest(GameData data, Options options, int ticks)
    {
        var clock = Stopwatch.StartNew();
        if (LevelCollision.Load(data, options.MapOrDefault) is not var (world, map, wld, stats))
        {
            Log.Error("WalkTest", $"MAP.{options.MapOrDefault:D3} is not in the data or does not parse.");
            return 1;
        }

        Log.Info("WalkTest", $"MAP.{options.MapOrDefault:D3}: {(map.IsOutdoor ? "outdoor" : "indoor")}; collision {stats.Meshes} meshes, " +
            $"{stats.Triangles} triangles{(wld != null ? " + the ground" : "")}, built in {clock.Elapsed.TotalMilliseconds:0} ms");
        if (stats.Missing.Count > 0)
        {
            Log.Info("WalkTest", $"Missing: {string.Join(", ", stats.Missing)}");
        }
        var player = new PlayerController(world);
        SpawnPlayer(player, world, map, wld, options);
        var keys = new PlayerActions(Forward: 0, Back: 1, Left: 2, Right: 3, Run: 4, Jump: 5, Crouch: 6);
        var held = TickInput.Bit(keys.Forward) | (options.WalkRun ? TickInput.Bit(keys.Run) : 0UL);
        var input = new TickInput(held, 0, 0, 0, 0, 0, 0, 0);
        var c = CultureInfo.InvariantCulture;
        var names = map.Entities.Where(e => e.Variant == MapEntityVariant.Mesh && e.NameIndex >= 0 && e.NameIndex < map.Names.Length)
            .ToDictionary(e => e.FileOffset, e => map.Names[e.NameIndex]);
        string Owner(int owner) => owner < 0 ? "ground" : names.TryGetValue(owner, out var n) ? $"{n}@0x{owner:X}" : $"0x{owner:X}";
        string Feet(Vector3 p) => string.Create(c, $"{p.X:0.0},{p.Y:0.0},{p.Z:0.0}");
        Log.Info("WalkTest", string.Create(c, $"start {Feet(player.Position)} yaw {player.Yaw * 180f / MathF.PI:0.#}"));
        for (var i = 0; i < TicksPerSecond / 2; i++)
        {
            player.Step(default, 1f / TicksPerSecond, keys);
        }

        Log.Info("WalkTest", $"settled {Feet(player.Position)} floor {player.OnFloor}");
        var timer = Stopwatch.StartNew();
        var last = player.Position;
        for (var tick = 1; tick <= ticks; tick++)
        {
            if (options.WalkTo is { } target)
            {
                var to = new Vector2(target.X - player.Position.X, target.Y - player.Position.Z);
                if (to.Length() < 20f)
                {
                    Log.Info("WalkTest", string.Create(c, $"t {tick / (float)TicksPerSecond:0.00}s reached the target"));
                    break;
                }

                player.Yaw = MathF.Atan2(-to.X, -to.Y);
            }

            player.Step(input, 1f / TicksPerSecond, keys);
            if (tick % options.WalkEvery == 0 || tick == ticks)
            {
                var moved = new Vector2(player.Position.X - last.X, player.Position.Z - last.Z).Length();
                last = player.Position;
                Log.Info("WalkTest", string.Create(c,
                    $"t {tick / (float)TicksPerSecond:0.00}s feet {Feet(player.Position)} speed {new Vector2(player.Velocity.X, player.Velocity.Z).Length():0} " +
                    $"moved {moved:0.0} floor {(player.OnFloor ? Owner(player.FloorOwner) : "-")} wall {(player.OnWall ? Owner(player.WallOwner) : "-")}"));
            }
        }

        Log.Info("WalkTest", string.Create(c, $"{ticks} ticks in {timer.Elapsed.TotalMilliseconds:0} ms; fall damage {player.FallDamage:0}"));
        return 0;
    }

    // The map's north offset in degrees: the first marker of type 7 carries it (0 without one).
    internal static int NorthOffset(MapFile map) =>
        map.Entities.FirstOrDefault(e => e.Variant == MapEntityVariant.Sprite && e.IsMarker && e.MarkerType == 7) is { } m
            ? ((m.Param16 % 360) + 360) % 360
            : 0;

    internal static string Describe(in ShotReport r)
    {
        var c = CultureInfo.InvariantCulture;
        var what = r.Weapon >= 0 && r.Weapon < WeaponTable.Names.Count ? WeaponTable.Names[r.Weapon] : "-";
        var owner = r.Owner >= 0 ? string.Create(c, $" owner 0x{r.Owner:X}") : r.Target == null ? " ground/geometry" : "";
        return string.Create(c, $"{what}: {r.What} at {r.Point.X:0.0},{r.Point.Y:0.0},{r.Point.Z:0.0}{owner}{(r.Points > 0 ? string.Create(c, $" {r.Points:0.#} points") : "")}");
    }

    // --music-wav: the map's song straight from the synth, the first N seconds, as a WAV file.
    internal static int MusicWav(GameData data, Options options, (string Path, double Seconds) wav)
    {
        var map = data.LoadMap(options.MapOrDefault);
        var name = map != null ? MusicTracks.ForMap(map) : MusicTracks.ForMapType(0);
        if (data.ReadLoose(name) is not { } bytes || HmiFile.Parse(bytes) is not { } song)
        {
            Log.Error("Music", $"{name} is not in the data or does not parse.");
            return 1;
        }

        var clock = Stopwatch.StartNew();
        File.WriteAllBytes(wav.Path, WavWriter.RenderSong(song, wav.Seconds));
        Log.Info("Music", string.Create(CultureInfo.InvariantCulture,
            $"MAP.{options.MapOrDefault:D3}: {name}, {wav.Seconds:0.#} s rendered in {clock.Elapsed.TotalMilliseconds:0} ms to {wav.Path}"));
        return 0;
    }

    // --fire-test: the collision world without a window; the soldier settles, then fires the UZI five
    // times, swings the pipe, throws a pipe bomb and fires a rocket, printing every impact and blast.
    internal static int FireTest(GameData data, Options options)
    {
        if (LevelCollision.Load(data, options.MapOrDefault) is not var (world, map, wld, _))
        {
            Log.Error("FireTest", $"MAP.{options.MapOrDefault:D3} is not in the data or does not parse.");
            return 1;
        }

        var player = new PlayerController(world);
        SpawnPlayer(player, world, map, wld, options);
        var keys = new PlayerActions(Forward: 0, Back: 1, Left: 2, Right: 3, Run: 4, Jump: 5, Crouch: 6);
        for (var i = 0; i < TicksPerSecond / 2; i++)
        {
            player.Step(default, 1f / TicksPerSecond, keys);
        }

        var table = WeaponTable.Load(data);
        var soldier = new SoldierTarget(player);
        var weapons = new WeaponSystem(table, world) { Player = soldier };

        // Without --yaw, aim at the nearest placed mesh in plain sight and stand a test target on it
        // (a prop the behaviour layer will register the same way, by its collision owner).
        if (options.Yaw == null && NearestMesh(world, player.EyePosition) is var (owner, point, name))
        {
            var to = point - player.EyePosition;
            player.Yaw = MathF.Atan2(-to.X, -to.Z);
            player.Pitch = MathF.Asin(Math.Clamp(to.Y / to.Length(), -1f, 1f));
            var prop = new TestTarget($"{(map.Names.Length > 0 ? name(map) : "prop")}@0x{owner:X}", point, 0f);
            weapons.Targets.Register(owner, prop);
            Log.Info("FireTest", string.Create(CultureInfo.InvariantCulture,
                $"Target: {prop.Name}, {to.Length():0} units away at {point.X:0},{point.Y:0},{point.Z:0}"));
        }
        var c = CultureInfo.InvariantCulture;
        var ticks = 0;
        weapons.Reported += r => Log.Info("FireTest", string.Create(c, $"  t {ticks / (float)TicksPerSecond:0.00}s ") + Describe(r));
        Log.Info("FireTest", string.Create(c, $"MAP.{options.MapOrDefault:D3}: table {table.Source}; eye {player.EyePosition.X:0},{player.EyePosition.Y:0},{player.EyePosition.Z:0} " +
            $"yaw {player.Yaw * 180f / MathF.PI:0.#} pitch {player.Pitch * 180f / MathF.PI:0.#}"));
        var dt = 1f / TicksPerSecond;

        void Run(string label, int seconds, Func<int, WeaponInput> input)
        {
            Log.Info("FireTest", label);
            for (var t = 0; t < seconds * TicksPerSecond; t++, ticks++)
            {
                weapons.Tick(input(t), new Shooter(player.EyePosition, player.Yaw, player.Pitch), dt);
            }
        }

        weapons.Inventory.AddAmmo(3, 5);
        weapons.Inventory.Grant(6);
        Run("UZI, trigger held 1 s:", 1, t => new WeaponInput(Fire: true, Throw: false, Run: false, Select: t == 0 ? 1 : -1));
        Log.Info("FireTest", $"  bullets left {weapons.Inventory.Pool(0)}");
        Run("PIPE, one swing:", 1, t => new WeaponInput(Fire: t == 15, Throw: false, Run: false, Select: t == 0 ? 0 : -1));
        Run("PIPE BOMB, one throw:", 4, t => new WeaponInput(Fire: false, Throw: t == 0, Run: false, SelectThrown: t == 0 ? 14 : -1));
        Log.Info("FireTest", $"  pipe bombs left {weapons.Inventory.Pool(5)}");
        Run("ROCKET LAUNCHER, one shot:", 4, t => new WeaponInput(Fire: t == 15, Throw: false, Run: false, Select: t == 0 ? 6 : -1));
        Log.Info("FireTest", string.Create(c, $"soldier took {soldier.Taken:0.#} points"));
        foreach (var t in weapons.Targets.All.OfType<TestTarget>())
        {
            Log.Info("FireTest", string.Create(c, $"{t.Name} took {t.Taken:0.#} points in {t.Hits} hits"));
        }

        return 0;
    }

    // The nearest triangle of a placed mesh the eye sees straight (its centroid, within 1500 units):
    // its owner, the point, and its name in the map.
    internal static (int Owner, Vector3 Point, Func<MapFile, string> Name)? NearestMesh(CollisionWorld world, Vector3 eye)
    {
        (int, Vector3, Func<MapFile, string>)? best = null;
        var bestDistance = 1500f;
        foreach (var grid in world.Sources.OfType<TriangleGrid>())
        {
            foreach (var tri in grid.Triangles)
            {
                var centre = (tri.A + tri.B + tri.C) / 3f;
                var to = centre - eye;
                var d = to.Length();
                if (tri.Owner < 0 || d >= bestDistance || d < 100f || MathF.Abs(to.Y) > d * 0.5f)
                {
                    continue;
                }

                if (world.Raycast(eye, to / d, d + 1f, out var hit) && hit.Owner == tri.Owner)
                {
                    var owner = tri.Owner;
                    bestDistance = d;
                    best = (owner, hit.Point, m => m.Entities.FirstOrDefault(e => e.FileOffset == owner) is { NameIndex: >= 0 } e
                        && e.NameIndex < m.Names.Length ? m.Names[e.NameIndex] : "mesh");
                }
            }
        }

        return best;
    }
}

/// <summary>The soldier as a blast target: the middle of his body; the points are counted (the
/// health and the HUD are not wired here yet).</summary>
internal sealed class SoldierTarget(PlayerController player) : IDamageable
{
    public float Taken { get; private set; }
    /// <summary>DOS's 1000 points less what the blasts took (no death yet).</summary>
    public float Health => MathF.Max(0f, HudRecords.SoldierHealth - Taken);
    public Vector3 Centre => player.Position + new Vector3(0f, PlayerController.BodyHeight / 2f, 0f);
    public float Radius => 0f;
    public bool Alive => true;

    public void TakeDamage(in Damage damage)
    {
        Taken += damage.Points;
        Log.Info("FireTest", string.Create(CultureInfo.InvariantCulture, $"Soldier: {damage.Kind} {damage.Points:0.#} points"));
    }
}

/// <summary>A target for the fire test: counts what it takes.</summary>
internal sealed class TestTarget(string name, Vector3 centre, float radius) : IDamageable
{
    public string Name => name;
    public float Taken { get; private set; }
    public int Hits { get; private set; }
    public Vector3 Centre => centre;
    public float Radius => radius;
    public bool Alive => true;

    public void TakeDamage(in Damage damage)
    {
        Taken += damage.Points;
        Hits++;
    }
}
