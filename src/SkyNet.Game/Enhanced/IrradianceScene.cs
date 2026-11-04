using System.Numerics;
using RetroEng.Render;
using SkyNet.Formats;
using SkyNet.Game.Baking;

namespace SkyNet.Game.Enhanced;

/// <summary>What an irradiance bake of one map takes: its static triangles with their albedo and
/// emission, its steady lamps, the volume and the sky.</summary>
/// <param name="Triangles">The triangles in <paramref name="Meshes"/>.</param>
public sealed record IrradianceInput(int Map, LightPreset Preset, IReadOnlyList<BakeMesh> Meshes, PointLight[] Lamps,
    IrradianceBakeSettings Settings, int Triangles);

/// <summary>
/// A map's irradiance bake input (<see cref="IrradianceBake"/>), from its baked block:
/// <code>
///   block.World (props, walls; no doors, movers, robots, sprites) ─┐
///   block.Terrain outdoors, cut to the built area ──────────────────┼─► BakeMesh: front = the drawn side,
///       albedo = the record's mean linear colour, emission = its lit texels' (EmissionClassifier)
///   LevelLights: map lights that start on, art lamps, fires at their mean flicker ─► lamps
///   LightPreset: night = moon + night sky, dusk = sun + dusk sky, interior = no sun, a dim sky
///   box of the built area (+ margin outdoors) ─► spacing ≥ MinSpacing, ≤ ProbeBudget probes
/// </code>
/// The doors and movers are left out (dynamic): the bake sees every door open.
/// </summary>
public static class IrradianceScene
{
    /// <summary>The closest probes (world units): about a metre (the soldier's eye is 75 u up).</summary>
    public const float MinSpacing = 48f;
    /// <summary>The spacing is a multiple of this (units).</summary>
    public const float SpacingStep = 8f;
    /// <summary>Probes at most: about 2 MB saved at 25 bytes a probe.</summary>
    public const int ProbeBudget = 80_000;
    /// <summary>Outdoors, the ground and air kept round the placed meshes (units).</summary>
    public const float OutdoorMargin = 512f;
    /// <summary>Outdoors, the air kept above the highest mesh (units).</summary>
    public const float OutdoorHeadroom = 256f;
    /// <summary>Rays a probe and bounces: the engine's defaults, measured ≲ 1 min a map.</summary>
    public const int Rays = 128, Bounces = 2;
    /// <summary>Interiors: what a ray leaving the map sees (a gap to the void), the old ambient dimmed.</summary>
    public const float InteriorSkyShare = 0.25f;
    /// <summary>The emissive art's own glow: its lit texels' mean colour times their share, scaled.</summary>
    public const float EmissionGain = 1f;

    private const string DomeArchive = "MDMDOBJS.BSA";
    private const string DomeModel = "SKY_SKY.3D";

    /// <summary>The bake input of MAP.<paramref name="number"/> from its block; null when it has no triangles.</summary>
    public static IrradianceInput? Build(GameData data, MapBlock block, MapFile map, int number)
    {
        var palette = data.LoadPalette() ?? throw new InvalidDataException("No game palette in the data folder.");
        var textures = new TextureStore(null, data);
        var litTable = EmissionClassifier.LitTable(palette);
        var preset = LookLighting.PresetOf(number, map.IsOutdoor);

        // The volume: the placed meshes' box, outdoors with ground and air round it.
        if (Box(block.World.SelectMany(b => b.Vertices).Select(v => v.Position)) is not var (min, max))
        {
            return null;
        }

        if (map.IsOutdoor)
        {
            (min, max) = Outdoor(min, max, block.Terrain);
        }

        var meshes = new List<BakeMesh>();
        var triangles = 0;
        foreach (var bucket in block.World)
        {
            var (albedo, emission) = SurfaceOf(bucket.Key, textures, palette, litTable);
            if (Mesh(bucket.Vertices, bucket.Indices, null) is { } mesh)
            {
                meshes.Add(new BakeMesh(mesh.Positions, mesh.Indices, albedo) { Emission = emission });
                triangles += mesh.Indices.Length / 3;
            }
        }

        if (map.IsOutdoor && block.Terrain is { } ground && Ground(ground, textures, palette, min, max) is { } terrain)
        {
            meshes.Add(terrain);
            triangles += terrain.Indices.Length / 3;
        }

        // The frame's lamps as they light it: the bake is unscaled, the frame scales the bounce
        // (LookLighting.BounceOf, Lighting.IrradianceScale).
        var lights = new LevelLights(LevelLights.MapLightsOf(map, data, number), block.Lamps, number);
        var lamps = new List<PointLight>();
        lights.AppendSteady(lamps);

        var duskSun = preset == LightPreset.Dusk ? DuskSun(data, textures, palette) : null;
        var settings = SettingsFor(preset, min, max, SpacingFor(min, max), duskSun);
        return new IrradianceInput(number, preset, meshes, [.. lamps], settings, triangles);
    }

    /// <summary>The bake's volume, light and effort for a preset (<see cref="LookLighting.For(LightPreset, Vector3?, float, float, ShadowQuality, float, bool, bool)"/>'s
    /// sun and sky at their moods; interiors no sun and a dim sky).</summary>
    public static IrradianceBakeSettings SettingsFor(LightPreset preset, Vector3 min, Vector3 max, float spacing, Vector3? duskSun)
    {
        var (direction, sun, sky) = preset switch
        {
            LightPreset.Night => (LookLighting.MoonLightDirection(), LookLighting.MoonLight * LookLighting.MoonMood,
                LookLighting.NightSky * LookLighting.NightAmbientMood),
            LightPreset.Dusk => (duskSun ?? LookLighting.MoonLightDirection(), LookLighting.DuskSunLight * LookLighting.DuskSunMood,
                LookLighting.DuskSky * LookLighting.DuskAmbientMood),
            _ => (Vector3.Zero, Vector3.Zero, LookLighting.IndoorAmbient * (LookLighting.InteriorAmbientMood * InteriorSkyShare)),
        };
        return new IrradianceBakeSettings(min, max, spacing)
        {
            SunDirection = direction,
            Sun = sun,
            Sky = sky,
            Rays = Rays,
            Bounces = Bounces,
            MaxDegreeOfParallelism = -1,
        };
    }

    /// <summary>The probes' spacing for a box: at least <see cref="MinSpacing"/>, a multiple of
    /// <see cref="SpacingStep"/>, wide enough that the probes stay within <see cref="ProbeBudget"/>
    /// and <see cref="IrradianceVolume.MaxProbesPerAxis"/> an axis.</summary>
    public static float SpacingFor(Vector3 min, Vector3 max)
    {
        var size = Vector3.Max(max - min, Vector3.Zero);
        var spacing = MathF.Max(MinSpacing, MathF.Ceiling(MathF.Cbrt(size.X * size.Y * size.Z / ProbeBudget) / SpacingStep) * SpacingStep);
        while (ProbesOf(size, spacing) is var (x, y, z)
               && ((long)x * y * z > ProbeBudget || Math.Max(x, Math.Max(y, z)) > IrradianceVolume.MaxProbesPerAxis))
        {
            spacing += SpacingStep;
        }

        return spacing;
    }

    /// <summary>Probes an axis over <paramref name="size"/> (the engine's rule: from the minimum every
    /// spacing up to the maximum, at least two).</summary>
    public static (int X, int Y, int Z) ProbesOf(Vector3 size, float spacing)
    {
        int Axis(float extent) => Math.Max(IrradianceVolume.MinProbesPerAxis, (int)MathF.Ceiling(extent / spacing) + 1);
        return (Axis(size.X), Axis(size.Y), Axis(size.Z));
    }

    /// <summary>A record's mean linear colour, its transparent texels (index 0) left out; black when none is left.</summary>
    public static Vector3 Albedo(ReadOnlySpan<byte> pixels, Palette palette)
    {
        var sum = Vector3.Zero;
        var count = 0;
        foreach (var index in pixels)
        {
            if (index == 0)
            {
                continue;
            }

            sum += Linear(palette, index);
            count++;
        }

        return count == 0 ? Vector3.Zero : sum / count;
    }

    /// <summary>A record's glow: its lit texels' mean colour (linear) times their share, when the
    /// classifier calls it a lamp; zero otherwise. E.g. a strip light 10 % lit white: 0.1.</summary>
    public static Vector3 Emission(ReadOnlySpan<byte> pixels, Palette palette, bool[] litTable)
    {
        var r = EmissionClassifier.Classify(pixels, palette, litTable);
        if (!r.Emissive)
        {
            return Vector3.Zero;
        }

        var c = r.MeanLitColour;
        return new Vector3(Srgb.ToLinear(c.X), Srgb.ToLinear(c.Y), Srgb.ToLinear(c.Z)) * (r.LitFraction * EmissionGain);
    }

    /// <summary>A bucket's albedo and emission: its record's, or its flat palette colour (no glow).</summary>
    private static (Vector3 Albedo, Vector3 Emission) SurfaceOf(BlockKey key, TextureStore textures, Palette palette, bool[] litTable)
    {
        if (key.Texture is not { } t)
        {
            return (Linear(palette, key.Flat), Vector3.Zero);
        }

        if (textures.Record(t.Bank, t.Record) is not { } record)
        {
            return (Vector3.Zero, Vector3.Zero);
        }

        var pixels = record.Pixels.AsSpan();
        return (Albedo(pixels, palette), Emission(pixels, palette, litTable));
    }

    /// <summary>Triangles of renderer vertices as bake positions and indices, each wound so that
    /// cross(b − a, c − a) points to its drawn side (its vertices' normal); <paramref name="keep"/>
    /// filters triangles by their centre (null: all). Null when none is kept.</summary>
    public static (Vector3[] Positions, int[] Indices)? Mesh(ReadOnlySpan<Vertex> vertices, ReadOnlySpan<uint> indices, Func<Vector3, bool>? keep)
    {
        var positions = new Vector3[vertices.Length];
        for (var i = 0; i < vertices.Length; i++)
        {
            positions[i] = vertices[i].Position;
        }

        var kept = new List<int>(indices.Length);
        for (var t = 0; t + 2 < indices.Length; t += 3)
        {
            int a = (int)indices[t], b = (int)indices[t + 1], c = (int)indices[t + 2];
            var (pa, pb, pc) = (positions[a], positions[b], positions[c]);
            if (keep != null && !keep((pa + pb + pc) / 3f))
            {
                continue;
            }

            var normal = Vertex.UnpackNormal(vertices[a].Normal) + Vertex.UnpackNormal(vertices[b].Normal) + Vertex.UnpackNormal(vertices[c].Normal);
            var flip = Vector3.Dot(Vector3.Cross(pb - pa, pc - pa), normal) < 0f;
            kept.AddRange(flip ? [a, c, b] : [a, b, c]);
        }

        return kept.Count == 0 ? null : (positions, [.. kept]);
    }

    /// <summary>The terrain inside the volume's footprint, each triangle its tile's mean colour.</summary>
    private static BakeMesh? Ground(Terrain.TerrainGeometry ground, TextureStore textures, Palette palette, Vector3 min, Vector3 max)
    {
        var inside = (Vector3 p) => p.X >= min.X && p.X <= max.X && p.Z >= min.Z && p.Z <= max.Z;
        if (Mesh(ground.Vertices, ground.Indices, inside) is not var (positions, indices))
        {
            return null;
        }

        // A triangle's tile from its atlas slot (8 × 8 slots of layout.Slot texels).
        var bank = textures.Bank(Terrain.TileBank);
        var layout = Terrain.Layout(bank);
        var columns = layout.Side / layout.Slot;
        var tiles = new Vector3[layout.Sizes.Length];
        for (var t = 0; t < tiles.Length; t++)
        {
            tiles[t] = bank != null && t < bank.Records.Length && !bank.Records[t].IsEmpty ? Albedo(bank.Records[t].Pixels.AsSpan(), palette) : Vector3.Zero;
        }

        var albedo = new Vector3[indices.Length / 3];
        for (var t = 0; t < albedo.Length; t++)
        {
            var uv = (ground.Vertices[indices[t * 3]].Uv + ground.Vertices[indices[t * 3 + 1]].Uv + ground.Vertices[indices[t * 3 + 2]].Uv) / 3f;
            var col = Math.Clamp((int)(uv.X * columns), 0, columns - 1);
            var row = Math.Clamp((int)(uv.Y * columns), 0, columns - 1);
            albedo[t] = tiles[Math.Min(row * columns + col, tiles.Length - 1)];
        }

        return new BakeMesh(positions, indices, Vector3.Zero) { TriangleAlbedo = albedo };
    }

    /// <summary>An outdoor volume: the placed meshes' footprint grown by <see cref="OutdoorMargin"/>,
    /// from the lowest ground in it up to <see cref="OutdoorHeadroom"/> over the highest of mesh and ground.</summary>
    public static (Vector3 Min, Vector3 Max) Outdoor(Vector3 min, Vector3 max, Terrain.TerrainGeometry? ground)
    {
        var lo = new Vector3(min.X - OutdoorMargin, min.Y, min.Z - OutdoorMargin);
        var hi = new Vector3(max.X + OutdoorMargin, max.Y, max.Z + OutdoorMargin);
        if (ground != null)
        {
            foreach (var v in ground.Vertices)
            {
                var p = v.Position;
                if (p.X < lo.X || p.X > hi.X || p.Z < lo.Z || p.Z > hi.Z)
                {
                    continue;
                }

                lo.Y = MathF.Min(lo.Y, p.Y);
                hi.Y = MathF.Max(hi.Y, p.Y);
            }
        }

        return (lo, hi with { Y = hi.Y + OutdoorHeadroom });
    }

    /// <summary>The box round <paramref name="points"/>; null when there is none.</summary>
    public static (Vector3 Min, Vector3 Max)? Box(IEnumerable<Vector3> points)
    {
        var min = new Vector3(float.MaxValue);
        var max = new Vector3(float.MinValue);
        var any = false;
        foreach (var p in points)
        {
            min = Vector3.Min(min, p);
            max = Vector3.Max(max, p);
            any = true;
        }

        return any ? (min, max) : null;
    }

    /// <summary>The dusk sun's way as the level finds it on its dome (<see cref="Level.DuskSunDirection"/>).</summary>
    private static Vector3? DuskSun(GameData data, TextureStore textures, Palette palette)
    {
        if (!data.Exists(DomeArchive))
        {
            return null;
        }

        using var objects = data.OpenArchive(DomeArchive);
        return objects.Read(DomeModel) is { Length: > 0 } b && Model3D.Parse(b) is { } dome
            ? LookLighting.DuskSunDirection(dome, textures.Record, palette)
            : null;
    }

    private static Vector3 Linear(Palette palette, int index)
    {
        var (r, g, b) = palette[index];
        return new Vector3(Srgb.ToLinear(r / 255f), Srgb.ToLinear(g / 255f), Srgb.ToLinear(b / 255f));
    }
}
