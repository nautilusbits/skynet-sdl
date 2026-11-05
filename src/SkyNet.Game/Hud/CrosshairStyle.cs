namespace SkyNet.Game.Hud;

/// <summary>DISPLAY's CROSSHAIR on foot: the port's own cross (CURRENT), DOS's MDMAIM.IMG (aim table
/// 0x443ca), a centre dot, or four short lines round a gap. The vehicles keep CROSHAIR.IMG.</summary>
public enum CrosshairStyle { Current, Dos, Dot, Cross }

/// <summary>CROSSHAIR's steps and box text.</summary>
public static class Crosshairs
{
    private static readonly string[] Names = ["CURRENT", "DOS", "DOT", "CROSS"];

    /// <summary>The next style, wrapping: CURRENT → DOS → DOT → CROSS → CURRENT (negative steps back).</summary>
    public static CrosshairStyle Step(CrosshairStyle style, int step)
    {
        var n = Names.Length;
        return (CrosshairStyle)((((int)style + step) % n + n) % n);
    }

    public static string Name(CrosshairStyle style) => Names[(int)style];
}
