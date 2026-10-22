using System.Numerics;
using RetroEng.Render;
using SkyNet.Data;
using SkyNet.Game.Menu;
using SkyNet.Game.Weapons;

namespace SkyNet.Game.Enhanced;

/// <summary>
/// One effect of <see cref="EffectTable"/> shown on its own at a point, drawn by the game's own
/// effect code with a given row (the editor's live preview of an unsaved edit). The effects are
/// functions of their age, so spawning sets the place and age 0, advancing moves the age on (a
/// one-off effect loops after <see cref="Duration"/>), drawing queues the particles.
/// <code>
///   Spawn("explosion", at) ─► Advance(dt) per frame ─► Draw(renderer, row, eye)
///                                                       └─► ImpactEffects / FireFlames / Atmosphere / Storm
/// </code>
/// Nothing needs the GPU: <see cref="Draw"/> only calls <see cref="IFrameRecorder.DrawParticles"/>.
/// </summary>
public sealed class EffectPreview
{
    /// <summary>The preview's size (units): a fire's height, a blast's radius, a smoke source's height.</summary>
    public const float DefaultScale = 120f;

    /// <summary>A preview's lightning strikes this far off (metres).</summary>
    private const float StrikeMetres = 3000f;
    /// <summary>A strike's flashes (start in seconds, strength).</summary>
    private static readonly (float At, float Strength)[] StrikePulses = [(0f, 1f), (0.1f, 0.7f)];
    private const int Seed = 7;
    private const int PreviewPieces = 10;

    private readonly FireFlames _fire = new();
    private readonly Atmosphere _atmosphere = new();
    private readonly ImpactEffects _impacts = new();
    private readonly Storm _storm = new(Seed);
    private readonly StormStrike _strike = new(Seed, StrikeMetres, 0f, StrikePulses, Storm.Bolt.Make(new Random(Seed)));
    private readonly ImpactPieces.Piece[] _pieces = new ImpactPieces.Piece[PreviewPieces];

    /// <summary>The effect shown (a name of <see cref="EffectTable.Names"/>).</summary>
    public string Effect { get; private set; } = EffectTable.Fire;

    /// <summary>Where it is (a fire's foot, a blast's or strike's centre).</summary>
    public Vector3 At { get; private set; }

    /// <summary>Seconds since <see cref="Spawn"/> (looped for a one-off effect).</summary>
    public float Age { get; private set; }

    /// <summary>The effect's size (units, <see cref="DefaultScale"/>).</summary>
    public float Scale { get; set; } = DefaultScale;

    /// <summary>How long a one-off effect lasts before it starts again (seconds); a lasting one
    /// (fire, smoke, motes) <see cref="float.PositiveInfinity"/>.</summary>
    public static float Duration(string effect) => effect.ToLowerInvariant() switch
    {
        EffectTable.Explosion or EffectTable.ExplosionDust or EffectTable.ExplosionDebris or EffectTable.ExplosionSmoke => BlastCloud.MaxLife,
        EffectTable.ImpactDust or EffectTable.ImpactSparks => BulletPuff.MaxLife,
        EffectTable.Sparks => 1f,
        EffectTable.Lightning => 1f,
        _ => float.PositiveInfinity,
    };

    /// <summary>Starts <paramref name="effect"/> at <paramref name="at"/>, age 0. Throws
    /// <see cref="KeyNotFoundException"/> for a name not in <see cref="EffectTable.Names"/>.</summary>
    public void Spawn(string effect, Vector3 at)
    {
        _ = EffectTable.Reads(effect);
        (Effect, At, Age) = (effect.ToLowerInvariant(), at, 0f);
    }

    /// <summary>Moves the effect on by <paramref name="seconds"/>; a one-off one starts again past its <see cref="Duration"/>.</summary>
    public void Advance(float seconds)
    {
        Age += Math.Max(seconds, 0f);
        var duration = Duration(Effect);
        if (Age >= duration)
        {
            Age %= duration;
        }
    }

    /// <summary>Queues the effect seen from <paramref name="eye"/> as <paramref name="row"/> says
    /// (its <see cref="EffectRow.Effect"/> must be <see cref="Effect"/>'s), the other effects as
    /// <see cref="EffectTable.Current"/>, lit by <paramref name="lighting"/> (a view target's own; null: the
    /// recorder's). Nothing without either (the original look).</summary>
    public void Draw(IFrameRecorder renderer, EffectRow row, Vector3 eye, ParticleLevel level = ParticleLevel.High, Lighting? lighting = null)
    {
        if (!string.Equals(row.Effect, Effect, StringComparison.OrdinalIgnoreCase))
        {
            throw new ArgumentException($"Row {row.Effect} is not the preview's {Effect}", nameof(row));
        }

        lighting ??= renderer.Lighting;
        if (lighting == null)
        {
            return;
        }

        var effects = EffectTable.Current.With(row);
        var ambient = (lighting.Sky + lighting.Ground) * 0.5f;
        switch (Effect)
        {
            case EffectTable.Fire or EffectTable.Embers:
                _fire.Draw(renderer, [new FireFlame(At, Scale * 0.6f, Scale, 0f, 1.3f)], level, eye, Age, false, effects);
                break;
            case EffectTable.Smoke or EffectTable.SmokeColumns or EffectTable.Motes:
                DrawAir(renderer, lighting, effects, eye, level);
                break;
            case EffectTable.Explosion or EffectTable.ExplosionDust or EffectTable.ExplosionDebris or EffectTable.ExplosionSmoke:
                _impacts.DrawBlasts(renderer, [new BlastPose(At, Scale, Seed, Age)], level, ambient, effects);
                break;
            case EffectTable.ImpactDust or EffectTable.ImpactSparks:
                _impacts.DrawPuffs(renderer, [new PuffPose(At, Vector3.UnitY, -Vector3.UnitY, true, Scale / DefaultScale, Seed, Age)],
                    level, ambient, effects);
                break;
            case EffectTable.Sparks:
                _impacts.DrawSparks(renderer, Pieces(), level, effects);
                break;
            case EffectTable.Lightning:
                DrawStrike(renderer, effects, eye);
                break;
        }
    }

    // Smoke from a source at At (indoors: no horizon), or the smoke columns round At (outdoors,
    // past a far clip set so At lies on it); the motes only for themselves.
    private void DrawAir(IFrameRecorder renderer, Lighting lighting, EffectTable effects, Vector3 eye, ParticleLevel level)
    {
        foreach (var other in (string[])[EffectTable.Smoke, EffectTable.SmokeColumns, EffectTable.Motes])
        {
            if (other != Effect)
            {
                effects = effects.With(effects[other] with { Low = 0, High = 0 });
            }
        }

        var columns = Effect == EffectTable.SmokeColumns;
        var far = columns ? Vector3.Distance(eye, At) / Atmosphere.HorizonDistance : 0f;
        _atmosphere.Draw(renderer, lighting, level, columns, eye, Age, [new SmokeSource(At, Scale)], 0, far, null, null, effects);
    }

    // Pieces thrown up and out of At, each where it is at Age (gravity on).
    private ReadOnlySpan<ImpactPieces.Piece> Pieces()
    {
        for (var i = 0; i < _pieces.Length; i++)
        {
            var j = Atmosphere.Hash3(i, 71) * 2f - Vector3.One;
            var thrown = new Vector3(j.X * 220f, 260f + 160f * MathF.Abs(j.Y), j.Z * 220f);
            _pieces[i] = new ImpactPieces.Piece
            {
                Position = At + thrown * Age - new Vector3(0f, 0.5f * ImpactPieces.Gravity * Age * Age, 0f),
                Velocity = thrown - new Vector3(0f, ImpactPieces.Gravity * Age, 0f),
                Age = Age,
                Life = Duration(EffectTable.Sparks),
                Phase = i * 16,
                Active = true,
            };
        }

        return _pieces;
    }

    // The strike on the way to At, drawn there (a far clip set so the bolt stands at At).
    private void DrawStrike(IFrameRecorder renderer, EffectTable effects, Vector3 eye)
    {
        var to = At - eye;
        var strike = _strike with { Azimuth = MathF.Atan2(to.X, to.Z) };
        var frame = new StormFrame(strike, Age, Storm.FlashAt(strike, Age), Storm.BoltAlphaAt(strike, Age));
        var far = new Vector2(to.X, to.Z).Length() / Storm.DrawDistance;
        _storm.Draw(renderer, frame, eye, far, effects);
    }
}
