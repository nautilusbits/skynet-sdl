using System.Numerics;
using RetroEng.Render;
using SkyNet.Data;
using SkyNet.Game.Menu;
using SkyNet.Game.Weapons;

namespace SkyNet.Game.Enhanced;

/// <summary>
/// The enhanced look's weapon effects as particles, drawn as <see cref="EffectTable"/> says: the
/// explosions, the bullet strikes and the impact pieces' sparks. GPU-free: the weapons' view
/// (<see cref="WeaponRenderer"/>) and the editor's preview (<see cref="EffectPreview"/>) queue them.
/// </summary>
public sealed class ImpactEffects
{
    /// <summary>Per explosion at PARTICLES LOW, HIGH: fireball quads, dust in the ring, sparks and
    /// debris, smoke puffs.</summary>
    public static readonly int[] BlastFire = [0, 7, 11];
    public static readonly int[] BlastRing = [0, 8, 16];
    public static readonly int[] BlastSparks = [0, 12, 28];
    public static readonly int[] BlastSmoke = [0, 6, 12];
    /// <summary>The fireball's life, the sparks' and the dust ring's (seconds).</summary>
    public const float FireballLife = 0.85f, BlastSparkLife = 0.9f, RingLife = 1.6f;

    /// <summary>Dust puffs per bullet strike (PARTICLES LOW, HIGH) and sparks per strike on metal.</summary>
    public static readonly int[] PuffDust = [0, 4, 7];
    public static readonly int[] PuffSparks = [0, 4, 8];
    /// <summary>A strike's sparks fly this long (seconds).</summary>
    public const float PuffSparkLife = 0.35f;

    /// <summary>A spark's colour (linear, added) and how long a piece throws them (seconds).</summary>
    public static readonly Vector3 SparkColour = new(4.2f, 2.1f, 0.6f);
    public const float SparkTime = 0.45f;
    /// <summary>Sparks thrown off each flying piece besides its own streak (PARTICLES OFF, LOW, HIGH).</summary>
    public static readonly int[] ThrownSparks = [0, 0, 2];

    private Particle[] _blastAlpha = [];
    private Particle[] _blastAdd = [];
    private Particle[] _dust = [];
    private Particle[] _sparks = [];

    /// <summary>
    /// The explosions (<see cref="WeaponSystem.Blasts"/>), sized by their radius R (a grenade's under
    /// a rocket's under a big robot's or a vehicle's), in the light all around <paramref name="ambient"/>.
    /// All hashed from the blast: nothing kept, a frozen clock draws the same frame. The flash light
    /// is the effect's (<see cref="GameLights.ExplosionLight"/>).
    /// <code>
    ///   0 – 0.2 s    white-hot core (added)
    ///   0 – 0.85 s   fireball: layered rising-noise quads swelling to ~1.4 R, cooling
    ///   0 – 1.6 s    a ring of dust blown out along the ground to ~1.3 R
    ///   0 – 0.9 s    sparks and debris thrown up and out, falling
    ///   0.1 – 4.5 s  dark smoke rising and drifting downwind, thinning out
    /// </code>
    /// </summary>
    public void DrawBlasts(IFrameRecorder renderer, IReadOnlyList<BlastPose> blasts, ParticleLevel level, Vector3 ambient, EffectTable effects)
    {
        if (level == ParticleLevel.Off || blasts.Count == 0)
        {
            return;
        }

        EffectRow fireRow = effects[EffectTable.Explosion], ringRow = effects[EffectTable.ExplosionDust],
            sparkRow = effects[EffectTable.ExplosionDebris], smokeRow = effects[EffectTable.ExplosionSmoke];
        int fire = EffectTable.Count(fireRow, level), ring = EffectTable.Count(ringRow, level),
            sparks = EffectTable.Count(sparkRow, level), smoke = EffectTable.Count(smokeRow, level);
        if (_blastAlpha.Length < blasts.Count * (ring + smoke))
        {
            _blastAlpha = new Particle[blasts.Count * (ring + smoke)];
        }

        if (_blastAdd.Length < blasts.Count * (fire + 1 + sparks))
        {
            _blastAdd = new Particle[blasts.Count * (fire + 1 + sparks)];
        }

        var dustColour = (ambient * 1.8f * new Vector3(1f, 0.92f, 0.8f) + new Vector3(0.03f, 0.027f, 0.022f)) * EffectTable.Tint(ringRow);
        var smokeColour = (ambient * 0.22f + new Vector3(0.006f, 0.0055f, 0.005f)) * EffectTable.Tint(smokeRow);
        var wind = Atmosphere.OutdoorWind * (float)smokeRow.Speed;
        float fireballLife = (float)fireRow.Life, ringLife = (float)ringRow.Life, sparkLife = (float)sparkRow.Life;
        var fireLight = EffectTable.Light(fireRow);
        var sparkLight = SparkColour * EffectTable.Light(sparkRow);
        var gravity = ImpactPieces.Gravity * (float)sparkRow.Gravity;
        int na = 0, nb = 0;
        foreach (var b in blasts)
        {
            float t = b.Age, r = b.Radius;
            // Smoke first (under the dust ring and the fire).
            for (var k = 0; k < smoke; k++)
            {
                var j = Atmosphere.Hash3(b.Seed * 29 + k, 61);
                var born = 0.08f + 0.35f * j.X;
                if (t < born)
                {
                    continue;
                }

                var age = t - born;
                var u = age / (BlastCloud.MaxLife - born);
                var start = (j * 2f - Vector3.One) * (r * 0.45f) * (float)smokeRow.Spread;
                var rise = r * (0.35f + 0.35f * j.Y) * (float)smokeRow.Rise;
                var at = b.At + start + new Vector3(0f, rise * MathF.Sqrt(age) + r * 0.5f, 0f) + wind * (age * 1.2f);
                var size = r * (0.45f + 0.95f * MathF.Sqrt(u)) * (0.8f + 0.4f * j.Z) * (float)smokeRow.Size;
                var alpha = 0.55f * MathF.Min(age * 4f, 1f) * MathF.Pow(MathF.Max(1f - u, 0f), 1.3f) * (float)smokeRow.Intensity;
                _blastAlpha[na++] = new Particle(at, size, new Vector4(smokeColour, alpha), Spin: j.Z * 6f + age * 0.3f, Seed: 3f + k + (b.Seed & 31));
            }

            if (t < ringLife)
            {
                var spread = 1f - MathF.Exp(-t * 4f);
                var fade = MathF.Pow(1f - t / ringLife, 2f);
                for (var k = 0; k < ring; k++)
                {
                    var j = Atmosphere.Hash3(b.Seed * 31 + k, 62);
                    var angle = (k + 0.5f * j.X) / ring * MathF.Tau;
                    var dir = new Vector3(MathF.Cos(angle), 0f, MathF.Sin(angle));
                    var at = b.At + dir * (r * (0.25f + 1.05f * spread) * (float)ringRow.Spread) + new Vector3(0f, r * (0.08f + 0.15f * t), 0f);
                    var size = r * (0.22f + 0.33f * spread) * (0.8f + 0.4f * j.Y) * (float)ringRow.Size;
                    _blastAlpha[na++] = new Particle(at, size, new Vector4(dustColour, 0.35f * fade * MathF.Min(t * 20f, 1f) * (float)ringRow.Intensity),
                        Spin: j.Z * 6f + t, Seed: 5f + k + (b.Seed & 31));
                }
            }

            if (t < fireballLife)
            {
                var u = t / fireballLife;
                var grow = 1f - MathF.Exp(-t * 7f);
                var heat = MathF.Pow(1f - u, 1.4f);
                if (t < 0.2f)
                {
                    var c = 1f - t / 0.2f;
                    _blastAdd[nb++] = new Particle(b.At + new Vector3(0f, r * 0.3f, 0f), r * (0.8f + 0.8f * grow) * (float)fireRow.Size,
                        new Vector4(new Vector3(6f, 4.6f, 2.6f) * fireLight * c, c));
                }

                for (var k = 0; k < fire; k++)
                {
                    var j = Atmosphere.Hash3(b.Seed * 37 + k, 63);
                    var off = (j * 2f - Vector3.One) * (r * 0.4f * grow) * (float)fireRow.Spread
                        + new Vector3(0f, r * (0.35f + 0.4f * u) * (float)fireRow.Rise, 0f);
                    var w = r * (0.55f + 0.85f * grow) * (0.7f + 0.4f * j.Y) * (float)fireRow.Size;
                    var bright = 2.6f * heat * (k == 0 ? 1.2f : 0.8f);
                    _blastAdd[nb++] = new Particle(b.At + off, w, new Vector4(fireLight * bright, heat),
                        Spin: t * 2.2f + j.X * 9f, Seed: j.Z * 10f + k * 1.618f, Shape: ParticleShape.Flame, Stretch: 1.05f + 0.3f * j.X);
                }
            }

            if (t < sparkLife)
            {
                var speed = Math.Clamp(r / 120f, 0.6f, 2.2f);
                for (var k = 0; k < sparks; k++)
                {
                    var j = Atmosphere.Hash3(b.Seed * 41 + k, 64) * 2f - Vector3.One;
                    var life = sparkLife * (0.55f + 0.45f * MathF.Abs(j.Z));
                    if (t >= life)
                    {
                        continue;
                    }

                    var v = Vector3.Normalize(new Vector3(j.X, 0.8f + 0.6f * MathF.Abs(j.Y), j.Z)) * ((380f + 520f * MathF.Abs(j.Y)) * speed)
                        * (float)sparkRow.Speed;
                    var at = b.At + v * t - new Vector3(0f, 0.5f * gravity * t * t, 0f);
                    v.Y -= gravity * t;
                    var hot = MathF.Pow(1f - t / life, 1.3f);
                    // Every third a darker, bigger bit of debris.
                    _blastAdd[nb++] = k % 3 == 2
                        ? new Particle(at, 2.6f * speed * (float)sparkRow.Size, new Vector4(sparkLight * 0.35f, hot), Shape: ParticleShape.Streak, Axis: v * 0.02f)
                        : new Particle(at, 1.4f * (float)sparkRow.Size, new Vector4(sparkLight, hot), Shape: ParticleShape.Streak, Axis: v * 0.035f);
                }
            }
        }

        renderer.DrawParticles(_blastAlpha.AsSpan(0, na), ParticleBlend.Alpha, 10f);
        renderer.DrawParticles(_blastAdd.AsSpan(0, nb), ParticleBlend.Add, 12f);
    }

    /// <summary>The bullet strikes (<see cref="WeaponSystem.Puffs"/>): a small cloud of dust blown
    /// off the surface, swelling, drifting up and thinning out; on metal a burst of sparks first,
    /// thrown off the surface and falling. All hashed from the strike: nothing kept.</summary>
    public void DrawPuffs(IFrameRecorder renderer, IReadOnlyList<PuffPose> puffs, ParticleLevel level, Vector3 ambient, EffectTable effects)
    {
        if (level == ParticleLevel.Off || puffs.Count == 0)
        {
            return;
        }

        EffectRow dustRow = effects[EffectTable.ImpactDust], sparkRow = effects[EffectTable.ImpactSparks];
        int dust = EffectTable.Count(dustRow, level), sparks = EffectTable.Count(sparkRow, level);
        if (_dust.Length < puffs.Count * dust)
        {
            _dust = new Particle[puffs.Count * dust];
        }

        if (_sparks.Length < puffs.Count * sparks)
        {
            _sparks = new Particle[puffs.Count * sparks];
        }

        var colour = (ambient * 1.8f * new Vector3(1f, 0.92f, 0.8f) + new Vector3(0.03f, 0.027f, 0.022f)) * EffectTable.Tint(dustRow);
        var sparkLight = SparkColour * EffectTable.Light(sparkRow);
        float sparkLife = (float)sparkRow.Life, gravity = ImpactPieces.Gravity * (float)sparkRow.Gravity;
        int nd = 0, ns = 0;
        foreach (var p in puffs)
        {
            var t = p.Age / BulletPuff.MaxLife;
            var bounce = Vector3.Reflect(p.Incoming, p.Normal);
            for (var k = 0; k < dust; k++)
            {
                var j = Atmosphere.Hash3(p.Seed * 13 + k, 51) * 2f - Vector3.One;
                var way = Vector3.Normalize(p.Normal * 1.4f + j * 0.8f);
                // Blown out fast, slowing; then rising a little.
                var out_ = (1f - MathF.Exp(-p.Age * 5f)) * 26f * p.Scale * (float)dustRow.Speed;
                var at = p.At + way * out_ + new Vector3(0f, 9f * p.Age * (float)dustRow.Rise, 0f);
                var size = (5f + 22f * MathF.Sqrt(t)) * p.Scale * (0.8f + 0.3f * j.Z) * (float)dustRow.Size;
                var alpha = 0.42f * MathF.Pow(1f - t, 1.6f) * MathF.Min(p.Age * 30f, 1f) * (float)dustRow.Intensity;
                _dust[nd++] = new Particle(at, size, new Vector4(colour, alpha), Spin: j.X * 3f + t, Seed: 2f + k + (p.Seed & 63));
            }

            if (!p.Metal || p.Age >= sparkLife)
            {
                continue;
            }

            var heat = MathF.Pow(1f - p.Age / sparkLife, 1.3f);
            for (var k = 0; k < sparks; k++)
            {
                var j = Atmosphere.Hash3(p.Seed * 17 + k, 52) * 2f - Vector3.One;
                var v = Vector3.Normalize(bounce * 0.6f + p.Normal * 0.6f + j * 0.7f) * (260f + 220f * MathF.Abs(j.Y)) * (float)sparkRow.Speed;
                var at = p.At + v * p.Age - new Vector3(0f, 0.5f * gravity * p.Age * p.Age, 0f);
                v.Y -= gravity * p.Age;
                _sparks[ns++] = new Particle(at, 1.2f * (float)sparkRow.Size, new Vector4(sparkLight, heat), Shape: ParticleShape.Streak, Axis: v * 0.03f);
            }
        }

        renderer.DrawParticles(_dust.AsSpan(0, nd), ParticleBlend.Alpha, 8f);
        renderer.DrawParticles(_sparks.AsSpan(0, ns), ParticleBlend.Add, 4f);
    }

    /// <summary>The impact pieces' sparks: a hot streak behind each piece in flight for its first
    /// life (<see cref="SparkTime"/>) seconds, and more thrown off it (hashed from its slot), falling;
    /// nothing is kept.</summary>
    public void DrawSparks(IFrameRecorder renderer, ReadOnlySpan<ImpactPieces.Piece> slots, ParticleLevel level, EffectTable effects)
    {
        if (level == ParticleLevel.Off)
        {
            return;
        }

        var row = effects[EffectTable.Sparks];
        var extra = EffectTable.Count(row, level);
        if (_sparks.Length < slots.Length * (1 + extra))
        {
            _sparks = new Particle[slots.Length * (1 + extra)];
        }

        var light = SparkColour * EffectTable.Light(row);
        float time = (float)row.Life, size = (float)row.Size, scatter = 140f * (float)row.Spread, gravity = ImpactPieces.Gravity * (float)row.Gravity;
        var n = 0;
        for (var i = 0; i < slots.Length; i++)
        {
            ref readonly var p = ref slots[i];
            if (!p.Active || p.Resting || p.Age >= time || p.Velocity.LengthSquared() < 1f)
            {
                continue;
            }

            var heat = MathF.Pow(1f - p.Age / time, 1.5f);
            _sparks[n++] = new Particle(p.Position, 1.6f * size, new Vector4(light, heat), Shape: ParticleShape.Streak, Axis: p.Velocity * 0.06f);
            for (var k = 0; k < extra; k++)
            {
                var j = Atmosphere.Hash3(i * 3 + k + p.Phase * 7, 41) * 2f - Vector3.One;
                var v = p.Velocity * (0.6f + 0.3f * j.X) + j * scatter;
                var at = p.Position - p.Velocity * p.Age + v * p.Age - new Vector3(0f, 0.5f * gravity * p.Age * p.Age, 0f);
                v.Y -= gravity * p.Age;
                _sparks[n++] = new Particle(at, 1.1f * size, new Vector4(light, heat * 0.8f), Shape: ParticleShape.Streak, Axis: v * 0.04f);
            }
        }

        renderer.DrawParticles(_sparks.AsSpan(0, n), ParticleBlend.Add, 4f);
    }
}
