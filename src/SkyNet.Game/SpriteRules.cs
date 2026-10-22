namespace SkyNet.Game;

/// <summary>Which map sprites are pickups, and which DOS draws at full light.</summary>
public static class SpriteRules
{
    /// <summary>The sprite indices (bank &lt;&lt; 7 | record) of the SKYNET.EXE item table (v1.00 VA
    /// 0x35800, 32-byte records, sprite first): FUN_0011d600 gives exactly these act 0xFD at map
    /// start. The weapon/equipment banks 200, 201, 214 hold records the table leaves out; those
    /// stay scenery. The same list as the Godot port's pickup_data.gd ITEMS.</summary>
    public static readonly IReadOnlySet<int> Items = new HashSet<int>
    {
        25600, 25601, 25602, 25603, 25604, 25605, 25606, 25607, 25608, 25609, 25611, 25612, 25613, 25614, 25615,
        25728, 25729, 25731, 25733, 25734, 25735, 25736, 25737, 25738,
        27392, 27393, 27394, 27395, 27399, 27400, 27401, 27402, 27405, 27406,
    };

    public static bool IsItem(int spriteIndex) => Items.Contains(spriteIndex);

    /// <summary>The sprites FUN_00124716 (v1.01) lights at 0x3f whatever the room: the fires —
    /// 192/0, 192/2, 206/1, 208/2-3, 216/5, 216/11-12, 218/0-3.</summary>
    public static bool FullBright(int spriteIndex)
    {
        if (spriteIndex is 0x6701 or 0x6002 or 0x6000)
        {
            return true;
        }

        var record = spriteIndex & 0x7F;
        return (spriteIndex >> 7) switch
        {
            0xd0 => record is >= 2 and <= 3,
            0xd8 => record is 5 or 11 or 12,
            0xda => record <= 3,
            _ => false,
        };
    }
}
