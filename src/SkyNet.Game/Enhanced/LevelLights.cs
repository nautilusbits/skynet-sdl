using System.Numerics;
using RetroEng.Render;
using RetroEng.Render.Lights;
using SkyNet.Formats;

namespace SkyNet.Game.Enhanced;

/// <summary>What the level's behaviour says about a variant-2 light now (<see cref="Behaviour.LevelBehaviour"/>
/// implements it through <see cref="BehaviourLights"/>): whether it tracks the light (a light with a
/// light act), and then whether it is on and its intensity word.</summary>
public interface ILightStates
{
    bool Tracks(int offset);
    bool On(int offset);
    int Intensity(int offset);
}

/// <summary>The live behaviour's light state: toggle (act 0x01), flicker / strobe (0x02 / 0x03) and
/// the intensity acts 0x0C+ (<see cref="Behaviour.LevelBehaviour.LightOn"/>,
/// <see cref="Behaviour.LevelBehaviour.LightIntensity"/>).</summary>
public sealed class BehaviourLights(Behaviour.LevelBehaviour behaviour) : ILightStates
{
    public bool Tracks(int offset) => behaviour.HasLight(offset);
    public bool On(int offset) => behaviour.LightOn(offset);
    public int Intensity(int offset) => behaviour.LightIntensity(offset);
}

/// <summary>One variant-2 map light as the enhanced look lights it.</summary>
/// <param name="Offset">Its MAP file offset (the behaviour's key).</param>
/// <param name="Position">World position (Y up).</param>
/// <param name="Colour">Linear colour.</param>
/// <param name="Range">From the enable word (<see cref="LightFalloff.MapLightRange"/>).</param>
/// <param name="Energy">The energy while its intensity word is <see cref="IntensityWord"/>.</param>
/// <param name="IntensityWord">The intensity word the map gives it (the intensity acts scale from it).</param>
/// <param name="StartsOn">The enable word is positive.</param>
/// <param name="Heads">A lamp post's floodlight lit in its place (<see cref="MastLamps"/>; null: the light itself).</param>
public readonly record struct MapLight(int Offset, Vector3 Position, Vector3 Colour, float Range, float Energy, int IntensityWord, bool StartsOn,
    PointLight[]? Heads = null);

/// <summary>
/// The level's own point lights in the enhanced look, made once per map and lit each frame:
/// <code>
///   MAP variant 2 (every map) ── range |enable|·10, energy intensity/14 ──┬─ indoors: capped at 0.3 at 150 u
///                                                                         └─ outdoors: street lamps, ×0.35, 0.15–0.9, warm;
///                                                                            at a lamp post: a spot down from its head (MastLamps)
///      live: on/off (toggle, flicker, strobe) and the intensity acts ×(1 ± f) from the behaviour
///   EmissiveLamps.ForMap ── art lamps (steady) + fire lamps (× FlickerScale each frame)
///      art lamps facing one way lit as spots (LampSpots); a mod's lights/lamps.json overrides any light
/// </code>
/// Ported from the Godot port's ENHANCED _place_map_lights / _light_step (main.gd, action_system.gd,
/// removed in git 3dfe3b6). Godot later raised an interior light's cap for a fixture with room
/// around it (_fit_map_light_energy, a ray probe in six directions): not ported, every interior
/// light keeps the flat cap. Godot's colours are sRGB (its Color); they are linear here.
/// </summary>
public sealed class LevelLights
{
    /// <summary>main.gd MAP_LIGHT_MAX_ENHANCED: an interior map light's cap, intensity at 150 u.</summary>
    public const float MapLightMaxEnhanced = 0.3f;
    /// <summary>main.gd OUTDOOR_LAMP_SCALE and its clamp: the street lamps are accents.</summary>
    public const float OutdoorLampScale = 0.35f, OutdoorLampMin = 0.15f, OutdoorLampMax = 0.9f;
    /// <summary>main.gd LAMP_COLOR (sRGB): the outdoor lamps' sodium warmth.</summary>
    public static readonly Vector3 OutdoorLampSrgb = new(1f, 0.86f, 0.62f);
    /// <summary>The classifier's lit-fraction cap for the art lamps: 0.25 instead of
    /// <see cref="EmissionClassifier.EmissionMax"/> (MAP.214's corridor strips are 22.9 % lit read
    /// 1:1; Godot read them shrunk and took them).</summary>
    public const float ArtLampMaxFraction = 0.25f;

    private readonly MapLight[] _map;
    private readonly LampLight[] _lamps;
    /// <summary>Each lamp's light as lit (spot or omni, overrides applied); a fire's energy flickers on it.</summary>
    private readonly PointLight[] _lampLights;
    /// <summary>Each map light's override applied to its light at its own energy (null: none); its
    /// energy is scaled live by the behaviour.</summary>
    private readonly PointLight?[] _mapLights;

    /// <param name="mapNumber">The map (for the overrides that name one; −1: those never match).</param>
    /// <param name="overrides">The lamp overrides (null: the mounted mods', <see cref="LampSpots.Current"/>).</param>
    public LevelLights(IReadOnlyList<MapLight> map, IReadOnlyList<LampLight> lamps, int mapNumber = -1, IReadOnlyList<LampOverride>? overrides = null)
    {
        _map = [.. map];
        _lamps = [.. lamps];
        overrides ??= LampSpots.Current;
        _lampLights = new PointLight[_lamps.Length];
        for (var i = 0; i < _lamps.Length; i++)
        {
            var l = _lamps[i];
            var light = LampSpots.Derive(l, Linear(l.Colour));
            foreach (var row in LampSpots.For(overrides, mapNumber, l.Entity, l.Flicker ? null : l.Model, l.Texture))
            {
                light = LampSpots.Apply(light, row, l.Facing);
            }

            _lampLights[i] = light;
        }

        _mapLights = new PointLight?[_map.Length];
        for (var i = 0; i < _map.Length; i++)
        {
            var m = _map[i];
            PointLight? light = null;
            foreach (var row in LampSpots.For(overrides, mapNumber, m.Offset, null, -1))
            {
                light = LampSpots.Apply(light ?? new PointLight(m.Position, m.Colour, m.Range, m.Energy), row);
            }

            _mapLights[i] = light;
        }

        SpotCount = _lampLights.Count(l => l.IsSpot) + _mapLights.Count(l => l is { IsSpot: true });
    }

    /// <summary>The lights lit as spots (art lamps derived or overridden, map lights overridden).</summary>
    public int SpotCount { get; }

    public IReadOnlyList<MapLight> MapLights => _map;
    /// <summary>The art lamps (steady) and the fire lamps (<see cref="LampLight.Flicker"/>).</summary>
    public IReadOnlyList<LampLight> Lamps => _lamps;

    /// <summary>The lights of a loaded map: its variant-2 lights and the lamps of its art and fires.</summary>
    public static LevelLights ForMap(GameData data, MapFile map, int number, float? maxFraction = ArtLampMaxFraction) =>
        new(MapLightsOf(map, data, number),
            EmissiveLamps.ForMap(data, map, number, maxFraction), number);

    /// <summary>Every variant-2 light of a map, the ones that start off included (a chain may switch
    /// them on): indoors at most <see cref="MapLightMaxEnhanced"/> at 150 u, white; outdoors the
    /// street lamps' ×<see cref="OutdoorLampScale"/> in <see cref="OutdoorLampMin"/>..<see cref="OutdoorLampMax"/>, warm.</summary>
    public static List<MapLight> MapLightsOf(MapFile map)
    {
        return MapLightsOf(map, null, -1);
    }

    /// <summary><see cref="MapLightsOf(MapFile)"/> with an outdoor map's lamp posts lit from their
    /// heads (<see cref="MastLamps"/>; <paramref name="data"/> null: as placed).</summary>
    public static List<MapLight> MapLightsOf(MapFile map, GameData? data, int number)
    {
        var lights = Placed(map);
        return data != null && map.IsOutdoor ? MastLamps.Apply(lights, MastLamps.PostsOf(data, map, number)) : lights;
    }

    private static List<MapLight> Placed(MapFile map)
    {
        var lights = new List<MapLight>();
        var outdoorColour = Linear(OutdoorLampSrgb);
        foreach (var e in map.Entities)
        {
            if (e.Variant != MapEntityVariant.Light)
            {
                continue;
            }

            var dos = LightFalloff.MapLightEnergy(e.LightIntensity);
            var intensity = map.IsOutdoor
                ? Math.Clamp(dos * OutdoorLampScale, OutdoorLampMin, OutdoorLampMax)
                : MathF.Min(dos, MapLightMaxEnhanced);
            lights.Add(new MapLight(e.FileOffset, new Vector3(e.X, -e.Y, -e.Z), map.IsOutdoor ? outdoorColour : Vector3.One,
                LightFalloff.MapLightRange(e.LightEnable), LightFalloff.EnergyAtRef(intensity), e.LightIntensity, e.LightEnable > 0));
        }

        return lights;
    }

    /// <summary>A map light's energy now: off → 0; else its energy scaled as its intensity word was
    /// by the intensity acts (Godot multiplied the light's energy by the same 1 ± f).</summary>
    public static float EnergyNow(in MapLight light, ILightStates? states)
    {
        if (states == null || !states.Tracks(light.Offset))
        {
            return light.StartsOn ? light.Energy : 0f;
        }

        if (!states.On(light.Offset))
        {
            return 0f;
        }

        var word = states.Intensity(light.Offset);
        return light.IntensityWord > 0 && word >= 0 ? light.Energy * word / light.IntensityWord : light.Energy;
    }

    /// <summary>Adds the level's lights that shine now to <paramref name="into"/>: the map lights by
    /// <paramref name="states"/>, the art lamps, the fires flickering at <paramref name="seconds"/>.</summary>
    public void Append(List<PointLight> into, float seconds, ILightStates? states)
    {
        for (var i = 0; i < _map.Length; i++)
        {
            var m = _map[i];
            var energy = EnergyNow(m, states);
            if (energy > 0f && _mapLights[i] == null && m.Heads is { } heads)
            {
                foreach (var h in heads)
                {
                    into.Add(m.Energy > 0f ? h with { Energy = h.Energy * energy / m.Energy } : h);
                }
            }
            else if (energy > 0f)
            {
                into.Add(_mapLights[i] is { } o
                    ? o with { Energy = m.Energy > 0f ? o.Energy * energy / m.Energy : o.Energy }
                    : new PointLight(m.Position, m.Colour, m.Range, energy));
            }
        }

        for (var i = 0; i < _lamps.Length; i++)
        {
            var light = _lampLights[i];
            into.Add(_lamps[i].Flicker ? light with { Energy = light.Energy * EmissiveLamps.FlickerScale(seconds, _lamps[i].Phase) } : light);
        }
    }

    /// <summary>A Godot (sRGB) colour in linear light.</summary>
    public static Vector3 Linear(Vector3 srgb) => new(Srgb.ToLinear(srgb.X), Srgb.ToLinear(srgb.Y), Srgb.ToLinear(srgb.Z));
}
