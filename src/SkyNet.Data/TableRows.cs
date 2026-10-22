using System.Collections.Immutable;
using SkyNet.Formats;

namespace SkyNet.Data;

/// <summary>
/// One table file in data/tables: a header naming the table, where it lies in the source program
/// and which program and tool wrote it, then the rows. docs/formats/exe-tables.md describes each.
/// </summary>
public sealed record TableFile<T>(string Table, string Address, string Source, string SourceMd5, string Tool, T Rows);

/// <summary>
/// One ammo-type row (v1.01 0x40828, 25 × 0x32 bytes). <see cref="Family"/> is named from the
/// callback at +0x00 ("hitscan", "gravity", "bolt"); <see cref="Model"/> is the .3D a bolt draws
/// when <see cref="Look"/> (+0x04) is a pointer to a model handle, else <see cref="Look"/> is the
/// in-flight sprite (bank &lt;&lt; 7 | record). <see cref="Effect"/> (+0x08) is the impact effect
/// sprite (bank &lt;&lt; 7). Then damage +0x0c (negative = blast), size +0x10, life +0x18, fire sound
/// +0x1c, impact sound +0x20, melee reach +0x24, flags u16 +0x28, pieces u16 +0x2a, piece type u16 +0x2e.
/// </summary>
public sealed record AmmoRow(int Index, uint Callback, string Family, string Model, int Look, int Effect, int Damage, int Size,
    int Life, int FireSound, int ImpactSound, int Reach, int Flags, int Pieces, int PieceType);

/// <summary>
/// One weapon row (v1.01 0x43714, 26 × 0x60 bytes): view X +0x00, ammo type +0x14, speed +0x18,
/// lift +0x1c, rate +0x24, muzzle +0x30/+0x34/+0x38, fire sound +0x48, pool +0x4c, cost +0x50,
/// select sound +0x54, dry sound +0x58, flags +0x5c.
/// </summary>
public sealed record WeaponRow(int Index, int ViewX, int Ammo, int Speed, int Lift, int Rate, int MuzzleX, int MuzzleY, int MuzzleZ,
    int FireSound, int Pool, int Cost, int SelectSound, int DrySound, int Flags);

/// <summary>One ammo pool <c>{current, initial, max}</c> (v1.01 0x440ec, 13 × 12 bytes).</summary>
public sealed record PoolRow(int Index, int Current, int Initial, int Max);

/// <summary>The campaign start lists (v1.01 0x4356c SkyNET, 0x43538 Future Shock; dwords ended by −1).</summary>
public sealed record StartLists(ImmutableArray<int> SkyNet, ImmutableArray<int> FutureShock);

/// <summary>One impact-piece type (v1.01 0x3844b, 24 bytes, ended by type −1).</summary>
public sealed record PieceRow(int Type, int Sprite, int Life, int Speed, int Spread, int Flags);

/// <summary>One destruction row (v1.01 0x424d6, 140 × 14 bytes <c>{u16 effect[4], i16 spread, i16 drop list, i16 sound}</c>).</summary>
public sealed record DestructionRow(int Index, ImmutableArray<int> Effects, int Spread, int DropList, int Sound);

/// <summary>One drop list (v1.01 0x42c7e: 10 pointers to <c>{i32 count, u16 sprite[count]}</c>); sprite 0 = nothing.</summary>
public sealed record DropListRow(int Index, ImmutableArray<int> Sprites);

/// <summary>The "YOU ENTERED" line: the prefix (v1.01 0x35d00) and the rows <c>{i16 map, char name[36]}</c> (0x53d00).</summary>
public sealed record LocationTable(string Prefix, ImmutableArray<LocationRow> Rows);

public sealed record LocationRow(int Map, string Name);

/// <summary>The enemy count of each mission's statistics row (v1.01 0x53520 SkyNET, 0x53400 Future Shock; +8 of 16-byte rows).</summary>
public sealed record MissionEnemies(ImmutableArray<int> SkyNet, ImmutableArray<int> FutureShock);

/// <summary>
/// One pickup item (SKYNET.EXE v1.00 0x35800; docs/game-logic/pickups.md): the sprite (bank &lt;&lt; 7 |
/// record), the ammo pool and amount (pool −1 none), the heal percentage, armour in 1/65536 of the
/// bar, the weapon granted and selected (−1 none) and the STRINGS.PRS key of its "PICKED UP" line.
/// </summary>
public sealed record ItemRow(int Sprite, int Pool, int Amount, int Heal, int Armour, int Weapon, string Message);

/// <summary>One DIFFICULTY setting's multipliers (0x5c210 rows 1, 4, 7): "low", "medium" or "high".</summary>
public sealed record DifficultyRow(string Level, double EnemyFire, double DamageToEnemy, double DamageToPlayer, double ArmourRegen);

/// <summary>
/// A sprite drawn as a glTF model in the enhanced look (a mod rule, not in SKYNET.EXE): the sprite
/// "BANK/RECORD" ("214/007"), the model file in the mod's <c>models/</c> ("medkit.glb"), world units
/// per glTF unit (0: fit to the sprite's height), the yaw (degrees), the spin (degrees a second), the
/// bob's amplitude and the lift (world units).
/// </summary>
public sealed record SpriteModelRow(string Sprite, string Model, double Scale, double Yaw, double Spin, double Bob, double OffsetY);

/// <summary>
/// How a sample plays (a mod rule, not in SKYNET.EXE): the sample's name without extension
/// ("EXPLO1" for EXPLO1.RAW), a gain multiplier (0-2), the audible distance of a placed sound in
/// world units (0: the game's own), a pitch multiplier, the voice-stealing priority (higher keeps
/// its voice) and whether a sound the game loops loops (false: once per approach). A sample
/// without a row plays as <see cref="Default"/>, which is the game without the table.
/// </summary>
public sealed record SoundEventRow(string Sample, double Volume, double Range, double Pitch, int Priority, bool Loop)
{
    public const double MaxVolume = 2, MinPitch = 0.25, MaxPitch = 4;

    /// <summary>The row that changes nothing.</summary>
    public static SoundEventRow Default(string sample) => new(sample, 1, 0, 1, 0, true);

    /// <summary>The table's key of a sample name: "explo1.raw" → "EXPLO1".</summary>
    public static string KeyOf(string sample) => Path.GetFileNameWithoutExtension(sample).ToUpperInvariant();
}

/// <summary>
/// How one enhanced-look effect draws (a mod rule, not in SKYNET.EXE): the effect's name ("fire",
/// "smoke", "explosion"...), its particles at PARTICLES LOW and HIGH, its life (seconds), then
/// multipliers over the game's own shape (1: as the game draws it): size, speed, rise, spread,
/// gravity, intensity (brightness or cover) and flicker; and a colour filter (0-1 a channel, 1 1 1:
/// the game's colours). An effect reads only some columns (SkyNet.Game.Enhanced.EffectTable.Reads).
/// </summary>
public sealed record EffectRow(string Effect, int Low, int High, double Life, double Size, double Speed, double Rise,
    double Spread, double Gravity, double Intensity, double Flicker, double R, double G, double B)
{
    /// <summary>The most particles a row may ask for at a level.</summary>
    public const int MaxCount = 1024;
}

/// <summary>
/// A dusk map's sky in the enhanced look (a mod rule, not in SKYNET.EXE): its map (a decade's first,
/// 250, covers 250-259; an exact map's row wins), the sRGB colours (0-1) of the horizon (the haze the
/// far terrain fades into), the deep red low over it, the zenith, the sunset's glow and the clouds;
/// the glow's height and half width along the horizon (degrees) and the clouds' cover (0-1).
/// </summary>
public sealed record SkyRow(int Map, double HorizonR, double HorizonG, double HorizonB, double LowR, double LowG, double LowB,
    double ZenithR, double ZenithG, double ZenithB, double GlowR, double GlowG, double GlowB, double SunElevation, double GlowWidth,
    double Cover, double CloudR, double CloudG, double CloudB);

/// <summary>
/// A night map's sky in the enhanced look (a mod rule): its map (a decade's first covers the decade;
/// an exact map's row wins), the stars (a multiple of the default count, 0-4), their brightness (0-4),
/// the Milky Way's strength (0-4), the night clouds' cover (0-1) and sRGB colour (0-1), the moon's halo (0-4).
/// </summary>
public sealed record NightSkyRow(int Map, double Stars, double StarBrightness, double MilkyWay, double Cover, double CloudR,
    double CloudG, double CloudB, double MoonGlow);

/// <summary>The table files, by name (data/tables/&lt;name&gt;.json).</summary>
public static class TableNames
{
    /// <summary>Tables without a shipped file: the game's defaults in code are what a mod's file lies over.</summary>
    public const string Items = "items", Difficulty = "difficulty", SpriteModels = "sprite_models",
        Sounds = "sounds", Effects = "effects", Skies = "skies", NightSkies = "night_skies";

    public const string EnemyTypes = "enemy_types", AisOps = "ais_ops", AisAnims = "ais_anims", Ammo = "ammo_types",
        Weapons = "weapons", Pools = "ammo_pools", StartLists = "start_weapons", Pieces = "impact_pieces",
        Destruction = "destruction", DropLists = "drop_lists", Locations = "location_names", MissionEnemies = "mission_enemies";

    public static readonly ImmutableArray<string> All =
        [EnemyTypes, AisOps, AisAnims, Ammo, Weapons, Pools, StartLists, Pieces, Destruction, DropLists, Locations, MissionEnemies];
}
