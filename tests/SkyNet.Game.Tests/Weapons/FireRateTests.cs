using SkyNet.Game.Weapons;

namespace SkyNet.Game.Tests.Weapons;

/// <summary>A held trigger fires as SKYNET.EXE v1.01 at the port's 35 fps: a shot arms 0x10000 /
/// rate (16.16 s, 0x1261b1), each frame takes its time [0x43200] off (0x125b76), and the gun fires on
/// the first frame below 1, so the gap is whole frames: ceil((0x10000 / rate) / 1872).</summary>
public class FireRateTests
{
    private const float Dt = 1f / 60f;
    private const int DosFps = 35, Seconds = 7;
    // [0x43200] at 35 fps: 0x1000000 / (35 << 8).
    private const int FrameTime = 0x1000000 / (DosFps << 8);

    // DOS's frames between shots, as the loop above counts them.
    private static int DosFrames(int rate)
    {
        var cooldown = 0x10000 / rate;
        var frames = 0;
        while (cooldown >= 1)
        {
            cooldown -= FrameTime;
            frames++;
        }

        return frames;
    }

    // Rate → frames: UZI 5 → 8 (4.4 shots/s), laser 6 → 6 (5.8/s), plasma 3 → 12, super UZI 20 → 2.
    [Theory]
    [InlineData(1, 36)]
    [InlineData(2, 18)]
    [InlineData(3, 12)]
    [InlineData(4, 9)]
    [InlineData(5, 8)]
    [InlineData(6, 6)]
    [InlineData(8, 5)]
    [InlineData(20, 2)]
    public void Dos_gap_is_whole_frames(int rate, int frames)
    {
        Assert.Equal(frames, DosFrames(rate));
        var record = new WeaponRecord(0, "", 0, 0, 0, rate, 0, 0, 0, -1, -1, 0, -1, -1, 0, 0);
        Assert.Equal(frames / (float)DosFps, record.Cooldown, 5);
    }

    // Every on-foot gun held for 7 s at 60 Hz fires as often as DOS's 245 frames do (±1 at the ends).
    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(3)]
    [InlineData(4)]
    [InlineData(5)]
    [InlineData(6)]
    [InlineData(7)]
    [InlineData(8)]
    [InlineData(9)]
    [InlineData(10)]
    [InlineData(11)]
    [InlineData(12)]
    public void Held_trigger_fires_as_dos(int record)
    {
        var inv = new Inventory(WeaponTable.Shipped);
        inv.Grant(record);
        var w = inv.Table.Weapons[record];
        inv.AddAmmo(w.Pool, 100000);
        var shots = 0;
        for (var tick = 0; tick < Seconds * 60; tick++)
        {
            inv.Tick(Dt);
            shots += inv.TryFire(record) == FireResult.Fired ? 1 : 0;
        }

        var dos = (Seconds * DosFps - 1) / DosFrames(w.Rate) + 1;
        Assert.InRange(shots, dos - 1, dos + 1);
    }
}
