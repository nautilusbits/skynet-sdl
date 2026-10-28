using SkyNet.Game.Net;
using SkyNet.Game.Weapons;

namespace SkyNet.Game.Tests.Net;

/// <summary>DOS's avatar table (SKYNET.EXE v1.01 0x312bf): thirteen avatars, the skins by texture
/// bank, avatars 7 and 13 the machines.</summary>
public class DmAvatarTableTests
{
    [Fact]
    public void Thirteen_avatars_three_soldiers_four_terminators_six_women_two_machines()
    {
        Assert.Equal(13, DmAvatarTable.All.Count);
        Assert.Equal(Enumerable.Range(1, 13), DmAvatarTable.All.Select(a => a.Number));
        Assert.Equal([7, 13], DmAvatarTable.All.Where(a => a.Machine).Select(a => a.Number));
        Assert.Equal([0x132, 0x133, 0x13d, 0x13c, 0x13b, 0x13a, 0x130, 0x125, 0x134, 0x135, 0x136, 0x137, 0x13e],
            DmAvatarTable.All.Select(a => a.Skin));
        Assert.Equal(3, DmAvatarTable.All.Count(a => a.IdleBody == "AVSOLIDB.3D"));
        Assert.Equal(4, DmAvatarTable.All.Count(a => a.IdleBody == "AVTRMIDB.3D"));
        Assert.Equal(6, DmAvatarTable.All.Count(a => a.IdleBody == "AVFEMIDB.3D"));
        // The files' own skins need no remap.
        Assert.Equal([1, 7, 8], DmAvatarTable.All.Where(a => a.SkinRemap == null).Select(a => a.Number));
        Assert.Equal((0x132, 0x13d), DmAvatarTable.Get(3).SkinRemap);
    }

    [Fact]
    public void The_box_cycles_either_way_and_wraps()
    {
        Assert.Equal(2, DmAvatarTable.Step(1, 1));
        Assert.Equal(13, DmAvatarTable.Step(1, -1));
        Assert.Equal(1, DmAvatarTable.Step(13, 1));
        Assert.Equal(("TERMINATOR 4", "WOMAN 1"), (DmAvatarTable.Get(7).Name, DmAvatarTable.Get(8).Name));
        Assert.Equal((1, 7), (DmAvatarTable.ForClass(DmClass.Human), DmAvatarTable.ForClass(DmClass.Terminator)));
    }

    [Fact]
    public void Each_avatar_holds_its_kinds_gun_for_records_0_to_11()
    {
        // +0x45: 0x31c37 (AVSOL*, terminators too) or 0x31adb (AVFEM*), one model per record 0..11.
        Assert.Equal("AVSOLUZI.3D", DmAvatarTable.Gun(1, 1));
        Assert.Equal("AVFEMLSC.3D", DmAvatarTable.Gun(8, 11));
        Assert.All(Enumerable.Range(4, 4), n => Assert.Equal("AVSOLTRK.3D", DmAvatarTable.Gun(n, 0)));
        Assert.Equal("AVFEMM16.3D", DmAvatarTable.Gun(13, 2));
        // The owner's call: the super UZI (no entry in DOS) shows the UZI.
        Assert.Equal("AVFEMUZI.3D", DmAvatarTable.Gun(8, WeaponTable.SuperUzi));
        Assert.Null(DmAvatarTable.Gun(1, WeaponTable.SuperUzi + 1));
        Assert.Null(DmAvatarTable.Gun(1, -1));
    }
}
