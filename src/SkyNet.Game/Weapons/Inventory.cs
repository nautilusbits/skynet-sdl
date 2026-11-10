namespace SkyNet.Game.Weapons;

/// <summary>What a trigger pull did.</summary>
public enum FireResult { Fired, Cooling, Dry, Empty, NotOwned }

/// <summary>A saved kit (<see cref="Inventory.Save"/>).</summary>
public sealed record InventoryState(bool[] Owned, int[] Pools, int Primary, int Thrown);

/// <summary>
/// The soldier's arsenal: owned records (+0x5c bit 0), the 13 shared pools, the two selection
/// registers (the gun on FIRE, the thrown item on THROW) and their separate cooldowns.
/// <list type="bullet">
/// <item>A shot arms the cooldown at DOS's whole frames at 35 fps (<see cref="WeaponRecord.Cooldown"/>),
/// counted down by the tick's delta; an empty pool plays the dry sound and waits <see cref="DryDelay"/>.</item>
/// <item>The throw arms its own cooldown (rate 1: 36 DOS frames) before it looks at the pool, and an empty
/// item fails silently (FUN_001263cc).</item>
/// <item>Number keys 1-7 pick a group of <see cref="KeyGroups"/>, again to cycle in it; <c>;</c>/<c>'</c> cycle the owned
/// records 0-12 (and the deathmatch HUMAN's motion detector, record 13, while it is owned), F1-F5
/// pick the thrown records 15, 14, 16, 18, 19.</item>
/// </list>
/// </summary>
public sealed class Inventory
{
    /// <summary>The Godot port's wait after a dry click (DOS: UNCERTAIN).</summary>
    public const float DryDelay = 0.25f;
    /// <summary>The Godot port's pause after a weapon change.</summary>
    public const float SelectDelay = 0.15f;
    /// <summary>F1-F5 → thrown record (the key poller's chain at 0x11b0f5).</summary>
    public static readonly int[] ThrowKeys = [15, 14, 16, 18, 19];
    /// <summary>The thrown item selected at the start (0x4449a, on foot).</summary>
    public const int DefaultThrown = 15;
    /// <summary>The primary held at the start (the Godot port: the UZI).</summary>
    public const int DefaultPrimary = 1;
    /// <summary>The records <c>;</c> and <c>'</c> step through (v1.00 0x435e0: records 0..12).</summary>
    public const int CycleCount = 13;
    /// <summary>Keys 1-7 → their records, tried in this order (v1.01 lists at 0x43668..0x436c4,
    /// read by the key poller 0x11b032; 8-0 have none).</summary>
    public static readonly int[][] KeyGroups = [[0, 13], [3, 2, 1, 12], [4], [5], [6], [8, 7], [11, 10, 9]];
    private const float Epsilon = 1e-4f;

    private readonly bool[] _owned;
    private readonly int[] _pools;
    // The group a number key last selected from and the index it took (DOS 0x44472 + list +0).
    private int _group = -1, _groupAt;

    public WeaponTable Table { get; }
    public int Primary { get; private set; }
    public int Thrown { get; private set; } = DefaultThrown;
    /// <summary>Seconds until the gun may fire again.</summary>
    public float FireCooldown { get; private set; }
    public float ThrowCooldown { get; private set; }

    public Inventory(WeaponTable table)
    {
        Table = table;
        _owned = new bool[table.Weapons.Length];
        _pools = new int[table.Pools.Length];
        Reset();
    }

    /// <summary>The campaign start: the start list owned, every pool at its initial count, the UZI
    /// in hand and the MOLOTOV selected.</summary>
    public void Reset()
    {
        Array.Clear(_owned);
        foreach (var r in Table.StartList)
        {
            if (r >= 0 && r < _owned.Length)
            {
                _owned[r] = true;
            }
        }

        for (var p = 0; p < _pools.Length; p++)
        {
            _pools[p] = Table.Pools[p].Initial;
        }

        Primary = Owns(DefaultPrimary) ? DefaultPrimary : 0;
        Thrown = DefaultThrown;
        FireCooldown = ThrowCooldown = 0f;
        _group = -1;
    }

    /// <summary>The kit as it stands (owned records, pools, both selections): the mission-start
    /// snapshot RESTART MISSION goes back to (DOS FUN_0011d346 / FUN_0011d3b0).</summary>
    public InventoryState Save() => new([.. _owned], [.. _pools], Primary, Thrown);

    /// <summary>Puts a <see cref="Save"/>d kit back; the cooldowns start ready.</summary>
    public void Restore(InventoryState state)
    {
        state.Owned.AsSpan(0, Math.Min(state.Owned.Length, _owned.Length)).CopyTo(_owned);
        state.Pools.AsSpan(0, Math.Min(state.Pools.Length, _pools.Length)).CopyTo(_pools);
        Primary = state.Primary;
        Thrown = state.Thrown;
        FireCooldown = ThrowCooldown = 0f;
        _group = -1;
    }

    public bool Owns(int record) => record >= 0 && record < _owned.Length && _owned[record];

    public void Grant(int record)
    {
        if (record >= 0 && record < _owned.Length)
        {
            _owned[record] = true;
        }
    }

    /// <summary>Takes a record away (dm_game.gd drop_weapon: a TERMINATOR's spawn takes the HUMAN's
    /// motion detector); held, the next owned record comes into the hand.</summary>
    public void Take(int record)
    {
        if (!Owns(record))
        {
            return;
        }

        _owned[record] = false;
        if (Primary == record && !Cycle(1))
        {
            Primary = Owns(DefaultPrimary) ? DefaultPrimary : 0;
        }
    }

    public int Pool(int pool) => pool >= 0 && pool < _pools.Length ? _pools[pool] : 0;

    /// <summary>Adds to a pool, capped at its max; returns what was taken.</summary>
    public int AddAmmo(int pool, int amount)
    {
        if (pool < 0 || pool >= _pools.Length)
        {
            return 0;
        }

        var before = _pools[pool];
        _pools[pool] = Math.Clamp(before + amount, 0, Table.Pools[pool].Max);
        return _pools[pool] - before;
    }

    /// <summary>Rounds left for a record: its pool's count; -1 when it has none (the pipe).</summary>
    public int AmmoFor(int record)
    {
        var pool = Table.Weapons[record].Pool;
        return pool < 0 ? -1 : Pool(pool);
    }

    /// <summary>Counts the cooldowns down by one tick.</summary>
    public void Tick(float seconds)
    {
        // A tick's float steps leave crumbs (0.2 − 12 × 1/60 ≈ 1e-8): those count as ready.
        // The tick that runs a cooldown out keeps its overshoot (< 0) for a held trigger's next shot,
        // so 60 Hz ticks average DOS's 35 fps gap; unused, it rests at 0 the next tick.
        var left = FireCooldown - seconds;
        FireCooldown = FireCooldown <= 0f ? 0f : left > Epsilon ? left : MathF.Min(left, 0f);
        ThrowCooldown = ThrowCooldown - seconds > Epsilon ? ThrowCooldown - seconds : 0f;
    }

    /// <summary>Takes a gun in hand; false when not owned or already held (DOS ignores the key).</summary>
    public bool Select(int record)
    {
        if (record == Primary || !Owns(record) || record >= Table.Weapons.Length)
        {
            return false;
        }

        Primary = record;
        FireCooldown = MathF.Max(FireCooldown, SelectDelay);
        return true;
    }

    /// <summary>A number key (FUN_00125981): a new group starts at its head, the same one steps on;
    /// the first owned record not in hand is selected (2: MACHINE GUN, ASSAULT RIFLE, UZI, ...).</summary>
    public bool SelectGroup(int group)
    {
        if (group < 0 || group >= KeyGroups.Length)
        {
            return false;
        }

        var list = KeyGroups[group];
        var at = group == _group ? _groupAt : -1;
        for (var k = 0; k < list.Length; k++)
        {
            at = (at + 1) % list.Length;
            if (Select(list[at]))
            {
                (_group, _groupAt) = (group, at);
                return true;
            }
        }

        return false;
    }

    /// <summary>The next owned record among 0..12 in <paramref name="direction"/> (±1), selected; the
    /// motion detector (13, owned only by a deathmatch HUMAN) joins the ring after the super UZI, as
    /// the Godot port's wheel reaches it.</summary>
    public bool Cycle(int direction)
    {
        var detector = Owns(WeaponTable.MotionDetector) ? WeaponTable.MotionDetector + 1 : CycleCount;
        var n = Math.Min(detector, Table.Weapons.Length);
        var r = Primary;
        for (var k = 0; k < n; k++)
        {
            r = ((r + direction) % n + n) % n;
            if (Owns(r))
            {
                // DOS's ring is a group too: the next number key starts its group afresh.
                _group = -1;
                return Select(r);
            }
        }

        return false;
    }

    /// <summary>Picks the thrown item (owned bit only: an empty one is selectable).</summary>
    public bool SelectThrown(int record)
    {
        if (record == Thrown || !Owns(record) || record < WeaponTable.FirstThrown || record > WeaponTable.LastThrown)
        {
            return false;
        }

        Thrown = record;
        return true;
    }

    /// <summary>The next thrown item in F1-F5 order that is owned and not empty, selected (wraps;
    /// the touch screen's one button for F1-F5). False when no other one is left.</summary>
    public bool CycleThrown()
    {
        var at = Array.IndexOf(ThrowKeys, Thrown);
        for (var k = 1; k <= ThrowKeys.Length; k++)
        {
            var r = ThrowKeys[(at + k + ThrowKeys.Length) % ThrowKeys.Length];
            if (AmmoFor(r) != 0 && SelectThrown(r))
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>One trigger pull of the gun: on success the cost is paid and the cooldown armed.</summary>
    public FireResult TryFire(int record)
    {
        if (!Owns(record))
        {
            return FireResult.NotOwned;
        }

        if (FireCooldown > 0f)
        {
            return FireResult.Cooling;
        }

        var w = Table.Weapons[record];
        if (w.Pool >= 0 && Pool(w.Pool) < w.Cost)
        {
            FireCooldown = DryDelay;
            return FireResult.Dry;
        }

        if (w.Pool >= 0)
        {
            _pools[w.Pool] -= w.Cost;
        }

        FireCooldown += w.Cooldown;
        return FireResult.Fired;
    }

    /// <summary>One press of THROW: the cooldown is armed first, then an empty pool throws nothing.</summary>
    public FireResult TryThrow()
    {
        if (ThrowCooldown > 0f)
        {
            return FireResult.Cooling;
        }

        var w = Table.Weapons[Thrown];
        ThrowCooldown = w.Cooldown;
        if (w.Pool >= 0 && Pool(w.Pool) < Math.Max(1, w.Cost))
        {
            return FireResult.Empty;
        }

        if (w.Pool >= 0)
        {
            _pools[w.Pool] -= Math.Max(1, w.Cost);
        }

        return FireResult.Fired;
    }
}
