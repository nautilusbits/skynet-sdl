using System.Buffers.Binary;
using System.Text;

namespace SkyNet.Formats.Tests;

public class AisDataTests
{
    const uint Base = 0x30000;
    const int Stub = 0x100;          // the application's MZ stub, after a fake extender
    const int DataPages = 0x400;     // relative to the stub
    const int ObjSize = 0x20000;

    /// <summary>A bound LE image: a leading "extender" MZ, then the app stub, header and one object at VA 0x30000.</summary>
    sealed class Image
    {
        public readonly byte[] File = new byte[Stub + DataPages + ObjSize];

        public Image(string version = "1.01")
        {
            File[0] = (byte)'M'; File[1] = (byte)'Z';
            File[Stub] = (byte)'M'; File[Stub + 1] = (byte)'Z';
            W(Stub + 0x3C, 0x80);
            int le = Stub + 0x80;
            "LE"u8.CopyTo(File.AsSpan(le));
            W(le + 0x14, ObjSize / 4096);
            W(le + 0x28, 4096);
            W(le + 0x2C, 4096);
            W(le + 0x40, 0xC4);
            W(le + 0x44, 1);
            W(le + 0x80, DataPages);
            int ot = le + 0xC4;
            W(ot, ObjSize); W(ot + 4, Base); W(ot + 12, 1); W(ot + 16, ObjSize / 4096);
            Str(0x4F000, "TFS: SkyNet v" + version + " (---)");
        }

        void W(int off, long v) => BinaryPrimitives.WriteUInt32LittleEndian(File.AsSpan(off), (uint)v);
        int Fo(uint va) => Stub + DataPages + (int)(va - Base);
        public void U32(uint va, long v) => W(Fo(va), v);
        public void U16(uint va, int v) => BinaryPrimitives.WriteUInt16LittleEndian(File.AsSpan(Fo(va)), (ushort)v);
        public void Ptr(uint va, uint target) => U32(va, target - Base);
        public void Str(uint va, string s) => Encoding.ASCII.GetBytes(s).CopyTo(File, Fo(va));
        public void Op(uint va, uint handlerV100, long a, long b, long c, uint shift = 0x500)
        {
            U32(va, handlerV100 + shift - Base); U32(va + 4, a); U32(va + 8, b); U32(va + 12, c);
        }
    }

    static Image Globe()
    {
        var img = new Image();
        uint t = AisData.V101.EnemyTable;
        img.Ptr(t, 0x46000);
        img.U32(t + 8, 6);
        img.Ptr(t + 0x10, 0x46600);
        img.Ptr(t + 0x14, 0x46100);
        img.Str(0x46004, "GLOBE.3D");
        int[] p = new int[36];
        p[0] = 50; p[4] = 120; p[6] = 25; p[7] = 512;
        p[12] = 1; p[13] = 2; p[14] = 3; p[15] = 6; p[16] = 800; p[17] = 46340; p[19] = 100; p[20] = 500;
        for (int k = 0; k < 36; k++)
            img.U32(0x46100 + (uint)(4 * k), p[k]);
        img.Ptr(0x46100 + 4 * 21, 0x46400);
        // IF see ne0 -> 0x46440; ANIM 0x46500; SOUND 9; GOTO start; (0x46440) WAIT 70 freeze; unknown -> end.
        img.Op(0x46400, 0x12A5C1, 0x12A6B8 + 0x500 - Base, 0, 0x46440 - Base);
        img.Op(0x46410, 0x12A60C, 0x46500 - Base, 0, 0);
        img.Op(0x46420, 0x12A640, 9, 0, 0);
        img.Op(0x46430, 0x12A5D4, 0x46400 - Base, 0, 0);
        img.Op(0x46440, 0x12A5D9, 70, 0, 1);
        img.U32(0x46450, 0x1234);
        img.U32(0x46500, 1); img.U32(0x46504, 2); img.U32(0x46508, 12); img.U16(0x4650C, 0x108);
        img.U16(0x4650E, 3); img.U16(0x46510, 0); img.U16(0x46512, 4); img.U16(0x46514, 1);
        // Death: kind 1 -> a two-entry list.
        img.U32(0x46600, 1); img.Ptr(0x46604, 0x46700);
        img.U32(0x46700, 1); img.U32(0x46704, 88); img.U32(0x46708, 35);
        img.U32(0x46720, 0); img.U32(0x46724, 89); img.U32(0x46728, -100);
        return img;
    }

    [Fact]
    public void Le_image_is_found_behind_the_extender()
    {
        var exe = LeExecutable.Parse(new Image().File)!;
        Assert.Equal(Stub, exe.StubOffset);
        Assert.Equal("1.01", exe.SkyNetVersion);
        Assert.Equal(Stub + DataPages + 0x10, exe.FileOffsetOf(Base + 0x10));
        Assert.Equal(-1, exe.FileOffsetOf(Base + ObjSize));
        Assert.Null(LeExecutable.Parse("not an exe"u8));
    }

    [Fact]
    public void Type_params_script_anim_and_death_decode()
    {
        var ais = AisData.Parse(Globe().File)!;
        var t = Assert.Single(ais.Types);
        Assert.Equal(("globe", "GLOBE.3D", 6, 50), (t.Name, t.ModelFile, t.State, t.HitPoints));
        Assert.Equal((120, 25, 512), (t.Speed, t.Near, t.Turn));
        Assert.Null(t.Axis);
        Assert.Equal(new AisFire(1, 2, 3, 6, 800, 100, 500, 46340), t.Fire);
        Assert.Equal(0x46400u, t.ScriptVa);
        Assert.Equal([new EnemyDeathPart(88, 35, 0, 0), new EnemyDeathPart(89, -100, 0, 0)], t.Death.ToArray());

        var ops = ais.ScriptOps(0x46400);
        Assert.Equal([AisOpKind.If, AisOpKind.Anim, AisOpKind.Wait, AisOpKind.Sound, AisOpKind.End, AisOpKind.Goto],
            ops.Select(o => o.Kind));
        Assert.Equal((AisPredicate.See, AisCompare.Ne0, 0x46440u), (ops[0].Predicate, ops[0].Compare, ops[0].Target));
        Assert.Equal((70, 1), (ops[2].A, ops[2].C));
        var anim = ais.Anims[ops[1].AnimVa];
        Assert.Equal((12, (ushort)0x108), (anim.Fps, anim.Flags));
        Assert.Equal([new AisAnimFrame(3, 0), new AisAnimFrame(4, 1)], anim.Frames.ToArray());
    }

    [Fact]
    public void Rate_or_range_zero_means_no_fire_and_v100_uses_its_own_tables()
    {
        var img = Globe();
        img.U32(0x46100 + 4 * 19, 0);
        Assert.Null(AisData.Parse(img.File)!.Types[0].Fire);

        // A v1.00 image: tables 0x100 lower, handlers unshifted; the v1.01 handler is unknown there.
        var old = new Image("1.00");
        old.Ptr(AisData.V100.EnemyTable, 0x46000);
        old.U32(AisData.V100.EnemyTable + 8, 10);
        old.Ptr(AisData.V100.EnemyTable + 0x14, 0x46100);
        old.Ptr(0x46100 + 4 * 6, 0x46400);
        old.Op(0x46400, 0x12A668, 56, 300, 0, shift: 0);
        old.Op(0x46410, 0x12A668, 56, 300, 0, shift: 0x500);
        var ais = AisData.Parse(old.File)!;
        Assert.Same(AisData.V100, ais.Tables);
        Assert.Equal([AisOpKind.Set, AisOpKind.End], ais.ScriptOps(0x46400).Select(o => o.Kind));
    }

    [Fact]
    public void Unknown_version_is_rejected()
    {
        Assert.Null(AisData.Parse(new Image("2.00").File));
    }
}
