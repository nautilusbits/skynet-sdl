using System.Numerics;
using RetroEng.Render;
using RetroEng.Render.Lights;
using SkyNet.Formats;

namespace SkyNet.Game.Enhanced;

/// <summary>
/// Floodlights for the enhanced look's lamp posts. An outdoor street lamp is a tall billboard
/// (MAP.210's base: 204/22, a post ~400 u high with two floodlight heads) with a variant-2 map light
/// placed at its foot, ~25 u over the ground; the heads' lenses aren't lit in the art, so no art lamp
/// marks them. Lit where the map puts it, the light (omni, 0.15–0.9 at 150 u) only made a smudge round
/// the foot. A map light standing at a tall sprite is lifted to the sprite's head and lit as a spot
/// down, a pool round the post on the ground and the walls near it:
/// <code>
///   outdoor map light ≤ <see cref="Reach"/> u across from a sprite ≥ <see cref="MinHeight"/> u high
///     ─► at the sprite's foot + height × <see cref="HeadHeight"/>, spot straight down (a billboard
///        turns its heads to the viewer, so the pool is round), cone <see cref="Inner"/>°..<see cref="Outer"/>°,
///        <see cref="IntensityAtRef"/> at 150 u, range height × <see cref="RangePerHeight"/> (at least the map's)
/// </code>
/// It keeps the map light's colour; the behaviour still switches and dims it through the map light
/// (<see cref="LevelLights.Append"/>), and a <c>lights/lamps.json</c> row for the light replaces it.
/// </summary>
public static class MastLamps
{
    public const float MinHeight = 250f, Reach = 96f, HeadHeight = 0.92f;
    public const float Inner = 30f, Outer = 62f;
    public const float IntensityAtRef = 12f;
    public const float RangePerHeight = 6f, RangeMax = 3200f;

    /// <summary>A sprite standing in the world: its foot, its world height and its index (bank &lt;&lt; 7 | record).</summary>
    public readonly record struct Post(Vector3 Foot, float Height, int SpriteIndex);

    /// <summary>A map's non-marker sprites at least <see cref="MinHeight"/> high, placed as
    /// <see cref="Level"/> places its billboards outdoors (the foot on the ground).</summary>
    public static List<Post> PostsOf(GameData data, MapFile map, int number)
    {
        var posts = new List<Post>();
        if (!map.IsOutdoor)
        {
            return posts;
        }

        var banks = new Dictionary<int, TextureBank?>();
        TextureBank? Bank(int n)
        {
            if (!banks.TryGetValue(n, out var b))
            {
                banks[n] = b = data.LoadTextureBank(n);
            }

            return b;
        }

        var heights = new Dictionary<int, float?>();
        WldTerrain? wld = null;
        var wldLoaded = false;
        foreach (var e in map.Entities)
        {
            if (e.Variant != MapEntityVariant.Sprite || e.IsMarker || e.MarkerType != -1 || e.SpriteIndex < 0)
            {
                continue;
            }

            if (!heights.TryGetValue(e.SpriteIndex, out var h))
            {
                heights[e.SpriteIndex] = h = EmissiveLamps.SpriteHeight(data, e.SpriteBank, e.SpriteRecord, Bank);
            }

            if (h is not { } height || height < MinHeight)
            {
                continue;
            }

            if (!wldLoaded)
            {
                wld = data.LoadWld(number);
                wldLoaded = true;
            }

            var footY = wld != null ? (float)wld.HeightAt(e.X, e.Z) : -e.Y;
            posts.Add(new Post(new Vector3(e.X, footY, -e.Z), height, e.SpriteIndex));
        }

        return posts;
    }

    /// <summary>The map lights with their posts' floodlights (<see cref="MapLight.Heads"/>) set.</summary>
    public static List<MapLight> Apply(List<MapLight> lights, IReadOnlyList<Post> posts)
    {
        for (var i = 0; i < lights.Count; i++)
        {
            if (Heads(lights[i], posts) is { } heads)
            {
                lights[i] = lights[i] with { Heads = heads };
            }
        }

        return lights;
    }

    /// <summary>A map light's floodlight when it stands at a post (above), else null.</summary>
    public static PointLight[]? Heads(in MapLight light, IReadOnlyList<Post> posts)
    {
        var p = light.Position;
        Post? found = null;
        var best = float.MaxValue;
        foreach (var post in posts)
        {
            var across = new Vector2(post.Foot.X - p.X, post.Foot.Z - p.Z).Length();
            if (across <= Reach && across < best && p.Y < post.Foot.Y + post.Height * HeadHeight)
            {
                (found, best) = (post, across);
            }
        }

        if (found is not { } lamp)
        {
            return null;
        }

        var head = new Vector3(lamp.Foot.X, lamp.Foot.Y + lamp.Height * HeadHeight, lamp.Foot.Z);
        var range = MathF.Max(light.Range, MathF.Min(lamp.Height * RangePerHeight, RangeMax));
        return [new PointLight(head, light.Colour, range, LightFalloff.EnergyAtRef(IntensityAtRef)).AsSpot(-Vector3.UnitY, Inner, Outer)];
    }
}
