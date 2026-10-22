using System.Numerics;
using RetroEng.Render;
using RetroEng.Render.Lights;
using SkyNet.Game.Enhanced;

namespace SkyNet.Edit;

/// <summary>
/// The asset viewers' own scene (a <see cref="TargetScene"/> per view target, RetroEng 0.25): the subject at the
/// origin, lit by a neutral studio light in the enhanced look (a key light and a soft ambient, no haze, stars or
/// clouds: the backdrop is the clear colour), unlit in the original look. The window's shadow quality is kept
/// (another one rebuilds the shared shadow map).
/// <code>
///   frame Lighting null ─► TargetScene(null): the palette look
///   frame Lighting      ─► key (upper left, camera side) + sky/ground fill, frame's shadows, glow, relief
///   effect lights       ─► fire: a warm lamp over its foot; explosion: a flash fading with its age
/// </code></summary>
public static class ViewerStudio
{
    /// <summary>Where a viewer's subject stands.</summary>
    public static readonly Vector3 Subject = Vector3.Zero;

    // The key light's way (from the upper left, on the camera's side: the camera starts at −Z) and colour (linear).
    private static readonly Vector3 KeyDirection = Vector3.Normalize(new Vector3(0.5f, -1f, 0.7f));
    private static readonly Vector3 Key = new(1.05f, 1f, 0.95f);
    // The soft fill: light from above, less from below.
    private static readonly Vector3 Sky = new(0.34f, 0.35f, 0.38f), Ground = new(0.16f, 0.16f, 0.17f);
    private static readonly Vector3 Blast = LevelLights.Linear(GameLights.ExplosionSrgb);

    /// <summary>The viewer's scene: unlit without <paramref name="frame"/> (the original look), else the studio
    /// light with the frame's shadow quality, its shadows reaching <paramref name="reach"/> (the camera's far end).</summary>
    public static TargetScene Scene(Lighting? frame, float reach, ReadOnlyMemory<PointLight> lights = default)
    {
        if (frame == null)
        {
            return new TargetScene(null);
        }

        var studio = new Lighting(KeyDirection, Key, Sky, Ground, frame.ShadowQuality, MathF.Min(frame.ShadowDistance, reach),
            frame.Glow, Vector4.Zero, 0f)
        {
            Up = Vector3.UnitY,
            ShadowReach = reach,
            ShadowFilter = frame.ShadowFilter,
            OcclusionRadius = frame.OcclusionRadius,
            OcclusionStrength = frame.OcclusionStrength,
            GlowThreshold = frame.GlowThreshold,
            GlowIntensity = frame.GlowIntensity,
            SurfaceRelief = frame.SurfaceRelief,
            ReliefStrength = frame.ReliefStrength,
            ReliefFill = frame.ReliefFill,
        };
        return new TargetScene(studio) { Lights = lights };
    }

    /// <summary>The point lights an effect previewed at <see cref="Subject"/> casts (none for most):
    /// a fire's lamp as the game's (<see cref="EmissiveLamps.Fires"/>), an explosion's flash.</summary>
    public static PointLight[] EffectLights(string effect, float scale, float age)
    {
        if (effect is EffectTable.Fire or EffectTable.Embers)
        {
            var intensity = Math.Clamp(scale / EmissiveLamps.FireHeightDiv, EmissiveLamps.FireIntensityMin, EmissiveLamps.FireIntensityMax);
            var range = Math.Clamp(scale * EmissiveLamps.FireRangeScale, EmissiveLamps.FireRangeMin, EmissiveLamps.FireRangeMax);
            return [new PointLight(Subject + Vector3.UnitY * (scale * EmissiveLamps.FireLightHeight), EmissiveLamps.FireColour,
                range, LightFalloff.EnergyAtRef(intensity))];
        }

        if (effect is not (EffectTable.Explosion or EffectTable.ExplosionDust or EffectTable.ExplosionDebris or EffectTable.ExplosionSmoke))
        {
            return [];
        }

        // Fading over the preview's run (the game's steps down its frames).
        var fade = MathF.Max(0f, 1f - age / EffectPreview.Duration(effect));
        return [new PointLight(Subject + Vector3.UnitY * (scale * 0.5f), Blast,
            MathF.Max(scale * GameLights.ExplosionRangeScale, GameLights.ExplosionRangeMin),
            LightFalloff.EnergyAtRef(GameLights.ExplosionIntensity * fade))];
    }
}
