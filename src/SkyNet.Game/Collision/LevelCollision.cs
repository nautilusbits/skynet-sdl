using System.Numerics;
using RetroEng.Physics;
using SkyNet.Formats;

namespace SkyNet.Game.Collision;

/// <summary>
/// The collision world of one map, in the level's axes (DOS (x, y, z) → (x, −y, −z)):
/// <list type="bullet">
/// <item>outdoors, the whole WLD heightfield, split NW–SE exactly as <see cref="Terrain"/> draws
/// it and <see cref="WldTerrain.HeightAt"/> computes it;</item>
/// <item>every placed variant-1 mesh (frame 0, both sides), as the Godot port gives every static
/// mesh its trimesh shape (level_loader.gd, main.gd _bake_entity_collision). Movers and
/// destructibles collide where they stand; door leaves as AABB boxes, moving bodies and wrecks
/// wait for the entity logic.</item>
/// </list>
/// Triangle owners are the map record's file offset (−1 for the ground).
/// Needs no renderer, so a headless walk test can build it.
/// </summary>
public static class LevelCollision
{
    /// <summary>What a build took in: meshes placed, their triangles, the models that did not load.</summary>
    public readonly record struct Stats(int Meshes, int Triangles, IReadOnlyList<string> Missing);

    public const float GridCell = 256f;

    /// <summary>The ground as a heightfield in world axes: corner (col, row) at x = col·256,
    /// z = row·256 − 65 536 (row 0 north).</summary>
    public static HeightField Ground(WldTerrain wld) =>
        new(wld.HeightGrid(), WldTerrain.GridW, WldTerrain.GridH, (float)WldTerrain.WorldPerCell,
            new Vector2(0f, -(float)WldTerrain.ZFlipK));

    /// <summary>Builds the world of MAP.<paramref name="number"/>; null when the map is missing.</summary>
    public static (CollisionWorld World, MapFile Map, WldTerrain? Wld, Stats Stats)? Load(GameData data, int number)
    {
        var map = data.LoadMap(number);
        if (map == null)
        {
            return null;
        }

        var wld = map.IsOutdoor ? data.LoadWld(number) : null;
        var (world, stats) = Build(data, map, wld);
        return (world, map, wld, stats);
    }

    /// <summary>The world of a parsed map: its ground (when given) and its placed meshes.</summary>
    /// <param name="skip">Records left out of the static grid (the behaviour's movable and breakable
    /// meshes, which collide through <c>Behaviour.DynamicColliders</c>).</param>
    public static (CollisionWorld World, Stats Stats) Build(GameData data, MapFile map, WldTerrain? wld, Func<MapEntity, bool>? skip = null)
    {
        var world = new CollisionWorld();
        if (wld != null)
        {
            world.Add(Ground(wld));
        }

        var triangles = new List<Triangle>();
        var meshes = 0;
        var models = new Dictionary<string, Vector3[][]?>(StringComparer.OrdinalIgnoreCase);
        var objects = data.Exists("MDMDOBJS.BSA") ? data.OpenArchive("MDMDOBJS.BSA") : null;
        var enemies = data.Exists("MDMDENMS.BSA") ? data.OpenArchive("MDMDENMS.BSA") : null;
        try
        {
            foreach (var e in map.Entities)
            {
                if (e.Variant != MapEntityVariant.Mesh || e.NameIndex < 0 || e.NameIndex >= map.Names.Length || skip?.Invoke(e) == true)
                {
                    continue;
                }

                var name = map.Names[e.NameIndex];
                if (string.IsNullOrEmpty(name))
                {
                    continue;
                }

                if (!models.TryGetValue(name, out var faces))
                {
                    var file = name + ".3D";
                    var bytes = objects?.Read(file) is { Length: > 0 } b ? b : enemies?.Read(file);
                    var model = bytes is { Length: > 0 } ? Model3D.Parse(bytes) : null;
                    models[name] = faces = model != null ? Faces(model) : null;
                }

                if (faces == null)
                {
                    continue;
                }

                meshes++;
                // The placement Level.BuildModels draws with (DOS FUN_0014e100 in Y-up axes).
                var placed = Placement(e);
                foreach (var tri in faces)
                {
                    triangles.Add(new Triangle(Vector3.Transform(tri[0], placed), Vector3.Transform(tri[1], placed),
                        Vector3.Transform(tri[2], placed), e.FileOffset));
                }
            }
        }
        finally
        {
            objects?.Dispose();
            enemies?.Dispose();
        }

        var grid = new TriangleGrid(triangles, GridCell);
        if (grid.Count > 0)
        {
            world.Add(grid);
        }

        var missing = models.Where(m => m.Value == null).Select(m => m.Key + ".3D").Order(StringComparer.Ordinal).ToArray();
        return (world, new Stats(meshes, grid.Count, missing));
    }

    /// <summary>The placement Level.BuildModels draws with (DOS FUN_0014e100 in Y-up axes).</summary>
    public static Matrix4x4 Placement(MapEntity e) =>
        Matrix4x4.CreateRotationY((float)e.YawRadians)
        * Matrix4x4.CreateRotationX((float)e.PitchRadians)
        * Matrix4x4.CreateRotationZ(-(float)e.RollRadians)
        * Matrix4x4.CreateTranslation(e.X, -e.Y, -e.Z);

    /// <summary>The world of a baked map: its ground (when given) and the block's triangles, the
    /// records <paramref name="skip"/> says left out, as <see cref="Build"/> gives it from the raw files.</summary>
    public static (CollisionWorld World, Stats Stats) FromBlock(Baking.MapBlock block, MapFile map, WldTerrain? wld, Func<MapEntity, bool>? skip = null)
    {
        var world = new CollisionWorld();
        if (wld != null)
        {
            world.Add(Ground(wld));
        }

        bool Skipped(int offset) => skip != null && map.TryGetEntityAt(offset, out var e) && skip(e);
        var meshes = block.Colliders.Count(c => !Skipped(c.Offset));
        var grid = new TriangleGrid(block.Triangles(Skipped), GridCell);
        if (grid.Count > 0)
        {
            world.Add(grid);
        }

        return (world, new Stats(meshes, grid.Count, block.CollisionMissing));
    }

    /// <summary>A model's frame-0 triangles in its own Y-up axes.</summary>
    internal static Vector3[][] Faces(Model3D model)
    {
        var list = new List<Vector3[]>();
        foreach (var face in model.Faces)
        {
            foreach (var (a, b, c) in Model3D.TriangleFan(face))
            {
                list.Add([model.Vertices[face.Indices[a]].ToYUp(), model.Vertices[face.Indices[b]].ToYUp(),
                    model.Vertices[face.Indices[c]].ToYUp()]);
            }
        }

        return [.. list];
    }
}
