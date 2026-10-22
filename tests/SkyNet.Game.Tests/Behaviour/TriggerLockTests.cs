using System.Globalization;
using System.Text.RegularExpressions;

namespace SkyNet.Game.Tests.Behaviour;

/// <summary>
/// A subset of the Godot port's trigger verifier: for chosen records, activating the source the
/// way its mode says (use key, crosshair, proximity, walk-on pad, shot) must produce the effects the
/// Godot port's trigger lock (tests/rules/skynet.triggers.lock, rules 8b26fae25fdffc41) lists for
/// the first activation, and for the second where a second one can happen. The lines below are
/// copied from that lock: offsets and effect names only, no game data.
/// </summary>
public partial class TriggerLockTests
{
    public static TheoryData<string, string> Rows => new()
    {
        // MAP.210: the canyon gate, the cargo truck's path, the doorways.
        { "prox", "210 077f3 F1 prox:eye/3d/r256/onchain/latch chain[077f3 078f7:42 07991:41 079cb:E3 077c3:1D 07643:ED] first[snd77 hint1 voice21029 move@078f7:slideX-128 move@07991:slideX+128] second[-]" },
        { "prox", "210 0c084 F1 prox:eye/3d/r256/onchain/latch chain[0c084 078d3:00 078af:00 0788b:00 07c2c:00 07bd8:00] first[path@078d3] second[-]" },
        { "use", "210 07fcb EF use:eye/3d/r60+26/always/nolatch chain[07fcb* 07fa7:DB 07f83:F0] first[snd40 exit214/0] second[-]" },
        { "use", "210 06db5 EF use:eye/3d/r60+26/always/nolatch chain[06db5* 06d91:DB 06d6d:F0] first[snd40 exit211/0] second[-]" },
        { "use", "211 02882 EF use:eye/3d/r60+26/always/nolatch chain[02882* 0291e:DB 0294c:F0] first[snd40 exitback/11] second[-]" },
        // Doors and lifts behind wall buttons (crosshair).
        { "aim", "214 031b4 EF use:eye/3d/r600/always/nolatch chain[031b4* 02a16:E0 03163:5C 02986:DC] first[snd45 snd41 move@03163:slideY-128] second[snd45 snd41 move@03163:slideY+128]" },
        { "aim", "214 03223 EF use:eye/3d/r600/always/nolatch chain[03223* 02a46:E0 02a76:A9 029b6:E3] first[snd45 snd77 move@02a76:swingY+512] second[snd45 snd77 move@02a76:swingY-512]" },
        { "aim", "214 0365a EF use:eye/3d/r600/always/nolatch chain[0365a* 03461:E0 03620:5C 033ba:DC 035e6:5C] first[snd45 snd41 move@03620:slideY-128 move@035e6:slideY-128] second[snd45 snd41 move@03620:slideY+128 move@035e6:slideY+128]" },
        { "aim", "215 02e54 EF use:eye/3d/r600/always/nolatch chain[02e54* 02f55:6E 02fc9:DC] first[snd41 move@02f55:slideY-378] second[snd41 move@02f55:slideY+378]" },
        { "aim", "215 0303d EF use:eye/3d/r600/always/nolatch chain[0303d* 036f2:E0 03003:5B 02fc9:DC] first[snd45 snd41 move@03003:slideY+128] second[snd45 snd41 move@03003:slideY-128]" },
        { "aim", "215 02f8f EF use:eye/3d/r600/always/nolatch chain[02f8f* 047f0:70 04719:70 031c9:E3 03515:5C 041a7:27 048e3:ED] first[snd77 obj1 voice21039 move@047f0:slideY-1024 move@04719:slideY-1024 move@03515:slideY-128] second[snd77 voice21039 move@047f0:slideY+1024 move@04719:slideY+1024 move@03515:slideY+128]" },
        { "aim", "216 0886b EF use:eye/3d/r600/always/nolatch chain[0886b* 088e2:E1 08947:32 07f53:DD] first[snd46 snd42 move@08947:jumpY-512] second[snd46 snd42 move@08947:jumpY+512]" },
        { "aim", "216 094a8 EF use:eye/3d/r600/always/nolatch chain[094a8* 07d49:AA 0808b:A9 07d0f:E3 093a2:E0] first[snd77 snd45 move@07d49:swingY-512 move@0808b:swingY+512] second[snd77 snd45 move@07d49:swingY+512 move@0808b:swingY-512]" },
        { "aim", "232 0325c EF use:eye/3d/r600/always/nolatch chain[0325c* 031fe:E1 031c4:37 04bb7:26] first[snd46 obj0 move@031c4:rotY+1024] second[snd46 move@031c4:rotY-1024]" },
        // Walk-on pad (MAP.215's floor plate: the four walls slide, objective [M1]).
        { "walk", "215 032cb EF use:eye/3d/r60+26/always/nolatch,walk:feet/floor/never chain[032cb* 03125:77 030eb:44 030b1:78 03077:43 03291:E3 03335:26 048bf:ED] first[snd77 obj0 voice21038 move@03125:slideZ+256 move@030eb:slideX-256 move@030b1:slideZ-256 move@03077:slideX+256] second[snd77 voice21038 move@03125:slideZ-256 move@030eb:slideX+256 move@030b1:slideZ+256 move@03077:slideX-256]" },
        // Use-key gates with destructibles and objectives on the chain.
        { "use", "248 0381d EF use:eye/3d/r60+26/always/nolatch chain[0381d* 03887:E7 02e31:73 035a6:19] first[snd110 break@035a6:1 move@02e31:slideZ+64] second[snd110 break@035a6:2 move@02e31:slideZ-64]" },
        { "use!", "284 03565 EF use:eye/3d/r60+26/always/nolatch chain[03565* 03471:AA 034b8:DC 0335f:19 03441:26] first[snd41 break@0335f:1 obj0 move@03471:swingY-512] second[snd41 break@0335f:2 move@03471:swingY+512]" },
        { "use", "292 03728 EF use:eye/3d/r60+26/always/nolatch chain[03728* 036e1:A9 0342c:26 0282d:E0 0428c:ED] first[obj0 snd45 voice24038 move@036e1:swingY+512] second[snd45 voice24038 move@036e1:swingY-512]" },
        { "use", "253 02f5e EF use:eye/3d/r60+26/always/nolatch chain[02f5e* 02f0d:A9 031d1:EA 03261:1C 032f1:ED 032cd:F0] first[snd113 hint0 voice25028 exit250/10 move@02f0d:swingY+512] second[-]" },
        { "use", "230 0e770 EF use:eye/3d/r60+26/always/nolatch chain[0e770* 0e794:DF 0e7b8:F0] first[snd44 exit231/0] second[-]" },
        // Proximity straight into a doorway (mission 7's tunnel).
        { "prox", "270 0df56 F1 prox:eye/3d/r256/onchain/latch chain[0df56 0df90:F0] first[exit271/0] second[-]" },
        // Shot: a TRANSFRM.PRS wreck one stage per hit; a death chain.
        { "shot", "200 074a5 19 shot:always chain[074a5] first[break@074a5:1] second[break@074a5:2]" },
        { "death", "200 0742e 3B death:hp64/never chain[0742e 073b7:1D 08716:ED] first[hint1 voice20029] second[voice20029 spin@0742e]" },
    };

    [GeneratedRegex(@"^(\d+) ([0-9a-f]{5}) \S+ (\S+) .*first\[([^\]]*)\] second\[([^\]]*)\]")]
    private static partial Regex LockLine();

    private static List<string> Effects(string list) =>
        list == "-" ? [] : [.. list.Split(' ', StringSplitOptions.RemoveEmptyEntries).Order(StringComparer.Ordinal)];

    [BehaviourDataTheory]
    [MemberData(nameof(Rows))]
    public void ActivationMatchesTheLock(string how, string line)
    {
        var m = LockLine().Match(line);
        Assert.True(m.Success, line);
        var map = int.Parse(m.Groups[1].Value, CultureInfo.InvariantCulture);
        var id = int.Parse(m.Groups[2].Value, NumberStyles.HexNumber, CultureInfo.InvariantCulture);
        var mode = m.Groups[3].Value;
        var h = new Harness(map);

        List<string> Activate() => how switch
        {
            "use" or "use!" => h.Use(id),
            "aim" => h.Use(id, aim: true),
            "prox" => h.ProxOnly(id),
            "walk" => h.Walk(id),
            "shot" => h.Shoot(id, 1f),
            "death" => h.Shoot(id, float.Parse(Regex.Match(mode, @"hp(\d+)").Groups[1].Value, CultureInfo.InvariantCulture)),
            _ => throw new ArgumentException(how),
        };

        Assert.Equal(Effects(m.Groups[4].Value), Harness.Comparable(Activate()));
        // A record shot to death and a pad stood on answer nothing more (the pad stays a use-key
        // gate); the lock's second activation is a second chain walk, checked through the key.
        if (how == "walk")
        {
            Assert.Empty(Harness.Comparable(h.Walk(id)));
            Assert.Equal(Effects(m.Groups[5].Value), Harness.Comparable(h.Use(id)));
        }
        else if (how == "use!")
        {
            // Godot xfail (skynet.xfail: 284 03565 chain_short): the wreck is at its last stage after
            // one blow, so the runtime has no second stage to show where the static graph counts one.
            Assert.NotEqual(Effects(m.Groups[5].Value), Harness.Comparable(Activate()));
        }
        else if (how != "death")
        {
            Assert.Equal(Effects(m.Groups[5].Value), Harness.Comparable(Activate()));
        }
    }
}
