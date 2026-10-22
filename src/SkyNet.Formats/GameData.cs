namespace SkyNet.Formats;

public enum GameKind
{
    None,
    /// <summary>SkyNET: MDMDMAP2.BSA, archive key <see cref="BsaVariant.SkyNetFull"/>.</summary>
    SkyNet,
    /// <summary>Terminator: Future Shock: MDMDMAPS.BSA, archive key <see cref="BsaVariant.FutureShockFull"/>.</summary>
    FutureShock,
}

/// <summary>
/// Locating the player's original data folder and the files in it (ported from the Godot port's
/// skynet_paths.gd, without its settings file and command line, which belong to the app).
/// A folder is game data when it holds MDMDMAP2.BSA (SkyNET) or MDMDMAPS.BSA (Future Shock).
/// File names are matched whatever their letter case (data copied to Linux often arrives
/// lower-case); when two spellings exist, the upper-case DOS one wins.
/// </summary>
public static class GameDataLocator
{
    public const string ProbeSkyNet = "MDMDMAP2.BSA";
    public const string ProbeFutureShock = "MDMDMAPS.BSA";

    /// <summary><paramref name="dir"/>/<paramref name="name"/> spelt as on disk; null when absent.</summary>
    public static string? FindFile(string? dir, string name)
    {
        if (string.IsNullOrEmpty(dir) || !Directory.Exists(dir))
            return null;
        string up = name.ToUpperInvariant();
        string? found = null;
        try
        {
            foreach (var path in Directory.EnumerateFiles(dir))
            {
                string f = Path.GetFileName(path);
                if (!string.Equals(f, up, StringComparison.OrdinalIgnoreCase))
                    continue;
                if (f == up)
                    return path;
                found ??= path;
            }
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            // A folder that cannot be listed may still open files by name.
        }
        if (found != null)
            return found;
        string exact = Path.Combine(dir, name);
        return File.Exists(exact) ? exact : null;
    }

    /// <summary>Which game a data folder holds; SkyNET wins when both probes exist.</summary>
    public static GameKind GameOf(string? dir)
    {
        if (FindFile(dir, ProbeSkyNet) != null) return GameKind.SkyNet;
        if (FindFile(dir, ProbeFutureShock) != null) return GameKind.FutureShock;
        return GameKind.None;
    }

    public static bool HasData(string? dir) => GameOf(dir) != GameKind.None;

    /// <summary>The archive key of a folder's data: Future Shock only when it has MDMDMAPS.BSA and no MDMDMAP2.BSA.</summary>
    public static BsaVariant VariantFor(string? dir) =>
        GameOf(dir) == GameKind.FutureShock ? BsaVariant.FutureShockFull : BsaVariant.SkyNetFull;

    public static string MapArchiveFor(GameKind game) => game == GameKind.FutureShock ? ProbeFutureShock : ProbeSkyNet;

    /// <summary>
    /// The data folder, in priority order: an explicit folder (a command-line switch), the
    /// remembered one, then for each base folder (the exe's or the project's)
    /// <c>base/gamedata</c>, <c>parent/gamedata</c>, <c>base/GAMEDATA</c>, <c>parent/GAMEDATA</c>.
    /// Null when none holds data.
    /// </summary>
    public static string? Locate(string? explicitDir, string? rememberedDir, IEnumerable<string> baseDirs)
    {
        foreach (var d in new[] { TrimSep(explicitDir), rememberedDir })
            if (HasData(d)) return d;
        foreach (var b0 in baseDirs)
        {
            string b = TrimSep(b0)!;
            string? parent = Path.GetDirectoryName(b);
            var cands = new List<string> { Path.Combine(b, "gamedata") };
            if (parent != null) cands.Add(Path.Combine(parent, "gamedata"));
            cands.Add(Path.Combine(b, "GAMEDATA"));
            if (parent != null) cands.Add(Path.Combine(parent, "GAMEDATA"));
            foreach (var c in cands)
                if (HasData(c)) return c;
        }
        return null;
    }

    /// <summary>
    /// A folder the player picked may be the data folder or the install root: tries it,
    /// gamedata, GAMEDATA, shock/GAMEDATA, shock/gamedata, SHOCK/GAMEDATA. Null when none holds data.
    /// </summary>
    public static string? ResolveDataDir(string picked)
    {
        string? d = TrimSep(picked.Trim());
        if (string.IsNullOrEmpty(d))
            return null;
        foreach (var sub in new[] { "", "gamedata", "GAMEDATA", "shock/GAMEDATA", "shock/gamedata", "SHOCK/GAMEDATA" })
        {
            string c = sub.Length == 0 ? d : Path.Combine(d, sub);
            if (HasData(c)) return c;
        }
        return null;
    }

    /// <summary>
    /// The other game's data folder where the installer puts it: from SkyNET's
    /// <c>root/gamedata</c>, Future Shock at <c>root/shock/GAMEDATA</c> (and variants, also under
    /// the data folder itself); from Future Shock's, SkyNET two levels up. A remembered folder
    /// for that game wins. Null when there is none.
    /// </summary>
    public static string? OtherGameDir(string gamedataDir, string? rememberedOther = null)
    {
        GameKind game = GameOf(gamedataDir);
        GameKind want = game == GameKind.SkyNet ? GameKind.FutureShock : GameKind.SkyNet;
        if (rememberedOther != null && GameOf(rememberedOther) == want)
            return rememberedOther;
        string self = TrimSep(gamedataDir)!;
        string root = Path.GetDirectoryName(self) ?? self;
        var cands = new List<string>();
        if (game == GameKind.SkyNet)
        {
            foreach (var sub in new[] { "shock/GAMEDATA", "shock/gamedata", "SHOCK/GAMEDATA", "futureshock/GAMEDATA" })
            {
                cands.Add(Path.Combine(root, sub));
                cands.Add(Path.Combine(self, sub));
            }
        }
        else
        {
            string up = Path.GetDirectoryName(root) ?? root;
            foreach (var sub in new[] { "gamedata", "GAMEDATA", "skynet/gamedata", "skynet/GAMEDATA" })
            {
                cands.Add(Path.Combine(up, sub));
                cands.Add(Path.Combine(root, sub));
            }
        }
        string selfFull = Path.GetFullPath(self).TrimEnd('/', '\\');
        foreach (var c in cands)
        {
            // Godot only rules out the identical string; the full-path compare also catches
            // ".." spellings of the same folder.
            if (HasData(c) && !string.Equals(Path.GetFullPath(c).TrimEnd('/', '\\'), selfFull, StringComparison.OrdinalIgnoreCase))
                return c;
        }
        return null;
    }

    static string? TrimSep(string? d) => d?.TrimEnd('/', '\\');
}

/// <summary>
/// An opened data folder: which game it is, its archive key and map archive, and the lookups the
/// game makes (palettes, front-end archives, the hi-res set).
/// </summary>
public sealed class GameData
{
    public string Dir { get; }
    public GameKind Game { get; }
    public BsaVariant Variant { get; }
    public string MapArchive { get; }
    /// <summary>
    /// SkyNET's own data folder: this one in SkyNET; in Future Shock the folder given, or the
    /// installer's layout (<see cref="GameDataLocator.OtherGameDir"/>). Null when unknown.
    /// DOS SKYNET.EXE plays Future Shock from inside SkyNET: menus, briefing art, fonts and the
    /// hi-res set come from SkyNET's folder.
    /// </summary>
    public string? SkyNetDir { get; }

    GameData(string dir, GameKind game, string? skynetDir)
    {
        Dir = dir;
        Game = game;
        Variant = game == GameKind.FutureShock ? BsaVariant.FutureShockFull : BsaVariant.SkyNetFull;
        MapArchive = GameDataLocator.MapArchiveFor(game);
        SkyNetDir = game == GameKind.SkyNet ? dir : skynetDir;
    }

    /// <summary>Open a data folder; null when it holds neither game.</summary>
    /// <param name="skynetDir">For Future Shock: SkyNET's folder, when the app knows it (its <c>--skynet-data=</c>).</param>
    public static GameData? Open(string dir, string? skynetDir = null)
    {
        dir = dir.TrimEnd('/', '\\');
        var game = GameDataLocator.GameOf(dir);
        if (game == GameKind.None)
            return null;
        if (game == GameKind.FutureShock)
        {
            if (GameDataLocator.GameOf(skynetDir) != GameKind.SkyNet)
                skynetDir = GameDataLocator.OtherGameDir(dir);
        }
        return new GameData(dir, game, skynetDir);
    }

    /// <summary>A file in the data folder spelt as on disk, or the plain join when it does not exist.</summary>
    public string PathOf(string name) => GameDataLocator.FindFile(Dir, name) ?? Path.Combine(Dir, name);

    public bool Exists(string name) => GameDataLocator.FindFile(Dir, name) != null;

    public BsaArchive OpenArchive(string name) => BsaArchive.Open(PathOf(name), Variant);

    public BsaArchive OpenMapArchive() => OpenArchive(MapArchive);

    /// <summary>
    /// [path, key] of a front-end archive (MDMDIMGS.BSA, MDMDBRIF.BSA): SkyNET's own copy when
    /// this is Future Shock and SkyNET's folder is known and has it, else this game's.
    /// </summary>
    public (string Path, BsaVariant Variant) FrontArchive(string name)
    {
        if (Game == GameKind.FutureShock)
        {
            string? p = GameDataLocator.FindFile(SkyNetDir, name);
            if (p != null)
                return (p, BsaVariant.SkyNetFull);
        }
        return (PathOf(name), Variant);
    }

    /// <summary>
    /// The 640 × 480 art, MDMDHRES.BSA. SKYNET.EXE tries its search directories in order
    /// (0x5f400, 0x5f480, then 0x5f500), SkyNET's install first, so Future Shock (which ships
    /// none) reads SkyNET's copy too.
    /// </summary>
    public (string Path, BsaVariant Variant) HiresArchive() => FrontArchive("MDMDHRES.BSA");

    /// <summary>A loose front-end file (FONT00NN.FNT): SkyNET's copy in Future Shock when known.</summary>
    public string FrontPath(string name)
    {
        if (Game == GameKind.FutureShock)
        {
            string? p = GameDataLocator.FindFile(SkyNetDir, name);
            if (p != null)
                return p;
        }
        return PathOf(name);
    }

    /// <summary>Raw bytes of a loose file in the data folder; null when absent.</summary>
    public byte[]? ReadLoose(string name)
    {
        string? p = GameDataLocator.FindFile(Dir, name);
        return p != null ? File.ReadAllBytes(p) : null;
    }

    byte[]? ReadFromImages(string name)
    {
        string? p = GameDataLocator.FindFile(Dir, "MDMDIMGS.BSA");
        if (p == null)
            return null;
        using var a = BsaArchive.Open(p, Variant);
        return a.Read(name);
    }

    /// <summary>
    /// The game palette bytes: SKYNET.COL then BRIEF.COL from MDMDIMGS.BSA, then loose
    /// SHOCK.COL, BRIEF.COL, SKYNET.COL. Null when none exists. SKYNET.EXE always loads
    /// "skynet.col" (0x1175fe, no Future Shock branch) from the image archive its search finds
    /// first, SkyNET's own, so Future Shock takes SkyNET's archive when its folder is known
    /// (SHOCK.COL differs from it only at 0x11-0x1B; 0x11 is the night sky's fill).
    /// </summary>
    public byte[]? PaletteBytes()
    {
        var (p, variant) = FrontArchive("MDMDIMGS.BSA");
        if (File.Exists(p))
        {
            using var a = BsaArchive.Open(p, variant);
            foreach (var n in new[] { "SKYNET.COL", "BRIEF.COL" })
            {
                var b = a.Read(n);
                if (b is { Length: > 0 }) return b;
            }
        }
        foreach (var n in new[] { "SHOCK.COL", "BRIEF.COL", "SKYNET.COL" })
        {
            var b = ReadLoose(n);
            if (b is { Length: > 0 }) return b;
        }
        return null;
    }

    /// <summary>Any .COL by name: the image archive first, then loose. Null when absent.</summary>
    public byte[]? ColBytes(string name)
    {
        var b = ReadFromImages(name);
        if (b is { Length: > 0 }) return b;
        return ReadLoose(name);
    }

    /// <summary>The briefing / menu palette: BRIEF.COL in the archive, loose, else the game palette.</summary>
    public byte[]? UiPaletteBytes()
    {
        var b = ReadFromImages("BRIEF.COL");
        if (b is { Length: > 0 }) return b;
        b = ReadLoose("BRIEF.COL");
        if (b is { Length: > 0 }) return b;
        return PaletteBytes();
    }

    public Palette? LoadPalette() => PaletteBytes() is { } b ? Palette.Parse(b) : null;

    /// <summary>HAZE.000, its steps on the colours it was made for: Future Shock's table indexes
    /// SHOCK.COL, the game draws with <see cref="LoadPalette"/> (<see cref="HazeTable.Repoint"/>).</summary>
    public HazeTable? LoadHaze()
    {
        if (ReadLoose("HAZE.000") is not { } b || HazeTable.Parse(b) is not { } haze)
        {
            return null;
        }

        if (Game != GameKind.FutureShock || ReadLoose("SHOCK.COL") is not { } own || Palette.Parse(own) is not { } made || LoadPalette() is not { } used)
        {
            return haze;
        }

        return haze.Repoint(made, used);
    }

    /// <summary>TEXTURE.NNN, loose in the data folder.</summary>
    public TextureBank? LoadTextureBank(int bank) =>
        ReadLoose($"TEXTURE.{bank:D3}") is { } b ? TextureBank.Parse(b) : null;

    /// <summary>WLD.NNN: a mod's (<see cref="WldResolver"/>), else loose in the data folder (DOS opens
    /// <c>gamedata\wld.NNN</c>). Null when absent.</summary>
    public WldTerrain? LoadWld(int map) =>
        WldBytes(map) is { } b ? WldTerrain.Parse(b) : null;

    /// <summary>WLD.NNN's bytes: a mod's (<see cref="WldResolver"/>), else the data folder's; null when absent.</summary>
    public byte[]? WldBytes(int map) => WldResolver?.Invoke(map) ?? ReadLoose($"WLD.{map:D3}");

    /// <summary>
    /// The mods' terrains: given a map number, a mod's WLD for it, or null for the data folder's. Set by
    /// the mods (SkyNet.Game.Mods.MapTerrains); null: every map stands on its DOS WLD. Replaced whole,
    /// read from any thread.
    /// </summary>
    public static Func<int, byte[]?>? WldResolver
    {
        get => Volatile.Read(ref _wldResolver);
        set => Volatile.Write(ref _wldResolver, value);
    }

    private static Func<int, byte[]?>? _wldResolver;

    /// <summary>
    /// MAP.NNN from the map archive (MDMDMAP2.BSA or MDMDMAPS.BSA), else a map a mod adds
    /// (<see cref="MapResolver"/>), with its layer's edits (<see cref="ApplyLayer"/>). Null when absent or unparsable.
    /// </summary>
    public MapFile? LoadMap(int map)
    {
        using var a = OpenMapArchive();
        return MapBytes(a, map) is { } b && MapFile.Parse(b) is { } m ? ApplyLayer(map, m) : null;
    }

    /// <summary>MAP.NNN's bytes from <paramref name="archive"/> (the map archive), else a mod's added map; null when absent.</summary>
    public static byte[]? MapBytes(BsaArchive archive, int map) => archive.Read($"MAP.{map:D3}") ?? MapResolver?.Invoke(map);

    /// <summary>
    /// The maps the mods add: given a map number the map archive lacks, a mod's MAP.NNN, or null. Set
    /// by the mods (SkyNet.Game.Mods.ModMaps); a mod never replaces a DOS map (its layer edits it).
    /// Replaced whole, read from any thread.
    /// </summary>
    public static Func<int, byte[]?>? MapResolver
    {
        get => Volatile.Read(ref _mapResolver);
        set => Volatile.Write(ref _mapResolver, value);
    }

    private static Func<int, byte[]?>? _mapResolver;

    /// <summary>
    /// The map layers: given a map number, the layer it plays as ("MAP.210", or a named copy) and the map as parsed,
    /// the map the game plays (the base when no mod has that layer). Set by the mods
    /// (SkyNet.Game.Mods.MapLayers); null: every map is its DOS file. One immutable delegate over
    /// what it was made from, replaced whole: bake workers read it from any thread.
    /// </summary>
    public static Func<int, string, MapFile, MapFile>? LayerResolver
    {
        get => Volatile.Read(ref _layerResolver);
        set => Volatile.Write(ref _layerResolver, value);
    }

    private static Func<int, string, MapFile, MapFile>? _layerResolver;

    private readonly System.Collections.Concurrent.ConcurrentDictionary<int, string> _layers = new();

    /// <summary>The layer MAP.<paramref name="map"/> plays as: the one <see cref="UseLayer"/> set, else "MAP.NNN".</summary>
    public string LayerName(int map) => _layers.TryGetValue(map, out var l) ? l : $"MAP.{map:D3}";

    /// <summary>Plays MAP.<paramref name="map"/> as <paramref name="layer"/> (a mission instance's
    /// layer) from now on, for every reader of <see cref="LoadMap"/>; null: its own "MAP.NNN".</summary>
    public void UseLayer(int map, string? layer)
    {
        if (layer == null || layer == $"MAP.{map:D3}")
            _layers.TryRemove(map, out _);
        else
            _layers[map] = layer;
    }

    /// <summary><paramref name="map"/> (MAP.<paramref name="number"/> as parsed) with its layer's edits.</summary>
    public MapFile ApplyLayer(int number, MapFile map) => LayerResolver is { } r ? r(number, LayerName(number), map) : map;

    /// <summary>
    /// A model's bytes by file name ("NAME.3D"): MDMDOBJS.BSA (props) first, then MDMDENMS.BSA
    /// (enemies and actors), as the Godot port's level loader looks them up. Null when absent.
    /// </summary>
    public byte[]? ModelBytes(string fileName)
    {
        foreach (var arc in new[] { "MDMDOBJS.BSA", "MDMDENMS.BSA" })
        {
            if (!Exists(arc))
                continue;
            using var a = OpenArchive(arc);
            if (a.Read(fileName) is { Length: > 0 } b)
                return b;
        }
        return null;
    }

    /// <summary>A model by file name ("NAME.3D"); see <see cref="ModelBytes"/>. Null when absent or unparsable.</summary>
    public Model3D? LoadModel(string fileName) => ModelBytes(fileName) is { } b ? Model3D.Parse(b) : null;

    /// <summary>
    /// The heightmap an outdoor map stands on: its own WLD.NNN, or when the data has none (Future
    /// Shock's MAP.040 and MAP.080) the best fit among every WLD in the folder (<see cref="WldFit"/>).
    /// Suffix is the WLD actually used; null when neither exists.
    /// </summary>
    public (WldTerrain Wld, string Suffix)? LoadWldFor(int map)
    {
        if (LoadWld(map) is { } own)
            return (own, $"{map:D3}");
        if (LoadMap(map) is not { } m || WldFit.BestFit(m, WldCandidates()) is not { } fit)
            return null;
        return LoadWld(int.Parse(fit.Suffix)) is { } w ? (w, fit.Suffix) : null;
    }

    /// <summary>Every WLD.NNN in the data folder, by name, parsed.</summary>
    public IEnumerable<(string Suffix, WldTerrain Wld)> WldCandidates()
    {
        var names = Directory.EnumerateFiles(Dir)
            .Select(Path.GetFileName)
            .Where(f => f!.Length == 7 && f.StartsWith("WLD.", StringComparison.OrdinalIgnoreCase) && int.TryParse(f[4..], out _))
            .Select(f => f![4..])
            .Distinct()
            .Order(StringComparer.Ordinal);
        foreach (var suffix in names)
            if (LoadWld(int.Parse(suffix)) is { } w)
                yield return (suffix, w);
    }
}
