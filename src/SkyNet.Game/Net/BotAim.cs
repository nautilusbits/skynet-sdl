using System.Numerics;

namespace SkyNet.Game.Net;

/// <summary>
/// A bot's aim on foot (Quake III's bot aim, ideas only): a fresh target is acquired with an error
/// that settles while it is tracked; a target crossing the view drags the aim behind it; slow
/// shots are led when the aim skill allows; the view turns no faster than its rate.
/// <code>
///   error ── acquire: random, AcquireError × spread
///         └─ each tick: → −(target's angular speed × Lag), e^(−Settle·dt) of the way left
///   aim = direction to (eye + velocity × flight time [+ the shell's drop]) + error
///   view yaw/pitch ── at most TurnRate·dt towards aim
/// </code>
/// </summary>
internal sealed class BotAim
{
    /// <summary>A new target's error: this many shot spreads.</summary>
    private const float AcquireError = 3f;
    /// <summary>The target's velocity estimate: weight of each new sample.</summary>
    private const float VelocityBlend = 0.25f;
    /// <summary>A lead never looks further ahead than this (s).</summary>
    private const float MaxLead = 1.5f;
    /// <summary>Pitch error is this share of the yaw's (the target moves mostly across).</summary>
    private const float PitchShare = 0.4f;

    private Vector2 _error;
    private Vector3 _last, _velocity;
    private bool _tracking;

    /// <param name="settle">How fast the error settles (1/s).</param>
    /// <param name="lag">Seconds of the target's angular motion the aim trails by.</param>
    /// <param name="turnRate">The view's top turn (rad/s).</param>
    /// <param name="leads">Leads slow shots.</param>
    public BotAim(float settle, float lag, float turnRate, AimLead leads)
    {
        Settle = settle;
        Lag = lag;
        TurnRate = turnRate;
        Leads = leads;
    }

    public float Settle { get; }
    public float Lag { get; }
    public float TurnRate { get; }
    public AimLead Leads { get; }

    /// <summary>The aim's error now (yaw, pitch; radians).</summary>
    public Vector2 Error => _error;

    /// <summary>The target's estimated velocity.</summary>
    public Vector3 Velocity => _velocity;

    /// <summary>A target is (re)acquired: a random error of <see cref="AcquireError"/> ×
    /// <paramref name="spread"/> (radians), scaled by <paramref name="share"/> (a re-sighted one less).</summary>
    public void Acquire(Random rng, float spread, float share)
    {
        var a = (float)(rng.NextDouble() * Math.Tau);
        var size = AcquireError * spread * share;
        _error = new Vector2(MathF.Cos(a) * size, MathF.Sin(a) * size * PitchShare);
        _tracking = false;
    }

    /// <summary>The yaw and pitch to look along at the target (its eye <paramref name="targetEye"/>
    /// from <paramref name="eye"/>), the error moved on by <paramref name="dt"/>.
    /// <paramref name="shotSpeed"/> 0: a ray (no lead); <paramref name="drop"/>: a shell's gravity.</summary>
    public (float Yaw, float Pitch) Track(Vector3 eye, Vector3 targetEye, float shotSpeed, float drop, float dt)
    {
        Observe(targetEye, dt);
        var to = targetEye - eye;
        var dist = MathF.Max(to.Length(), 1f);
        var aimAt = targetEye;
        if (Leads == AimLead.Slow && shotSpeed > 0f)
        {
            var t = MathF.Min(dist / shotSpeed, MaxLead);
            aimAt += _velocity * t + new Vector3(0f, 0.5f * drop * t * t, 0f);
        }

        // The aim trails a target crossing the view: its angular speed across × Lag.
        var right = Vector3.Normalize(new Vector3(-to.Z, 0f, to.X) + new Vector3(1e-4f, 0f, 0f));
        var across = Vector3.Dot(_velocity, right) / dist;
        var trail = new Vector2(across * Lag, 0f);
        _error = trail + (_error - trail) * MathF.Exp(-Settle * dt);

        var d = aimAt - eye;
        var yaw = MathF.Atan2(-d.X, -d.Z) + _error.X;
        var pitch = MathF.Atan2(d.Y, new Vector2(d.X, d.Z).Length()) + _error.Y;
        return (yaw, pitch);
    }

    /// <summary><paramref name="from"/> turned towards <paramref name="to"/> by at most <paramref name="step"/> (radians, wrapped).</summary>
    public static float Toward(float from, float to, float step) => from + Math.Clamp(Wrap(to - from), -step, step);

    public static float Wrap(float a)
    {
        a %= MathF.Tau;
        return a > MathF.PI ? a - MathF.Tau : a < -MathF.PI ? a + MathF.Tau : a;
    }

    /// <summary>The target's velocity from its moves (the first sample only sets the place).</summary>
    private void Observe(Vector3 at, float dt)
    {
        if (!_tracking || dt <= 0f)
        {
            _last = at;
            _velocity = Vector3.Zero;
            _tracking = true;
            return;
        }

        _velocity = Vector3.Lerp(_velocity, (at - _last) / dt, VelocityBlend);
        _last = at;
    }
}

/// <summary>Whether a bot's aim leads its shots.</summary>
internal enum AimLead
{
    /// <summary>At the target as it is.</summary>
    None,
    /// <summary>Slow shots (bolts, shells) aimed where the target will be.</summary>
    Slow,
}
