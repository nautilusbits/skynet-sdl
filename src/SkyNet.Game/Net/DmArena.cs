using System.Numerics;
using SkyNet.Formats;
using SkyNet.Game.Player;

namespace SkyNet.Game.Net;

/// <summary>
/// What a deathmatch needs from a map (the Godot port's dm_game.gd on_level_ready): the spawns,
/// the item spots and the vehicle spots. DOS MP spawn sets are markers 10, 12 ... 28, each facing
/// the nearest marker of the next type, plus the single-player 0/1 pair; SKYNET.EXE v1.01 0x11c200
/// counts markers 100 / 101 / 102 as item / jeep / HK spots: every item (weapons too) lies on a
/// marker 100 (0x11c34d), 101 and 102 hold only the vehicles (0x130b00); types 30-39 come in pairs
/// and fence the map, see <see cref="Vehicles.MapBorder"/>.
/// Spawns stand at marker Y + 0x10 (as <see cref="PlayerStart"/>), lifted out of the terrain;
/// item spots sit on the terrain outdoors and at the marker indoors (dm_game.gd _ground).
/// </summary>
public sealed class DmArena
{
    public static readonly int[] SpawnSets = [0, 10, 12, 14, 16, 18, 20, 22, 24, 26, 28];
    public const int ItemMarker = 100;
    public const int JeepMarker = 101, HkMarker = 102;

    public required int Map { get; init; }
    public required IReadOnlyList<DmSpawn> Spawns { get; init; }
    public required IReadOnlyList<Vector3> ItemSpots { get; init; }
    /// <summary>Where the jeeps park, in marker order (the marker, lifted out of the terrain; the host
    /// drops it on the floor under it).</summary>
    public IReadOnlyList<Vector3> JeepSpots { get; init; } = [];
    public IReadOnlyList<Vector3> HkSpots { get; init; } = [];

    /// <summary>The arena of map <paramref name="mapNumber"/>; null when the map is missing.</summary>
    public static DmArena? Load(GameData data, int mapNumber)
    {
        var map = data.LoadMap(mapNumber);
        if (map == null)
        {
            return null;
        }

        var wld = map.IsOutdoor ? data.LoadWldFor(mapNumber)?.Wld : null;
        return From(map, wld, mapNumber);
    }

    public static DmArena From(MapFile map, WldTerrain? wld, int mapNumber)
    {
        var byType = new Dictionary<int, List<MapEntity>>();
        foreach (var e in map.Entities)
        {
            if (e.Variant == MapEntityVariant.Sprite && e.IsMarker && e.MarkerType >= 0)
            {
                if (!byType.TryGetValue(e.MarkerType, out var l))
                {
                    byType[e.MarkerType] = l = [];
                }

                l.Add(e);
            }
        }

        var spawns = new List<DmSpawn>();
        foreach (var set in SpawnSets)
        {
            if (!byType.TryGetValue(set, out var list))
            {
                continue;
            }

            var facing = byType.TryGetValue(set + 1, out var f) ? f.Select(Feet).ToList() : [];
            foreach (var m in list)
            {
                var p = Feet(m);
                var yaw = 0f;
                if (facing.Count > 0)
                {
                    var d = facing.MinBy(x => Vector3.DistanceSquared(x, p)) - p;
                    if (new Vector2(d.X, d.Z).Length() > 0.1f)
                    {
                        yaw = MathF.Atan2(-d.X, -d.Z);
                    }
                }

                if (wld != null)
                {
                    var ground = (float)wld.HeightAt(p.X, -p.Z);
                    if (p.Y < ground + PlayerStart.GroundLift)
                    {
                        p.Y = ground + PlayerStart.GroundLift;
                    }
                }

                spawns.Add(new DmSpawn(p, yaw));
            }
        }

        List<Vector3> Parking(int type) =>
        [
            .. (byType.GetValueOrDefault(type) ?? []).Select(m =>
            {
                var p = new Vector3(m.X, -m.Y, -m.Z);
                return wld != null ? p with { Y = MathF.Max(p.Y, (float)wld.HeightAt(p.X, -p.Z)) } : p;
            }),
        ];

        List<Vector3> Spots(int type) => [.. (byType.GetValueOrDefault(type) ?? []).Select(m => Ground(m, wld))];

        return new DmArena
        {
            Map = mapNumber,
            Spawns = spawns,
            ItemSpots = Spots(ItemMarker),
            JeepSpots = Parking(JeepMarker),
            HkSpots = Parking(HkMarker),
        };
    }

    private static Vector3 Feet(MapEntity e) => new(e.X, -(e.Y + 0x10), -e.Z);

    private static Vector3 Ground(MapEntity e, WldTerrain? wld)
    {
        var p = new Vector3(e.X, -e.Y, -e.Z);
        return wld != null ? p with { Y = (float)wld.HeightAt(p.X, -p.Z) } : p;
    }
}
