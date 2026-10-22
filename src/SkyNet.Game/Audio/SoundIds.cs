namespace SkyNet.Game.Audio;

/// <summary>
/// SKYNET.EXE's sound-id table (v1.00 VA 0x4ff00: 126 records of 20 bytes, field 0 a pointer to the
/// 8.3 file name in MDMDSFXS.BSA). Every id-based sound in the engine indexes it: weapon records
/// (fire, select, dry fire), ammo types (fire, impact), the action table's one-shot sound nodes
/// (0xdb-0xec) and AI frame events. -1 means "no sound" in every DOS table. v1.01 keeps the table
/// at VA 0x50100: +0 the name, +4 the volume (0-128), +8 the pitch (128 = the clip's 11025 Hz),
/// +0xc a second level the 3D path checks, +0x10 how many voices play it now.
/// </summary>
public static class SoundIds
{
    public const int PipeHit = 0;
    public const int JumpMetal = 0x18;
    public const int Explosion1 = 33;
    public const int Wind = 87;

    public static readonly IReadOnlyList<string> Names =
    [
        "pipehit1.raw", "shots5.raw", "shots2.raw", "shots3.raw", "shtgun.raw",
        "sgcock1.raw", "sgcock2.raw", "grnlaun2.raw", "rocket2.raw", "uzicock3.raw",
        "click.raw", "laser1.raw", "laser2.raw", "laser8.raw", "laser6.raw",
        "laser3.raw", "fizzle1.raw", "ppcload.raw", "grunt1.raw", "geiger1.raw",
        "geiger2.raw", "heart1.raw", "heart2.raw", "jmpcon.raw", "jmpmet.raw",
        "jmpwod.raw", "rocket1.raw", "collide1.raw", "hit2.raw", "skid2.raw",
        "carcoll2.raw", "richo5.raw", "richo8.raw", "explo1.raw", "explo2.raw",
        "explo3.raw", "explo4.raw", "explo5.raw", "gauss1.raw", "ppc100.raw",
        "doora.raw", "doorb.raw", "doorc.raw", "doord.raw", "doorw.raw",
        "button1.raw", "button2.raw", "lever1.raw", "hk2.raw", "hk2.raw",
        "hk2.raw", "hk2.raw", "hk2.raw", "hk2.raw", "hk2.raw",
        "tank1.raw", "tank1.raw", "tank1.raw", "hvyft5.raw", "hydra2.raw",
        "hydra5.raw", "hvyft4.raw", "press1.raw", "fire.raw", "amtech1.raw",
        "amtech2.raw", "amtech3.raw", "amtech4.raw", "hk2.raw", "careng1.raw",
        "lfwgrv.raw", "rfwgrv.raw", "lfwwod.raw", "rfwwod.raw", "lfwmet.raw",
        "rfwmet.raw", "explo6.raw", "elevat1.raw", "fastgun2.raw", "fastgun2.raw",
        "hydra3.raw", "comm1.raw", "swish1.raw", "fire.raw", "lfwsew.raw",
        "rfwsew.raw", "jmpsew.raw", "wind.raw", "windmet.raw", "windwod.raw",
        "bubbles.raw", "drips.raw", "water.raw", "metdoor.raw", "miltkill.raw",
        "miltdies.raw", "motfind1.raw", "motloop2.raw", "head1.raw", "head2.raw",
        "hitbycar.raw", "grunt2.raw", "grunt3.raw", "grunt4.raw", "grunt5.raw",
        "grunt6.raw", "grunt7.raw", "grunt8.raw", "grunt9.raw", "movedoor.wav",
        "ibeam.wav", "power1.wav", "watmove.wav", "subdoor.wav", "rap.wav",
        "splash.wav", "drown.wav", "getair.wav", "bubbles2.wav", "pings.wav",
        "subalarm.wav", "carstart.wav", "getair2.wav", "leftwatr.wav", "rightwtr.wav",
        "torpedo.wav",
    ];

    /// <summary>Each id's volume of 128 (v1.01 0x50100 +4): the 2D path plays it at volume × master / 128
    /// (FUN_0012ef06), the 3D path also scales it by the distance (FUN_0012f018).</summary>
    public static readonly IReadOnlyList<byte> DosVolume =
    [
        100, 100, 100, 128, 128, 100, 100, 128, 100, 100, 100, 105, 100, 100, 100,
        100, 100, 100, 80, 80, 80, 60, 60, 100, 100, 100, 100, 100, 100, 100,
        100, 100, 100, 128, 128, 128, 128, 128, 128, 60, 100, 100, 100, 100, 100,
        100, 100, 100, 128, 128, 80, 60, 60, 40, 50, 128, 100, 70, 100, 50,
        50, 100, 100, 60, 50, 50, 100, 100, 30, 90, 40, 40, 40, 40, 40,
        40, 128, 100, 128, 128, 40, 100, 100, 40, 100, 100, 128, 100, 100, 100,
        100, 100, 100, 100, 100, 100, 70, 70, 128, 100, 128, 128, 128, 128, 128,
        128, 128, 128, 128, 128, 128, 128, 128, 128, 128, 128, 128, 128, 128, 128,
        128, 128, 128, 128, 128, 128,
    ];

    /// <summary>Each id's pitch of 128 (v1.01 0x50100 +8): any other value plays the clip at
    /// 11025 × pitch / 128 Hz (the HK's hk2.raw variants, the left gravel step's 80).</summary>
    public static readonly IReadOnlyList<byte> DosPitch =
    [
        128, 128, 128, 128, 128, 128, 128, 128, 128, 128, 128, 128, 128, 128, 128,
        128, 128, 128, 128, 128, 128, 128, 128, 128, 128, 128, 128, 128, 128, 128,
        128, 128, 128, 128, 128, 128, 128, 128, 128, 128, 128, 128, 128, 128, 128,
        128, 128, 128, 128, 77, 134, 115, 173, 154, 102, 102, 128, 154, 128, 128,
        128, 128, 128, 128, 128, 128, 128, 128, 128, 128, 80, 128, 128, 128, 128,
        128, 128, 128, 128, 128, 128, 128, 128, 128, 128, 128, 128, 128, 128, 128,
        128, 128, 128, 128, 128, 128, 128, 128, 128, 128, 128, 128, 128, 128, 128,
        128, 128, 128, 128, 128, 128, 128, 128, 128, 128, 128, 128, 128, 128, 128,
        128, 128, 128, 128, 128, 128,
    ];

    /// <summary>An id's DOS volume as a gain (volume / 128); 0 for a bad id.</summary>
    public static float DosGain(int id) => id >= 0 && id < DosVolume.Count ? DosVolume[id] / 128f : 0f;

    /// <summary>An id's DOS pitch as a rate multiple (pitch / 128); 1 for a bad id.</summary>
    public static float DosRate(int id) => id >= 0 && id < DosPitch.Count ? DosPitch[id] / 128f : 1f;

    /// <summary>The archive file of a sound id, null for -1 and ids out of range.</summary>
    public static string? NameOf(int id) => id >= 0 && id < Names.Count ? Names[id] : null;
}
