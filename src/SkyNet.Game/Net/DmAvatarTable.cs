namespace SkyNet.Game.Net;

/// <summary>One of DOS's thirteen deathmatch avatars (SKYNET.EXE v1.01 table 0x312bf + n·0x6d, file
/// offset 0x84dd0): +0 the skin's texture bank, +4.. the body, head, head-shot, idle body and idle
/// head models; <see cref="NativeSkin"/> is the bank the models are textured from in the files,
/// which the skin replaces.</summary>
public sealed record DmAvatarLook(int Number, int Skin, string Body, string Head, string IdleBody, string IdleHead, int NativeSkin)
{
    /// <summary>Avatars 7 and 13 are machines (0x134a39): the TERMINATOR class here.</summary>
    public bool Machine => Number is DmAvatarTable.MachineSoldier or DmAvatarTable.MachineWoman;

    public DmClass Class => Machine ? DmClass.Terminator : DmClass.Human;

    /// <summary>The skin as a bank remap for <see cref="ModelMesh.Surfaces"/> (none for the files' own).</summary>
    public (int From, int To)? SkinRemap => Skin == NativeSkin ? null : (NativeSkin, Skin);

    /// <summary>"SOLDIER 2", "TERMINATOR 4", "WOMAN 1": the kind and which of its skins.</summary>
    public string Name => $"{Kind} {Number - DmAvatarTable.FirstOf(Number) + 1}";

    /// <summary>The kind: SOLDIER (1-3), TERMINATOR (4-7), WOMAN (8-13).</summary>
    public string Kind => Number <= 3 ? "SOLDIER" : Number <= 7 ? "TERMINATOR" : "WOMAN";
}

/// <summary>DOS's avatar table (NETMENU1's left box cycles it, FUN_00128b3b: [0x31f37] 1..13).</summary>
public static class DmAvatarTable
{
    public const int Count = 13;
    public const int MachineSoldier = 7, MachineWoman = 13;

    private const int SoldierBank = 0x132, TerminatorBank = 0x130, WomanBank = 0x125;

    private static DmAvatarLook Soldier(int n, int skin) => new(n, skin, "AVSOLDER.3D", "AVSOLHED.3D", "AVSOLIDB.3D", "AVSOLIDH.3D", SoldierBank);

    private static DmAvatarLook Terminator(int n, int skin) => new(n, skin, "AVTRMNTR.3D", "AVTRMHED.3D", "AVTRMIDB.3D", "AVTRMIDH.3D", TerminatorBank);

    private static DmAvatarLook Woman(int n, int skin) => new(n, skin, "AVFEMALE.3D", "AVFEMHED.3D", "AVFEMIDB.3D", "AVFEMIDH.3D", WomanBank);

    /// <summary>Avatars 1..13 (index 0 = avatar 1).</summary>
    public static readonly IReadOnlyList<DmAvatarLook> All =
    [
        Soldier(1, 0x132), Soldier(2, 0x133), Soldier(3, 0x13d),
        Terminator(4, 0x13c), Terminator(5, 0x13b), Terminator(6, 0x13a), Terminator(7, 0x130),
        Woman(8, 0x125), Woman(9, 0x134), Woman(10, 0x135), Woman(11, 0x136), Woman(12, 0x137), Woman(13, 0x13e),
    ];

    /// <summary>Avatar <paramref name="number"/> (1..13; out of range wraps to the first).</summary>
    public static DmAvatarLook Get(int number) => All[number is >= 1 and <= Count ? number - 1 : 0];

    /// <summary>The next avatar <paramref name="step"/> away, wrapping (FUN_00128b3b's either way).</summary>
    public static int Step(int number, int step) => ((Math.Clamp(number, 1, Count) - 1 + step) % Count + Count) % Count + 1;

    /// <summary>The gun avatar <paramref name="number"/> holds for weapon record <paramref name="record"/>
    /// (its +0x45 table, 29-byte entries: 0x31c37 AVSOL* for soldiers and terminators, 0x31adb AVFEM*
    /// for women; each a model with the body's frames); the super UZI shows the UZI (the owner's
    /// call: DOS has no entry for it); null for any other record past 11.</summary>
    public static string? Gun(int number, int record)
    {
        if (record == Weapons.WeaponTable.SuperUzi)
        {
            record = 1;
        }

        if (record < 0 || record >= GunNames.Length)
        {
            return null;
        }

        return Get(number).Body == "AVFEMALE.3D" ? WomenGuns[record] : SoldierGuns[record];
    }

    /// <summary>The weapon records with a gun in hand (0..11).</summary>
    internal static int GunCount => GunNames.Length;

    // Records 0..11: PIPE, UZI, ASSAULT RIFLE … PLASMA CANNON.
    private static readonly string[] GunNames = ["TRK", "UZI", "M16", "CHN", "SHT", "GRN", "RKT", "LSR", "BIG", "LSP", "LSL", "LSC"];

    // The file names made once: the capture asks every frame.
    private static readonly string[] SoldierGuns = [.. GunNames.Select(g => $"AVSOL{g}.3D")];
    private static readonly string[] WomenGuns = [.. GunNames.Select(g => $"AVFEM{g}.3D")];

    /// <summary>The first avatar of <paramref name="number"/>'s kind.</summary>
    public static int FirstOf(int number) => number <= 3 ? 1 : number <= 7 ? 4 : 8;

    /// <summary>The avatar a saved class stands for when no avatar was saved: the first soldier, or the machine.</summary>
    public static int ForClass(DmClass cls) => cls == DmClass.Terminator ? MachineSoldier : 1;

    /// <summary>Avatar <paramref name="number"/> when it is one (1..13) of <paramref name="cls"/>, else
    /// the class's default: what a server keeps of what a player asked for.</summary>
    public static int Resolve(int number, DmClass cls) =>
        number is >= 1 and <= Count && Get(number).Class == cls ? number : ForClass(cls);

    /// <summary>The avatar on the wire: its index 0..12.</summary>
    public static byte ToWire(int number) => (byte)(Math.Clamp(number, 1, Count) - 1);

    /// <summary>A wire index read back (0..12, else the class default), checked against the class.</summary>
    public static int FromWire(byte index, DmClass cls) => index < Count ? Resolve(index + 1, cls) : ForClass(cls);

    /// <summary>The avatars of <paramref name="cls"/> (a bot picks one of them).</summary>
    public static IReadOnlyList<int> OfClass(DmClass cls) => cls == DmClass.Terminator ? Machines : Humans;

    private static readonly int[] Machines = [.. All.Where(a => a.Machine).Select(a => a.Number)];
    private static readonly int[] Humans = [.. All.Where(a => !a.Machine).Select(a => a.Number)];
}
