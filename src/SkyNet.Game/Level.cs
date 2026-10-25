using System.Diagnostics;
using System.Numerics;
using RetroEng.Render;
using SkyNet.Data;
using SkyNet.Formats;
using SkyNet.Game.Baking;
using SkyNet.Game.Behaviour;
using SkyNet.Game.Enhanced;
using SkyNet.Game.Menu;
using SkyNet.Game.Mods;
using SkyNet.Game.Water;

namespace SkyNet.Game;

/// <summary>RENDER DETAIL (SKYNET.EXE v1.01 table 0x61a00): the far clip and where the depth cue starts.</summary>
public enum Detail { Low, Medium, High }

/// <summary>
/// One map on the renderer, in the original palette look: the WLD ground (outdoor maps), the
/// variant-1 meshes (shared models drawn instanced), the variant-3 sprites as billboards turned
/// to the camera, the night sky (fill and stars) and the HAZE.000 depth cue.
/// World axes are the Godot port's: DOS (x, y, z) → (x, −y, −z), Y up, −Z north.
/// </summary>
public sealed partial class Level
{
    /// <summary>World units per sprite texel: scenery, and the item banks (200, 201, 214); DOS draws
    /// both 1:1 (map walker scale 0x100, v1.01 0x136f3f).</summary>
    public const float SpriteScale = 1f;
    public const float ItemScale = 1f;
    /// <summary>Indoors a sprite's record Y sits this far above the floor.</summary>
    public const float IndoorSpriteLift = 16f;
    /// <summary>The sky's fill: SKYNET.EXE sets 0x11 at every map load (FUN_0013404b /
    /// FUN_00134067 → 0x52f00, copied to the frame clear 0x30a43), a palette index fixed in code.
    /// In SKYNET.COL, the palette SKYNET.EXE loads for both games, it is black.</summary>
    public const int NightSkyIndex = 0x11;
    /// <summary>The palette index the frame is cleared to on map <paramref name="map"/>: 0x11 on
    /// every map, the dusk ones and indoors too (FUN_00121bdf calls FUN_00134067 at each map
    /// load, with no test of the map).</summary>
    public static int ClearIndex(int map) => NightSkyIndex;

    /// <summary>The frame's clear colour: palette index <see cref="NightSkyIndex"/> (black in SKYNET.COL).</summary>
    public static Vector4 ClearColour(Palette palette)
    {
        var (r, g, b) = palette[NightSkyIndex];
        return new Vector4(r / 255f, g / 255f, b / 255f, 1f);
    }

    /// <summary>Maps that draw the dusk dome SKY_SKY.3D instead (list 0x52d51).</summary>
    public static readonly int[] DuskMaps = [250, 260, 270, 280];
    /// <summary>The night moon (FUN_0013a132): TEXTURE.359 record 2, blitted 1:1 at the projection
    /// of the camera-rotated direction (0x64000, −0x3e800, 0x32000) (DOS x, y, z) — 29.2° up.
    /// (Its first vector, (−6400, 4000, −3200), is the moon light's direction, FUN_0011b933.)</summary>
    public const int MoonBank = 359, MoonRecord = 2;
    public static readonly Vector3 MoonDosDirection = new(0x64000, -0x3e800, 0x32000);
    /// <summary>DOS's 320 × 200 focal length in pixels (FUN_0014fb74 ← 0x14fa22: x 200, y 180),
    /// so one moon texel spans 1/200 radian across.</summary>
    public const float DosFocal = 200f;
    /// <summary>A 320 × 200 pixel is shown 1.2 times as tall as wide on a 4:3 screen: the moon's
    /// 57 × 47 texels look round there.</summary>
    public const float DosPixelAspect = 1.2f;
    private const float MoonDistance = 1000f;
    /// <summary>The Godot port's interior ambient (main.gd INDOOR_AMBIENT) and lamp rule
    /// (LIGHT_RANGE_PER_UNIT, LIGHT_ENERGY_DIV and their clamps).</summary>
    public static readonly Vector3 IndoorAmbient = new(0.62f, 0.62f, 0.68f);
    private const int StarCount = 256;
    private const int StarSeed = 1;
    private const float StarDistance = 1000f;
    /// <summary>The picture FUN_0015859c shimmers, in its own lines (320 × 200).</summary>
    public const int WaterShimmerLines = 200;
    /// <summary>The shimmer's steps per second: one per DOS frame (UNCERTAIN: the frame rate of the
    /// machines of the day; 24 reads like the original's drift).</summary>
    public const int WaterShimmerSteps = 24;

    /// <summary>The far clip and the depth-cue start per <see cref="Detail"/>, world units.</summary>
    public static readonly float[] DetailFar = [1176f, 1944f, 3992f];
    public static readonly float[] DetailCueStart = [720f, 1744f, 1744f];


    public int Number { get; }
    public MapFile Map { get; }
    public WldTerrain? Wld { get; }
    public bool Outdoor => Map.IsOutdoor;
    public Terrain? Ground { get; }
    /// <summary>The ground left undrawn (an editor draws its edited field instead).</summary>
    public bool HideGround { get; set; }

    /// <summary>The session's moon: where it is drawn as it falls (null: at rest).</summary>
    public NightMoon? Moon { get; set; }

    /// <summary>The level's textures (an editor sets their materials again: <see cref="TextureStore.ApplySurfaces"/>).</summary>
    public TextureStore Textures { get; }

    public Vector3 Start { get; }
    /// <summary>Yaw (radians, about +Y, 0 looking −Z) from the start towards the facing marker.</summary>
    public float StartYaw { get; }
    public bool HasStart { get; }
    public TextureHandle Palette { get; }
    /// <summary>The palette with its fire colours cut out (<see cref="FireFlames.MaskPalette"/>): the
    /// enhanced look's burning sprites that keep their body under their flame.</summary>
    public TextureHandle FireMaskPalette { get; }
    public Vector4 SkyColour { get; }
    public TimeSpan LoadTime { get; }
    public int ModelCount { get; private set; }
    public int MeshInstances { get; }
    public int SpriteCount { get; }
    /// <summary>Robots placed in their rest pose (marker type 2), segments not counted.</summary>
    public int EnemyCount { get; private set; }
    /// <summary>Of <see cref="SpriteCount"/>, the sprites the item table lists (pickups).</summary>
    public int PickupCount { get; private set; }
    /// <summary>Indoors: the shade level (HAZE.000 row) of the ambient, and the lights that add to it.</summary>
    public int AmbientLevel { get; private set; } = HazeTable.Steps - 1;
    public int LightCount => _shading?.LightCount ?? 0;
    /// <summary>The baked block the level was built from.</summary>
    public MapBlock Block { get; }
    /// <summary>The time an in-memory bake took (zero when the block was given).</summary>
    public TimeSpan BakeTime { get; }
    public bool HasDome => _sky != null;
    public IReadOnlyList<string> Missing => _missing;

    /// <summary>The animation clock (ms) when set; else the level's own free-running one, as DOS
    /// runs every animated record off one millisecond clock.</summary>
    public long? ClockMs { get; set; }

    private readonly Renderer _renderer;
    private readonly Stopwatch _clock = Stopwatch.StartNew();
    private ModelMesh? _world;
    /// <summary>The meshes that move, change stage or vanish (<see cref="BehaviourModel.IsDynamic"/>):
    /// one mesh per stage each, built where the map placed them and drawn through <see cref="Poses"/>.</summary>
    private readonly List<(int Offset, ModelMesh?[] Stages)> _dynamic = [];
    private ModelMesh? _sky;
    /// <summary>The enhanced look's dusk sky, drawn in place of <see cref="_sky"/> (<see cref="SunsetSky"/>).</summary>
    private MeshHandle _duskSky;
    private Material _duskSkyMaterial;
    private int _duskSkyIndices;
    private readonly List<SpriteGroup> _sprites = [];
    private IndoorShading? _shading;
    private MeshHandle _moon;
    private TextureHandle _waterTable;
    private Material _moonMaterial;
    private Vector2 _moonTexels;
    private Vector3 _moonDrawn;

    /// <param name="Original">The original look's material (<see cref="Material"/> is the look's in use).</param>
    /// <param name="SelfLit">A fire (<see cref="EmissiveLamps.IsFire"/>: DOS draws most at full light, and each
    /// carries its own fire lamp): unlit in the enhanced look, not lit again by the point lights.</param>
    /// <param name="Index">The sprite (bank &lt;&lt; 7 | record): how the enhanced look draws it under its flames (<see cref="FireFlames.SpriteLook"/>).</param>
    private sealed record SpriteGroup(MeshHandle Quad, Material Material, GpuTexture Texture, int Phase,
        (Vector3 Centre, Vector2 Size)[] Sprites, int[] Offsets, Instance[] Scratch, Material Original, bool SelfLit, int Index);
    /// <summary>The map's fire sprites as the enhanced look's flames (<see cref="FireFlames"/>), by MAP file offset.</summary>
    private readonly List<(int Offset, FireFlame Flame)> _fireSprites = [];
    private readonly List<FireFlame> _flamesNow = [];
    private readonly FireFlames _fireFlames = new();
    private GroundSpan? _groundReach;

    /// <summary>The terrain's middle at its mean height, and its reach (null: no terrain): the horizon's smoke rises past it.</summary>
    private GroundSpan? GroundReach => _groundReach ??= Ground is { ChunkCount: > 0 } g ? SpanOf(g.Chunks) : null;

    /// <summary>The chunks' box's middle (at their mean height) and half its diagonal across.</summary>
    private static GroundSpan SpanOf(IReadOnlyList<Terrain.Chunk> chunks)
    {
        var min = chunks.Aggregate(chunks[0].Min, (m, c) => Vector3.Min(m, c.Min));
        var max = chunks.Aggregate(chunks[0].Max, (m, c) => Vector3.Max(m, c.Max));
        var middle = (min + max) / 2f;
        return new GroundSpan(middle with { Y = chunks.Average(c => c.Centre.Y) }, Vector2.Distance(new(min.X, min.Z), new(max.X, max.Z)) / 2f);
    }
    private readonly List<string> _missing = [];
    private readonly TextureHandle _hazeTable;
    private MeshHandle _stars;
    private ushort[] _starIndexList = [];
    private readonly Vector3[] _starDirections = new Vector3[StarCount];
    private readonly uint[] _starTints = new uint[StarCount];
    private Material _starMaterial;
    private float _starPixel;

    public Detail Detail { get; set; } = Detail.High;
    /// <summary>Where the dynamic meshes are now (the level behaviour); none: where the map put them.</summary>
    public IEntityPoses? Poses { get; set; }
    /// <summary>The sprites not drawn, by MAP file offset (pickups taken).</summary>
    public IReadOnlySet<int>? HiddenSprites { get; set; }

    /// <summary>The most point lights the enhanced look gathers for a frame: ENHANCED OPTIONS'
    /// LIGHTS (<see cref="MenuSettings.LightBudget"/>, 64 or 256), set with the look (the app's
    /// ApplyLook); the light gathering takes the nearest / brightest up to it.</summary>
    public int LightBudget { get; set; } = MenuSettings.HighLightBudget;
    /// <summary>ENHANCED OPTIONS' PARTICLES (the app's ApplyLook): the enhanced look's dust, ash and
    /// smoke (<see cref="Atmosphere"/>); off until set.</summary>
    public ParticleLevel Particles { get; set; } = ParticleLevel.Off;
    /// <summary>The enhanced look draws its fires as flames (<see cref="FireFlames"/>); off (the editor's
    /// View > Effects) they stay the DOS sprites. Set before <see cref="Apply"/>.</summary>
    public bool Flames { get; set; } = true;
    /// <summary>What destruction left burning on the map (the session's drops): smoke rises from
    /// those that are fires.</summary>
    public IReadOnlyList<Campaign.DroppedSprite>? Drops { get; set; }
    /// <summary>A mod's wind (<see cref="Enhanced.Wind"/>): the ash and smoke outdoors blow with it; null: their own breeze.
    /// Set on the thread that draws.</summary>
    public WindState? Wind { get; set; }
    private readonly Atmosphere _atmosphere = new();
    /// <summary>The frame <see cref="Draw(Vector3, float)"/> captures into (a draw on the simulation's thread).</summary>
    private readonly LevelFrame _frame = new();
    /// <summary>The TRANSFRM.PRS stages the level's dynamic meshes were built with.</summary>
    public TransfrmPrs Transfrm { get; private set; } = TransfrmPrs.Parse([]);
    /// <summary>The map's water (marker 103/104) as loaded; null on a dry map.</summary>
    public WaterLevel? Water { get; }
    /// <summary>The water's surface now (world Y; the session's, moved by acts 0xD6-0xDA); null: none drawn.</summary>
    public float? WaterSurface { get; set; }
    /// <summary>False skips the water's pass ([0x38904] = 2: the torpedo ride).</summary>
    public bool WaterPass { get; set; } = true;
    /// <summary>The water's blend colours (the surface pass and the underwater palette).</summary>
    public WaterLook WaterLook { get; }
    /// <summary>The far clip in use: <see cref="FarOverride"/>, else RENDER DETAIL's.</summary>
    public float Far => FarOverride ?? DetailFar[(int)Detail];
    /// <summary>A far clip for this run (the console's <c>far</c>), whatever RENDER DETAIL says; null: the detail's.
    /// The depth cue keeps the detail's start (below the far clip), so a longer far clip stretches the haze.</summary>
    public float? FarOverride { get; set; }
    /// <summary>The way the dusk sun's light goes in the enhanced look, from the dome's brightest
    /// texels (<see cref="LookLighting.DuskSunDirection"/>); null without a dome.</summary>
    public Vector3? DuskSunDirection { get; private set; }
    /// <summary>The palette's haze colour (sRGB): the enhanced look's depth cue goes towards it.</summary>
    public Vector4 HazeColour { get; private set; }
    /// <summary>The enhanced look's sky on a dusk map (<see cref="SunsetSky"/>); null on the others.</summary>
    internal SunsetLook? DuskLook { get; private set; }
    public float CueStart => MathF.Min(DetailCueStart[(int)Detail], Far);

    private Level(Renderer renderer, GameData data, int number, MapFile map, MapBlock block, bool staticRobots, TimeSpan bake, KeptSurfaces? kept)
    {
        var clock = Stopwatch.StartNew();
        _renderer = renderer;
        _kept = kept;
        Number = number;
        Map = map;
        Block = block;
        BakeTime = bake;

        var palette = data.LoadPalette() ?? throw new InvalidDataException("No game palette (SKYNET.COL) in the data folder.");
        var rgba = new byte[256 * 4];
        for (var i = 0; i < 256; i++)
        {
            var (r, g, b) = palette[i];
            (rgba[i * 4], rgba[i * 4 + 1], rgba[i * 4 + 2], rgba[i * 4 + 3]) = (r, g, b, 255);
        }

        Palette = renderer.CreatePalette(rgba);
        FireMaskPalette = renderer.CreatePalette(FireFlames.MaskPalette(rgba));
        SkyColour = ClearColour(palette);
        HazeColour = LookLighting.HazeColour(palette);
        var haze = data.LoadHaze();
        if (haze != null)
        {
            var table = new byte[HazeTable.Size];
            for (var s = 0; s < HazeTable.Steps; s++)
            {
                haze.Row(s).CopyTo(table.AsSpan(s * 256));
            }

            // Row 63 is the nearest step: the base level of everything drawn.
            _hazeTable = renderer.CreateShadeTable(table, HazeTable.Steps - 1);
        }

        var textures = new TextureStore(renderer, data);
        Textures = textures;
        PrefetchHd(textures, block);
        if (Outdoor)
        {
            Wld = data.LoadWld(number);
            if (Wld != null && block.Terrain != null)
            {
                Ground = new Terrain(renderer, textures, Palette, block.Terrain);
            }
        }

        if (!Outdoor && haze != null)
        {
            _shading = new IndoorShading(map, haze, palette);
            AmbientLevel = _shading.AmbientLevel;
        }

        Water = WaterLevel.Find(map);
        if (Water != null)
        {
            WaterLook = WaterLook.Load(data, palette, Water);
            WaterSurface = Water.Surface;
            BuildWater(renderer);
        }

        _missing.AddRange(block.Missing);
        if (staticRobots)
        {
            _missing.AddRange(block.MissingRobots);
        }

        MeshInstances = block.MeshInstances;
        EnemyCount = block.EnemyCount;
        ModelCount = staticRobots ? block.ModelCountWithRobots : block.ModelCount;
        Transfrm = BehaviourModel.LoadTransfrm(data);
        BuildModels(renderer, textures, block, staticRobots);
        BuildReplacements(renderer, data, textures, block, staticRobots);
        BuildSprites(renderer, textures, block);
        SpriteCount = block.SpriteCount;
        PickupCount = block.PickupCount;
        (Start, StartYaw, HasStart) = FindStart();
        if (Outdoor && IsDusk(number))
        {
            BuildDome(renderer, data, textures, palette);
        }
        else if (Outdoor)
        {
            BuildStars(renderer, palette);
            BuildMoon(renderer, textures);
            BuildNight(renderer, palette);
        }

        LoadTime = clock.Elapsed;
    }

    /// <summary>Loads MAP.<paramref name="number"/> and what it stands on, from its baked block
    /// (<paramref name="block"/>; none: baked here from the raw files, <see cref="BakeTime"/>).</summary>
    /// <param name="staticRobots">False: the robots are left out of the world mesh (the
    /// <c>Enemies.EnemyRenderer</c> draws them animated).</param>
    /// <param name="edited">The map as an editor has it (its block baked here from it); null: the data's.</param>
    public static Level Load(Renderer renderer, GameData data, int number, bool staticRobots = true, MapBlock? block = null, MapFile? edited = null,
        KeptSurfaces? kept = null)
    {
        var map = edited ?? data.LoadMap(number) ?? throw new FileNotFoundException($"MAP.{number:D3} is not in {data.MapArchive} or does not parse.");
        if (block != null)
        {
            return new Level(renderer, data, number, map, block, staticRobots, TimeSpan.Zero, kept);
        }

        var clock = Stopwatch.StartNew();
        block = MapBaker.Bake(data, number, edited: edited) ?? throw new FileNotFoundException($"MAP.{number:D3} does not bake.");
        return new Level(renderer, data, number, map, block, staticRobots, clock.Elapsed, kept);
    }

    /// <summary>The shade level (HAZE.000 row) of the indoor light at <paramref name="position"/>;
    /// the base row outdoors (what a moving model is drawn at).</summary>
    public int ShadeAt(Vector3 position) =>
        Outdoor || _shading == null ? HazeTable.Steps - 1 : _shading.VertexShade(position, Vector3.Zero);

    /// <summary>The frame's depth cue: <paramref name="cued"/> (outdoors) the HAZE rows from
    /// <paramref name="start"/> to <paramref name="far"/>, nothing beyond; else, with a far clip
    /// <paramref name="clip"/> (a view distance indoors, where DOS drew whole rooms), that clip alone at an
    /// unchanged shade; else none.</summary>
    public static Fog? CueOf(bool cued, float start, float far, float? clip, Vector4 colour)
    {
        const int Lit = HazeTable.Steps - 1;
        if (cued)
        {
            return new Fog(start, far, Lit, 0) { Beyond = FogBeyond.Clip, Colour = colour };
        }

        // Indoors: every row the same, only the clip (start just short of the end: a range, not a step);
        // the enhanced look clips only a coloured cue.
        return clip is { } at ? new Fog(at - 1f, at, Lit, Lit) { Beyond = FogBeyond.Clip, Colour = colour } : null;
    }

    /// <summary>The renderer's scene state for this level: the HAZE.000 table and, outdoors, the
    /// depth cue from the cue start to the far clip (row 63 down to row 0) with nothing beyond.
    /// In the enhanced look (set <see cref="Renderer.Lighting"/> first) the cue is a linear haze
    /// towards <see cref="HazeColour"/> over the same range, and the sprites take its light.</summary>
    public void Apply(Look look = Look.Original)
    {
        var enhanced = look == Look.Enhanced && _renderer.Lighting != null;
        _enhanced = enhanced;
        _renderer.ShadeTable = _hazeTable;
        _renderer.Fog = CueOf(Outdoor && !_hazeTable.IsNone, CueStart, Far, Outdoor ? null : FarOverride, enhanced ? HazeColour : default);
        var tint = enhanced ? LookLighting.SpriteTint(_renderer.Lighting!) : Vector4.One;
        for (var i = 0; i < _sprites.Count; i++)
        {
            var g = _sprites[i];
            _sprites[i] = g with
            {
                Material = !enhanced ? g.Original
                    : FireLook(g.Index) == FireSpriteLook.Masked ? g.Original with { Shading = Shading.Sprite, Colour = tint, Palette = FireMaskPalette }
                    : g.SelfLit ? g.Original with { Shading = Shading.Unlit }
                    : g.Original with { Shading = Shading.Sprite, Colour = tint },
            };
        }

        if (enhanced)
        {
            PrepareSpriteModels();
        }
    }

    /// <summary>How the enhanced look draws <paramref name="sprite"/>: as DOS does with its flames off.</summary>
    private FireSpriteLook FireLook(int sprite) => Flames ? FireFlames.SpriteLook(sprite, Particles) : FireSpriteLook.Dos;

    /// <summary>Whether a map draws the dusk dome: its decade is in the list at 0x52d51
    /// (missions 5-8; the Godot port treats every map of those decades the same).</summary>
    public static bool IsDusk(int map) => DuskMaps.Contains(map / 10 * 10);

    private long Now => ClockMs ?? _clock.ElapsedMilliseconds;

    /// <summary>Copies what the simulation moves (<see cref="Poses"/>, <see cref="HiddenSprites"/>,
    /// <see cref="Drops"/>, the water) into <paramref name="frame"/>, for a draw on another thread; the
    /// movers <paramref name="alpha"/> of the way from the previous tick (render interpolation).</summary>
    public void Capture(LevelFrame frame, float alpha = 1f)
    {
        if (frame.Poses.Length != _dynamic.Count)
        {
            frame.Poses = new EntityPose[_dynamic.Count];
        }

        for (var i = 0; i < _dynamic.Count; i++)
        {
            frame.Poses[i] = Poses != null && Poses.TryGetPose(_dynamic[i].Offset, alpha, out var p) ? p : EntityPose.Rest;
        }

        frame.Hidden.Clear();
        if (HiddenSprites != null)
        {
            frame.Hidden.UnionWith(HiddenSprites);
        }

        frame.Drops.Clear();
        if (Drops != null)
        {
            frame.Drops.AddRange(Drops);
        }

        (frame.WaterSurface, frame.WaterPass) = (WaterSurface, WaterPass);
        frame.MoonWay = Moon?.Direction ?? MoonDirection();
    }

    /// <summary>Queues the level for one frame seen from <paramref name="eye"/>, as it is now.</summary>
    /// <param name="pixelAngle">The angle one pixel spans (radians): the stars' size.</param>
    public void Draw(Vector3 eye, float pixelAngle)
    {
        Capture(_frame);
        Draw(_frame, eye, pixelAngle);
    }

    /// <summary>Queues the level for one frame seen from <paramref name="eye"/>, as
    /// <paramref name="frame"/> caught it (<see cref="Capture"/>).</summary>
    public void Draw(LevelFrame frame, Vector3 eye, float pixelAngle)
    {
        var now = Now;
        // The mod models' levels of detail are picked from this eye (the robots' too, drawn after).
        _lodView = LodView.Of(eye, pixelAngle * _renderer.Target.Height);
        if (_enhanced && !_duskSky.IsNone)
        {
            _renderer.Draw(_duskSky, 0, _duskSkyIndices, _duskSkyMaterial);
        }
        else if (_sky != null)
        {
            DrawParts(_sky, now);
        }

        if (NightShown)
        {
            DrawNight(frame, pixelAngle);
        }

        if (!_stars.IsNone && !NightShown)
        {
            if (Math.Abs(pixelAngle - _starPixel) > 1e-7f)
            {
                _starPixel = pixelAngle;
                _renderer.UpdateMesh(_stars, StarVertices(StarDistance * MathF.Tan(pixelAngle)), _starIndexList);
            }

            _renderer.Draw(_stars, 0, _starIndexList.Length, _starMaterial);
        }

        if (!_moon.IsNone && !NightShown)
        {
            // The shot moon falls (NightMoon): the quad follows its way.
            var way = frame.MoonWay == Vector3.Zero ? MoonDirection() : frame.MoonWay;
            if (way != _moonDrawn)
            {
                _moonDrawn = way;
                _renderer.UpdateMesh(_moon, MoonVertices(way), MoonIndices);
            }

            _renderer.Draw(_moon, 0, 6, _moonMaterial);
        }

        var far = Outdoor || FarOverride != null ? Far : float.MaxValue;
        if (!HideGround)
        {
            Ground?.Draw(_renderer, eye, far);
        }

        if (_enhanced && _worldEnhanced != null)
        {
            // The enhanced look with replacement models: the world without the static props they replace.
            DrawParts(_worldEnhanced, now);
            DrawReplacements();
        }
        else if (_world != null)
        {
            DrawParts(_world, now);
        }

        for (var d = 0; d < _dynamic.Count; d++)
        {
            var (offset, stages) = _dynamic[d];
            var pose = frame.Poses[d];
            if (pose.Visible && !DrawReplacedStage(offset, pose.Stage, pose.Delta) && stages[Math.Clamp(pose.Stage, 0, stages.Length - 1)] is { } mesh)
            {
                foreach (var part in mesh.Parts)
                {
                    mesh.Draw(_renderer, part, part.Texture?.FrameAt(now) ?? 0, pose.Delta);
                }
            }
        }

        DrawWater(frame);
        var hidden = frame.Hidden;
        foreach (var g in _sprites)
        {
            if (DrawSpriteModels(g, hidden, now))
            {
                continue;
            }

            if (_enhanced && FireLook(g.Index) == FireSpriteLook.Hidden)
            {
                // The enhanced look draws its flame instead (below).
                continue;
            }

            var n = 0;
            for (var i = 0; i < g.Sprites.Length; i++)
            {
                if (hidden.Contains(g.Offsets[i]))
                {
                    continue;
                }

                var (centre, size) = g.Sprites[i];
                // Turned about Y to face the eye (the Godot port's fixed-Y billboard).
                var angle = MathF.Atan2(eye.X - centre.X, eye.Z - centre.Z);
                g.Scratch[n++] = new Instance(Matrix4x4.CreateScale(size.X, size.Y, 1f) * Matrix4x4.CreateRotationY(angle) * Matrix4x4.CreateTranslation(centre));
            }

            if (n > 0)
            {
                _renderer.DrawInstanced(g.Quad, 0, 6, g.Material, g.Scratch.AsSpan(0, n), Matrix4x4.Identity, g.Texture.FrameAt(now, g.Phase));
            }
        }

        // The enhanced look's stars twinkle and its air moves by the level's clock.
        _renderer.EffectTime = now / 1000f;
        if (_enhanced && Flames && FireFlames.Burns(Particles) && _renderer.Lighting != null)
        {
            _flamesNow.Clear();
            foreach (var (offset, flame) in _fireSprites)
            {
                if (!hidden.Contains(offset))
                {
                    _flamesNow.Add(flame);
                }
            }

            _fireFlames.Draw(_renderer, _flamesNow, Particles, eye, now / 1000f, Outdoor);
        }

        if (_enhanced && Particles != ParticleLevel.Off && _renderer.Lighting is { } lighting)
        {
            _atmosphere.Draw(_renderer, lighting, Particles, Outdoor, eye, now / 1000f,
                Atmosphere.SourcesOf(Block.Lamps, frame.Drops.Select(d => (d.Sprite, d.At))), Number, Far, GroundReach, Outdoor ? Wind : null);
        }
    }

    /// <summary>The water pass (FUN_0015859c): what lies beyond the surface, seen from above or
    /// below, each pixel read a picture pixel to the left or right (the shimmer). The original look
    /// remaps those pixels' indices through the HAZE row itself (the renderer's
    /// <see cref="PlaneRemap"/>); the enhanced look grades its finished frame there by the row's fit
    /// over the palette, ≈ 0.9·c + (10, 12, 19) in display colours (<see cref="PlaneGrade"/>), as
    /// DOS's pass ran on its finished frame.</summary>
    private void DrawWater(LevelFrame frame)
    {
        _renderer.PlaneRemap = null;
        _renderer.PlaneGrade = null;
        if (Water == null || !frame.WaterPass || frame.WaterSurface is not { } y)
        {
            return;
        }

        var plane = new Plane(Vector3.UnitY, -y);
        var step = (int)(Now * WaterShimmerSteps / 1000);
        if (_renderer.Lighting != null)
        {
            // c' = (1 − α)·c + α·C: the blend form of the fit.
            var look = WaterLook.Surface;
            var colour = new Vector3(look.X, look.Y, look.Z);
            _renderer.PlaneGrade = new PlaneGrade(plane, new Vector3(1f - look.W), colour * look.W)
            {
                ShimmerLines = WaterShimmerLines,
                Step = step,
            };
        }
        else if (!_waterTable.IsNone)
        {
            _renderer.PlaneRemap = new PlaneRemap(plane, _waterTable)
            {
                ShimmerLines = WaterShimmerLines,
                Step = step,
            };
        }
    }

    /// <summary>The underwater palette (SKYNTWTR.COL) over the whole canvas, HUD included, as
    /// DOS swaps the VGA palette; call it after the HUD while the head is under.</summary>
    public void DrawUnderwater()
    {
        if (Water != null)
        {
            _renderer.FillRect(new System.Drawing.RectangleF(0f, 0f, _renderer.CanvasWidth, Renderer.CanvasHeight), WaterLook.Underwater);
        }
    }

    private void BuildWater(Renderer renderer)
    {
        if (WaterLook.Remap is { } remap)
        {
            _waterTable = renderer.CreateShadeTable(remap);
        }
    }

    private void DrawParts(ModelMesh mesh, long now)
    {
        foreach (var part in mesh.Parts)
        {
            mesh.Draw(_renderer, part, part.Texture?.FrameAt(now) ?? 0, Matrix4x4.Identity);
        }
    }

    /// <summary>The materials the level draws with, for <see cref="Renderer.WarmUp"/>.</summary>
    public IEnumerable<Material> Materials()
    {
        if (Ground != null) yield return Ground.Material;
        foreach (var mesh in new[] { _world, _worldEnhanced, _sky }.Concat(_dynamic.SelectMany(d => d.Stages)).Concat(_replacements.SelectMany(r => r.Mesh.All))
                     .Concat(_dynamicReplacements.Values.SelectMany(r => r.Stages).SelectMany(s => s?.All ?? [])).Distinct())
        {
            if (mesh == null) continue;
            foreach (var part in mesh.Parts) yield return part.Material;
        }

        foreach (var s in _sprites) yield return s.Material;
        foreach (var m in _spriteModels?.Materials() ?? []) yield return m;
        if (!_stars.IsNone) yield return _starMaterial;
        if (!_moon.IsNone) yield return _moonMaterial;
        if (!_duskSky.IsNone) yield return _duskSkyMaterial;
        foreach (var m in NightMaterials()) yield return m;
    }

    /// <summary>The block's meshes on the GPU: every variant-1 mesh and (with
    /// <paramref name="staticRobots"/>) every robot merged into one world mesh, a range per texture;
    /// the dynamic meshes one per stage. The textures are made in the order the raw build made them.</summary>
    private void BuildModels(Renderer renderer, TextureStore textures, MapBlock block, bool staticRobots)
    {
        var vertexShade = _shading != null;
        foreach (var t in block.ModelTextures)
        {
            textures.Get(t.Bank, t.Record, t.Opaque);
        }

        foreach (var d in block.Dynamic)
        {
            var stages = d.Stages.Select(s => s == null ? null
                : StaticBatch.Build(renderer, Palette, RenderState.Solid, s.Select(b => Bucket(textures, b)), vertexShade)).ToArray();
            _dynamic.Add((d.Offset, stages));
        }

        IEnumerable<BlockBucket> buckets = block.World;
        if (staticRobots)
        {
            foreach (var t in block.RobotTextures)
            {
                textures.Get(t.Bank, t.Record, t.Opaque);
            }

            buckets = Merge(block.World, block.Robots);
        }

        _world = StaticBatch.Build(renderer, Palette, RenderState.Solid, buckets.Select(b => Bucket(textures, b)), vertexShade);
    }

    /// <summary>DOS FUN_0014e100: R = Rz(roll)·Rx(pitch)·Ry(−yaw) in DOS axes; under (x, −y, −z)
    /// that is Rz(−roll)·Rx(pitch)·Ry(yaw) (column vectors), so for row vectors Ry·Rx·Rz(−roll).</summary>
    internal static Matrix4x4 Rotation(int yaw, int pitch, int roll) =>
        Matrix4x4.CreateRotationY((float)MapFile.AngleRadians(yaw))
        * Matrix4x4.CreateRotationX((float)MapFile.AngleRadians(pitch))
        * Matrix4x4.CreateRotationZ(-(float)MapFile.AngleRadians(roll));

    /// <summary>A block bucket as the batch's (key, texture, vertices, indices, pieces), its texture made or found.</summary>
    private static (SurfaceKey, GpuTexture?, IReadOnlyList<Vertex>, IReadOnlyList<uint>, IReadOnlyList<MeshChunk>?) Bucket(TextureStore textures,
        BlockBucket b)
    {
        // Each map cell's piece, culled by its box (the world mesh); none: the part's box alone.
        MeshChunk[]? pieces = b.Pieces.Length > 0 ? [.. b.Pieces.Select(p => new MeshChunk(p.First, p.Count, new Bounds(p.Min, p.Max)))] : null;
        if (b.Key.Texture is { } r && textures.Get(r.Bank, r.Record, r.Opaque) is { } t)
        {
            return (new SurfaceKey(t.Handle, b.Key.Flat), t, b.Vertices, b.Indices, pieces);
        }

        return (new SurfaceKey(default, b.Key.Texture != null ? HazeTable.HazeIndex : b.Key.Flat), null, b.Vertices, b.Indices, pieces);
    }

    /// <summary>The props' buckets with the robots' appended per key (the robots after the props, as placed).</summary>
    private static IEnumerable<BlockBucket> Merge(BlockBucket[] world, BlockBucket[] robots)
    {
        var byKey = robots.ToDictionary(r => r.Key);
        foreach (var w in world)
        {
            if (byKey.Remove(w.Key, out var r))
            {
                var at = (uint)w.Vertices.Length;
                var after = w.Indices.Length;
                yield return new BlockBucket(w.Key, [.. w.Vertices, .. r.Vertices], [.. w.Indices, .. r.Indices.Select(i => i + at)],
                    [.. w.Pieces, .. r.Pieces.Select(p => p with { First = p.First + after })]);
            }
            else
            {
                yield return w;
            }
        }

        foreach (var r in robots)
        {
            if (byKey.ContainsKey(r.Key))
            {
                yield return r;
            }
        }
    }

    private void BuildSprites(Renderer renderer, TextureStore textures, MapBlock block)
    {
        var quad = renderer.CreateMesh(
        [
            new Vertex(new Vector3(-0.5f, -0.5f, 0f), new Vector2(0f, 1f)),
            new Vertex(new Vector3(0.5f, -0.5f, 0f), new Vector2(1f, 1f)),
            new Vertex(new Vector3(0.5f, 0.5f, 0f), new Vector2(1f, 0f)),
            new Vertex(new Vector3(-0.5f, 0.5f, 0f), new Vector2(0f, 0f)),
        ], MoonIndices);
        var state = new RenderState(Queues.AlphaTest, Cull.None);
        foreach (var t in block.SpriteTextures)
        {
            textures.Get(t.Bank, t.Record, t.Opaque);
        }

        foreach (var g in block.Sprites)
        {
            if (textures.Get(g.Index >> 7, g.Index & 0x7F, opaque: false) is not { } t)
            {
                continue;
            }

            var material = new Material(t.Handle, Palette, Vector4.One, t.Frames, state);
            // Indoors the sprites take the room's ambient, except the fires DOS draws at full light.
            if (!Outdoor && _shading != null && !SpriteRules.FullBright(g.Index))
            {
                material = material with { Shade = AmbientLevel };
            }

            _sprites.Add(new SpriteGroup(quad, material, t, g.Phase, [.. g.Sprites.Select(s => (s.Centre, s.Size))],
                [.. g.Sprites.Select(s => s.Offset)], new Instance[g.Sprites.Length], material, EmissiveLamps.IsFire(g.Index), g.Index));
            foreach (var s in g.Sprites)
            {
                if (FireFlames.Of(g.Index, s.Centre - new Vector3(0f, s.Size.Y / 2f, 0f), s.Size.X, s.Size.Y, s.Offset) is { } flame)
                {
                    _fireSprites.Add((s.Offset, flame));
                }
            }
        }
    }

    /// <summary>The dusk dome SKY_SKY.3D (FUN_00133b67), drawn first round the eye with no depth
    /// and no fog over the frame's clear (palette index 0x11, as on every map).</summary>
    private void BuildDome(Renderer renderer, GameData data, TextureStore textures, Palette palette)
    {
        var model = data.Exists("MDMDOBJS.BSA") ? LoadDome(data) : null;
        if (model == null)
        {
            _missing.Add("SKY_SKY.3D");
            return;
        }

        var state = new RenderState(Queues.Background, Cull.Back, Blend.Opaque, Depth.Off, CameraRelative: true);
        _sky = ModelMesh.Build(renderer, model, textures, Palette, state, shading: Shading.Unlit);
        DuskSunDirection = LookLighting.DuskSunDirection(model, textures.Record, palette);
        BuildDuskSky(renderer);
    }

    /// <summary>The enhanced look's dusk sky, its glow on the light's heading; the depth cue's haze
    /// takes its horizon's colour, so the far terrain fades into the sky.</summary>
    private void BuildDuskSky(Renderer renderer)
    {
        var look = SunsetTable.Current.For(Number);
        var sun = SunsetSky.SunOf(DuskSunDirection, LookLighting.MoonLightDirection(), look.SunElevation);
        var (vertices, indices) = SunsetSky.Mesh(look, sun);
        _duskSky = renderer.CreateMesh(vertices, indices);
        _duskSkyIndices = indices.Length;
        _duskSkyMaterial = Material.Flat(Vector4.One, RenderState.Sky) with { Shading = Shading.Unlit, Unfogged = true };
        DuskLook = look;
        HazeColour = SunsetSky.HazeOf(look);
    }

    private static Model3D? LoadDome(GameData data)
    {
        using var objects = data.OpenArchive("MDMDOBJS.BSA");
        return objects.Read("SKY_SKY.3D") is { Length: > 0 } b ? Model3D.Parse(b) : null;
    }

    private static ReadOnlySpan<ushort> MoonIndices => [0, 1, 2, 0, 2, 3];

    /// <summary>The night moon as one quad round the eye, after the stars.</summary>
    private void BuildMoon(Renderer renderer, TextureStore textures)
    {
        if (textures.Get(MoonBank, MoonRecord, opaque: false) is not { } t)
        {
            _missing.Add($"TEXTURE.{MoonBank} record {MoonRecord}");
            return;
        }

        _moonTexels = new Vector2(t.Width, t.Height);
        _moonDrawn = MoonDirection();
        _moon = renderer.CreateMesh(MoonVertices(_moonDrawn), MoonIndices);
        var state = new RenderState(Queues.Background, Cull.None, Blend.Opaque, Depth.Off, CameraRelative: true);
        _moonMaterial = new Material(t.Handle, Palette, Vector4.One, t.Frames, state, Shading.Unlit) { Unfogged = true };
    }

    /// <summary>The moon's direction in world axes: DOS (x, y, z) → (x, −y, −z).</summary>
    public static Vector3 MoonDirection() =>
        Vector3.Normalize(new Vector3(MoonDosDirection.X, -MoonDosDirection.Y, -MoonDosDirection.Z));

    /// <summary>The moon quad: as wide as its texels at DOS's focal length (the same angle to the
    /// world as in DOS, whatever the port's field of view), each texel row 1.2 times as tall.</summary>
    private Vertex[] MoonVertices(Vector3 d)
    {
        var w = MoonDistance * _moonTexels.X / DosFocal;
        var h = MoonDistance * _moonTexels.Y * DosPixelAspect / DosFocal;
        var right = Vector3.Normalize(Vector3.Cross(Vector3.UnitY, -d)) * (w / 2f);
        var up = Vector3.Normalize(Vector3.Cross(-d, right)) * (h / 2f);
        var c = d * MoonDistance;
        return
        [
            new Vertex(c - right - up, new Vector2(0f, 1f)),
            new Vertex(c + right - up, new Vector2(1f, 1f)),
            new Vertex(c + right + up, new Vector2(1f, 0f)),
            new Vertex(c - right + up, new Vector2(0f, 0f)),
        ];
    }

    /// <summary>The player start (marker 0, its Y + 0x10) facing the nearest marker 1, as DOS
    /// FUN_00121f72 places the player; else above the middle of the map's entities.</summary>
    private (Vector3, float, bool) FindStart()
    {
        Vector3? start = null;
        var facing = new List<Vector3>();
        var sum = Vector3.Zero;
        var n = 0;
        foreach (var e in Map.Entities)
        {
            if (e.Variant == MapEntityVariant.Sprite && e.MarkerType == 0)
            {
                start ??= new Vector3(e.X, -(e.Y + 0x10), -e.Z);
            }
            else if (e.Variant == MapEntityVariant.Sprite && e.MarkerType == 1)
            {
                facing.Add(new Vector3(e.X, -(e.Y + 0x10), -e.Z));
            }

            if (e.Variant == MapEntityVariant.Mesh)
            {
                sum += new Vector3(e.X, -e.Y, -e.Z);
                n++;
            }
        }

        if (start is { } s)
        {
            var yaw = 0f;
            if (facing.Count > 0)
            {
                var d = facing.MinBy(f => Vector3.DistanceSquared(f, s)) - s;
                yaw = MathF.Atan2(-d.X, -d.Z);
            }

            return (s, yaw, true);
        }

        var centre = n > 0 ? sum / n : new Vector3(128 * 256f, 0f, 128 * 256f - (float)WldTerrain.ZFlipK);
        var ground = Wld != null ? (float)Wld.HeightAt(centre.X, -centre.Z) : centre.Y;
        return (new Vector3(centre.X, ground + 1000f, centre.Z), 0f, false);
    }

    /// <summary>The 256 stars of FUN_0013a000 (the Godot port's fixed seed), each a quad one pixel
    /// across (sized at draw time) in its palette colour 0x50-0x5f, around the eye.</summary>
    private void BuildStars(Renderer renderer, Palette palette)
    {
        var r = StarSeed;
        int Next() => r = (r & 0xffff) * 0x31d % 0x1003;
        for (var i = 0; i < StarCount; i++)
        {
            int colour, a, b;
            do
            {
                colour = Math.Clamp((Next() & 0xf) + 0x55, 0x50, 0x5f);
                a = Next() & 0x7ff;
                b = Next() & 0x7ff;
            }
            while (!(b >= 0x400 && a < 0x400));

            var ta = a * MathF.Tau / 2048f;
            var tb = b * MathF.Tau / 2048f;
            // DOS (x, y, z) → (x, −y, −z).
            _starDirections[i] = Vector3.Normalize(new Vector3(MathF.Sin(ta) * MathF.Cos(tb), -MathF.Sin(ta) * MathF.Sin(tb), -MathF.Cos(ta)));
            var (cr, cg, cb) = palette[colour];
            _starTints[i] = Vertex.Rgba(cr, cg, cb, 255);
        }

        _starIndexList = new ushort[StarCount * 6];
        for (var i = 0; i < StarCount; i++)
        {
            var at = (ushort)(i * 4);
            ((ReadOnlySpan<ushort>)[at, (ushort)(at + 1), (ushort)(at + 2), at, (ushort)(at + 2), (ushort)(at + 3)]).CopyTo(_starIndexList.AsSpan(i * 6));
        }

        _starPixel = 0f;
        _stars = renderer.CreateMesh(StarVertices(1f), _starIndexList);
        _starMaterial = Material.Flat(Vector4.One, RenderState.Sky) with { Shading = Shading.Unlit };
    }

    private Vertex[] StarVertices(float size)
    {
        var vertices = new Vertex[StarCount * 4];
        for (var i = 0; i < StarCount; i++)
        {
            var d = _starDirections[i];
            var u = Vector3.Normalize(Vector3.Cross(MathF.Abs(d.Y) < 0.99f ? Vector3.UnitY : Vector3.UnitX, d)) * size;
            var v = Vector3.Normalize(Vector3.Cross(d, u)) * size;
            var c = d * StarDistance;
            vertices[i * 4] = new Vertex(c - (u + v) / 2f, Vector2.Zero, _starTints[i]);
            vertices[i * 4 + 1] = new Vertex(c + (u - v) / 2f, Vector2.Zero, _starTints[i]);
            vertices[i * 4 + 2] = new Vertex(c + (u + v) / 2f, Vector2.Zero, _starTints[i]);
            vertices[i * 4 + 3] = new Vertex(c - (u - v) / 2f, Vector2.Zero, _starTints[i]);
        }

        return vertices;
    }
}

/// <summary>What a <see cref="Level"/> draw reads of the simulation, copied by <see cref="Level.Capture"/>
/// so a frame is drawn on another thread while the next tick runs (engine-threads plan B1).</summary>
public sealed class LevelFrame
{
    /// <summary>The dynamic meshes' poses, in the level's order.</summary>
    internal EntityPose[] Poses = [];
    /// <summary>The sprites not drawn, by MAP file offset.</summary>
    internal readonly HashSet<int> Hidden = [];
    internal readonly List<Campaign.DroppedSprite> Drops = [];
    internal float? WaterSurface;
    internal bool WaterPass;
    /// <summary>The way to the moon (it falls when shot, <see cref="NightMoon"/>).</summary>
    internal Vector3 MoonWay;
}
