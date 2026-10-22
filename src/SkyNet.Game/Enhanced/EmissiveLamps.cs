using System.Numerics;
using RetroEng.Render.Lights;
using SkyNet.Formats;

namespace SkyNet.Game.Enhanced;

/// <summary>A point light the enhanced look derives from the DOS art.</summary>
/// <param name="Position">World position (the port's axes: Y up).</param>
/// <param name="Colour">Linear 0–1 tint.</param>
/// <param name="Range">Where <see cref="LightFalloff.Window"/> reaches 0, world units.</param>
/// <param name="Energy">The energy <see cref="LightFalloff.Light"/> takes (the Godot light_energy,
/// i.e. <see cref="LightFalloff.EnergyAtRef"/> of the intensity at 150 u). A flickering lamp's
/// base energy, scaled per frame by <see cref="EmissiveLamps.FlickerScale"/>.</param>
/// <param name="Flicker">A fire: its energy follows <see cref="EmissiveLamps.FlickerScale"/>.</param>
/// <param name="Phase">The flicker's phase, radians (0 for a steady lamp).</param>
/// <param name="Lifted">A lit wall patch whose lamp went up under its mesh's top (diagnostics).</param>
/// <param name="Facing">The way its lit patch faces in the world (unit; zero: none known, a fire).</param>
/// <param name="Coherence">How much its lit triangles agree on <paramref name="Facing"/>: |Σ wₖ nₖ| / Σ wₖ,
/// 1 for one flat patch, near 0 for a bulb lit all round (<see cref="LampSpots"/> reads it).</param>
/// <param name="Model">The placed mesh's model name (no ".3D"; empty: a fire).</param>
/// <param name="Texture">The lit patch's main texture word (bank &lt;&lt; 7 | record; −1: none).</param>
/// <param name="Entity">The placed record's MAP file offset (−1: none).</param>
public readonly record struct LampLight(
    Vector3 Position, Vector3 Colour, float Range, float Energy, bool Flicker, float Phase = 0f, bool Lifted = false,
    Vector3 Facing = default, float Coherence = 0f, string Model = "", int Texture = -1, int Entity = -1);

/// <summary>The lit texels of an emissive record, row-major <see cref="Width"/> × <see cref="Height"/>.
/// <see cref="ScanSize"/> is the long side of the mask image the Godot port sampled (its sample
/// density rule reads it).</summary>
public sealed record LitMask(int Width, int Height, bool[] Lit, int ScanSize)
{
    /// <summary>Whether the texel under <paramref name="uv"/> is lit (1 = the record's size, wrapped).</summary>
    public bool At(Vector2 uv)
    {
        var x = (int)MathF.Floor(uv.X * Width) % Width;
        var y = (int)MathF.Floor(uv.Y * Height) % Height;
        if (x < 0) x += Width;
        if (y < 0) y += Height;
        return Lit[y * Width + x];
    }
}

/// <summary>One emissive record's triangles of a mesh, in the mesh's own frame: positions, texel
/// coordinates (1 = the record's size) and the visible-side normal per vertex; indices in triples.</summary>
public sealed record EmissiveSurface(LitMask Mask, Vector3[] Positions, Vector2[] Uvs, Vector3[] Normals, int[] Indices)
{
    /// <summary>The texture word these triangles draw with (−1: not known).</summary>
    public int Texture { get; init; } = -1;
}

/// <summary>A placed mesh's emissive surfaces, its placement and its local bounds (for the top a
/// wall panel's lamp is hung under).</summary>
public sealed record EmissiveMesh(Matrix4x4 World, Vector3 BoundsMin, Vector3 BoundsMax, IReadOnlyList<EmissiveSurface> Surfaces)
{
    /// <summary>The model's name (no ".3D") and the placed record's MAP file offset, for overrides.</summary>
    public string Model { get; init; } = "";
    public int Entity { get; init; } = -1;
}

/// <summary>A fire sprite: its sprite index, the foot of its billboard (world), its world height and
/// a seed for the flicker's phase (the MAP file offset, as the Godot port seeded it).</summary>
public readonly record struct FireSprite(int SpriteIndex, Vector3 Base, float WorldHeight, int Seed);

/// <summary>
/// Lamps where the DOS art is lit: the Godot port's ENHANCED <c>_place_emissive_lights</c> (main.gd,
/// removed in git 3dfe3b6) and the light of its <c>fire_effect.gd</c>. A DOS interior has hardly any
/// light entities; the light is painted into the textures (strips, panels, screens), so every
/// triangle of an emissive record (<see cref="EmissionClassifier"/>) is sampled against the
/// record's lit texels on a barycentric grid, the lit points are gathered into
/// <see cref="Cell"/>-sized cells per mesh, in its own frame, and each cell gets a lamp
/// <see cref="Drop"/> in front of its patch — the brightest <see cref="MaxLamps"/> cells.
/// A patch that doesn't face down (a wall panel, a screen) hangs its lamp under the top of its mesh
/// instead, dimmer. Pure CPU, no GPU objects.
/// <para>Where this follows the Godot code over its comments: the range is
/// <c>clamp(extent · 1.2 + 600, 750, 3200)</c> (<see cref="RangeScale"/> 9 was declared and never
/// used); the colour is the fixed <see cref="Colour"/>, not the texels' mean.</para>
/// </summary>
public static class EmissiveLamps
{
    /// <summary>main.gd EMIT_LIGHT_MAX: the lamp budget per level.</summary>
    public const int MaxLamps = 128;
    /// <summary>main.gd EMIT_LIGHT_SHADOWS: how many of the first lamps cast shadows in Godot.</summary>
    public const int ShadowLamps = 4;
    /// <summary>main.gd EMIT_CELL: the cell the lit points are gathered in, world units.</summary>
    public const float Cell = 900f;
    /// <summary>main.gd EMIT_DROP: how far in front of its patch a lamp hangs.</summary>
    public const float Drop = 60f;
    /// <summary>main.gd EMIT_LIGHT_COLOR.</summary>
    public static readonly Vector3 Colour = new(1f, 0.94f, 0.84f);
    /// <summary>main.gd EMIT_LIGHT_ENERGY: the intensity at 150 u (a bright corridor bleached at 0.14).</summary>
    public const float IntensityAtRef = 0.10f;
    /// <summary>main.gd EMIT_RANGE_SCALE: declared there, never used (see the class notes).</summary>
    public const float RangeScale = 9f;
    /// <summary>The range rule main.gd used: extent · 1.2 + 600, clamped 750–3200.</summary>
    public const float RangeExtentScale = 1.2f, RangeBase = 600f, RangeMin = 750f, RangeMax = 3200f;
    /// <summary>main.gd EMIT_CEILING_NY: a patch whose world normal's Y is at or under −this is a ceiling.</summary>
    public const float CeilingNy = 0.5f;
    /// <summary>main.gd EMIT_CEILING_GAP: a wall patch's lamp hangs at least this far under its mesh's top.</summary>
    public const float CeilingGap = 30f;
    /// <summary>main.gd EMIT_WALL_SCALE: a wall patch's lamp energy factor.</summary>
    public const float WallScale = 0.6f;
    /// <summary>main.gd _lit_samples: samples per triangle side = clamp(texels across / 5, 2, 14).</summary>
    public const int SamplesMin = 2, SamplesMax = 14;
    public const float TexelsPerSample = 5f;
    /// <summary>The Godot mask image: the ENHANCED texture (4× upscale), at most 96 px on the long side.</summary>
    public const int GodotUpscale = 4;

    /// <summary>fire_effect.gd: the fire's light colour.</summary>
    public static readonly Vector3 FireColour = new(1f, 0.58f, 0.22f);
    /// <summary>fire_effect.gd: intensity at 150 u = clamp(h / 450, 0.18, 0.32).</summary>
    public const float FireHeightDiv = 450f, FireIntensityMin = 0.18f, FireIntensityMax = 0.32f;
    /// <summary>fire_effect.gd: range = clamp(h · 9, 500, 2400).</summary>
    public const float FireRangeScale = 9f, FireRangeMin = 500f, FireRangeMax = 2400f;
    /// <summary>fire_effect.gd: the light sits at flame base + h · 0.35.</summary>
    public const float FireLightHeight = 0.35f;

    /// <summary>The kinds fire_effect.gd shapes a fire into, which set its light's height.</summary>
    public enum FireKind { Flame, Camp, Barrel, Wreck }

    /// <summary>fire_effect.gd FIRE_SPRITES: sprite index → kind and height factor.</summary>
    public static readonly IReadOnlyDictionary<int, (FireKind Kind, float H)> GodotFires = new Dictionary<int, (FireKind, float)>
    {
        [27653] = (FireKind.Flame, 1f),   // 216/005
        [27659] = (FireKind.Flame, 1f),   // 216/011
        [27660] = (FireKind.Flame, 1f),   // 216/012
        [27661] = (FireKind.Flame, 1f),   // 216/013
        [27904] = (FireKind.Camp, 1f),    // 218/000
        [27905] = (FireKind.Camp, 1f),    // 218/001
        [27906] = (FireKind.Camp, 0.9f),  // 218/002
        [27907] = (FireKind.Camp, 1f),    // 218/003
        [26626] = (FireKind.Barrel, 1f),  // 208/002
        [26369] = (FireKind.Barrel, 1f),  // 206/001
        [26627] = (FireKind.Wreck, 1f),   // 208/003 burning tyres
        [27662] = (FireKind.Flame, 1f),   // 216/014 a small flame
    };

    /// <summary>Whether a sprite carries a fire lamp: the Godot table, or a sprite DOS draws at full
    /// light (<see cref="SpriteRules.FullBright"/>: 192/0 and 192/2 are only there, as a flame).</summary>
    public static bool IsFire(int spriteIndex) => GodotFires.ContainsKey(spriteIndex) || SpriteRules.FullBright(spriteIndex);

    /// <summary>fire_effect.gd _process: the energy factor at <paramref name="seconds"/>.</summary>
    public static float FlickerScale(float seconds, float phase) =>
        0.82f + 0.12f * MathF.Sin(seconds * 9.3f + phase) + 0.06f * MathF.Sin(seconds * 23.7f + phase * 2f);

    /// <summary>A record's lit mask, or null when <see cref="EmissionClassifier"/> doesn't call it
    /// a lamp. The record's first frame, as the classifier reads it. <paramref name="maxFraction"/>
    /// replaces <see cref="EmissionClassifier.EmissionMax"/> when given: the classifier reads the
    /// record 1:1, where Godot read a 4× upscale bilinear-shrunk to 96 px, and MAP.214's corridor
    /// strips (448/4, 448/5) come out at 22.9 % lit here, just over the cut (see the tests).</summary>
    public static LitMask? MaskOf(TextureRecord record, Palette palette, bool[] litTable, float? maxFraction = null)
    {
        var r = EmissionClassifier.Classify(record, palette, litTable);
        var emissive = maxFraction is { } max
            ? r.CountedTexels > 0 && r.LitFraction >= EmissionClassifier.EmissionMin && r.LitFraction <= max
            : r.Emissive;
        if (!emissive)
        {
            return null;
        }

        var lit = new bool[record.Width * record.Height];
        for (var i = 0; i < lit.Length && i < record.Pixels.Length; i++)
        {
            var index = record.Pixels[i];
            lit[i] = index != 0 && litTable[index];
        }

        return new LitMask(record.Width, record.Height, lit,
            Math.Min(Math.Max(record.Width, record.Height) * GodotUpscale, EmissionClassifier.GodotScan));
    }

    /// <summary>A model's emissive surfaces (frame 0) placed by <paramref name="world"/>; null when
    /// none of its records emits. <paramref name="mask"/> gives a texture word's mask (cache it).</summary>
    public static EmissiveMesh? FromModel(Model3D model, Matrix4x4 world, Func<int, LitMask?> mask)
    {
        var verts = model.Vertices;
        if (verts.Length == 0)
        {
            return null;
        }

        var surfaces = new List<EmissiveSurface>();
        foreach (var group in model.Faces.Where(f => !f.IsSolidColour).GroupBy(f => f.Texture).OrderBy(g => g.Key))
        {
            if (mask(group.Key) is not { } m)
            {
                continue;
            }

            var positions = new List<Vector3>();
            var uvs = new List<Vector2>();
            var normals = new List<Vector3>();
            var indices = new List<int>();
            foreach (var face in group)
            {
                var faceUvs = model.FaceUvs(face, m.Width, m.Height);
                var normal = model.FaceNormal(face);
                var at = positions.Count;
                for (var k = 0; k < face.VertexCount; k++)
                {
                    positions.Add(verts[face.Indices[k]].ToYUp());
                    uvs.Add(faceUvs[k]);
                    normals.Add(normal);
                }

                foreach (var (a, b, c) in Model3D.TriangleFan(face))
                {
                    indices.AddRange([at + a, at + b, at + c]);
                }
            }

            if (indices.Count > 0)
            {
                surfaces.Add(new EmissiveSurface(m, [.. positions], [.. uvs], [.. normals], [.. indices]) { Texture = group.Key });
            }
        }

        if (surfaces.Count == 0)
        {
            return null;
        }

        var min = new Vector3(float.MaxValue);
        var max = new Vector3(float.MinValue);
        foreach (var v in verts)
        {
            var p = v.ToYUp();
            min = Vector3.Min(min, p);
            max = Vector3.Max(max, p);
        }

        return new EmissiveMesh(world, min, max, surfaces);
    }

    private struct Accumulator
    {
        public float Weight;
        public Vector3 Centre, Normal, Min, Max;
        /// <summary>The main texture so far and its weight; the lit weight per texture.</summary>
        public int Texture;
        public float TextureWeight;
        public Dictionary<int, float> Textures;
    }

    private readonly record struct CellLamp(float Weight, int Mesh, int Order, Vector3 Centre, Vector3 Normal, float Extent,
        float Coherence, int Texture);

    /// <summary>The lamps of a level's emissive meshes: the brightest <see cref="MaxLamps"/> cells
    /// (weight = lit area), ties kept in mesh and cell order. Deterministic.</summary>
    public static List<LampLight> Build(IReadOnlyList<EmissiveMesh> meshes)
    {
        var cells = new List<CellLamp>();
        var hits = new List<Vector2>();
        for (var mi = 0; mi < meshes.Count; mi++)
        {
            var local = new Dictionary<(int, int, int), Accumulator>();
            var order = new List<(int, int, int)>();
            foreach (var s in meshes[mi].Surfaces)
            {
                for (var t = 0; t + 2 < s.Indices.Length; t += 3)
                {
                    int ia = s.Indices[t], ib = s.Indices[t + 1], ic = s.Indices[t + 2];
                    Vector3 a = s.Positions[ia], b = s.Positions[ib], c = s.Positions[ic];
                    var cross = Vector3.Cross(b - a, c - a);
                    var area = cross.Length() * 0.5f;
                    if (area <= 0f)
                    {
                        continue;
                    }

                    var total = LitSamples(s.Mask, s.Uvs[ia], s.Uvs[ib], s.Uvs[ic], hits);
                    if (hits.Count == 0)
                    {
                        continue;
                    }

                    // The lit part of the triangle, not the triangle: a strip across one corner of a
                    // wall quad puts the lamp under the strip.
                    var centre = Vector3.Zero;
                    foreach (var bc in hits)
                    {
                        centre += a + (b - a) * bc.X + (c - a) * bc.Y;
                    }

                    centre /= hits.Count;
                    var w = area * hits.Count / total;
                    var n = s.Normals[ia] + s.Normals[ib] + s.Normals[ic];
                    if (n == Vector3.Zero)
                    {
                        n = cross;
                    }

                    var key = ((int)MathF.Floor(centre.X / Cell), (int)MathF.Floor(centre.Y / Cell), (int)MathF.Floor(centre.Z / Cell));
                    if (!local.TryGetValue(key, out var acc))
                    {
                        acc = new Accumulator { Min = centre, Max = centre, Texture = -1, Textures = [] };
                        order.Add(key);
                    }

                    var tw = acc.Textures.GetValueOrDefault(s.Texture) + w;
                    acc.Textures[s.Texture] = tw;
                    if (tw > acc.TextureWeight)
                    {
                        (acc.Texture, acc.TextureWeight) = (s.Texture, tw);
                    }

                    acc.Weight += w;
                    acc.Centre += centre * w;
                    acc.Normal += Vector3.Normalize(n) * w;
                    acc.Min = Vector3.Min(acc.Min, centre);
                    acc.Max = Vector3.Max(acc.Max, centre);
                    local[key] = acc;
                }
            }

            for (var k = 0; k < order.Count; k++)
            {
                var acc = local[order[k]];
                var normal = acc.Normal == Vector3.Zero ? Vector3.Zero : Vector3.Normalize(acc.Normal);
                cells.Add(new CellLamp(acc.Weight, mi, k, acc.Centre / acc.Weight, normal, (acc.Max - acc.Min).Length(),
                    Math.Clamp(acc.Normal.Length() / acc.Weight, 0f, 1f), acc.Texture));
            }
        }

        // The brightest cells first, up to the budget (Godot's sort wasn't stable; this one is).
        cells.Sort((x, y) =>
        {
            var c = y.Weight.CompareTo(x.Weight);
            if (c == 0) c = x.Mesh.CompareTo(y.Mesh);
            return c != 0 ? c : x.Order.CompareTo(y.Order);
        });

        var lamps = new List<LampLight>(Math.Min(cells.Count, MaxLamps));
        foreach (var cell in cells)
        {
            if (lamps.Count >= MaxLamps)
            {
                break;
            }

            var mesh = meshes[cell.Mesh];
            var world = Vector3.Transform(cell.Centre + cell.Normal * Drop, mesh.World);
            var intensity = IntensityAtRef;
            // Which way the lit patch faces in the world. A ceiling fitting keeps its lamp Drop under
            // it; anything else lends it to the ceiling above: at least CeilingGap under the top of
            // its mesh, dimmer (a lit wall panel's own lamp made the wall the brightest thing).
            var facing = Vector3.TransformNormal(cell.Normal, mesh.World);
            facing = facing == Vector3.Zero ? facing : Vector3.Normalize(facing);
            var lifted = facing.Y > -CeilingNy;
            if (lifted)
            {
                world.Y = MathF.Max(world.Y, TopY(mesh) - CeilingGap);
                intensity *= WallScale;
            }

            lamps.Add(new LampLight(world, Colour,
                Math.Clamp(cell.Extent * RangeExtentScale + RangeBase, RangeMin, RangeMax),
                LightFalloff.EnergyAtRef(intensity), Flicker: false, Lifted: lifted,
                Facing: facing, Coherence: cell.Coherence, Model: mesh.Model, Texture: cell.Texture, Entity: mesh.Entity));
        }

        return lamps;
    }

    /// <summary>The fire lamps (fire_effect.gd's light): the sprite's world height shaped by its
    /// kind, intensity clamp(h / 450, 0.18, 0.32) at 150 u, range clamp(h · 9, 500, 2400), at
    /// flame base + h · 0.35 over the foot, flickering.</summary>
    public static List<LampLight> Fires(IEnumerable<FireSprite> fires)
    {
        var lamps = new List<LampLight>();
        foreach (var f in fires)
        {
            if (!IsFire(f.SpriteIndex))
            {
                continue;
            }

            var (kind, hScale) = GodotFires.TryGetValue(f.SpriteIndex, out var info) ? info : (FireKind.Flame, 1f);
            var h = f.WorldHeight * hScale;
            var flameBase = 0f;
            switch (kind)
            {
                case FireKind.Camp:
                    h *= 0.55f;
                    break;
                case FireKind.Wreck:
                    h *= 0.7f;
                    break;
                case FireKind.Barrel:
                    flameBase = h * 0.55f;
                    h *= 0.6f;
                    break;
            }

            var intensity = Math.Clamp(h / FireHeightDiv, FireIntensityMin, FireIntensityMax);
            lamps.Add(new LampLight(f.Base + new Vector3(0f, flameBase + h * FireLightHeight, 0f), FireColour,
                Math.Clamp(h * FireRangeScale, FireRangeMin, FireRangeMax), LightFalloff.EnergyAtRef(intensity),
                Flicker: true, Phase: PhaseOf(f.Seed)));
        }

        return lamps;
    }

    /// <summary>A fire's flicker phase from its seed (Godot hashed it; any fixed spread will do).</summary>
    public static float PhaseOf(int seed) => (uint)seed * 2654435761u % 1000u / 1000f * MathF.Tau;

    /// <summary>A mesh's highest point in the world (its local box's corners).</summary>
    public static float TopY(EmissiveMesh mesh)
    {
        var top = float.MinValue;
        for (var i = 0; i < 8; i++)
        {
            var corner = new Vector3((i & 1) != 0 ? mesh.BoundsMax.X : mesh.BoundsMin.X,
                (i & 2) != 0 ? mesh.BoundsMax.Y : mesh.BoundsMin.Y, (i & 4) != 0 ? mesh.BoundsMax.Z : mesh.BoundsMin.Z);
            top = MathF.Max(top, Vector3.Transform(corner, mesh.World).Y);
        }

        return top;
    }

    /// <summary>main.gd _lit_samples: the lit points of a triangle on a barycentric grid of
    /// clamp(texels across / 5, 2, 14) steps a side, wrapped; fills <paramref name="hits"/> with
    /// their (s, t) and returns the samples taken.</summary>
    public static int LitSamples(LitMask mask, Vector2 u0, Vector2 u1, Vector2 u2, List<Vector2> hits)
    {
        hits.Clear();
        var edge = MathF.Max(MathF.Max((u1 - u0).Length(), (u2 - u0).Length()), (u2 - u1).Length());
        var px = edge * mask.ScanSize;
        var n = Math.Clamp((int)(px / TexelsPerSample), SamplesMin, SamplesMax);
        var total = 0;
        for (var i = 0; i <= n; i++)
        {
            for (var j = 0; j <= n - i; j++)
            {
                float s = i / (float)n, t = j / (float)n;
                total++;
                if (mask.At(u0 + (u1 - u0) * s + (u2 - u0) * t))
                {
                    hits.Add(new Vector2(s, t));
                }
            }
        }

        return total;
    }

    /// <summary>A map's lamps from the data: every variant-1 mesh placed as <see cref="Level"/>
    /// places it (robots left out, as Godot's pass read only the map's meshes), then the fires.
    /// Returns the art lamps (capped) followed by the fire lamps. <paramref name="maxFraction"/>:
    /// see <see cref="MaskOf"/>.</summary>
    public static List<LampLight> ForMap(GameData data, MapFile map, int number, float? maxFraction = null)
    {
        var palette = data.LoadPalette() ?? throw new InvalidDataException("No game palette in the data folder.");
        var litTable = EmissionClassifier.LitTable(palette);
        var banks = new Dictionary<int, TextureBank?>();
        TextureBank? Bank(int n)
        {
            if (!banks.TryGetValue(n, out var b))
            {
                banks[n] = b = data.LoadTextureBank(n);
            }

            return b;
        }

        var masks = new Dictionary<int, LitMask?>();
        LitMask? Mask(int word)
        {
            if (!masks.TryGetValue(word, out var m))
            {
                var b = Bank(word >> 7);
                m = b == null || b.Records.IsEmpty ? null
                    : b.Records[Math.Clamp(word & 0x7F, 0, b.Records.Length - 1)] is { IsEmpty: false } r ? MaskOf(r, palette, litTable, maxFraction) : null;
                masks[word] = m;
            }

            return m;
        }

        var models = new Dictionary<string, Model3D?>(StringComparer.OrdinalIgnoreCase);
        var meshes = new List<EmissiveMesh>();
        foreach (var e in map.Entities)
        {
            if (e.Variant != MapEntityVariant.Mesh || e.NameIndex < 0 || e.NameIndex >= map.Names.Length
                || string.IsNullOrEmpty(map.Names[e.NameIndex]))
            {
                continue;
            }

            var file = map.Names[e.NameIndex] + ".3D";
            if (!models.TryGetValue(file, out var model))
            {
                models[file] = model = data.LoadModel(file);
            }

            if (model == null)
            {
                continue;
            }

            if (FromModel(model, PlacementOf(e), Mask) is { } mesh)
            {
                meshes.Add(mesh with { Model = map.Names[e.NameIndex], Entity = e.FileOffset });
            }
        }

        var lamps = Build(meshes);
        lamps.AddRange(Fires(FireSprites(data, map, number, Bank)));
        return lamps;
    }

    /// <summary>A variant-1 mesh's placement as <see cref="Level"/> builds it: DOS FUN_0014e100's
    /// rotation in the port's axes, at (x, −y, −z).</summary>
    public static Matrix4x4 PlacementOf(MapEntity e) =>
        Matrix4x4.CreateRotationY((float)MapFile.AngleRadians(e.Yaw))
        * Matrix4x4.CreateRotationX((float)MapFile.AngleRadians(e.Pitch))
        * Matrix4x4.CreateRotationZ(-(float)MapFile.AngleRadians(e.Roll))
        * Matrix4x4.CreateTranslation(e.X, -e.Y, -e.Z);

    /// <summary>The map's fire sprites placed as <see cref="Level"/> places its billboards: the foot
    /// on the ground outdoors, <see cref="Level.IndoorSpriteLift"/> under the record's Y indoors,
    /// the height of the record's largest frame at <see cref="Level.SpriteScale"/>.</summary>
    private static IEnumerable<FireSprite> FireSprites(GameData data, MapFile map, int number, Func<int, TextureBank?> bank)
    {
        WldTerrain? wld = null;
        var wldLoaded = false;
        var heights = new Dictionary<int, float?>();
        foreach (var e in map.Entities)
        {
            if (e.Variant != MapEntityVariant.Sprite || e.IsMarker || e.MarkerType != -1 || e.SpriteIndex < 0
                || !IsFire(e.SpriteIndex))
            {
                continue;
            }

            if (!heights.TryGetValue(e.SpriteIndex, out var h))
            {
                heights[e.SpriteIndex] = h = SpriteHeight(data, e.SpriteBank, e.SpriteRecord, bank);
            }

            if (h is not { } height)
            {
                continue;
            }

            if (map.IsOutdoor && !wldLoaded)
            {
                wld = data.LoadWld(number);
                wldLoaded = true;
            }

            var baseY = map.IsOutdoor && wld != null ? (float)wld.HeightAt(e.X, e.Z) : -e.Y - Level.IndoorSpriteLift;
            yield return new FireSprite(e.SpriteIndex, new Vector3(e.X, baseY, -e.Z), height, e.FileOffset);
        }
    }

    /// <summary>A sprite's world height: its record's tallest frame at <see cref="Level.SpriteScale"/>.</summary>
    internal static float? SpriteHeight(GameData data, int bankNo, int record, Func<int, TextureBank?> bank)
    {
        var b = bank(bankNo);
        if (b == null || b.Records.IsEmpty)
        {
            return null;
        }

        var index = Math.Clamp(record, 0, b.Records.Length - 1);
        var r = b.Records[index];
        if (r.IsEmpty)
        {
            return null;
        }

        var h = r.Height;
        if (r.Depth > 1 && data.ReadLoose($"TEXTURE.{bankNo:D3}") is { } bytes)
        {
            foreach (var f in TextureBank.ParseRecordFrames(bytes, index))
            {
                if (!f.IsEmpty) h = Math.Max(h, f.Height);
            }
        }

        return h * Level.SpriteScale;
    }
}
