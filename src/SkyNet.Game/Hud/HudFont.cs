using RetroEng.Render;
using SkyNet.Formats;

namespace SkyNet.Game.Hud;

/// <summary>An XnGine FONT00NN.FNT as an engine <see cref="BitmapFont"/>: every codepoint of the
/// table (0x21..0x110), an empty lower-case slot drawn with its capital (<see cref="FntFont.ResolveIndex"/>),
/// a glyph advancing its width + 1 and a space the em width (FUN_0014691c, spacing [0x33348] = 1).</summary>
public static class HudFont
{
    /// <summary>The HUD's font number: the counts, the message box and the status lines (FONT0004).</summary>
    public const int HudText = 4;

    /// <summary>FONT00NN.FNT for font number <paramref name="font"/> (SKYNET.EXE loads 0..11 through "????.fnt" at start-up).</summary>
    public static string FileName(int font) => $"FONT{font:D4}.FNT";

    /// <summary>
    /// The font DOS's 640 × 480 HUD draws for <paramref name="font"/>: in /g (flag 0x20000 of
    /// [0x30a50]) FUN_0012ad00 looks the number up in the dword table at 0x4d000
    /// ({8, 9, 0, 0, 10, 0, 0, 11}, 0 = keep) before setting it, so 0 → 8, 1 → 9, 4 → 10, 7 → 11.
    /// </summary>
    public static int HiResFont(int font) => font switch
    {
        0 => 8,
        1 => 9,
        4 => 10,
        7 => 11,
        _ => font,
    };

    public static BitmapFont FromFnt(FntFont fnt)
    {
        var glyphs = new List<BitmapGlyphSource>(FntFont.GlyphCount);
        for (var cp = FntFont.FirstCodepoint; cp < FntFont.FirstCodepoint + FntFont.GlyphCount; cp++)
        {
            var i = fnt.ResolveIndex(cp);
            if (i >= 0 && fnt.Glyphs[i].Width > 0)
            {
                glyphs.Add(new BitmapGlyphSource(cp, fnt.Glyphs[i].Width, fnt.GlyphBitmap(i)));
            }
        }

        return BitmapFont.Build(fnt.LineHeight, fnt.SpaceAdvance, glyphs, spacing: 1);
    }

    /// <summary>A loose FONT00NN.FNT from the data (SkyNET's copy in Future Shock); null when absent.</summary>
    public static BitmapFont? Load(GameData data, string name)
    {
        var path = data.FrontPath(name);
        return File.Exists(path) && FntFont.Parse(File.ReadAllBytes(path)) is { } fnt ? FromFnt(fnt) : null;
    }
}
