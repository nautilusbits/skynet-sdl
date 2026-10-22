using System.Numerics;
using RetroEng.Render;
using SkyNet.Formats;
using SkyNet.Game.Menu;

namespace SkyNet.Game.Enhanced;

/// <summary>The kinds of light a SkyNET map gets in the enhanced look.</summary>
public enum LightPreset { Night, Dusk, Interior }

/// <summary>
/// The enhanced look's light per map (<see cref="Renderer.Lighting"/>): night maps lit by the
/// moon, the dusk maps (<see cref="Level.DuskMaps"/>) by a low warm sun where their dome's sky is
/// brightest, interiors by an even ambient and (later) their own lamps. The haze is the level's
/// depth cue (<see cref="Level.Apply"/>), so the lighting's own stays off.
/// <code>
///   night:    moon (pale blue) along −MoonDirection ─┐
///   dusk:     warm sun from the dome's bright texels ─┼─ + sky / ground hemisphere ─► Lighting
///   interior: no sun, INDOOR_AMBIENT_ENHANCED ───────┘    exposure = BRIGHTNESS × ExposureMood
/// </code>
/// </summary>
public static class LookLighting
{
    /// <summary>The Godot port's outdoor ambient (main.gd OUTDOOR_AMBIENT, git 3dfe3b6^): the reference
    /// the night and dusk hemispheres are scaled from.</summary>
    public static readonly Vector3 OutdoorAmbient = new(0.55f, 0.55f, 0.65f);
    /// <summary>The Godot port's ENHANCED interior ambient (main.gd INDOOR_AMBIENT_ENHANCED).</summary>
    public static readonly Vector3 IndoorAmbient = new(0.36f, 0.37f, 0.43f);

    /// <summary>Moonlight: pale blue (linear).</summary>
    public static readonly Vector3 MoonLight = new(0.50f, 0.58f, 0.80f);
    /// <summary>The night sky's light from above (dark sky blue) and the ground's from below.</summary>
    public static readonly Vector3 NightSky = OutdoorAmbient * new Vector3(0.55f, 0.60f, 0.75f);
    public static readonly Vector3 NightGround = OutdoorAmbient * new Vector3(0.28f, 0.28f, 0.32f);

    /// <summary>The dusk sun (warm) and its hemisphere.</summary>
    public static readonly Vector3 DuskSunLight = new(1.10f, 0.70f, 0.42f);
    public static readonly Vector3 DuskSky = OutdoorAmbient * new Vector3(0.66f, 0.56f, 0.58f);
    public static readonly Vector3 DuskGround = OutdoorAmbient * new Vector3(0.34f, 0.28f, 0.24f);

    /// <summary>The dusk sun's height is kept between these (degrees): a sun on the horizon would
    /// leave every slope facing away black and throw shadows past the far clip.</summary>
    public const float MinSunElevation = 15f, MaxSunElevation = 60f;

    /// <summary>The ambient occlusion's radius (SkyNET units: about a doorway's depth).</summary>
    public const float OcclusionRadius = 70f;
    /// <summary>The end of the sun's first shadow cascade (view depth, units); the last ends at
    /// the level's far clip (<see cref="Lighting.ShadowReach"/>).</summary>
    public const float ShadowDistance = 600f;
    /// <summary>The glow everything bright gets a little of (post.hlsl).</summary>
    public const float Glow = 0.08f;

    /// <summary>The engine's own occlusion strength and glow intensity (what ON keeps).</summary>
    private static readonly Lighting EngineDefaults = new(Vector3.UnitY, Vector3.Zero, Vector3.Zero, Vector3.Zero, ShadowQuality.Off, 0f, 0f, Vector4.Zero, 0f);

    /// <summary>The mood: the enhanced look darker than the Godot port's light it started from, the
    /// night moodier. The light all around (sky / ground hemisphere, an interior's ambient) and the
    /// moon / sun are scaled down, the exposure a little; the point lights (lamps, fires, muzzle
    /// flashes, explosions) keep their energy, so they stand out more, and the HUD (the canvas) is
    /// not exposed at all. BRIGHTNESS and AMBIENT still scale on top.</summary>
    public const float NightAmbientMood = 0.5f, DuskAmbientMood = 0.7f, InteriorAmbientMood = 0.45f;
    public const float MoonMood = 0.7f, DuskSunMood = 0.85f, ExposureMood = 0.85f;

    /// <summary>The night sky's star field (<see cref="StarField"/>): its brightness, and the disc
    /// round the moon kept clear (radians: the moon's half width, 57 texels / 200 / 2, and a margin).</summary>
    public const float StarBrightness = 1f, MoonClearRadius = 0.16f;

    /// <summary>The fraction of the dome's samples taken as its brightest (the sun's glow).</summary>
    private const float BrightFraction = 0.01f;
    /// <summary>Samples per triangle edge on the dome.</summary>
    private const int SampleSteps = 6;

    /// <summary>A map's preset: indoors interior; outdoors dusk on the dome's maps, else night.</summary>
    public static LightPreset PresetOf(int map, bool outdoor) =>
        !outdoor ? LightPreset.Interior : Level.IsDusk(map) ? LightPreset.Dusk : LightPreset.Night;

    /// <summary>The way the moonlight goes: away from the moon (DOS's light vector (−6400, 4000,
    /// −3200), FUN_0011b933, is −1/64 of the moon sprite's).</summary>
    public static Vector3 MoonLightDirection() => -Level.MoonDirection();

    /// <summary>The level's enhanced light with the ENHANCED OPTIONS (shadows, ambient, occlusion,
    /// glow, surface relief) and BRIGHTNESS as the exposure.</summary>
    public static Lighting For(Level level, MenuSettings settings)
    {
        var (strength, fill) = ReliefOf(settings.Relief);
        return For(level, settings.Gamma, settings.Shadows, settings.AmbientScale, settings.AmbientOcclusion, settings.Glow)
            with
            {
                Clouds = SkyClouds(level),
                SurfaceRelief = settings.Relief != ReliefLevel.Off, ReliefStrength = strength, ReliefFill = fill,
                // Phones: 4 shadow fetches a pixel, not 9 (their texture rate and bandwidth).
                ShadowFilter = OperatingSystem.IsAndroid() ? ShadowFilter.Narrow : ShadowFilter.Wide,
            };
    }

    /// <summary>The map's own thin cloud deck: a dusk map's (<see cref="SunsetSky.CloudsOf"/>), a night
    /// map's (<see cref="StarrySky.CloudsOf"/>); null indoors.</summary>
    private static CloudLayer? SkyClouds(Level level)
    {
        var preset = PresetOf(level.Number, level.Outdoor);
        if (preset == LightPreset.Night && level.NightLook is { } night)
        {
            return StarrySky.CloudsOf(night, Level.MoonDirection());
        }

        if (preset != LightPreset.Dusk || level.DuskLook is not { } look)
        {
            return null;
        }

        return SunsetSky.CloudsOf(look, SunsetSky.SunOf(level.DuskSunDirection, MoonLightDirection(), look.SunElevation));
    }

    /// <summary>SURFACE RELIEF's level as the relief maps' tilt scale and the ambient's fill tilt
    /// (<see cref="Lighting.ReliefStrength"/>, <see cref="Lighting.ReliefFill"/>); off: no relief.</summary>
    public static (float Strength, float Fill) ReliefOf(ReliefLevel level) => level switch
    {
        ReliefLevel.Off => (0f, 0f),
        ReliefLevel.Low => (1f, 1f),
        ReliefLevel.Medium => (1.6f, 1.8f),
        _ => (2.5f, 3f),
    };

    /// <summary>The level's enhanced light: its preset, the dusk sun found on its dome, its far
    /// clip as the shadows' reach; <paramref name="exposure"/> from BRIGHTNESS (1: as is).</summary>
    public static Lighting For(Level level, float exposure, ShadowQuality shadows = ShadowQuality.High,
        float ambient = 1f, bool occlusion = true, bool glow = true) =>
        For(PresetOf(level.Number, level.Outdoor), level.DuskSunDirection, level.Far, exposure, shadows, ambient, occlusion, glow);

    /// <param name="shadows">The sun's shadows outdoors (interiors have no sun: off).</param>
    /// <param name="ambient">The sky / ground hemisphere scaled (ENHANCED OPTIONS' AMBIENT, 1: as is).</param>
    /// <param name="occlusion">The ambient occlusion on (ENHANCED OPTIONS' AMBIENT OCCLUSION).</param>
    /// <param name="glow">The glow on (ENHANCED OPTIONS' GLOW).</param>
    public static Lighting For(LightPreset preset, Vector3? duskSun, float far, float exposure,
        ShadowQuality shadows = ShadowQuality.High, float ambient = 1f, bool occlusion = true, bool glow = true)
    {
        var (direction, sun, sky, ground, mood) = preset switch
        {
            LightPreset.Night => (MoonLightDirection(), MoonLight * MoonMood, NightSky, NightGround, NightAmbientMood),
            LightPreset.Dusk => (duskSun ?? MoonLightDirection(), DuskSunLight * DuskSunMood, DuskSky, DuskGround, DuskAmbientMood),
            _ => (-Vector3.UnitY, Vector3.Zero, IndoorAmbient, IndoorAmbient, InteriorAmbientMood),
        };
        return new Lighting(direction, sun, sky * (ambient * mood), ground * (ambient * mood),
            preset == LightPreset.Interior ? ShadowQuality.Off : shadows, ShadowDistance, glow ? Glow : 0f, Vector4.Zero, 0f)
        {
            Up = Vector3.UnitY,
            Exposure = exposure * ExposureMood,
            Stars = preset == LightPreset.Night ? new StarField(StarBrightness, Vector3.UnitY, Level.MoonDirection(), MoonClearRadius) : null,
            ShadowReach = far,
            OcclusionRadius = OcclusionRadius,
            OcclusionStrength = occlusion ? EngineDefaults.OcclusionStrength : 0f,
            GlowIntensity = glow ? EngineDefaults.GlowIntensity : 0f,
        };
    }

    /// <summary>A billboard's light: it carries no normal (it turns to the camera), so it takes the
    /// hemisphere at a level face (half sky, half ground) and no sun, as an sRGB tint for
    /// <see cref="Shading.Sprite"/> (linear light up to 1; brighter is clamped).</summary>
    public static Vector4 SpriteTint(Lighting lighting)
    {
        var light = (lighting.Sky + lighting.Ground) * 0.5f;
        return new Vector4(Srgb.FromLinearValue(light.X), Srgb.FromLinearValue(light.Y), Srgb.FromLinearValue(light.Z), 1f);
    }

    /// <summary>The way the dusk sunlight goes: from the luminance-weighted direction of the
    /// brightest <see cref="BrightFraction"/> of the dome's texels (sampled on a barycentric grid of
    /// every textured face; the dome is centred on the eye, so a point's position is its direction),
    /// its height kept within <see cref="MinSunElevation"/>..<see cref="MaxSunElevation"/>.
    /// Null when the dome has no textured face.</summary>
    public static Vector3? DuskSunDirection(Model3D dome, Func<int, int, TextureRecord?> record, Palette palette)
    {
        var samples = new List<(float Luma, Vector3 Direction)>();
        foreach (var face in dome.Faces)
        {
            if (face.IsSolidColour || record(face.TextureBank, face.TextureRecord) is not { } t || t.Width <= 0 || t.Height <= 0)
            {
                continue;
            }

            var uvs = dome.FaceUvs(face, t.Width, t.Height);
            foreach (var (a, b, c) in Model3D.TriangleFan(face))
            {
                var (pa, pb, pc) = (dome.Vertices[face.Indices[a]].ToYUp(), dome.Vertices[face.Indices[b]].ToYUp(), dome.Vertices[face.Indices[c]].ToYUp());
                for (var i = 0; i <= SampleSteps; i++)
                {
                    for (var j = 0; j <= SampleSteps - i; j++)
                    {
                        float u = (float)i / SampleSteps, v = (float)j / SampleSteps, w = 1f - u - v;
                        var position = pa * w + pb * u + pc * v;
                        if (position.LengthSquared() < 1e-6f)
                        {
                            continue;
                        }

                        var uv = uvs[a] * w + uvs[b] * u + uvs[c] * v;
                        var x = Wrap((int)MathF.Floor(uv.X * t.Width), t.Width);
                        var y = Wrap((int)MathF.Floor(uv.Y * t.Height), t.Height);
                        var index = t.Pixels[y * t.Width + x];
                        if (index == 0)
                        {
                            continue;
                        }

                        var (r, g, bl) = palette[index];
                        samples.Add((0.299f * r + 0.587f * g + 0.114f * bl, Vector3.Normalize(position)));
                    }
                }
            }
        }

        if (samples.Count == 0)
        {
            return null;
        }

        samples.Sort((p, q) => q.Luma.CompareTo(p.Luma));
        var take = Math.Max(1, (int)(samples.Count * BrightFraction));
        var sum = Vector3.Zero;
        for (var k = 0; k < take; k++)
        {
            sum += samples[k].Direction * samples[k].Luma;
        }

        if (sum.LengthSquared() < 1e-12f)
        {
            return null;
        }

        return -ClampElevation(Vector3.Normalize(sum));
    }

    /// <summary>A direction towards the sun with its height kept within the allowed band.</summary>
    public static Vector3 ClampElevation(Vector3 toSun)
    {
        var flat = new Vector2(toSun.X, toSun.Z);
        var heading = flat.LengthSquared() > 1e-12f ? Vector2.Normalize(flat) : Vector2.UnitX;
        var elevation = MathF.Asin(Math.Clamp(toSun.Y, -1f, 1f)) * 180f / MathF.PI;
        var e = Math.Clamp(elevation, MinSunElevation, MaxSunElevation) * MathF.PI / 180f;
        return new Vector3(heading.X * MathF.Cos(e), MathF.Sin(e), heading.Y * MathF.Cos(e));
    }

    /// <summary>The level's depth cue as the enhanced look's linear haze: towards the palette's haze
    /// colour (<see cref="HazeTable.HazeIndex"/>; Fog takes it sRGB and the shader decodes it to
    /// linear) from the cue start to the far clip, nothing beyond (DOS's view distance).</summary>
    public static Vector4 HazeColour(Palette palette)
    {
        var (r, g, b) = palette[HazeTable.HazeIndex];
        return new Vector4(r / 255f, g / 255f, b / 255f, 1f);
    }

    private static int Wrap(int value, int size) => ((value % size) + size) % size;
}
