using RetroEng.Loop;
using System.Collections.Immutable;
using SkyNet.Data;

namespace SkyNet.Game.Campaign;

/// <summary>
/// What a destroyed record leaves (FUN_00124293 → FUN_00124619): the destruction table (v1.01
/// 0x424d6, v1.00 0x423d6; 140 rows of 14 bytes <c>{u16 effect[4], i16 spread, i16 drop list,
/// i16 sound}</c>, indexed by the link record's byte 0) names a drop list (v1.01 0x42c7e, v1.00
/// 0x42b7e: 10 data pointers to <c>{i32 count, u16 sprite[count]}</c>); one sprite of the list is
/// chosen at random, 0 = nothing. The sprite is a pickup when the item table lists it (crates →
/// ammo, lockers → medkits), else scenery (the burning wrecks of list 2).
/// </summary>
public sealed class DestructionTable
{
    public const int RowSize = 14, RowCount = 140, DropListCount = 10;

    /// <summary>Per destruction type: the drop list (−1 none).</summary>
    public ImmutableArray<int> DropList { get; }
    /// <summary>Per drop list: the sprite choices (0 = nothing).</summary>
    public ImmutableArray<ImmutableArray<int>> Drops { get; }

    /// <summary>Per destruction type: its effect sprites (up to 4, 0 = none; bank = sprite &gt;&gt; 7)
    /// and its sound (−1 none, −2 one of explosions 33..36). Empty when the table was built from
    /// drop lists alone.</summary>
    public ImmutableArray<(ImmutableArray<int> Effects, int Sound)> Blasts { get; }

    public DestructionTable(IEnumerable<int> dropList, IEnumerable<IEnumerable<int>> drops,
        IEnumerable<(ImmutableArray<int> Effects, int Sound)>? blasts = null)
    {
        DropList = [.. dropList];
        Drops = [.. drops.Select(d => d.ToImmutableArray())];
        Blasts = [.. blasts ?? []];
    }

    /// <summary>Nothing drops.</summary>
    public static DestructionTable Empty { get; } = new([], []);

    /// <summary>
    /// What a destruction of <paramref name="destroyType"/> shows and sounds (FUN_00124793, v1.01):
    /// type 0 is one blast of bank 358 (0xB300) and one of explosions 33..36; a listed type its
    /// row's effects and sound (FUN_001244bd).
    /// </summary>
    public (ImmutableArray<int> Effects, int Sound) BlastOf(int destroyType) =>
        destroyType > 0 && destroyType < Blasts.Length ? Blasts[destroyType] : ([TypeZeroEffect], -2);

    /// <summary>Destruction type 0's effect sprite (0xB300: bank 358, record 0), also the one a
    /// TRANSFRM.PRS stage shows (handler 0x120833).</summary>
    public const int TypeZeroEffect = 0xB300;

    /// <summary>The drop list of destruction type <paramref name="destroyType"/> (−1 none).</summary>
    public int DropListOf(int destroyType) =>
        destroyType >= 0 && destroyType < DropList.Length ? DropList[destroyType] : -1;

    /// <summary>The sprite a destruction of <paramref name="destroyType"/> leaves, picked with
    /// <paramref name="random"/>; −1 nothing.</summary>
    public int PickDrop(int destroyType, SimRandom random)
    {
        var list = DropListOf(destroyType);
        if (list < 0 || list >= Drops.Length || Drops[list].Length == 0)
        {
            return -1;
        }

        var sprite = Drops[list][random.Next(Drops[list].Length)];
        return sprite > 0 ? sprite : -1;
    }

    /// <summary>The shipped table (data/tables destruction + drop_lists).</summary>
    public static DestructionTable Load() =>
        new(GameTables.Destruction.Select(r => r.DropList), GameTables.DropLists.Select(d => d.Sprites.AsEnumerable()),
            GameTables.Destruction.Select(r => (r.Effects, r.Sound)));
}
