using System.Drawing;
using System.Numerics;
using RetroEng.Render;
using SkyNet.Formats;
using SkyNet.Game.Hud;

namespace SkyNet.Game.Menu;

/// <summary>
/// Draws the menus on the renderer's canvas in DOS pixels of the 320 × 200 screen
/// (<see cref="MenuFrame"/>): .IMG art from the front archive (MDMDIMGS.BSA, SkyNET's copy in
/// Future Shock) through the palette each screen uses (MENU.COL, BRIEF.COL, BRIEF2.COL,
/// SKYNTRMP.COL), the FONT00NN.FNT fonts, fills and highlights. Textures are made once each and
/// released with the painter.
/// </summary>
public sealed class MenuPainter : IDisposable
{
    /// <summary>The soft highlight over a button DOS has no lit art for (the Godot port's hover).</summary>
    public static readonly Vector4 HighlightColour = new(1f, 1f, 1f, 0.22f);
    /// <summary>A chosen option (difficulty, detail, stereo).</summary>
    public static readonly Vector4 MarkColour = new(0.35f, 0.95f, 0.55f, 0.30f);
    public static readonly Vector4 TextGreen = new(0.55f, 1f, 0.65f, 1f);
    public static readonly Vector4 TextPale = new(0.83f, 0.93f, 0.84f, 1f);
    public static readonly Vector4 Black = new(0f, 0f, 0f, 1f);

    private readonly Renderer _renderer;
    private readonly GameData _data;
    private readonly Dictionary<string, TextureHandle?> _palettes = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, Formats.Palette?> _colours = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, (TextureHandle Handle, int W, int H)?> _images = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, (TextureHandle Handle, int W, int H)[]> _cels = new(StringComparer.OrdinalIgnoreCase);
    private MenuFrame? _frame;
    private readonly Dictionary<string, BitmapFont?> _fonts = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, string?> _texts = new(StringComparer.OrdinalIgnoreCase);
    private readonly List<TextureHandle> _owned = [];
    private BsaArchive? _images0;
    private bool _imagesTried;

    public MenuPainter(Renderer renderer, GameData data)
    {
        _renderer = renderer;
        _data = data;
    }

    /// <summary>The whole window (in stereo an eye's): the DOS screen over it (<see cref="MenuFrame.Fit"/>).</summary>
    public MenuFrame Screen
    {
        get
        {
            var window = MenuFrame.ForCanvas(_renderer.CanvasWidth, _renderer.Target.Height);
            return MenuFrame.Fit(window.Width, window.Height);
        }
    }

    /// <summary>The frame the screen being drawn uses (<see cref="MenuScreen.FrameIn"/>); the window's by default.</summary>
    public MenuFrame Frame => _frame ?? Screen;

    /// <summary>Draws in <paramref name="frame"/> until the next call; null: the window's.</summary>
    public void Use(MenuFrame? frame) => _frame = frame;

    public Renderer Renderer => _renderer;

    /// <summary>The host screen's turning avatar (made on first use).</summary>
    public AvatarPreview Avatars => _avatars ??= new AvatarPreview(_renderer, _data);

    /// <summary>The TACTICAL tab's spinning unit (made on first use).</summary>
    public TacticalPreview Tactical => _tactical ??= new TacticalPreview(_renderer, _data);

    private AvatarPreview? _avatars;
    private TacticalPreview? _tactical;

    private BsaArchive? Archive
    {
        get
        {
            if (!_imagesTried)
            {
                _imagesTried = true;
                var (path, variant) = _data.FrontArchive("MDMDIMGS.BSA");
                _images0 = File.Exists(path) ? BsaArchive.Open(path, variant) : null;
            }

            return _images0;
        }
    }

    /// <summary>A palette texture by .COL name; the UI palette when the archive lacks it.</summary>
    public TextureHandle? Palette(string col)
    {
        // A handle the renderer let go (a level scope released it) is made again.
        if (!_palettes.TryGetValue(col, out var handle) || handle is { } old && !_renderer.Exists(old))
        {
            var bytes = Archive?.Read(col) ?? _data.ColBytes(col) ?? _data.UiPaletteBytes();
            handle = bytes != null && Formats.Palette.Parse(bytes) is { } pal ? Own(_renderer.CreatePalette(Rgba(pal))) : null;
            _palettes[col] = handle;
        }

        return handle;
    }

    /// <summary>The colour of <paramref name="index"/> in a .COL (a font's DOS colour); null when the archive lacks it.</summary>
    public Vector4? PaletteColour(string col, int index)
    {
        if (!_colours.TryGetValue(col, out var pal))
        {
            var bytes = Archive?.Read(col) ?? _data.ColBytes(col);
            pal = bytes != null ? Formats.Palette.Parse(bytes) : null;
            _colours[col] = pal;
        }

        if (pal == null || index is < 0 or > 255)
        {
            return null;
        }

        var (r, g, b) = pal[index];
        return new Vector4(r / 255f, g / 255f, b / 255f, 1f);
    }

    private static byte[] Rgba(Formats.Palette pal)
    {
        var rgba = new byte[256 * 4];
        for (var i = 0; i < 256; i++)
        {
            var (r, g, b) = pal[i];
            (rgba[i * 4], rgba[i * 4 + 1], rgba[i * 4 + 2], rgba[i * 4 + 3]) = (r, g, b, 255);
        }

        return rgba;
    }

    /// <summary>An .IMG's size; null when the archive lacks it.</summary>
    public Size? ImageSize(string name) => Load(name) is { } i ? new Size(i.W, i.H) : null;

    private (TextureHandle Handle, int W, int H)? Load(string name)
    {
        if (!_images.TryGetValue(name, out var image) || image is { } old && !_renderer.Exists(old.Handle))
        {
            image = Archive?.Read(name) is { } bytes && ImgFile.Parse(bytes) is { } img
                ? (Own(_renderer.CreateIndexTexture(img.Width, img.Height, [.. img.Pixels])), img.Width, img.Height)
                : null;
            _images[name] = image;
        }

        return image;
    }

    /// <summary>Draws an .IMG 1:1 with its top-left at DOS (x, y); false when it is missing.</summary>
    public bool Image(string name, string palette, int x, int y, float alpha = 1f) =>
        Load(name) is { } i && Image(name, palette, new Rectangle(x, y, i.W, i.H), alpha);

    /// <summary>Draws an .IMG stretched over a DOS rectangle.</summary>
    public bool Image(string name, string palette, Rectangle target, float alpha = 1f)
    {
        if (Load(name) is not { } i || Palette(palette) is not { } pal)
        {
            return false;
        }

        _renderer.DrawImage(i.Handle, pal, new Vector2(i.W, i.H), new RectangleF(0, 0, i.W, i.H), Frame.Canvas(target), new Vector4(1f, 1f, 1f, alpha));
        return true;
    }

    /// <summary>Part of an .IMG (<paramref name="source"/>, in its pixels) drawn over a DOS rectangle:
    /// the screens DOS has no art for are cut from the art it has. False when it is missing.</summary>
    public bool ImagePart(string name, string palette, Rectangle source, Rectangle target)
    {
        if (Load(name) is not { } i || Palette(palette) is not { } pal)
        {
            return false;
        }

        _renderer.DrawImage(i.Handle, pal, new Vector2(i.W, i.H), source, Frame.Canvas(target), Vector4.One);
        return true;
    }

    /// <summary>Part of an .IMG repeated over a DOS rectangle (the last copies cut to fit).</summary>
    public bool ImageTiled(string name, string palette, Rectangle source, Rectangle target)
    {
        if (Load(name) is null || source.Width <= 0 || source.Height <= 0)
        {
            return false;
        }

        for (var y = target.Top; y < target.Bottom; y += source.Height)
        {
            for (var x = target.Left; x < target.Right; x += source.Width)
            {
                var w = Math.Min(source.Width, target.Right - x);
                var h = Math.Min(source.Height, target.Bottom - y);
                ImagePart(name, palette, new Rectangle(source.X, source.Y, w, h), new Rectangle(x, y, w, h));
            }
        }

        return true;
    }

    /// <summary>Frame <paramref name="frame"/> of a .CFA (the lit buttons: MAIN1BTN, NETGAME2, OPTBTN…)
    /// with its top-left at DOS <paramref name="at"/>, index 0 clear; false when it is missing.</summary>
    public bool Cel(string name, int frame, Point at, string palette = MenuLayout.MenuPalette) => Cel(name, frame, at, int.MaxValue, palette);

    /// <summary>The left <paramref name="width"/> DOS pixels of a .CFA frame (a bar filling up).</summary>
    public bool Cel(string name, int frame, Point at, int width, string palette = MenuLayout.MenuPalette)
    {
        if (!_cels.TryGetValue(name, out var frames) || frames.Length > 0 && !_renderer.Exists(frames[0].Handle))
        {
            // An .IMG (LOADBTN.IMG) is a cel of one frame.
            frames = name.EndsWith(".IMG", StringComparison.OrdinalIgnoreCase)
                ? Load(name) is { } img ? [img] : []
                : Archive?.Read(name) is { } bytes && CfaFile.Parse(bytes) is { } cfa
                    ? [.. cfa.Frames.Select(f => (Own(_renderer.CreateIndexTexture(cfa.Width, cfa.Height, [.. f])), cfa.Width, cfa.Height))]
                    : [];
            _cels[name] = frames;
        }

        if (frame < 0 || frame >= frames.Length || Palette(palette) is not { } pal)
        {
            return false;
        }

        var (handle, w, h) = frames[frame];
        var cut = Math.Clamp(width, 0, w);
        if (cut > 0)
        {
            _renderer.DrawImage(handle, pal, new Vector2(w, h), new RectangleF(0, 0, cut, h), Frame.Canvas(at.X, at.Y, cut, h), Vector4.One);
        }

        return true;
    }

    public void Fill(Rectangle dos, Vector4 colour) => _renderer.FillRect(Frame.Canvas(dos), colour);

    /// <summary>The whole window (the strips beside the DOS screen too).</summary>
    public void FillWindow(Vector4 colour) => _renderer.FillRect(Frame.Window, colour);

    /// <summary>The highlight over a baked button: DOS's lit frame of it (<see cref="MenuItem.Lit"/>),
    /// else a soft light exactly over its rectangle.</summary>
    public void Highlight(MenuItem item)
    {
        if (item.Lit is not { } lit || !Cel(lit.Art, lit.Frame, lit.At))
        {
            Highlight(item.Rect);
        }
    }

    public void Highlight(Rectangle dos) => _renderer.FillRect(Frame.Canvas(dos), HighlightColour);

    /// <summary>A chosen option: DOS's lit frame when there is one, else <see cref="MarkColour"/>.</summary>
    public void Mark(MenuItem item, LitArt? lit)
    {
        if (lit is not { } l || !Cel(l.Art, l.Frame, l.At))
        {
            Mark(item.Rect);
        }
    }

    public void Mark(Rectangle dos) => _renderer.FillRect(Frame.Canvas(dos), MarkColour);

    public void Outline(Rectangle dos, Vector4 colour) => _renderer.FrameRect(Frame.Canvas(dos), Frame.Pixel, colour);

    /// <summary>A FONT00NN.FNT (SkyNET's copy in Future Shock); null when absent.</summary>
    public BitmapFont? Font(string name)
    {
        if (!_fonts.TryGetValue(name, out var font))
        {
            font = HudFont.Load(_data, name);
            _fonts[name] = font;
        }

        return font;
    }

    /// <summary>One line at DOS (x, y) (its top), aligned on x; returns the width in DOS pixels.</summary>
    public int Text(string font, string text, float x, float y, Vector4 colour, TextAlign align = TextAlign.Left)
    {
        if (Font(font) is not { } f)
        {
            return 0;
        }

        var frame = Frame;
        TextBatcher.Draw(_renderer, f, text, frame.CanvasPoint(x, y), frame.Pixel, colour, align);
        return f.Measure(text);
    }

    /// <summary>A line centred in a DOS rectangle, cut to fit its width.</summary>
    public void TextIn(string font, string text, Rectangle box, Vector4 colour)
    {
        if (Font(font) is not { } f)
        {
            return;
        }

        while (text.Length > 1 && f.Measure(text) > box.Width - 2)
        {
            text = text[..^1];
        }

        Text(font, text, box.X + box.Width / 2f, box.Y + MathF.Floor((box.Height - f.LineHeight) / 2f), colour, TextAlign.Centre);
    }

    /// <summary>The menu's short notice, centred near the bottom of the screen.</summary>
    public void Toast(string text)
    {
        Use(null);
        const string font = "FONT0004.FNT";
        var w = Font(font)?.Measure(text) ?? 0;
        var box = new Rectangle((MenuFrame.ScreenWidth - w) / 2 - 4, 184, w + 8, 11);
        Fill(box, new Vector4(0f, 0f, 0f, 0.8f));
        TextIn(font, text, box, new Vector4(1f, 0.85f, 0.4f, 1f));
    }

    /// <summary>A text file of the briefing archive (MDMDBRIF.BSA), e.g. a TACTICAL dossier; null when absent.</summary>
    public string? BriefText(string name)
    {
        if (!_texts.TryGetValue(name, out var text))
        {
            text = null;
            var (path, variant) = _data.FrontArchive("MDMDBRIF.BSA");
            if (File.Exists(path))
            {
                using var bsa = BsaArchive.Open(path, variant);
                if (bsa.Read(name) is { } bytes)
                {
                    var nul = Array.IndexOf(bytes, (byte)0);
                    text = System.Text.Encoding.Latin1.GetString(bytes, 0, nul >= 0 ? nul : bytes.Length);
                }
            }

            _texts[name] = text;
        }

        return text;
    }

    private TextureHandle Own(TextureHandle t)
    {
        _owned.Add(t);
        return t;
    }

    public void Dispose()
    {
        foreach (var t in _owned)
        {
            _renderer.Destroy(t);
        }

        _owned.Clear();
        _images.Clear();
        _cels.Clear();
        _palettes.Clear();
        _colours.Clear();
        _avatars = null;
        _tactical = null;
        _images0?.Dispose();
        _images0 = null;
        _imagesTried = false;
    }
}
