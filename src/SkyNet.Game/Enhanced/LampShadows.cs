using System.Numerics;
using RetroEng.Render;

namespace SkyNet.Game.Enhanced;

/// <summary>
/// Which map lamps cast shadows this frame (<see cref="PointLight.CastsShadow"/>). The engine
/// shadows at most <see cref="SlotsFor"/> lights a frame (<see cref="Lighting.PointShadowCount"/>) and keeps each one's still map (the
/// <see cref="Caster.Static"/> level geometry) while it holds its slot, so the choice is sticky:
/// <code>
///   slots for lamps = SlotsFor(SHADOWS) − the weapons' shadowed lights (the most of the last WeaponHoldSeconds)
///   score = (range / max(distance, CloseShare · range))² · energy · max colour   (the engine's own rank)
///   held lamps stay while they shine; a rival takes the weakest one's slot only above Hysteresis × its score
/// </code>
/// SHADOWS Off: none. Low and High both: a still map costs nothing a frame once drawn; a slot
/// changing hands fades over <see cref="FadeSeconds"/> (the engine's <see cref="Lighting.PointShadowFade"/>).
/// </summary>
public sealed class LampShadows(int cap = Lighting.MostPointShadows)
{
    /// <summary>A shadow fading in or out when its lamp gains or loses a slot (seconds).</summary>
    public const float FadeSeconds = 0.3f;
    /// <summary>A rival's score over the held lamp's that takes its slot (2: about 1.4 × nearer).</summary>
    public const float Hysteresis = 2f;
    /// <summary>A slot stays the weapons' this long after their last shadowed light (the flash between shots).</summary>
    public const float WeaponHoldSeconds = 1f;
    /// <summary>Nearer than this share of its range a lamp scores as if this far (the eye inside its pool).</summary>
    public const float CloseShare = 0.25f;

    private readonly (Vector3 Position, Vector3 Spot)[] _held = new (Vector3, Vector3)[cap];
    private readonly int[] _index = new int[cap];
    private readonly float[] _score = new float[cap];
    private int _count;
    /// <summary>When the weapons last shadowed at least k + 1 lights (seconds; −∞: never).</summary>
    private readonly float[] _weaponsAt = Enumerable.Repeat(float.NegativeInfinity, cap).ToArray();

    /// <summary>The engine's shadowed point lights for SHADOWS: High 16, Low 8 (a phone half;
    /// each slot costs 1.5 MB at Low, 6 MB at High, its still map as much again).</summary>
    public static int SlotsFor(ShadowQuality quality)
    {
        var slots = quality switch
        {
            ShadowQuality.High => 16,
            ShadowQuality.Low => 8,
            _ => 0,
        };
        return OperatingSystem.IsAndroid() ? slots / 2 : slots;
    }
    private float _clock = float.NegativeInfinity;

    /// <summary>A lamp's rank for an eye at <paramref name="eye"/>.</summary>
    public static float Score(in PointLight light, Vector3 eye)
    {
        var distance = MathF.Max(Vector3.Distance(light.Position, eye), CloseShare * light.Range);
        var reach = light.Range / distance;
        return reach * reach * light.Energy * MathF.Max(light.Colour.X, MathF.Max(light.Colour.Y, light.Colour.Z));
    }

    /// <summary>Sets <see cref="PointLight.CastsShadow"/> on the lamps <paramref name="first"/> to
    /// <paramref name="first"/> + <paramref name="count"/> of <paramref name="lights"/> that hold a slot
    /// for <paramref name="eye"/> at <paramref name="seconds"/>; the others' shadowed lights (weapons) keep theirs.</summary>
    public void Mark(List<PointLight> lights, int first, int count, Vector3 eye, float seconds, ShadowQuality quality)
    {
        var slots = Math.Min(cap, SlotsFor(quality));
        var free = Math.Max(0, slots - WeaponReserve(lights, first, count, seconds, slots));
        Rehold(lights, first, count, eye);

        // Fewer slots than held lamps: the weakest go.
        while (_count > free)
        {
            Drop(Weakest());
        }

        // Free slots: the best lamps not held; then one swap if a rival clearly beats the weakest.
        while (_count < free && Best(lights, first, count, eye) is var (i, s) && i >= 0)
        {
            Hold(lights[i], i, s);
        }

        if (_count > 0 && Best(lights, first, count, eye) is var (rival, score) && rival >= 0)
        {
            var weakest = Weakest();
            if (score > Hysteresis * _score[weakest])
            {
                Drop(weakest);
                Hold(lights[rival], rival, score);
            }
        }

        for (var i = first; i < first + count; i++)
        {
            var held = Array.IndexOf(_index, i, 0, _count) >= 0;
            if (lights[i].CastsShadow != held)
            {
                lights[i] = lights[i] with { CastsShadow = held };
            }
        }
    }

    /// <summary>The slots the weapons keep: the most lights they shadowed within the last
    /// <see cref="WeaponHoldSeconds"/> (a clock going back, a new map's, forgets the old).</summary>
    private int WeaponReserve(List<PointLight> lights, int first, int count, float seconds, int slots)
    {
        if (seconds < _clock)
        {
            Array.Fill(_weaponsAt, float.NegativeInfinity);
            _count = 0;
        }

        _clock = seconds;
        var casters = 0;
        for (var i = 0; i < lights.Count; i++)
        {
            casters += (i < first || i >= first + count) && lights[i].CastsShadow ? 1 : 0;
        }

        var reserve = 0;
        for (var k = 0; k < slots; k++)
        {
            if (casters > k)
            {
                _weaponsAt[k] = seconds;
            }

            reserve += seconds - _weaponsAt[k] < WeaponHoldSeconds ? 1 : 0;
        }

        return reserve;
    }

    /// <summary>Finds the held lamps among this frame's (by place and cone) and scores them; one that
    /// is gone or dark loses its slot.</summary>
    private void Rehold(List<PointLight> lights, int first, int count, Vector3 eye)
    {
        for (var h = _count - 1; h >= 0; h--)
        {
            _index[h] = -1;
            for (var i = first; i < first + count; i++)
            {
                var l = lights[i];
                if (l.Position == _held[h].Position && l.SpotDirection == _held[h].Spot && Shines(l))
                {
                    (_index[h], _score[h]) = (i, Score(l, eye));
                    break;
                }
            }

            if (_index[h] < 0)
            {
                Drop(h);
            }
        }
    }

    /// <summary>The best lamp not held (−1: none).</summary>
    private (int Index, float Score) Best(List<PointLight> lights, int first, int count, Vector3 eye)
    {
        var (best, top) = (-1, 0f);
        for (var i = first; i < first + count; i++)
        {
            if (!Shines(lights[i]) || Array.IndexOf(_index, i, 0, _count) >= 0)
            {
                continue;
            }

            var s = Score(lights[i], eye);
            if (s > top)
            {
                (best, top) = (i, s);
            }
        }

        return (best, top);
    }

    private int Weakest()
    {
        var w = 0;
        for (var h = 1; h < _count; h++)
        {
            w = _score[h] < _score[w] ? h : w;
        }

        return w;
    }

    private void Hold(in PointLight light, int index, float score)
    {
        _held[_count] = (light.Position, light.SpotDirection);
        (_index[_count], _score[_count]) = (index, score);
        _count++;
    }

    /// <summary>Removes slot <paramref name="h"/>, the last moved into its place.</summary>
    private void Drop(int h)
    {
        _count--;
        (_held[h], _index[h], _score[h]) = (_held[_count], _index[_count], _score[_count]);
    }

    private static bool Shines(in PointLight l) =>
        l.Energy > 0f && l.Range > 0f && MathF.Max(l.Colour.X, MathF.Max(l.Colour.Y, l.Colour.Z)) > 0f;
}
