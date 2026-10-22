using RetroEng.Loop;
using System.Numerics;
using RetroEng.Physics;
using SkyNet.Data;

namespace SkyNet.Game.Weapons;

/// <summary>
/// One row of DOS's piece-type table (SKYNET.EXE v1.01 0x3844b, 24 bytes, ended by type −1; looked up
/// by FUN_0011f902): the sprite index (bank &lt;&lt; 7 | record), the life in 70 Hz ticks (the clock
/// 0x38c00), the speed in u/s (negative: back against the bounced direction), the spread (16.16, a
/// random offset on the two minor axes of the direction) and the flags.
/// </summary>
public readonly record struct PieceType(int Type, int Sprite, int LifeTicks, int Speed, int Spread, int Flags)
{
    /// <summary>Flag bits as FUN_0011f91e reads them: 4 falls (392 u/s²), 8 expires after its life,
    /// 0x40 animates, 0x80 loops the animation (12 frames a second from a random frame; else the
    /// frames span the life), 0x100 drawn.</summary>
    public const int FlagGravity = 0x04, FlagTimed = 0x08, FlagAnimated = 0x40, FlagLoop = 0x80, FlagDrawn = 0x100;
    /// <summary>The piece clock runs at 70 ticks a second (FUN_00124c63: 0x460000 / fps).</summary>
    public const float TicksPerSecond = 70f;

    public float Life => LifeTicks / TicksPerSecond;
    public bool Falls => (Flags & FlagGravity) != 0;
    public bool Timed => (Flags & FlagTimed) != 0;
    public bool Animated => (Flags & FlagAnimated) != 0;
    public bool Loops => (Flags & FlagLoop) != 0;
    public bool Drawn => (Flags & FlagDrawn) != 0;
}

/// <summary>
/// DOS's impact pieces: the sparks and fragments a shot throws where it strikes (FUN_0012311f →
/// FUN_0011f761 with the ammo row's +0x2a count and +0x2e type), flown by FUN_0011f91e. A fixed
/// pool of <see cref="Capacity"/> slots (DOS stops at 0x94), so a burst allocates nothing.
/// <para>DOS gives the pieces no collision at all: they fall through the floor until their life
/// is out. Here a piece stops on what it meets (it lies on a floor, slides off a wall), so a burst
/// on the ground does not sink out of sight.</para>
/// </summary>
public sealed class ImpactPieces
{
    /// <summary>DOS's pool: a spawn finds no slot past index 0x93.</summary>
    public const int Capacity = 0x94;
    /// <summary>Gravity flag 4: 0x1880000 (16.16) a second, down.</summary>
    public const float Gravity = 392f;
    /// <summary>The looped animation: 0xC0 sixteenths of a frame a second.</summary>
    public const float LoopFps = 12f;
    /// <summary>A surface whose normal is this far up is a floor the piece lies on.</summary>
    public const float FloorUp = 0.7f;
    /// <summary>A wall keeps this share of the piece's speed (port only; DOS has no collision).</summary>
    public const float WallKeep = 0.3f;

    /// <summary>The piece types (v1.01 0x3844b, types 1-5), as shipped in data/tables impact_pieces.</summary>
    public static IReadOnlyList<PieceType> Types =>
        _types ??= [.. GameTables.Pieces.Select(p => new PieceType(p.Type, p.Sprite, p.Life, p.Speed, p.Spread, p.Flags))];

    private static PieceType[]? _types;

    public static PieceType? TypeOf(int type)
    {
        foreach (var t in Types)
        {
            if (t.Type == type)
            {
                return t;
            }
        }

        return null;
    }

    /// <summary>One slot of the pool.</summary>
    public struct Piece
    {
        public Vector3 Position;
        public Vector3 Velocity;
        public float Age;
        public float Life;
        public int Sprite;
        public int Flags;
        /// <summary>The looped animation's start frame (random, as DOS's rand &amp; 0xF0).</summary>
        public int Phase;
        public bool Active;
        public bool Resting;
        /// <summary>The position at the previous tick's end (render interpolation; never simulated).</summary>
        public Vector3 Previous;
    }

    private readonly Piece[] _pieces = new Piece[Capacity];
    private int _high;

    public ImpactPieces(int seed = 1) => Random = new SimRandom((ulong)seed);

    /// <summary>The pieces' generator: looks only, so never the simulation's (the session hands its cosmetic one).</summary>
    public SimRandom Random { get; set; }

    /// <summary>The pool; read slots 0 to <see cref="High"/> − 1 with <see cref="Piece.Active"/> set.</summary>
    public ReadOnlySpan<Piece> Slots => _pieces.AsSpan(0, _high);
    public int High => _high;
    public int ActiveCount
    {
        get
        {
            var n = 0;
            for (var i = 0; i < _high; i++)
            {
                n += _pieces[i].Active ? 1 : 0;
            }

            return n;
        }
    }

    /// <summary>
    /// <paramref name="count"/> pieces of <paramref name="type"/> at <paramref name="at"/> (FUN_0011f761):
    /// each flies along the shot's direction bounced in the surface (FUN_0011c100 at full keep),
    /// pushed on its two minor axes by a random share of the type's spread (FUN_0011fae5), at the
    /// type's speed. Returns how many found a slot.
    /// </summary>
    public int Spawn(int count, int type, Vector3 at, Vector3 incoming, Vector3 normal)
    {
        if (count <= 0 || TypeOf(type) is not { } t)
        {
            return 0;
        }

        var d = incoming.LengthSquared() > 1e-12f ? Vector3.Normalize(incoming) : -normal;
        if (normal.LengthSquared() > 1e-12f)
        {
            d = Vector3.Reflect(d, Vector3.Normalize(normal));
        }

        // FUN_0011fae5: k = ((spread + 0x80) >> 8) * 2, offsets (rand & 0xFFF folded into 0..k) − k/2,
        // << 8 against a 16.16 unit direction.
        var k = ((t.Spread + 0x80) >> 8) * 2;
        var made = 0;
        for (var n = 0; n < count; n++)
        {
            var slot = FreeSlot();
            if (slot < 0)
            {
                break;
            }

            var a = Fold(k) - k / 2;
            var b = Fold(k) - k / 2;
            var oa = a / 256f;
            var ob = b / 256f;
            var v = d;
            var ax = MathF.Abs(d.X);
            var ay = MathF.Abs(d.Y);
            var az = MathF.Abs(d.Z);
            if (ax >= ay && ax >= az)
            {
                v.Y += oa;
                v.Z += ob;
            }
            else if (ay >= az)
            {
                v.X += oa;
                v.Z += ob;
            }
            else
            {
                v.X += oa;
                v.Y += ob;
            }

            v = v.LengthSquared() > 1e-12f ? Vector3.Normalize(v) : d;
            ref var p = ref _pieces[slot];
            p = new Piece
            {
                Position = at,
                Previous = at,
                Velocity = v * t.Speed,
                Life = t.Timed ? t.Life : float.MaxValue,
                Sprite = t.Sprite,
                Flags = t.Flags,
                Phase = t.Loops ? Random.Next(16) : 0,
                Active = true,
            };
            made++;
        }

        return made;
    }

    /// <summary>A tick's start: every piece's position becomes the one the frames glide from.</summary>
    public void KeepPoses()
    {
        for (var i = 0; i < _high; i++)
        {
            _pieces[i].Previous = _pieces[i].Position;
        }
    }

    private int Fold(int k)
    {
        var r = Random.Next(0x1000);
        if (k <= 0)
        {
            return 0;
        }

        while (r > k)
        {
            r -= k;
        }

        return r;
    }

    private int FreeSlot()
    {
        for (var i = 0; i < _high; i++)
        {
            if (!_pieces[i].Active)
            {
                return i;
            }
        }

        return _high < Capacity ? _high++ : -1;
    }

    /// <summary>One tick: the fall, the flight against <paramref name="world"/> (null: none, as DOS)
    /// and the expiry.</summary>
    public void Step(float dt, CollisionWorld? world)
    {
        for (var i = 0; i < _high; i++)
        {
            ref var p = ref _pieces[i];
            if (!p.Active)
            {
                continue;
            }

            p.Age += dt;
            if (p.Age >= p.Life)
            {
                p.Active = false;
                continue;
            }

            if (p.Resting)
            {
                continue;
            }

            if ((p.Flags & PieceType.FlagGravity) != 0)
            {
                p.Velocity.Y -= Gravity * dt;
            }

            var motion = p.Velocity * dt;
            var length = motion.Length();
            if (length < 1e-6f)
            {
                continue;
            }

            if (world != null && world.Raycast(p.Position, motion / length, length, out var hit))
            {
                p.Position = hit.Point + hit.Normal;
                if (hit.Normal.Y >= FloorUp)
                {
                    p.Velocity = Vector3.Zero;
                    p.Resting = true;
                }
                else
                {
                    p.Velocity = Vector3.Reflect(p.Velocity, hit.Normal) * WallKeep;
                }

                continue;
            }

            p.Position += motion;
        }

        while (_high > 0 && !_pieces[_high - 1].Active)
        {
            _high--;
        }
    }

    /// <summary>The frame to draw of a sprite with <paramref name="frames"/> frames (FUN_0011f91e):
    /// looped at 12 a second from the piece's phase, else spread over its life.</summary>
    public static int FrameOf(in Piece p, int frames)
    {
        if (frames <= 1 || (p.Flags & PieceType.FlagAnimated) == 0)
        {
            return 0;
        }

        if ((p.Flags & PieceType.FlagLoop) != 0)
        {
            return (p.Phase + (int)(p.Age * LoopFps)) % frames;
        }

        if (p.Life >= float.MaxValue)
        {
            return 0;
        }

        return Math.Clamp((int)(p.Age / p.Life * (frames + 1)), 0, frames - 1);
    }

    public void Clear()
    {
        Array.Clear(_pieces);
        _high = 0;
    }
}
