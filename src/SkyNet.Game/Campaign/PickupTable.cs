using System.Collections.Immutable;
using RetroEng.Content;
using SkyNet.Data;

namespace SkyNet.Game.Campaign;

/// <summary>One item of the table: the ammo pool and amount (pool −1 none), the heal percentage,
/// armour in 1/65536 of the bar, the weapon record granted and selected (−1 none) and the
/// STRINGS.PRS key of the "PICKED UP …" line.</summary>
public readonly record struct PickupItem(int Pool, int Amount, int HealPercent, int Armour, int Weapon, string MessageKey);

/// <summary>
/// The item table (SKYNET.EXE v1.00 0x35800; docs/game-logic/pickups.md), as the Godot port's
/// generated pickup_data.gd ITEMS holds it: sprite index (bank &lt;&lt; 7 | record) → effect.
/// Handler 0x11d670 collects within 80 units across and 160 up or down, plays sound 179, and the
/// pickup is always consumed. Keys match <see cref="SpriteRules.Items"/>. A mod's
/// <c>tables/items.json</c> changes rows of <see cref="Dos"/> by sprite (docs/modding.md#tables).
/// </summary>
public static class PickupTable
{
    public const float GrabAcross = 80f, GrabVertical = 160f;
    /// <summary>SKYNET.EXE's pickup handler (0x11d670) asks for sound 0xB3, past the 126 names of the
    /// sound table, so the port plays CLICK.RAW instead, as the Godot port's pickup.gd does.</summary>
    public const int Sound = 10;

    /// <summary>The items in effect: <see cref="Dos"/> with the mods' rows (<see cref="GameTables.Mods"/>).</summary>
    public static IReadOnlyDictionary<int, PickupItem> Items
    {
        get
        {
            var mods = GameTables.Mods;
            var cached = Volatile.Read(ref _loaded);
            if (cached == null || !ReferenceEquals(cached.From, mods))
            {
                cached = new Loaded(Load(mods), mods);
                Volatile.Write(ref _loaded, cached);
            }

            return cached.Items;
        }
    }

    private sealed record Loaded(IReadOnlyDictionary<int, PickupItem> Items, VirtualFileSystem? From);

    private static Loaded? _loaded;

    /// <summary><see cref="Dos"/> as the items table's rows (what a mod's file lies over).</summary>
    public static TableFile<ImmutableArray<ItemRow>> DosTable => new(TableNames.Items, "0x35800", "SKYNET.EXE v1.00", "", "PickupTable",
        [.. Dos.Select(i => new ItemRow(i.Key, i.Value.Pool, i.Value.Amount, i.Value.HealPercent, i.Value.Armour, i.Value.Weapon, i.Value.MessageKey))]);

    /// <summary>The items with the <c>tables/items.json</c> of <paramref name="mods"/> laid over <see cref="Dos"/>;
    /// a row whose sprite is not a DOS item throws <see cref="InvalidDataException"/>.</summary>
    public static IReadOnlyDictionary<int, PickupItem> Load(VirtualFileSystem? mods)
    {
        var rows = GameTables.Layer(TableNames.Items, TableSchemas.Items, DosTable, mods).Rows;
        if (rows.FirstOrDefault(r => !Dos.ContainsKey(r.Sprite)) is { } stray)
        {
            throw new InvalidDataException($"Table {TableNames.Items}: sprite {stray.Sprite} is not an item sprite");
        }

        return rows.ToDictionary(r => r.Sprite, r => new PickupItem(r.Pool, r.Amount, r.Heal, r.Armour, r.Weapon, r.Message));
    }

    /// <summary>The DOS items, sprite → effect.</summary>
    public static readonly IReadOnlyDictionary<int, PickupItem> Dos = new Dictionary<int, PickupItem>
    {
        [25600] = new(0, 100, 0, 0, 3, "Str_50cal"),
        [25601] = new(0, 100, 0, 0, -1, "Str_Bullets"),
        [25602] = new(0, 25, 0, 0, -1, "Str_Bullet"),
        [25603] = new(8, 1, 0, 0, -1, "Str_Canister"),
        [25604] = new(9, 1, 0, 0, -1, "Str_Satchel"),
        [25605] = new(6, 1, 0, 0, -1, "Str_Molotov"),
        [25606] = new(2, 10, 0, 0, 5, "Str_GLaunch"),
        [25607] = new(2, 1, 0, 0, -1, "Str_Grenade"),
        [25608] = new(2, 1, 0, 0, -1, "Str_Grenade"),
        [25609] = new(4, 50, 0, 0, 8, "Str_LCann"),
        [25611] = new(4, 35, 0, 0, 7, "Str_LRifle"),
        [25612] = new(0, 50, 0, 0, 2, "Str_Machine"),
        [25613] = new(4, 100, 0, 0, 11, "Str_PprCann"),
        [25614] = new(2, 5, 0, 0, -1, "Str_Grenades"),
        [25615] = new(2, 5, 0, 0, -1, "Str_Grenades"),
        [25728] = new(5, 1, 0, 0, -1, "Str_Pipe"),
        [25729] = new(4, 50, 0, 0, 10, "Str_PprMed"),
        [25731] = new(4, 25, 0, 0, 9, "Str_PprSml"),
        [25733] = new(3, 2, 0, 0, 6, "Str_RLaunch"),
        [25734] = new(3, 1, 0, 0, -1, "Str_Rocket"),
        [25735] = new(1, 25, 0, 0, -1, "Str_Shell"),
        [25736] = new(1, 20, 0, 0, 4, "Str_Shotgun"),
        [25737] = new(0, 25, 0, 0, 1, "Str_Uzi"),
        [25738] = new(3, 5, 0, 0, -1, "Str_Rockets"),
        [27392] = new(-1, 0, 0, 3277, -1, "Str_Armor1"),
        [27393] = new(-1, 0, 0, 6554, -1, "Str_Armor2"),
        [27394] = new(-1, 0, 0, 16384, -1, "Str_Armor3"),
        [27395] = new(-1, 0, 0, 32768, -1, "Str_Armor4"),
        [27399] = new(-1, 0, 50, 0, -1, "Str_Health4"),
        [27400] = new(-1, 0, 25, 0, -1, "Str_Health3"),
        [27401] = new(-1, 0, 10, 0, -1, "Str_Health2"),
        [27402] = new(-1, 0, 5, 0, -1, "Str_Health1"),
        [27405] = new(4, 10, 0, 0, -1, "Str_EnergyC"),
        [27406] = new(4, 25, 0, 0, -1, "Str_EnergyB"),
    };
}
