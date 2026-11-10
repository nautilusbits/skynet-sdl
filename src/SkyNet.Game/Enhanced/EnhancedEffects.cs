using System.Diagnostics;
using System.Globalization;
using System.Numerics;
using RetroEng.Diagnostics;
using RetroEng.Physics;
using RetroEng.Render;
using SkyNet.Formats;
using SkyNet.Game.Behaviour;
using SkyNet.Game.Menu;
using SkyNet.Game.Weapons;

namespace SkyNet.Game.Enhanced;

/// <summary>
/// The enhanced look's effects as the game and the editor show them, one code for both: ENHANCED
/// OPTIONS put on the level and the weapons' view (<see cref="Apply"/>), and each frame's point lights
/// (<see cref="Gather"/>: the level's map lights with the lamp posts, its art lamps and fires, live from
/// its behaviour, and the weapons' flashes, blasts and bolts). The flames, embers, smoke columns and dust
/// draw with the level (<see cref="Level.Draw"/>), impact dust and explosions with the weapons' view.
/// Nothing here runs in the original look.
/// </summary>
public sealed class EnhancedEffects
{
    private readonly LightGather _gather = new();
    private readonly LampShadows _lampShadows = new();
    private LevelLights? _lights;
    private Level? _lightsOf;
    private bool _lightsWithPosts;
    private BehaviourLights? _states;
    private LevelBehaviour? _statesOf;
    private long _stormClock;

    /// <summary>A mod's storm's thunder over the outdoor maps (<see cref="Weather"/>): the game ticks
    /// it itself, <see cref="Gather"/> does for the editor.</summary>
    public Storm Storm { get; } = new();

    /// <summary>A mod's clouds, haze, dust and wind over the outdoor maps (<see cref="Weather"/>), as
    /// <see cref="Storm"/>; its cover is the rain's too.</summary>
    public Air Air { get; } = new();

    /// <summary>A mod's rain's hiss over the outdoor maps (<see cref="Weather"/>), as <see cref="Storm"/>.</summary>
    public Rain Rain { get; }

    /// <summary>A mod's dust, drawn from the air's view.</summary>
    public Dust Dust { get; } = new();

    /// <summary>The engine's weather (rain, lightning), the clouds and the haze on the renderer's light
    /// and depth cue (the frame build's); its strikes' thunder goes to <see cref="Storm"/>.</summary>
    public WeatherSky Sky { get; } = new();

    /// <summary>The lamps' glow through the haze and dust (the frame build's).</summary>
    public LampGlow Glow { get; } = new();

    private readonly AirView _airView = new();

    public EnhancedEffects()
    {
        Rain = new Rain(Air.Cover);
        Sky.Struck += Storm.Post;
    }

    /// <summary>The shown level's lights (null until the first <see cref="Gather"/> in the enhanced look).</summary>
    public LevelLights? Lights => _lights;

    /// <summary>ENHANCED OPTIONS' light budget and particles on <paramref name="level"/> and the weapons'
    /// view; with <paramref name="effects"/> off (the editor's View > Effects) no flames, smoke, embers or
    /// dust. Called before <see cref="Level.Apply"/>.</summary>
    public static void Apply(Level level, MenuSettings settings, WeaponRenderer? weapons = null, bool effects = true)
    {
        level.LightBudget = settings.LightBudget;
        level.Particles = effects ? settings.Particles : ParticleLevel.Off;
        level.Flames = effects;
        if (weapons != null)
        {
            weapons.Particles = settings.Particles;
            weapons.FireMask = level.FireMaskPalette;
        }
    }

    /// <summary>Sets <see cref="Renderer.Lights"/> for a frame seen from <paramref name="eye"/> at
    /// <paramref name="seconds"/>: none in the original look (no <see cref="Renderer.Lighting"/>) or with
    /// no level. <paramref name="behaviour"/> switches the map's lights (null: as placed),
    /// <paramref name="weapons"/> adds its lights, <paramref name="lampPosts"/> off leaves out the lamp
    /// posts' pools (<see cref="MastLamps"/>). A mod's weather shows too, no sound; its dust settles on
    /// <paramref name="world"/>'s cover (null: none drawn).</summary>
    public void Gather(Renderer renderer, Level? level, GameData data, Vector3 eye, float seconds,
        LevelBehaviour? behaviour = null, WeaponSystem? weapons = null, bool lampPosts = true, CollisionWorld? world = null)
    {
        if (renderer.Lighting == null || level == null)
        {
            renderer.Lights = default;
            Storm.Tick(0f, null, level, StormSky.Indoor, eye);
            Air.Tick(0f, Weather.None, level, StormSky.Indoor, eye, null);
            return;
        }

        _gather.Begin();
        Collect(_gather.All, level, data, seconds, eye, renderer.Lighting.ShadowQuality, PlayKind.Campaign, behaviour, weapons, lampPosts);
        renderer.Lights = _gather.Finish(eye, level.Far, level.LightBudget);

        // The storm by the wall clock (a long stall counts as a quarter second).
        var now = Stopwatch.GetTimestamp();
        var dt = _stormClock == 0 ? 0f : MathF.Min((float)Stopwatch.GetElapsedTime(_stormClock, now).TotalSeconds, 0.25f);
        _stormClock = now;
        var sky = level.Outdoor ? StormSky.Outdoor : StormSky.Indoor;
        var weather = Weather.Current;
        Storm.Tick(dt, weather.Storm, level, sky, eye);
        Air.Tick(dt, weather, level, sky, eye, world);
        Air.Capture(_airView);
        var shown = sky == StormSky.Outdoor ? weather : Weather.None;
        Sky.Apply(renderer, new SkyFrame(shown, dt, eye, Storm.TakeStrike()), _airView, level, level.Particles);
        level.Wind = _airView.Wind;
        Dust.Draw(renderer, _airView, eye, level.Particles);
        Glow.Draw(renderer, _airView, _gather.All, eye, level.Far);
    }

    /// <summary>Sets <see cref="Renderer.Lights"/> from <paramref name="lights"/> (<see cref="Collect"/>,
    /// on the simulation's thread): the most important for <paramref name="eye"/> within
    /// <paramref name="reach"/>, up to <paramref name="budget"/>.</summary>
    public void Apply(Renderer renderer, IReadOnlyList<PointLight> lights, Vector3 eye, float reach, int budget)
    {
        _gather.Begin();
        _gather.All.AddRange(lights);
        renderer.Lights = _gather.Finish(eye, reach, budget);
    }

    /// <summary>Adds the frame's candidate lights to <paramref name="into"/>: the level's map lights
    /// (switched by <paramref name="behaviour"/>), art lamps and fires, and the weapons' lights
    /// (shadowed at <paramref name="shadows"/> High: the frame's <see cref="Lighting.ShadowQuality"/>,
    /// no default, so a caller can't leave them unshadowed by omission, GitHub #17; none in a
    /// <paramref name="play"/> network game). The lamps that matter most to <paramref name="eye"/> take the
    /// shadowed slots the weapons leave (<see cref="LampShadows"/>).</summary>
    public void Collect(List<PointLight> into, Level level, GameData data, float seconds, Vector3 eye, ShadowQuality shadows, PlayKind play,
        LevelBehaviour? behaviour = null, WeaponSystem? weapons = null, bool lampPosts = true)
    {
        if (_lights == null || !ReferenceEquals(_lightsOf, level) || _lightsWithPosts != lampPosts)
        {
            var clock = Stopwatch.StartNew();
            // The lamps are baked in the level's block (EmissiveLamps.ForMap at bake time).
            var map = lampPosts ? LevelLights.MapLightsOf(level.Map, data, level.Number) : LevelLights.MapLightsOf(level.Map);
            _lights = new LevelLights(map, level.Block.Lamps, level.Number);
            (_lightsOf, _lightsWithPosts) = (level, lampPosts);
            Log.Info("Render", string.Create(CultureInfo.InvariantCulture,
                $"Lights on MAP.{level.Number:D3}: {_lights.MapLights.Count} map, {_lights.Lamps.Count(l => !l.Flicker)} art, {_lights.Lamps.Count(l => l.Flicker)} fire, {_lights.SpotCount} spot ({clock.Elapsed.TotalMilliseconds:0} ms)"));
        }

        if (behaviour != null && !ReferenceEquals(_statesOf, behaviour))
        {
            (_states, _statesOf) = (new BehaviourLights(behaviour), behaviour);
        }

        var first = into.Count;
        _lights.Append(into, seconds, behaviour == null ? null : _states);
        var lamps = into.Count - first;
        if (weapons != null)
        {
            GameLights.Append(into, weapons, shadows: shadows, play: play);
        }

        _lampShadows.Mark(into, first, lamps, eye, seconds, shadows);
    }

    /// <summary>Forgets the level's lights (its level is gone; the next <see cref="Gather"/> builds them).</summary>
    public void Forget() => (_lights, _lightsOf, _states, _statesOf) = (null, null, null, null);
}
