using System.Numerics;
using RetroEng.Physics;
using SkyNet.Formats;

namespace SkyNet.Game.Player;

/// <summary>Where the soldier starts on a map, as the Godot port places him (main.gd
/// _place_player): the feet at marker 0's Y + 0x10 (the MAP data's floor level), facing the
/// nearest marker 1, lifted out of the terrain, and nudged out of any prop the marker sits in.</summary>
public static class PlayerStart
{
    /// <summary>The Godot port's lift over the ground when a marker lies under it.</summary>
    public const float GroundLift = 4f;
    private static readonly float[] Rings = [0f, 50f, 100f, 200f, 320f, 460f, 640f];

    /// <summary>The start's feet and yaw (radians, about +Y, 0 looking −Z); false when the map has
    /// no start marker (then above the middle of its meshes).</summary>
    public static (Vector3 Feet, float Yaw, bool Found) Find(MapFile map, WldTerrain? wld)
    {
        Vector3? start = null;
        var facing = new List<Vector3>();
        var sum = Vector3.Zero;
        var n = 0;
        foreach (var e in map.Entities)
        {
            if (e.Variant == MapEntityVariant.Sprite && e.MarkerType == 0)
            {
                start ??= new Vector3(e.X, -(e.Y + 0x10), -e.Z);
            }
            else if (e.Variant == MapEntityVariant.Sprite && e.MarkerType == 1)
            {
                facing.Add(new Vector3(e.X, -(e.Y + 0x10), -e.Z));
            }
            else if (e.Variant == MapEntityVariant.Mesh)
            {
                sum += new Vector3(e.X, -e.Y, -e.Z);
                n++;
            }
        }

        if (start is not { } s)
        {
            var centre = n > 0 ? sum / n : new Vector3(128 * 256f, 0f, 128 * 256f - (float)WldTerrain.ZFlipK);
            var ground = wld != null ? (float)wld.HeightAt(centre.X, -centre.Z) : centre.Y;
            return (new Vector3(centre.X, ground + 1000f, centre.Z), 0f, false);
        }

        var yaw = 0f;
        if (facing.Count > 0)
        {
            var d = facing.MinBy(f => Vector3.DistanceSquared(f, s)) - s;
            if (new Vector2(d.X, d.Z).Length() > 0.1f)
            {
                yaw = MathF.Atan2(-d.X, -d.Z);
            }
        }

        if (wld != null)
        {
            var ground = (float)wld.HeightAt(s.X, -s.Z);
            if (s.Y < ground + GroundLift)
            {
                s.Y = ground + GroundLift;
            }
        }

        return (s, yaw, true);
    }

    /// <summary>Arriving through a doorway (PlrSetPosMarker, FUN_00121f72): marker set
    /// <paramref name="markerSet"/> is the first marker of that type, facing the nearest marker of
    /// type set + 1. A set below 0, or one the map lacks, is the map's own start.</summary>
    public static (Vector3 Feet, float Yaw, bool Found) Find(MapFile map, WldTerrain? wld, int markerSet)
    {
        if (markerSet <= 0)
        {
            return Find(map, wld);
        }

        Vector3? at = null;
        var facing = new List<Vector3>();
        foreach (var e in map.Entities)
        {
            if (e.Variant != MapEntityVariant.Sprite || !e.IsMarker)
            {
                continue;
            }

            if (e.MarkerType == markerSet)
            {
                at ??= new Vector3(e.X, -(e.Y + 0x10), -e.Z);
            }
            else if (e.MarkerType == markerSet + 1)
            {
                facing.Add(new Vector3(e.X, -(e.Y + 0x10), -e.Z));
            }
        }

        if (at is not { } s)
        {
            return Find(map, wld);
        }

        var yaw = 0f;
        if (facing.Count > 0)
        {
            var d = facing.MinBy(f => Vector3.DistanceSquared(f, s)) - s;
            if (new Vector2(d.X, d.Z).Length() > 0.1f)
            {
                yaw = MathF.Atan2(-d.X, -d.Z);
            }
        }

        if (wld != null)
        {
            var ground = (float)wld.HeightAt(s.X, -s.Z);
            if (s.Y < ground + GroundLift)
            {
                s.Y = ground + GroundLift;
            }
        }

        return (s, yaw, true);
    }

    /// <summary>The nearest spot to <paramref name="feet"/> where the body stands free (the Godot
    /// port's _find_clear_spawn): rings out to 640 units, each spot in sight of the marker at body
    /// height and with a floor under it.</summary>
    public static Vector3 Clear(CollisionWorld world, SphereBody body, Vector3 feet)
    {
        var chest = new Vector3(0f, 60f, 0f);
        foreach (var ring in Rings)
        {
            var steps = ring < 1f ? 1 : 12;
            for (var i = 0; i < steps; i++)
            {
                var a = MathF.Tau * i / steps;
                var p = feet + new Vector3(MathF.Cos(a) * ring, 0f, MathF.Sin(a) * ring);
                if (world.Overlaps(body, p + new Vector3(0f, 1f, 0f)))
                {
                    continue;
                }

                if (ring > 0f)
                {
                    var to = p - feet;
                    if (world.Raycast(feet + chest, to, to.Length(), out _))
                    {
                        continue;
                    }

                    if (!world.Raycast(p + chest, -Vector3.UnitY, 460f, out _))
                    {
                        continue;
                    }
                }

                return p;
            }
        }

        return feet;
    }
}
