using System.Numerics;
using RetroEng.Render;
using SkyNet.Formats;

namespace SkyNet.Game.Weapons;

/// <summary>
/// Draws what <see cref="WeaponSystem"/> has in the world: the effect sprites (impacts,
/// explosions, the muzzle flare) and the lobbed bodies as camera-facing sprites, and the bolts and
/// rockets with their .3D model (MDMDENMS.BSA) along their flight. Textures and models are made
/// on first use.
/// </summary>
public sealed class WeaponRenderer
{
    /// <summary>World units per texel of a thrown item's sprite (the item banks at 1:1).</summary>
    public const float SpriteScale = 1f;
    /// <summary>The enhanced look's laser bolt (across, along its flight) times its model: a thin
    /// streak, not a shard (issue #63). DOS draws every bolt's model 1:1.</summary>
    private static readonly Vector2 LaserStreak = new(0.5f, 2f);
    /// <summary>The enhanced look's laser core: its texels this many times (sRGB), past the glow's threshold.</summary>
    private const float LaserCore = 2f;

    private readonly Renderer _renderer;
    private readonly GameData _data;
    /// <summary>Made again at every map: what is first made on a map dies with its scope (MapReleased).</summary>
    private TextureStore _textures;
    private readonly TextureHandle _palette;
    private readonly MeshHandle _quad;
    private readonly Dictionary<(int Bank, int Record), (GpuTexture Texture, Material Material)?> _sprites = [];
    private readonly Dictionary<string, (ModelMesh Mesh, bool Forward)?> _models = new(StringComparer.OrdinalIgnoreCase);
    private static readonly RenderState SpriteState = new(Queues.AlphaTest, Cull.None);
    /// <summary>A bolt's model glows by its own light (<see cref="Enhanced.GameLights.BoltLight"/>): lit, no shadow (<see cref="Shadowless"/>).</summary>
    private const Shading BoltShading = Shading.Lit;
    /// <summary>The enhanced look's light on the lobbed bodies, pieces and drops (null: the original look).</summary>
    private Vector4? _spriteTint;
    /// <summary>The enhanced look's light all around (linear; the dust takes it).</summary>
    private Vector3 _ambient;
    private readonly Enhanced.ImpactEffects _effects = new();
    private readonly Enhanced.FireFlames _fireFlames = new();
    private readonly Enhanced.MuzzleFlashes _muzzleFlashes = new();
    /// <summary>The own shot's flash the last <see cref="Draw(WeaponFrame, Vector3, OwnFlashes)"/> saw (none: null): a 3D gun's flash (<see cref="DrawFlash"/>) burns as it.</summary>
    private EffectPose? _ownFlash;
    /// <summary>A 3D gun's flash triangles (<see cref="DrawFlash"/>), made once.</summary>
    private readonly (MeshHandle Mesh, Enhanced.GunFlashGeometry Geometry) _gunFlash;
    /// <summary>One white texel and its palette: the gun flash's colour is its vertices' and its material's.</summary>
    private readonly (TextureHandle Texture, TextureHandle Palette) _white;
    private readonly List<Enhanced.FireFlame> _dropFlames = [];
    /// <summary>The frame <see cref="Draw(WeaponSystem, Vector3)"/> captures into (a draw on the simulation's thread).</summary>
    private readonly WeaponFrame _frame = new();
    /// <summary>The drops a mod draws as models.</summary>
    private readonly Mods.SpriteModelView _spriteModels;

    /// <summary>ENHANCED OPTIONS' PARTICLES (the app's ApplyLook): the sparks off the impact pieces
    /// and the embers of burning drops in the enhanced look.</summary>
    public Menu.ParticleLevel Particles { get; set; } = Menu.ParticleLevel.Off;
    /// <summary>The level's palette with its fire colours cut out (<see cref="Level.FireMaskPalette"/>):
    /// a burning drop's body under its flame in the enhanced look; none: drawn whole.</summary>
    public TextureHandle FireMask { get; set; }

    public WeaponRenderer(Renderer renderer, GameData data, TextureHandle palette, Mods.KeptSurfaces? kept = null)
    {
        _renderer = renderer;
        _data = data;
        _palette = palette;
        _textures = new TextureStore(renderer, data);
        _spriteModels = new Mods.SpriteModelView(renderer, palette, kept);
        _quad = renderer.CreateMesh(
        [
            new Vertex(new Vector3(-0.5f, -0.5f, 0f), new Vector2(0f, 1f)),
            new Vertex(new Vector3(0.5f, -0.5f, 0f), new Vector2(1f, 1f)),
            new Vertex(new Vector3(0.5f, 0.5f, 0f), new Vector2(1f, 0f)),
            new Vertex(new Vector3(-0.5f, 0.5f, 0f), new Vector2(0f, 0f)),
        ], [0, 1, 2, 0, 2, 3]);
        var flash = Enhanced.MuzzleFlashes.GunFlashMesh();
        _gunFlash = (renderer.CreateMesh(flash.Vertices), flash);
        var white = new byte[256 * 4];
        white.AsSpan(4, 4).Fill(255);
        _white = (renderer.CreateIndexTexture(1, 1, [1]), renderer.CreatePalette(white));
    }

    /// <summary>The map's scope was released: the drops' models, sprites and bolts made in it are made
    /// again. A sprite first made on a map (a robot's death blast, bank 358) kept past its scope draws a
    /// stale handle the renderer drops: no explosion after a load or a map change (issues #10, #13).</summary>
    public void MapReleased()
    {
        _spriteModels.Release();
        _sprites.Clear();
        _models.Clear();
        _textures = new TextureStore(_renderer, _data);
    }

    /// <summary>The look: with the enhanced <paramref name="lighting"/> the effects (impacts,
    /// explosions, the muzzle flare) are unlit and the other sprites take the sun and the baked light
    /// (<see cref="Enhanced.LookLighting.SpriteMaterial"/>); null: the original look.</summary>
    public void Apply(Lighting? lighting)
    {
        _spriteTint = lighting != null ? Enhanced.LookLighting.SpriteTint(lighting) : null;
        _ambient = lighting != null ? (lighting.Sky + lighting.Ground) * 0.5f : Vector3.Zero;
    }

    /// <summary>A sprite's material as drawn now: an effect's unlit, the rest lit where it stands.</summary>
    private Material Look(Material material, bool effect) =>
        _spriteTint == null ? material
        : effect ? material with { Shading = Shading.Unlit }
        : Enhanced.LookLighting.SpriteMaterial(material, Menu.Look.Enhanced);

    /// <summary>Frames of an effect bank's record 0 (for <see cref="WeaponSystem.EffectFrames"/>).</summary>
    public int FramesOf(int bank) => Sprite(bank, 0) is { } s ? s.Texture.Frames : 1;

    /// <summary>The frame a lobbed body's sprite shows at <paramref name="animationMs"/> (issue #73):
    /// DOS's draw callback 0x123914 queues it with frame −1, which FUN_00149e00 turns into the
    /// free-running clock / ms % frames, as a map sprite's; in flight and after bounces alike.</summary>
    public int LobbedFrame(AmmoRecord ammo, long animationMs) =>
        LobbedSprite(ammo) is { } s ? s.Texture.FrameAt(animationMs) : 0;

    /// <summary>
    /// The turn (radians) of a thrown item's model at <paramref name="animationMs"/>: one turn a
    /// cycle of its sprite's frames (TEXTURE.217: 9 × 71 ms, about 1.6 a second). An item sprite
    /// (the satchel, dropped, not thrown) does not tumble.
    /// </summary>
    public float TumbleAngle(AmmoRecord ammo, long animationMs)
    {
        if (SpriteRules.IsItem(ammo.Sprite) || LobbedSprite(ammo) is not { Texture: { Animated: true } t })
        {
            return 0f;
        }

        var cycle = (long)t.Frames * (t.MsPerFrame > 0 ? t.MsPerFrame : GpuTexture.FallbackMsPerFrame);
        return animationMs % cycle / (float)cycle * MathF.Tau;
    }

    private (GpuTexture Texture, Material Material)? LobbedSprite(AmmoRecord ammo) =>
        ammo.Sprite > 0 ? Sprite(ammo.Sprite >> 7, ammo.Sprite & 0x7F) : null;

    /// <summary>Loads what a weapon table can show, so the first shot does not stall; returns the
    /// materials for <see cref="Renderer.WarmUp"/>.</summary>
    public List<Material> Preload(WeaponTable table)
    {
        var materials = new List<Material>();
        foreach (var a in table.Ammo)
        {
            if (a.ImpactBank > 0 && Sprite(a.ImpactBank, 0) is { } fx)
            {
                materials.Add(Look(fx.Material, effect: true));
            }

            if (a.Sprite > 0 && Sprite(a.Sprite >> 7, a.Sprite & 0x7F) is { } s)
            {
                materials.Add(Look(s.Material, effect: false));
            }

            if (a.Model.Length > 0 && Model(a.Model) is { } m)
            {
                materials.AddRange(m.Mesh.Parts.Select(p => p.Material));
            }
        }

        if (Sprite(WeaponSystem.FlashBank, 0) is { } flash)
        {
            materials.Add(Look(flash.Material, effect: true));
        }

        foreach (var t in ImpactPieces.Types)
        {
            if (Sprite(t.Sprite >> 7, t.Sprite & 0x7F) is { } piece)
            {
                materials.Add(Look(piece.Material, effect: false));
            }
        }

        return materials;
    }

    private (GpuTexture Texture, Material Material)? Sprite(int bank, int record)
    {
        if (!_sprites.TryGetValue((bank, record), out var s))
        {
            s = _textures.Get(bank, record, opaque: false) is { } t
                ? (t, new Material(t.Handle, _palette, Vector4.One, t.Frames, SpriteState) { Unfogged = false })
                : null;
            _sprites[(bank, record)] = s;
        }

        return s;
    }

    private (ModelMesh Mesh, bool Forward)? Model(string file)
    {
        if (!_models.TryGetValue(file, out var m))
        {
            m = null;
            foreach (var archive in new[] { "MDMDENMS.BSA", "MDMDOBJS.BSA" })
            {
                if (!_data.Exists(archive))
                {
                    continue;
                }

                using var bsa = _data.OpenArchive(archive);
                if (bsa.Read(file) is { Length: > 0 } bytes && Model3D.Parse(bytes) is { } model
                    && ModelMesh.Build(_renderer, model, _textures, _palette, RenderState.DoubleSided, smooth: true, BoltShading) is { } built)
                {
                    var mesh = new ModelMesh(built.Mesh, [.. built.Parts.Select(Shadowless)], built.Triangles);
                    // Which end is the nose: the side of the body's centre (projectile.gd _body_forward).
                    var z = model.Vertices.Length > 0 ? model.Vertices.Average(v => v.ToYUp().Z) : 0f;
                    m = (mesh, z > 0f);
                    break;
                }
            }

            _models[file] = m;
        }

        return m;
    }

    /// <summary>A bolt's part kept out of the sun's shadow map: Lit draws cast shadows (issue #4).</summary>
    private static ModelMesh.Part Shadowless(ModelMesh.Part part) => part with { Material = part.Material with { Shadowless = true } };

    /// <summary>Queues every effect, lobbed body and bolt for a frame seen from <paramref name="eye"/>,
    /// the sprites animated at <paramref name="animationMs"/>.</summary>
    public void Draw(WeaponSystem weapons, Vector3 eye, long animationMs)
    {
        Capture(weapons, _frame);
        Draw(_frame, eye, animationMs);
    }

    /// <summary>Copies what <see cref="Draw(WeaponFrame, Vector3)"/> reads of <paramref name="weapons"/>
    /// into <paramref name="frame"/>, for a draw on another thread; the shots and pieces in flight
    /// <paramref name="alpha"/> of the way from the previous tick (<see cref="Interpolation"/>).</summary>
    public static void Capture(WeaponSystem weapons, WeaponFrame frame, float alpha = 1f)
    {
        frame.Effects.Clear();
        foreach (var e in weapons.Effects)
        {
            frame.Effects.Add(new EffectPose(e.Kind, e.Bank, e.Record, e.Size, e.Frame, e.Position, e.Ammo, e.Age, e.Direction, e.Seed, e.DosScale));
        }

        frame.Projectiles.Clear();
        foreach (var p in weapons.Projectiles)
        {
            frame.Projectiles.Add(new ProjectilePose(p.Lobbed, p.Ammo, p.DrawnAt(alpha), p.Velocity));
        }

        var slots = weapons.Pieces.Slots;
        if (frame.Pieces.Length < slots.Length)
        {
            frame.Pieces = new ImpactPieces.Piece[ImpactPieces.Capacity];
        }

        slots.CopyTo(frame.Pieces);
        frame.PieceCount = slots.Length;
        if (alpha < 1f)
        {
            for (var i = 0; i < slots.Length; i++)
            {
                ref var piece = ref frame.Pieces[i];
                piece.Position = Interpolation.Lerp(piece.Previous, piece.Position, alpha);
            }
        }

        frame.Puffs.Clear();
        foreach (var p in weapons.Puffs)
        {
            frame.Puffs.Add(new PuffPose(p.At, p.Normal, p.Incoming, p.Metal, p.Scale, p.Seed, p.Age));
        }

        frame.Blasts.Clear();
        foreach (var b in weapons.Blasts)
        {
            frame.Blasts.Add(new BlastPose(b.At, b.Radius, b.Seed, b.Age));
        }

        frame.Smokes.Clear();
        foreach (var m in weapons.Smokes)
        {
            frame.Smokes.Add(new SmokePose(m.At, m.Direction, m.Size, m.Seed, m.Age));
        }
    }

    /// <summary>Queues the effects, lobbed bodies and bolts as <paramref name="weapons"/> caught them,
    /// the thrown items animated at <paramref name="animationMs"/>; <paramref name="own"/>: whether
    /// the own shot's DOS flash shows.</summary>
    public void Draw(WeaponFrame weapons, Vector3 eye, long animationMs, OwnFlashes own = OwnFlashes.Shown)
    {
        // The enhanced look with PARTICLES on draws impacts as dust (DrawPuffs) and explosions as
        // fireballs and smoke (DrawBlasts); their DOS sprites only otherwise.
        var generated = _spriteTint != null && Particles != Menu.ParticleLevel.Off;
        var particleFlash = Enhanced.MuzzleFlashes.ArtOf(LookNow) == Enhanced.FlashArt.Particles;
        _ownFlash = null;
        foreach (var e in weapons.Effects)
        {
            var flash = e.Kind is EffectKind.Flash or EffectKind.OwnFlash;
            if (e.Kind == EffectKind.OwnFlash)
            {
                _ownFlash = e;
            }

            if (generated && Generated(e.Kind))
            {
                continue;
            }

            if (e.Kind == EffectKind.OwnFlash && own == OwnFlashes.Hidden)
            {
                continue;
            }

            // The enhanced look's flash: particles, not the DOS flare.
            if (flash && particleFlash)
            {
                _muzzleFlashes.Add(ShotOf(e, e.Position, e.Size), eye);
                continue;
            }

            if (Sprite(e.Bank, e.Record) is not { } s)
            {
                continue;
            }

            var scale = EffectScale(e, Math.Max(s.Texture.Width, s.Texture.Height), LookNow);
            var frame = Math.Min(e.Frame, s.Texture.Frames - 1);
            DrawBillboard(s, eye, e.Position, s.Texture.Width * scale, s.Texture.Height * scale, frame, effect: true);
        }

        _muzzleFlashes.Flush(_renderer);
        if (particleFlash)
        {
            _muzzleFlashes.DrawSmoke(_renderer, weapons.Smokes, _ambient);
        }

        foreach (var p in weapons.Projectiles)
        {
            if (p.Lobbed && LobbedSprite(p.Ammo) is { } s)
            {
                var scale = (p.Ammo.Sprite >> 7) == 200 ? 1f : SpriteScale;
                if (!DrawThrownModel(p, s.Texture.Height * scale, animationMs))
                {
                    DrawBillboard(s, eye, p.Position, s.Texture.Width * scale, s.Texture.Height * scale, LobbedFrame(p.Ammo, animationMs));
                }
            }
            else if (!p.Lobbed && p.Ammo.Model.Length > 0 && Model(p.Ammo.Model) is { } m && p.Velocity.LengthSquared() > 1e-6f)
            {
                var dir = Vector3.Normalize(p.Velocity);
                var body = m.Forward ? dir : -dir;
                var up = MathF.Abs(body.Y) > 0.99f ? Vector3.UnitX : Vector3.UnitY;
                var x = Vector3.Normalize(Vector3.Cross(up, body));
                var y = Vector3.Cross(body, x);
                var scale = BoltScale(p.Ammo.Model, LookNow);
                var world = new Matrix4x4(
                    x.X * scale.X, x.Y * scale.X, x.Z * scale.X, 0f,
                    y.X * scale.X, y.Y * scale.X, y.Z * scale.X, 0f,
                    body.X * scale.Y, body.Y * scale.Y, body.Z * scale.Y, 0f,
                    p.Position.X, p.Position.Y, p.Position.Z, 1f);
                foreach (var part in m.Mesh.Parts)
                {
                    _renderer.Draw(m.Mesh.Mesh, part.First, part.Count, BoltMaterial(part.Material, p.Ammo.Model, LookNow), 0, world);
                }
            }
        }

        // The impact pieces: their sprite at the scenery's 1 unit a texel.
        foreach (ref readonly var p in weapons.Slots)
        {
            if (!p.Active || (p.Flags & PieceType.FlagDrawn) == 0 || Sprite(p.Sprite >> 7, p.Sprite & 0x7F) is not { } s)
            {
                continue;
            }

            DrawBillboard(s, eye, p.Position, s.Texture.Width * Level.SpriteScale, s.Texture.Height * Level.SpriteScale,
                ImpactPieces.FrameOf(p, s.Texture.Frames));
        }

        DrawSparks(weapons.Slots);
        DrawPuffs(weapons.Puffs);
        DrawBlasts(weapons.Blasts);
    }

    /// <summary>
    /// A mod's model for a thrown item's sprite in the enhanced look (<see cref="Mods.SpriteModels"/>),
    /// the size of the sprite, tumbling (<see cref="TumbleAngle"/>). False: no model (or its textures
    /// still decoding), or an item sprite (the satchel: its pickup's model would bob and turn as a
    /// pickup does): the billboard draws.
    /// </summary>
    private bool DrawThrownModel(in ProjectilePose p, float height, long animationMs)
    {
        if (SpriteRules.IsItem(p.Ammo.Sprite) || Mods.SpriteModels.Drawn(p.Ammo.Sprite, LookNow) is not { } model
            || !_spriteModels.Ready(model))
        {
            return false;
        }

        var world = model.Tumbled(p.Position, height, p.Velocity, TumbleAngle(p.Ammo, animationMs));
        _spriteModels.Draw(model, world, animationMs / 1000f, ThrownSeed);
        return true;
    }

    /// <summary>The level-of-detail key every thrown model shares (map offsets and drop ids are ≥ 0).</summary>
    private const int ThrownSeed = -1;

    // The enhanced look's explosions, bullet strikes and sparks (ImpactEffects), with PARTICLES on.
    private void DrawBlasts(List<BlastPose> blasts)
    {
        if (_spriteTint != null)
        {
            _effects.DrawBlasts(_renderer, blasts, Particles, _ambient, Enhanced.EffectTable.Current);
        }
    }

    private void DrawPuffs(List<PuffPose> puffs)
    {
        if (_spriteTint != null)
        {
            _effects.DrawPuffs(_renderer, puffs, Particles, _ambient, Enhanced.EffectTable.Current);
        }
    }

    private void DrawSparks(ReadOnlySpan<ImpactPieces.Piece> slots)
    {
        if (_spriteTint != null)
        {
            _effects.DrawSparks(_renderer, slots, Particles, Enhanced.EffectTable.Current);
        }
    }

    /// <summary>
    /// Queues the sprites destroyed records left (<see cref="Campaign.GameSession.Drops"/>), standing
    /// on their foot: items and scenery at 1:1, a multi-frame record animated as the
    /// map's own sprites are.
    /// </summary>
    public void DrawDrops(IReadOnlyList<Campaign.DroppedSprite> drops, Vector3 eye, long nowMs)
    {
        _dropFlames.Clear();
        foreach (var d in drops)
        {
            if (Sprite(d.Sprite >> 7, d.Sprite & 0x7F) is not { } s)
            {
                continue;
            }

            var scale = SpriteRules.IsItem(d.Sprite) ? Level.ItemScale : Level.SpriteScale;
            float w = s.Texture.Width * scale, h = s.Texture.Height * scale;
            // A mod's model in the enhanced look (Mods.SpriteModels), standing where the sprite would;
            // the billboard while a worker decodes its textures (a drop's first frames).
            if (Mods.SpriteModels.Drawn(d.Sprite, _spriteTint != null ? Menu.Look.Enhanced : Menu.Look.Original) is { } model
                && _spriteModels.Ready(model))
            {
                _spriteModels.Draw(model, d.At, h, nowMs / 1000f, d.Id);
                continue;
            }

            // The enhanced look's flame over a burning drop (instead of an all-fire sprite).
            var look = Enhanced.FireFlames.SpriteLook(d.Sprite, Particles);
            if (_spriteTint != null && look != Enhanced.FireSpriteLook.Dos && Enhanced.FireFlames.Of(d.Sprite, d.At, w, h, d.Id) is { } flame)
            {
                _dropFlames.Add(flame);
                if (look == Enhanced.FireSpriteLook.Hidden)
                {
                    continue;
                }

                if (!FireMask.IsNone)
                {
                    s = (s.Texture, s.Material with { Palette = FireMask });
                }
            }

            DrawBillboard(s, eye, d.At + new Vector3(0f, h / 2f, 0f), w, h, s.Texture.FrameAt(nowMs));
        }

        _fireFlames.Draw(_renderer, _dropFlames, Particles, eye, nowMs / 1000f, outdoor: true);
    }

    /// <summary>Queues a 3D gun's own muzzle flash (the enhanced look's, <see cref="Enhanced.MuzzleFlashes.GunFlashMesh"/>)
    /// <paramref name="size"/> units across at <paramref name="at"/>, facing <paramref name="eye"/>, in its inset over
    /// the gun (no depth test: a gun held along the view hides its own muzzle; an inset draws no particles):
    /// as the own shot's flash (its colour, seed and age), none once that burnt out.</summary>
    public void DrawFlash(Vector3 at, float size, Vector3 eye)
    {
        if (size <= 0f || _ownFlash is not { } own || Enhanced.MuzzleFlashes.ArtOf(LookNow) != Enhanced.FlashArt.Particles)
        {
            return;
        }

        var heat = Enhanced.MuzzleFlashes.HeatOf(own.Age);
        if (heat <= 0f)
        {
            return;
        }

        // Facing the eye, turned and sized by the shot's seed.
        var shape = Enhanced.MuzzleFlashes.ShapeOf(own.Seed);
        var back = eye - at;
        back = back.LengthSquared() > 1e-6f ? Vector3.Normalize(back) : Vector3.UnitZ;
        var up0 = MathF.Abs(back.Y) > 0.99f ? Vector3.UnitZ : Vector3.UnitY;
        var right0 = Vector3.Normalize(Vector3.Cross(up0, back));
        var up1 = Vector3.Cross(back, right0);
        var right = right0 * MathF.Cos(shape.Turn) + up1 * MathF.Sin(shape.Turn);
        var up = Vector3.Cross(back, right);
        size *= shape.Scale;
        Matrix4x4 Scaled(float k) => new(
            right.X * size * k, right.Y * size * k, right.Z * size * k, 0f,
            up.X * size * k, up.Y * size * k, up.Z * size * k, 0f,
            back.X, back.Y, back.Z, 0f,
            at.X, at.Y, at.Z, 1f);

        // Glow, petals, then the white-hot core, all added.
        var colour = Enhanced.MuzzleFlashes.ColourOf(own.Ammo);
        var hot = Enhanced.MuzzleFlashes.HotOf(colour);
        var g = _gunFlash.Geometry;
        var (first, count) = g.Variants[shape.Variant];
        _renderer.Draw(_gunFlash.Mesh, 0, g.FanCount, GunFlashMaterial(colour * (GunGlow * heat)), 0, Scaled(1.1f));
        _renderer.Draw(_gunFlash.Mesh, first, count, GunFlashMaterial(colour * (GunPetals * heat)), 0, Scaled(1.4f));
        _renderer.Draw(_gunFlash.Mesh, 0, g.FanCount, GunFlashMaterial(hot * (GunCore * heat)), 0, Scaled(0.4f));
    }

    // A 3D gun's flash: a flat colour added over the gun (inset: an 8-bit frame, no GLOW).
    private const float GunGlow = 0.9f, GunPetals = 1.4f, GunCore = 1.6f;
    private static readonly RenderState GunFlashState = new(Queues.Transparent, Cull.None, Blend.Add, Depth.Off);

    private Material GunFlashMaterial(Vector3 colour) =>
        new(_white.Texture, _white.Palette, new Vector4(colour, 1f), 1, GunFlashState, Shading.Unlit);

    /// <summary>The look the effects are drawn in now (<see cref="Apply"/>).</summary>
    /// <summary>World units per texel of an effect's sprite whose largest side is <paramref name="texMax"/>:
    /// DOS's own scale in the ORIGINAL look, the port's size otherwise (issue #44).</summary>
    /// <summary>Drawn by the ENHANCED look's particles in place of the sprite (dust, fireballs).</summary>
    private static bool Generated(EffectKind kind) => kind is EffectKind.Impact or EffectKind.Explosion;

    internal static float EffectScale(in EffectPose e, int texMax, Menu.Look look) =>
        look == Menu.Look.Original && e.DosScale > 0f ? e.DosScale : e.Size / MathF.Max(texMax, 1f);

    /// <summary>A bolt's model's scale (across, along its flight): DOS's 1:1, or an enhanced laser's streak.</summary>
    private static Vector2 BoltScale(string model, Menu.Look look) =>
        IsEnhancedLaser(model, look) ? LaserStreak : Vector2.One;

    /// <summary>A bolt's part as drawn: an enhanced laser shines by itself, bright into the glow; the rest as built.</summary>
    private static Material BoltMaterial(Material material, string model, Menu.Look look) =>
        IsEnhancedLaser(model, look) ? material with { Shading = Shading.Unlit, Colour = new Vector4(LaserCore, LaserCore, LaserCore, 1f) } : material;

    private static bool IsEnhancedLaser(string model, Menu.Look look) =>
        look == Menu.Look.Enhanced && model.StartsWith("LASER", StringComparison.OrdinalIgnoreCase);

    private Menu.Look LookNow => _spriteTint != null ? Menu.Look.Enhanced : Menu.Look.Original;

    /// <summary>A flash effect as <see cref="Enhanced.MuzzleFlashes"/> draws it: at <paramref name="at"/>, <paramref name="size"/> across.</summary>
    private static Enhanced.MuzzleShot ShotOf(in EffectPose e, Vector3 at, float size) =>
        new(at, e.Direction, size, Enhanced.MuzzleFlashes.ColourOf(e.Ammo), e.Seed, e.Age);

    private void DrawBillboard((GpuTexture Texture, Material Material) s, Vector3 eye, Vector3 at, float width, float height, int frame,
        bool effect = false)
    {
        var back = eye - at;
        back = back.LengthSquared() > 1e-6f ? Vector3.Normalize(back) : Vector3.UnitZ;
        var up0 = MathF.Abs(back.Y) > 0.99f ? Vector3.UnitZ : Vector3.UnitY;
        var right = Vector3.Normalize(Vector3.Cross(up0, back));
        var up = Vector3.Cross(back, right);
        var world = new Matrix4x4(
            right.X * width, right.Y * width, right.Z * width, 0f,
            up.X * height, up.Y * height, up.Z * height, 0f,
            back.X, back.Y, back.Z, 0f,
            at.X, at.Y, at.Z, 1f);
        _renderer.Draw(_quad, 0, 6, Look(s.Material, effect), frame, world);
    }
}

/// <summary>An effect sprite as caught for a frame (<see cref="Effect"/>).</summary>
public readonly record struct EffectPose(EffectKind Kind, int Bank, int Record, float Size, int Frame, Vector3 Position,
    AmmoRecord? Ammo = null, float Age = 0f, Vector3 Direction = default, int Seed = 0, float DosScale = 0f);

/// <summary>A projectile as caught for a frame (<see cref="Projectile"/>).</summary>
public readonly record struct ProjectilePose(bool Lobbed, AmmoRecord Ammo, Vector3 Position, Vector3 Velocity);

/// <summary>A bullet strike as caught for a frame (<see cref="BulletPuff"/>).</summary>
public readonly record struct PuffPose(Vector3 At, Vector3 Normal, Vector3 Incoming, bool Metal, float Scale, int Seed, float Age);

/// <summary>An explosion as caught for a frame (<see cref="BlastCloud"/>).</summary>
public readonly record struct BlastPose(Vector3 At, float Radius, int Seed, float Age);

/// <summary>A shot's muzzle smoke as caught for a frame (<see cref="MuzzleSmoke"/>).</summary>
public readonly record struct SmokePose(Vector3 At, Vector3 Direction, float Size, int Seed, float Age);

/// <summary>What <see cref="WeaponRenderer"/> draws of a <see cref="WeaponSystem"/>, copied by
/// <see cref="WeaponRenderer.Capture"/> (pooled): drawn on another thread while the next tick runs
/// (engine-threads plan B1).</summary>
public sealed class WeaponFrame
{
    internal readonly List<EffectPose> Effects = [];
    internal readonly List<ProjectilePose> Projectiles = [];
    internal ImpactPieces.Piece[] Pieces = new ImpactPieces.Piece[ImpactPieces.Capacity];
    internal int PieceCount;
    internal readonly List<PuffPose> Puffs = [];
    internal readonly List<BlastPose> Blasts = [];
    internal readonly List<SmokePose> Smokes = [];

    /// <summary>The impact pieces' slots caught (read those with <see cref="ImpactPieces.Piece.Active"/> set).</summary>
    internal ReadOnlySpan<ImpactPieces.Piece> Slots => Pieces.AsSpan(0, PieceCount);
}
