using System.Globalization;
using RetroEng.Render;
using SkyNet.Formats;
using SkyNet.Game.Enhanced;

namespace SkyNet.Game.Mods;

/// <summary>
/// The shipped <c>materials.json</c>'s first draft (<c>SkyNet --gen-materials</c>): a guess per wall
/// and floor record of the game's TEXTURE banks from its DOS colours, then its hand picks on top
/// (<see cref="HandPicks"/>, <see cref="FutureShockPicks"/>).
/// <code>
///   lit texels (EmissionClassifier) ─► screen (green / cyan / blue light) : glass, else emissive
///   brown, fairly saturated ─────────► wood
///   grey-blue, smooth, mid-bright ───► metal
///   saturated, smooth ───────────────► painted
///   grey, busy ──────────────────────► stone (contrasty) / concrete
///   the rest ────────────────────────► matte
/// </code>
/// Sprites are left out (they are not lit faces). Only what is not matte is written.
/// </summary>
public static class MaterialGuess
{
    /// <summary>A record's colours, sRGB 0-1 over its non-hole texels.</summary>
    /// <param name="Brightness">Mean luma.</param>
    /// <param name="Contrast">Luma's standard deviation.</param>
    /// <param name="Busy">Mean luma step to the right and down neighbours (fine detail, noise).</param>
    /// <param name="Saturation">Mean (max - min) / max.</param>
    /// <param name="Hue">Mean hue of the saturated texels, degrees (0 red, 120 green, 240 blue); -1 grey.</param>
    /// <param name="Blue">Mean blue minus mean red (a cold grey is above 0).</param>
    /// <param name="Holes">The share of index-0 texels (a cut-out: an object's skin, not a surface).</param>
    public readonly record struct Stats(float Brightness, float Contrast, float Busy, float Saturation, float Hue, float Blue, float Holes = 0f);

    // Thresholds, read off SkyNET's banks by eye (contact sheets per guess). "Rough" is Busy over
    // Brightness: a dark texture's steps are small in absolute terms. SKYNET.COL's panel greys are a
    // little blue; its warm greys (plaster, ceiling tiles, rust) are not metal.
    private const float WoodHueMin = 15f, WoodHueMax = 50f, WoodSaturationMin = 0.3f, WoodSaturationMax = 0.7f, WoodBrightnessMax = 0.5f;
    private const float GreyMax = 0.2f, MetalRoughMax = 0.07f, MetalContrastMin = 0.05f, MetalBrightnessMin = 0.3f, MetalBrightnessMax = 0.8f, MetalBlueMin = 0.02f;
    private const float PaintSaturationMin = 0.45f, PaintRoughMax = 0.1f, PaintContrastMin = 0.05f;
    private const float StoneSaturationMax = 0.2f, StoneRoughMin = 0.11f, StoneContrastMin = 0.08f;
    private const float ConcreteRoughMin = 0.07f;
    private const float LampFractionMax = 0.12f, LampSaturationMax = 0.4f, LampRecordSaturationMax = 0.25f, LampRecordBrightnessMax = 0.45f;
    private const float CutOutMin = 0.15f;
    private const float ScreenHueMin = 90f, ScreenHueMax = 260f, ScreenSaturationMin = 0.35f;
    private const float SaturatedTexel = 0.15f, DarkFloor = 0.05f;

    /// <summary>SkyNET's records whose guess is wrong or that the guess cannot tell, picked by eye
    /// ("BBB/RRR" to a preset).</summary>
    public static readonly IReadOnlyDictionary<string, SurfacePreset> HandPicks = new Dictionary<string, SurfacePreset>
    {
        // MAP.218's ceiling tiles: a cool grey, but plaster.
        ["381/001"] = SurfacePreset.Concrete,
        // A pale stone wall, not a panel.
        ["183/019"] = SurfacePreset.Concrete,
        // White walls with graffiti: paint over plaster.
        ["247/002"] = SurfacePreset.Painted,
        ["247/003"] = SurfacePreset.Painted,
        ["247/004"] = SurfacePreset.Painted,
        ["247/008"] = SurfacePreset.Painted,
        ["248/000"] = SurfacePreset.Painted,
        ["248/001"] = SurfacePreset.Painted,
        // Pin-ups: their skin passes for lamplight.
        ["267/002"] = SurfacePreset.Matte,
        ["267/003"] = SurfacePreset.Matte,
        // Framed paintings and film posters: busy, so the guess took them for stone.
        ["378/000"] = SurfacePreset.Matte,
        ["378/001"] = SurfacePreset.Matte,
        ["378/002"] = SurfacePreset.Matte,
        ["378/003"] = SurfacePreset.Matte,
        ["378/004"] = SurfacePreset.Matte,
        ["378/005"] = SurfacePreset.Matte,
        ["378/006"] = SurfacePreset.Matte,
    };

    /// <summary>Future Shock's hand picks: its banks share SkyNET's numbers but not their pictures.</summary>
    public static readonly IReadOnlyDictionary<string, SurfacePreset> FutureShockPicks = new Dictionary<string, SurfacePreset>
    {
        // Barrels and drums: busy grey or green, but steel.
        ["204/000"] = SurfacePreset.Metal,
        ["204/001"] = SurfacePreset.Metal,
        ["204/002"] = SurfacePreset.Metal,
        ["204/003"] = SurfacePreset.Metal,
        ["206/000"] = SurfacePreset.Metal,
        ["213/010"] = SurfacePreset.Metal,
        // Wall panels the guess took for stone.
        ["215/021"] = SurfacePreset.Metal,
        ["288/080"] = SurfacePreset.Metal,
        ["289/013"] = SurfacePreset.Metal,
        ["289/062"] = SurfacePreset.Metal,
        ["289/092"] = SurfacePreset.Metal,
        ["289/096"] = SurfacePreset.Metal,
        // Bus and train fronts.
        ["411/001"] = SurfacePreset.Metal,
        ["411/004"] = SurfacePreset.Metal,
        ["411/005"] = SurfacePreset.Metal,
        ["411/007"] = SurfacePreset.Metal,
        ["411/008"] = SurfacePreset.Metal,
        ["413/000"] = SurfacePreset.Metal,
        ["413/001"] = SurfacePreset.Metal,
        ["413/002"] = SurfacePreset.Metal,
        ["414/005"] = SurfacePreset.Metal,
        ["415/004"] = SurfacePreset.Metal,
        // A first-aid box and a red and yellow door.
        ["214/007"] = SurfacePreset.Painted,
        ["289/093"] = SurfacePreset.Painted,
        // Brown rock, cliffs, carved faces and brick: not wood.
        ["300/000"] = SurfacePreset.Stone,
        ["300/005"] = SurfacePreset.Stone,
        ["300/020"] = SurfacePreset.Stone,
        ["300/030"] = SurfacePreset.Stone,
        ["302/000"] = SurfacePreset.Stone,
        ["302/005"] = SurfacePreset.Stone,
        ["302/020"] = SurfacePreset.Stone,
        ["302/030"] = SurfacePreset.Stone,
        ["401/000"] = SurfacePreset.Stone,
        ["401/002"] = SurfacePreset.Stone,
        ["402/000"] = SurfacePreset.Stone,
        ["402/001"] = SurfacePreset.Stone,
        ["402/002"] = SurfacePreset.Stone,
        ["402/010"] = SurfacePreset.Stone,
        ["407/001"] = SurfacePreset.Stone,
        ["412/001"] = SurfacePreset.Stone,
        ["412/002"] = SurfacePreset.Stone,
        ["412/004"] = SurfacePreset.Stone,
        ["412/006"] = SurfacePreset.Stone,
        // Marble and rock the guess took for metal.
        ["396/004"] = SurfacePreset.Stone,
        ["410/000"] = SurfacePreset.Stone,
        ["413/005"] = SurfacePreset.Stone,
        ["415/005"] = SurfacePreset.Stone,
        // Lamps: white or coloured light filling the record.
        ["229/001"] = SurfacePreset.Emissive,
        ["362/003"] = SurfacePreset.Emissive,
        ["370/002"] = SurfacePreset.Emissive,
        ["374/011"] = SurfacePreset.Emissive,
        ["423/000"] = SurfacePreset.Emissive,
        ["448/004"] = SurfacePreset.Emissive,
        ["448/005"] = SurfacePreset.Emissive,
        // Posters, signs, papers and a gun: pictures, not surfaces.
        ["201/003"] = SurfacePreset.Matte,
        ["227/005"] = SurfacePreset.Matte,
        ["378/000"] = SurfacePreset.Matte,
        ["378/001"] = SurfacePreset.Matte,
        ["378/002"] = SurfacePreset.Matte,
        ["378/003"] = SurfacePreset.Matte,
        ["378/004"] = SurfacePreset.Matte,
        ["378/005"] = SurfacePreset.Matte,
        ["378/006"] = SurfacePreset.Matte,
        ["391/008"] = SurfacePreset.Matte,
        ["393/001"] = SurfacePreset.Matte,
        ["408/004"] = SurfacePreset.Matte,
        ["417/000"] = SurfacePreset.Matte,
        ["418/000"] = SurfacePreset.Matte,
        ["431/002"] = SurfacePreset.Matte,
        ["456/002"] = SurfacePreset.Matte,
    };

    /// <summary>The hand picks of <paramref name="game"/>'s data.</summary>
    public static IReadOnlyDictionary<string, SurfacePreset> PicksOf(GameKind game) =>
        game == GameKind.FutureShock ? FutureShockPicks : HandPicks;

    /// <summary>A record's <see cref="Stats"/> (its first frame), through <paramref name="palette"/>.</summary>
    public static Stats Measure(ReadOnlySpan<byte> pixels, int width, int height, Palette palette)
    {
        var luma = new float[width * height];
        double sum = 0, squares = 0, saturation = 0, red = 0, blue = 0, hueX = 0, hueY = 0;
        var counted = 0;
        for (var i = 0; i < luma.Length && i < pixels.Length; i++)
        {
            if (pixels[i] == 0)
            {
                luma[i] = float.NaN;
                continue;
            }

            var (r8, g8, b8) = palette[pixels[i]];
            float r = r8 / 255f, g = g8 / 255f, b = b8 / 255f;
            var l = 0.299f * r + 0.587f * g + 0.114f * b;
            var max = MathF.Max(r, MathF.Max(g, b));
            var s = max > 0f ? (max - MathF.Min(r, MathF.Min(g, b))) / max : 0f;
            luma[i] = l;
            (sum, squares, saturation, red, blue) = (sum + l, squares + l * l, saturation + s, red + r, blue + b);
            counted++;
            if (s >= SaturatedTexel)
            {
                var h = HueOf(r, g, b) * MathF.PI / 180f;
                (hueX, hueY) = (hueX + s * MathF.Cos(h), hueY + s * MathF.Sin(h));
            }
        }

        if (counted == 0)
        {
            return new Stats(0f, 0f, 0f, 0f, -1f, 0f, 1f);
        }

        var mean = sum / counted;
        var hue = hueX * hueX + hueY * hueY > 1e-6 ? (float)((Math.Atan2(hueY, hueX) * 180.0 / Math.PI + 360.0) % 360.0) : -1f;
        return new Stats((float)mean, (float)Math.Sqrt(Math.Max(squares / counted - mean * mean, 0)), Busy(luma, width, height),
            (float)(saturation / counted), hue, (float)((blue - red) / counted), 1f - (float)counted / Math.Max(luma.Length, 1));
    }

    // Mean |step| of luma to the right and downwards, over pairs of non-hole texels.
    private static float Busy(float[] luma, int width, int height)
    {
        double steps = 0;
        var pairs = 0;
        for (var y = 0; y < height; y++)
        {
            for (var x = 0; x < width; x++)
            {
                var here = luma[y * width + x];
                if (x + 1 < width && !float.IsNaN(here) && !float.IsNaN(luma[y * width + x + 1]))
                {
                    steps += Math.Abs(here - luma[y * width + x + 1]);
                    pairs++;
                }

                if (y + 1 < height && !float.IsNaN(here) && !float.IsNaN(luma[(y + 1) * width + x]))
                {
                    steps += Math.Abs(here - luma[(y + 1) * width + x]);
                    pairs++;
                }
            }
        }

        return pairs > 0 ? (float)(steps / pairs) : 0f;
    }

    private static float HueOf(float r, float g, float b)
    {
        var max = MathF.Max(r, MathF.Max(g, b));
        var delta = max - MathF.Min(r, MathF.Min(g, b));
        if (delta <= 0f)
        {
            return 0f;
        }

        var h = max == r ? (g - b) / delta % 6f : max == g ? (b - r) / delta + 2f : (r - g) / delta + 4f;
        return (h * 60f + 360f) % 360f;
    }

    /// <summary>The preset a record's colours suggest; <paramref name="emission"/> its lamp test.</summary>
    public static SurfacePreset Guess(Stats s, EmissionResult emission)
    {
        // Lamps: a little, whitish light (posters and faces light up more, and warmer); screens: a
        // green, cyan or blue light.
        var c = emission.MeanLitColour;
        var top = MathF.Max(c.X, MathF.Max(c.Y, c.Z));
        var litSaturation = top > 0f ? (top - MathF.Min(c.X, MathF.Min(c.Y, c.Z))) / top : 0f;
        if (emission.Emissive && litSaturation >= ScreenSaturationMin && HueOf(c.X, c.Y, c.Z) is >= ScreenHueMin and <= ScreenHueMax)
        {
            return SurfacePreset.Glass;
        }

        // A lamp sits in a dull, darkish record (posters and faces are lit too, but bright and coloured).
        if (emission.Emissive && emission.LitFraction <= LampFractionMax && litSaturation < LampSaturationMax
            && s.Saturation < LampRecordSaturationMax && s.Brightness <= LampRecordBrightnessMax)
        {
            return SurfacePreset.Emissive;
        }

        if (s.Holes >= CutOutMin)
        {
            return SurfacePreset.Matte;
        }

        var rough = s.Busy / MathF.Max(s.Brightness, DarkFloor);

        if (s.Hue is >= WoodHueMin and <= WoodHueMax && s.Saturation is >= WoodSaturationMin and <= WoodSaturationMax && s.Brightness <= WoodBrightnessMax)
        {
            return SurfacePreset.Wood;
        }

        if (s.Saturation < GreyMax && rough < MetalRoughMax && s.Contrast >= MetalContrastMin && s.Blue >= MetalBlueMin
            && s.Brightness is >= MetalBrightnessMin and <= MetalBrightnessMax)
        {
            return SurfacePreset.Metal;
        }

        if (s.Saturation >= PaintSaturationMin && rough < PaintRoughMax && s.Contrast >= PaintContrastMin)
        {
            return SurfacePreset.Painted;
        }

        if (s.Saturation < StoneSaturationMax && rough >= StoneRoughMin)
        {
            return s.Contrast >= StoneContrastMin ? SurfacePreset.Stone : SurfacePreset.Concrete;
        }

        if (s.Saturation < GreyMax && rough >= ConcreteRoughMin)
        {
            return SurfacePreset.Concrete;
        }

        return SurfacePreset.Matte;
    }

    /// <summary>The table for every wall and floor record of <paramref name="data"/>'s banks: the
    /// guesses that are not matte, then the game's hand picks. <paramref name="report"/> gets one
    /// line per record ("BBB/RRR preset stats").</summary>
    public static MaterialTable Generate(GameData data, Action<string>? report = null)
    {
        var palette = data.LoadPalette() ?? throw new InvalidDataException("no game palette");
        var litTable = EmissionClassifier.LitTable(palette);
        var table = MaterialTable.Empty with { Default = Surface.Matte };
        foreach (var bank in Banks(data))
        {
            if (data.ReadLoose($"TEXTURE.{bank:D3}") is not { } bytes)
            {
                continue;
            }

            foreach (var r in TextureStack.Records(bank, bytes).Where(r => !r.Sprite))
            {
                var first = r.Pixels.AsSpan(0, r.Width * r.Height);
                var stats = Measure(first, r.Width, r.Height, palette);
                var emission = EmissionClassifier.Classify(first, palette, litTable);
                var preset = Guess(stats, emission);
                report?.Invoke(string.Create(CultureInfo.InvariantCulture,
                    $"{MaterialTable.Key(bank, r.Index)} {MaterialTable.NameOf(preset)} L{stats.Brightness:0.00} C{stats.Contrast:0.00} B{stats.Busy:0.000} S{stats.Saturation:0.00} H{stats.Hue:0} U{stats.Blue:0.00} F{(emission.Emissive ? emission.LitFraction : 0f):0.000}"));
                if (preset != SurfacePreset.Matte)
                {
                    table = table.WithTexture(bank, r.Index, new Surface(preset));
                }
            }
        }

        foreach (var (key, preset) in PicksOf(data.Game))
        {
            var slash = key.IndexOf('/');
            var (bank, record) = (int.Parse(key[..slash], CultureInfo.InvariantCulture), int.Parse(key[(slash + 1)..], CultureInfo.InvariantCulture));
            table = table.WithTexture(bank, record, preset == SurfacePreset.Matte ? null : new Surface(preset));
        }

        return table;
    }

    /// <summary>The TEXTURE.NNN banks in the data folder, in number order.</summary>
    private static IEnumerable<int> Banks(GameData data) =>
        Directory.EnumerateFiles(data.Dir, "TEXTURE.*")
            .Select(f => int.TryParse(Path.GetExtension(f).AsSpan(1), NumberStyles.None, CultureInfo.InvariantCulture, out var n) ? n : -1)
            .Where(n => n >= 0)
            .Distinct()
            .Order();
}
