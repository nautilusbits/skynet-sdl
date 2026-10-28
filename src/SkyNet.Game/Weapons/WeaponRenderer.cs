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
    /// <summary>A bolt is drawn this many times fatter (projectile.gd BOLT_FATTEN).</summary>
    public const float BoltFatten = 2f;

    private readonly Renderer _renderer;
    private readonly GameData _data;
    private readonly TextureStore _textures;
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
    }

    /// <summary>The map's scope was released: the drops' models made in it are made again.</summary>
    public void MapReleased() => _spriteModels.Release();

    /// <summary>The look: with the enhanced <paramref name="lighting"/> the effects (impacts,
    /// explosions, the muzzle flare) are unlit and the other sprites take the hemisphere's light
    /// (<see cref="Enhanced.LookLighting.SpriteTint"/>); null: the original look.</summary>
    public void Apply(Lighting? lighting)
    {
        _spriteTint = lighting != null ? Enhanced.LookLighting.SpriteTint(lighting) : null;
        _ambient = lighting != null ? (lighting.Sky + lighting.Ground) * 0.5f : Vector3.Zero;
    }

    /// <summary>A sprite's material as drawn now: an effect's unlit, the rest lit by the tint.</summary>
    private Material Look(Material material, bool effect) =>
        _spriteTint is not { } tint ? material
        : effect ? material with { Shading = Shading.Unlit }
        : material with { Shading = Shading.Sprite, Colour = tint };

    /// <summary>Frames of an effect bank's record 0 (for <see cref="WeaponSystem.EffectFrames"/>).</summary>
    public int FramesOf(int bank) => Sprite(bank, 0) is { } s ? s.Texture.Frames : 1;

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
            materials.Add(Look(Glow(flash).Material, effect: true));
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

    /// <summary>Queues every effect, lobbed body and bolt for a frame seen from <paramref name="eye"/>.</summary>
    public void Draw(WeaponSystem weapons, Vector3 eye)
    {
        Capture(weapons, _frame);
        Draw(_frame, eye);
    }

    /// <summary>Copies what <see cref="Draw(WeaponFrame, Vector3)"/> reads of <paramref name="weapons"/>
    /// into <paramref name="frame"/>, for a draw on another thread; the shots and pieces in flight
    /// <paramref name="alpha"/> of the way from the previous tick (<see cref="Interpolation"/>).</summary>
    public static void Capture(WeaponSystem weapons, WeaponFrame frame, float alpha = 1f)
    {
        frame.Effects.Clear();
        foreach (var e in weapons.Effects)
        {
            frame.Effects.Add(new EffectPose(e.Kind, e.Bank, e.Record, e.Size, e.Frame, e.Position));
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
    }

    /// <summary>Queues the effects, lobbed bodies and bolts as <paramref name="weapons"/> caught them;
    /// <paramref name="own"/>: whether the own shot's DOS flash shows.</summary>
    public void Draw(WeaponFrame weapons, Vector3 eye, OwnFlashes own = OwnFlashes.Shown)
    {
        // The enhanced look with PARTICLES on draws impacts as dust (DrawPuffs) and explosions as
        // fireballs and smoke (DrawBlasts); their DOS sprites only otherwise.
        var generated = _spriteTint != null && Particles != Menu.ParticleLevel.Off;
        foreach (var e in weapons.Effects)
        {
            if (generated && e.Kind is not (EffectKind.Flash or EffectKind.OwnFlash))
            {
                continue;
            }

            if (e.Kind == EffectKind.OwnFlash && own == OwnFlashes.Hidden)
            {
                continue;
            }

            if (Sprite(e.Bank, e.Record) is not { } s)
            {
                continue;
            }

            var max = MathF.Max(s.Texture.Width, s.Texture.Height);
            var scale = e.Size / MathF.Max(max, 1f);
            var frame = Math.Min(e.Frame, s.Texture.Frames - 1);
            var flash = e.Kind is EffectKind.Flash or EffectKind.OwnFlash;
            DrawBillboard(flash && _spriteTint != null ? Glow(s) : s, eye, e.Position, s.Texture.Width * scale, s.Texture.Height * scale, frame, effect: true);
        }

        foreach (var p in weapons.Projectiles)
        {
            if (p.Lobbed && p.Ammo.Sprite > 0 && Sprite(p.Ammo.Sprite >> 7, p.Ammo.Sprite & 0x7F) is { } s)
            {
                var scale = (p.Ammo.Sprite >> 7) == 200 ? 1f : SpriteScale;
                DrawBillboard(s, eye, p.Position, s.Texture.Width * scale, s.Texture.Height * scale, 0);
            }
            else if (!p.Lobbed && p.Ammo.Model.Length > 0 && Model(p.Ammo.Model) is { } m && p.Velocity.LengthSquared() > 1e-6f)
            {
                var dir = Vector3.Normalize(p.Velocity);
                var body = m.Forward ? dir : -dir;
                var up = MathF.Abs(body.Y) > 0.99f ? Vector3.UnitX : Vector3.UnitY;
                var x = Vector3.Normalize(Vector3.Cross(up, body));
                var y = Vector3.Cross(body, x);
                var fat = p.Ammo.Model.StartsWith("LASER", StringComparison.OrdinalIgnoreCase) ? BoltFatten : 1f;
                var world = new Matrix4x4(
                    x.X * fat, x.Y * fat, x.Z * fat, 0f,
                    y.X * fat, y.Y * fat, y.Z * fat, 0f,
                    body.X, body.Y, body.Z, 0f,
                    p.Position.X, p.Position.Y, p.Position.Z, 1f);
                foreach (var part in m.Mesh.Parts)
                {
                    _renderer.Draw(m.Mesh.Mesh, part.First, part.Count, part.Material, 0, world);
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

    /// <summary>Queues the DOS muzzle flare (<see cref="WeaponSystem.FlashBank"/>) <paramref name="size"/>
    /// units across at <paramref name="at"/>, facing <paramref name="eye"/>: a 3D gun's own flash, drawn in its
    /// inset over the gun (no depth test: a gun held along the view hides its own muzzle).</summary>
    public void DrawFlash(Vector3 at, float size, Vector3 eye)
    {
        if (size <= 0f || Sprite(WeaponSystem.FlashBank, 0) is not { } s)
        {
            return;
        }

        var scale = size / MathF.Max(MathF.Max(s.Texture.Width, s.Texture.Height), 1f);
        var glow = Glow(s);
        var over = (glow.Texture, glow.Material with { State = glow.Material.State with { Depth = Depth.Off } });
        DrawBillboard(over, eye, at, s.Texture.Width * scale, s.Texture.Height * scale, 0, effect: true);
    }

    /// <summary>A muzzle flash in the enhanced look: added to the scene, so the flare's dark rim and
    /// the hard edge of its cut-out (an upscaled DOS sprite's square) do not show.</summary>
    private static (GpuTexture Texture, Material Material) Glow((GpuTexture Texture, Material Material) s) =>
        (s.Texture, s.Material with { State = s.Material.State with { Queue = Queues.Transparent, Blend = Blend.Add, Depth = Depth.Test } });

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
public readonly record struct EffectPose(EffectKind Kind, int Bank, int Record, float Size, int Frame, Vector3 Position);

/// <summary>A projectile as caught for a frame (<see cref="Projectile"/>).</summary>
public readonly record struct ProjectilePose(bool Lobbed, AmmoRecord Ammo, Vector3 Position, Vector3 Velocity);

/// <summary>A bullet strike as caught for a frame (<see cref="BulletPuff"/>).</summary>
public readonly record struct PuffPose(Vector3 At, Vector3 Normal, Vector3 Incoming, bool Metal, float Scale, int Seed, float Age);

/// <summary>An explosion as caught for a frame (<see cref="BlastCloud"/>).</summary>
public readonly record struct BlastPose(Vector3 At, float Radius, int Seed, float Age);

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

    /// <summary>The impact pieces' slots caught (read those with <see cref="ImpactPieces.Piece.Active"/> set).</summary>
    internal ReadOnlySpan<ImpactPieces.Piece> Slots => Pieces.AsSpan(0, PieceCount);
}
