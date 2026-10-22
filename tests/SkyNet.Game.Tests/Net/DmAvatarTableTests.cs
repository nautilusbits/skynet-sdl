using SkyNet.Game.Net;

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
}
