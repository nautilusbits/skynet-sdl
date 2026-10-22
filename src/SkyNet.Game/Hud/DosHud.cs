using System.Drawing;
using System.Numerics;
using RetroEng.Render;
using SkyNet.Formats;

namespace SkyNet.Game.Hud;

/// <summary>
/// The DOS HUD on the renderer's canvas, as FUN_00131fd4 draws it (docs/game-logic/damage-hud.md):
/// the weapon icons under PANEL0 (its index-0 holes show them), the counts in palette 0xB7, RADCOUNT.CFA, COMPASS.IMG through its 37 px window, the PANELBAR health and armour
/// gauges, and the message window typed in the right-hand recess; then the top-left status
/// lines and the crosshair.
/// <para>The 320 × 40 panel spans the window's width at the bottom, as the Godot port's
/// (hud_panel.gd): <see cref="PanelScaleX"/> across, <see cref="PanelScaleY"/> down (the same
/// unless the window is so wide the panel would pass 2/9 of its height, the Godot port's cap),
/// edges on whole pixels and the art nearest-sampled; text is drawn at the nearest whole scale
/// (<see cref="PixelScale"/>) so the bitmap fonts stay crisp. The 3D view runs behind it, its
/// projection centre in the middle of what is above it (<see cref="ViewCentre"/>). The hi-res set
/// (MDMDHRES.BSA) draws its bigger art into the same layout, as the Godot port does.</para>
/// <para>Text is FONT0004 in the low set. The hi-res set draws it as DOS's 640 × 480 HUD does
/// (SKYNET.EXE /g, flag 0x20000 of [0x30a50]): FUN_0012ad00 swaps the font through the table at
/// 0x4d000 (<see cref="HudFont.HiResFont"/>: 4 → FONT0010) and FUN_0012ad2e scales each
/// 320 × 200 position to 640 × 480 (<see cref="HiResX"/>, <see cref="HiResY"/>). Those positions
/// are mapped back into the panel (640 × 96 → 320 × 40) and the font drawn at half the low set's
/// pixel (<see cref="HiResPixelScale"/>); the message box wraps and holds as DOS's hi-res box.</para>
/// </summary>
public sealed partial class DosHud : IDisposable
{
    public const int ScreenWidth = 320;
    public const int ScreenHeight = 200;
    public const int PanelHeight = 40;

    /// <summary>Palette indices: the counts and messages, the empty gauge, the usual status line colour
    /// (each line carries its own, <see cref="StatusQueue"/>) and the status shadow (FUN_0012f953).</summary>
    public const int TextIndex = 0xB7;
    public const int EmptyBarIndex = 0x7A;
    public const int StatusIndex = 0xB3;
    public const int StatusShadowIndex = 0x7F;

    /// <summary>The crosshair colours of the Godot port (crosshair.gd).</summary>
    public static readonly Vector4 FootColour = new(0.55f, 1f, 0.65f, 0.85f);
    public static readonly Vector4 FootShadow = new(0f, 0f, 0f, 0.6f);
    public static readonly Vector4 ReticleColour = new(0.40f, 0.95f, 0.45f, 0.9f);

    // The 640 × 480 set's lines, and the width from which CROSHAIR.IMG is that set's art.
    private const float HiresLines = 480f;
    private const int HiresArtWidth = 100;

    private readonly Renderer _renderer;
    private readonly GameData _data;
    private readonly BitmapFont? _font;
    private readonly BitmapFont? _hiFont;
    private HudArt _art;
    private ReticleShape? _shape;
    private bool _shapeTried;

    public MessageBox Messages { get; }
    public StatusQueue Status { get; } = new();
    /// <summary>The text <see cref="Draw(in HudState, int, int)"/> captures into (a draw on the simulation's thread).</summary>
    private readonly HudText _text = new();

    /// <summary>Sound ids the HUD asks for (0x51 when a message starts).</summary>
    public event Action<int>? SoundRequested;

    public DosHud(Renderer renderer, GameData data, HudArtSet set = HudArtSet.LowRes)
    {
        _renderer = renderer;
        _data = data;
        _font = HudFont.Load(data, HudFont.FileName(HudFont.HudText));
        _hiFont = HudFont.Load(data, HudFont.FileName(HudFont.HiResFont(HudFont.HudText)));
        _art = new HudArt(renderer, data, set);
        var font = _font;
        Messages = new MessageBox(c => font?.Advance(c) ?? 6);
        Messages.SoundRequested += id => SoundRequested?.Invoke(id);
        ApplyTextLayout();
    }

    /// <summary>The art set; changing it reloads the images.</summary>
    public HudArtSet ArtSet
    {
        get => _art.Set;
        set
        {
            if (value == _art.Set)
            {
                return;
            }

            Load(value);
        }
    }

    /// <summary>Makes the art set's images again (a renderer scope released them: the set was
    /// changed while a level's scope was open).</summary>
    public void ReloadArt() => Load(_art.Set);

    private void Load(HudArtSet set)
    {
        _art.Dispose();
        _art = new HudArt(_renderer, _data, set);
        _shape = null;
        _shapeTried = false;
        ApplyTextLayout();
    }

    /// <summary>CLASSIC: DOS's panel; MODERN: corner widgets over a full-frame view (<see cref="ModernLayout"/>).</summary>
    public HudStyle Style { get; set; }

    /// <summary>The screen's pixels per millimetre for the text's minimum (<see cref="UiMetrics.PxPerMm"/>; 0: none).</summary>
    public float PxPerMm { get; set; }

    /// <summary>FONT0004, the low set's HUD font (null when the data lacks it).</summary>
    public BitmapFont? Font => _font;

    /// <summary>The font the HUD text is drawn in: FONT0010 in the hi-res set (FONT0004 when the
    /// data lacks it), FONT0004 otherwise.</summary>
    public BitmapFont? TextFont => HiResText ? _hiFont : _font;

    /// <summary>The hi-res set with FONT0010 present: text at DOS's 640 × 480 positions.</summary>
    private bool HiResText => _art.Set == HudArtSet.HiRes && _hiFont != null;

    /// <summary>Screen pixels per <see cref="TextFont"/> pixel: the status lines' scale (other HUD
    /// text over the view, the deathmatch's, uses it too).</summary>
    public int TextScale(int width, int height) => Style == HudStyle.Modern
        ? ModernLayout.For(width, height, PxPerMm).TextPixel(TextGrid)
        : HiResText ? HiResPixelScale(width, height) : PixelScale(width, height);

    /// <summary><see cref="TextFont"/> pixels per DOS 320 × 200 pixel: 2 on the hi-res text's
    /// 640 × 480 grid (FUN_0012ad2e), else 1.</summary>
    public int TextGrid => HiResText ? 2 : 1;

    /// <summary>The art set's palette colour <paramref name="index"/> (the status lines' colours).</summary>
    public Vector4 Colour(int index) => _art.Colour(index);

    /// <summary>The message box's rows on the 320 × 200 screen ((218,163)-(317,196) at 0x51b11).</summary>
    public const int MessageBoxRows = 196 - 163;

    /// <summary>DOS's 640 × 480 x of a 320 × 200 one (FUN_0012ad2e: × 2).</summary>
    public static int HiResX(int x) => x * 2;

    /// <summary>DOS's 640 × 480 y of a 320 × 200 one (FUN_0012ad2e: (y × 0x1333 + 0x400) >> 11, × 2.4 rounded).</summary>
    public static int HiResY(int y) => (y * 0x1333 + 0x400) >> 11;

    /// <summary>Lines the message box holds: FUN_0012fb4c scrolls instead of starting a line once
    /// the next would pass <paramref name="boxRows"/> − a line (a line = the font's height + 1).</summary>
    public static int MessageLines(int boxRows, int lineHeight) => (boxRows - (lineHeight + 1)) / (lineHeight + 1) + 1;

    private int SetX(int x) => HiResText ? HiResX(x) : x;

    private int SetY(int y) => HiResText ? HiResY(y) : y;

    /// <summary>The message box's wrap and line count for the text font (FUN_0012fa3d wraps to
    /// 0x62 scaled; FUN_0012fb4c holds the box's 33 rows scaled).</summary>
    private void ApplyTextLayout()
    {
        var font = TextFont;
        Messages.SetLayout(c => font?.Advance(c) ?? 6, SetX(MessageBox.WrapPx), MessageLines(SetY(MessageBoxRows), font?.LineHeight ?? 5));
    }

    public void ShowHint(string text) => Messages.ShowHint(text);

    public void ShowObjective(string text, bool playSound = true) => Messages.ShowObjective(text, playSound);

    /// <summary>A top-left status line in palette index <paramref name="colour"/> (FUN_0012f8f7's DL):
    /// 0xB3 pickups and most lines, 0x0C network events, 0x08 asides (<see cref="StatusQueue"/>).</summary>
    public void ShowStatus(string text, byte colour = StatusQueue.WhiteColour, float seconds = StatusQueue.DefaultSeconds) =>
        Status.Push(text, colour, seconds);

    /// <summary>A level start: the message window and the status lines empty.</summary>
    public void ClearMessages()
    {
        Messages.Clear();
        Status.Clear();
    }

    /// <summary>Types the message window and ages the status lines (once per frame or tick).</summary>
    public void Tick(float seconds)
    {
        Messages.Tick(seconds);
        Status.Tick(seconds);
    }

    /// <summary>Screen pixels per DOS pixel across the panel: it spans the window.</summary>
    public static float PanelScaleX(int width) => width / (float)ScreenWidth;

    /// <summary>Screen pixels per DOS pixel down the panel: as across, but the panel stays within
    /// 2/9 of the height (the Godot port's 160 of 720 lines).</summary>
    public static float PanelScaleY(int width, int height) => MathF.Min(PanelScaleX(width), height * (2f / 9f) / PanelHeight);

    /// <summary>Screen pixels per font pixel (and the status line's DOS pixel): the nearest whole
    /// number to the panel's scale, so the bitmap fonts stay crisp.</summary>
    public static int PixelScale(int width, int height) =>
        Math.Max(1, (int)MathF.Round(MathF.Min(PanelScaleX(width), PanelScaleY(width, height)), MidpointRounding.AwayFromZero));

    /// <summary>Screen pixels per FONT0010 pixel in the hi-res set: half the low set's (a 640 × 480
    /// pixel is half a 320 × 200 one across), a whole number so the font stays crisp.</summary>
    public static int HiResPixelScale(int width, int height) =>
        Math.Max(1, (int)MathF.Round(MathF.Min(PanelScaleX(width), PanelScaleY(width, height)) * 0.5f, MidpointRounding.AwayFromZero));

    /// <summary>Screen pixels the panel covers at the bottom (0 in the MODERN HUD): the view's projection
    /// centre belongs in the middle of what is left.</summary>
    public static int PanelPixels(int width, int height, HudStyle style = HudStyle.Classic) =>
        style == HudStyle.Modern ? 0 : (int)MathF.Round(PanelHeight * PanelScaleY(width, height));

    /// <summary>The aim point DOS uses, (160, 80) of its screen: the middle of the view above the panel
    /// (the frame's middle in the MODERN HUD).</summary>
    public static Vector2 ViewCentre(int width, int height, HudStyle style = HudStyle.Classic) =>
        new(MathF.Floor(width * 0.5f), MathF.Floor((height - PanelPixels(width, height, style)) * 0.5f));

    /// <summary>Screen pixels at the bottom the HUD keeps for itself (text over the view stays above
    /// them): the panel, or the MODERN HUD's bottom-left plate.</summary>
    public int BottomPixels(int width, int height) => Style == HudStyle.Modern
        ? height - (int)MathF.Floor(ModernLayout.For(width, height, PxPerMm).Plate.Top)
        : PanelPixels(width, height);

    /// <summary>Queues the HUD on the canvas for a <paramref name="width"/> × <paramref name="height"/>
    /// pixel screen (the renderer's target).</summary>
    public void Draw(in HudState state, int width, int height)
    {
        Capture(_text);
        Draw(state, _text, width, height, RectangleF.Empty);
    }

    /// <summary>Copies the message window's and the status lines' text into <paramref name="text"/>,
    /// for a draw on another thread.</summary>
    public void Capture(HudText text)
    {
        text.Messages.Clear();
        text.Messages.AddRange(Messages.Text);
        text.Typed.Clear();
        text.Typed.AddRange(Messages.Typed);
        text.Status.Clear();
        text.Status.AddRange(Status.Entries);
    }

    /// <summary>Queues the HUD with the text caught by <see cref="Capture"/>; the MODERN HUD keeps its
    /// widgets off <paramref name="avoid"/> (the touch buttons, screen pixels).</summary>
    public void Draw(in HudState state, HudText text, int width, int height, RectangleF avoid)
    {
        if (width <= 0 || height <= 0)
        {
            return;
        }

        var scale = TextScale(width, height);
        var frame = new Frame(Renderer.CanvasHeight / height, scale, PanelScaleX(width), PanelScaleY(width, height), width, height);
        if (!state.CrosshairHidden)
        {
            DrawCrosshair(state, frame);
        }

        if (!state.PanelHidden && Style == HudStyle.Modern)
        {
            DrawModern(state, text, frame, avoid);
        }
        else if (!state.PanelHidden)
        {
            DrawPanel(state, text, frame);
        }

        DrawStatus(text, frame);
    }

    /// <summary>A point of the HUD's screen (320 × 200, or 640 × 480 in the hi-res text) in the panel's 320 × 40.</summary>
    private Vector2 PanelAt(int x, int y)
    {
        var top = SetY(ScreenHeight - PanelHeight);
        return new Vector2(x * ScreenWidth / (float)SetX(ScreenWidth), (y - top) * PanelHeight / (float)(SetY(ScreenHeight) - top));
    }

    /// <summary>Canvas units per pixel, the text scale, the panel's scales and its top pixel.</summary>
    private readonly record struct Frame(float Unit, int Scale, float ScaleX, float ScaleY, int Width, int Height)
    {
        public int Top => Height - PanelPixels(Width, Height);

        private float X(float x) => MathF.Round(x * ScaleX);

        private float Y(float y) => Top + MathF.Round(y * ScaleY);

        /// <summary>A rectangle of the panel (DOS pixels, y from the panel's top) on the canvas, its edges on whole pixels.</summary>
        public RectangleF Panel(float x, float y, float w, float h)
        {
            float l = X(x), t = Y(y), r = X(x + w), b = Y(y + h);
            return new RectangleF(l * Unit, t * Unit, (r - l) * Unit, (b - t) * Unit);
        }

        public Vector2 PanelPoint(float x, float y) => new(X(x) * Unit, Y(y) * Unit);

        /// <summary>A rectangle in screen pixels on the canvas.</summary>
        public RectangleF Pixels(float x, float y, float w, float h) => new(x * Unit, y * Unit, w * Unit, h * Unit);
    }

    private void DrawPanel(in HudState s, HudText caught, Frame f)
    {
        var black = new Vector4(0f, 0f, 0f, 1f);
        _renderer.FillRect(f.Pixels(0, f.Top, f.Width, f.Height - f.Top), black);

        // Icons first, PANEL0 over them.
        var secondary = s.SecondaryRecord;
        Blit(_art.WeaponIcon(s.PrimaryRecord), f.Panel(50, 20, 83, 17));
        Blit(_art.WeaponIcon(secondary), f.Panel(3, 20, 39, 17));
        Blit(_art.Image("PANEL0.IMG"), f.Panel(0, 0, ScreenWidth, PanelHeight));

        // The counts: right-aligned at x 128 and x 38, row 191 (FUN_00132654: [0x524a1], [0x524b9]
        // and row [0x524ad] + the panel origin (0,160), scaled in the hi-res text).
        var text = _art.Colour(TextIndex);
        // Low set: square font pixels at the whole text scale. Hi-res text: on the 640 × 96 art's own
        // grid, as DOS draws it: a whole-number half pixel across, the art's row pitch (the panel's
        // 40 rows hold 96) down, so the lines land on DOS's rows (12 a line, 6 in the box).
        var pixel = HiResText
            ? new Vector2(f.Scale, f.ScaleY * PanelHeight / (SetY(ScreenHeight) - SetY(ScreenHeight - PanelHeight))) * f.Unit
            : new Vector2(f.Scale * f.Unit);
        var font = TextFont;
        if (font != null)
        {
            if (HudFormat.PrimaryCount(s.PrimaryAmmo) is { } p)
            {
                var at = PanelAt(SetX(128), SetY(191));
                TextBatcher.Draw(_renderer, font, p, f.PanelPoint(at.X, at.Y), pixel, text, TextAlign.Right);
            }

            if (secondary >= 0 && HudFormat.SecondaryCount(s.SecondaryAmmo) is { } q)
            {
                var at = PanelAt(SetX(38), SetY(191));
                TextBatcher.Draw(_renderer, font, q, f.PanelPoint(at.X, at.Y), pixel, text, TextAlign.Right);
            }
        }

        // RADCOUNT.CFA at (50,165).
        var rad = _art.Cel("RADCOUNT.CFA");
        if (rad.Length > 0)
        {
            Blit(rad[Math.Min(HudFormat.RadFrame(s.Radiation), rad.Length - 1)], f.Panel(50, 5, 41, 10));
        }

        // COMPASS.IMG through the 37 px window at (96,163).
        if (s.Bearing >= 0 && _art.Image("COMPASS.IMG") is { } compass)
        {
            var k = compass.Width / (float)HudFormat.CompassRibbon;
            var src = new RectangleF(-HudFormat.CompassScroll(s.Bearing) * k, 0, HudFormat.CompassWindow * k, compass.Height);
            _renderer.DrawImage(compass.Handle, _art.Palette, compass.Size, src, f.Panel(96, 3, HudFormat.CompassWindow, 14), Vector4.One);
        }

        // The gauges: health (172,185), armour or hull (172,169).
        var max = MathF.Max(s.MaxHealth, 1f);
        Gauge(s.Health / max, 25, f);
        Gauge(s.Armour, 9, f);

        // The message window: from (219,164) scaled, a line the font's height + 1 (FUN_0012fcf5, FUN_0012fb4c).
        if (font != null)
        {
            var lines = caught.Messages;
            for (var i = 0; i < lines.Count; i++)
            {
                var at = PanelAt(SetX(219), SetY(164) + (font.LineHeight + 1) * i);
                TextBatcher.Draw(_renderer, font, lines[i], f.PanelPoint(at.X, at.Y), pixel, text);
            }
        }
    }

    /// <summary>PANELBAR cut at the filled width, flat 0x7A after it (FUN_0013249c).</summary>
    private void Gauge(float fraction, int y, Frame f)
    {
        const int x = 172, h = 5, w = HudFormat.BarWidth;
        var filled = HudFormat.BarFill(fraction);
        if (filled > 0 && _art.Image("PANELBAR.IMG") is { } bar)
        {
            var k = bar.Width / (float)w;
            _renderer.DrawImage(bar.Handle, _art.Palette, bar.Size, new RectangleF(0, 0, filled * k, bar.Height), f.Panel(x, y, filled, h), Vector4.One);
        }

        if (filled < w)
        {
            _renderer.FillRect(f.Panel(x + filled, y, w - filled, h), _art.Colour(EmptyBarIndex));
        }
    }

    /// <summary>The status lines from (4,3) of the HUD's screen, unscaled (FUN_0012f953): 640 × 480
    /// pixels and FONT0010 in the hi-res text; each line in its queued colour over a 0x7F shadow at
    /// +1,+1, the next line the font's height + 2 lower.</summary>
    private void DrawStatus(HudText text, Frame f)
    {
        var font = TextFont;
        if (font == null || text.Status.Count == 0)
        {
            return;
        }

        // Over the view, not in the panel: free to grow to the screen's minimum.
        var pixel = UiMetrics.TextPixel(f.Scale, font.LineHeight, PxPerMm) * f.Unit;
        var i = 0;
        foreach (var (line, colour) in text.Status)
        {
            var y = StatusQueue.LineY(i++, font.LineHeight);
            TextBatcher.DrawShadowed(_renderer, font, line, new Vector2(StatusQueue.LineX * pixel, y * pixel), pixel,
                _art.Colour(colour), _art.Colour(StatusShadowIndex));
        }
    }

    private void DrawCrosshair(in HudState s, Frame f)
    {
        var c = s.AimPoint ?? ViewCentre(f.Width, f.Height, s.PanelHidden ? HudStyle.Modern : Style);
        c = new Vector2(MathF.Round(c.X), MathF.Round(c.Y));
        if (s.Vehicle != HudVehicle.None && DrawReticle(c, f))
        {
            return;
        }

        // On foot: the Godot port's small cross (gap 4, arms 8 on its 720-line canvas) with a shadow.
        var k = Math.Max(1f, MathF.Round(f.Height / 720f));
        float gap = 4 * k, len = 8 * k;
        void Arm(Vector4 colour, float off, float thick)
        {
            var half = MathF.Floor(thick * 0.5f);
            _renderer.FillRect(f.Pixels(c.X + gap + off, c.Y - half + off, len, thick), colour);
            _renderer.FillRect(f.Pixels(c.X - gap - len + off, c.Y - half + off, len, thick), colour);
            _renderer.FillRect(f.Pixels(c.X - half + off, c.Y + gap + off, thick, len), colour);
            _renderer.FillRect(f.Pixels(c.X - half + off, c.Y - gap - len + off, thick, len), colour);
        }

        Arm(FootShadow, k, 2 * k);
        Arm(FootColour, 0, k);
        _renderer.FillRect(f.Pixels(c.X - k, c.Y - k, 2 * k, 2 * k), FootColour);
    }

    /// <summary>The vehicle reticle as vectors at the frame's resolution wherever the 640 × 480 art is there,
    /// but in the low set's CLASSIC HUD: DOS's own pixels blown up.</summary>
    public static ReticleDraw ReticleDrawFor(HudArtSet set, HudStyle style, HiresArt hires) =>
        hires == HiresArt.Present && (set == HudArtSet.HiRes || style == HudStyle.Modern) ? ReticleDraw.Vectors : ReticleDraw.ScaledArt;

    /// <summary>CROSHAIR.IMG in the jeep and the HK: the low set's art blown up by a whole number
    /// (it is drawn for 200 lines, the 640 × 480 art for 480), or in the hi-res set and the MODERN HUD
    /// the 640 × 480 art as vectors at the screen's own resolution (<see cref="ReticleShape"/>).</summary>
    private bool DrawReticle(Vector2 c, Frame f)
    {
        var shape = Shape();
        if (ReticleDrawFor(_art.Set, Style, shape != null ? HiresArt.Present : HiresArt.Missing) == ReticleDraw.Vectors)
        {
            DrawShape(shape!, c, f.Height / HiresLines, f);
            return true;
        }

        if (_art.Image("CROSHAIR.IMG") is not { } art)
        {
            return false;
        }

        var unit = art.Width >= HiresArtWidth ? HiresLines : 200f;
        if (_art.Mask("CROSHAIR.IMG") is not { } mask)
        {
            return false;
        }

        var n = MathF.Max(1f, MathF.Round(f.Height / unit));
        float w = mask.Width * n, h = mask.Height * n;
        var target = f.Pixels(MathF.Round(c.X - w * 0.5f), MathF.Round(c.Y - h * 0.5f), w, h);
        _renderer.DrawImage(mask.Handle, _art.MaskPalette, mask.Size, new RectangleF(0, 0, mask.Width, mask.Height), target, ReticleColour);
        return true;
    }

    // The 640 × 480 CROSHAIR.IMG's shape, read once (null: no hi-res art).
    private ReticleShape? Shape()
    {
        if (!_shapeTried)
        {
            _shapeTried = true;
            _shape = _art.HiresImage("CROSHAIR.IMG") is { Width: >= HiresArtWidth } art
                ? ReticleShape.From(art.Width, art.Height, art.Pixels.AsSpan())
                : null;
        }

        return _shape;
    }

    /// <summary>Rects with their edges on whole pixels (a line keeps one width), arcs as strips one art pixel wide.</summary>
    private void DrawShape(ReticleShape shape, Vector2 c, float s, Frame f)
    {
        foreach (var r in shape.Rects)
        {
            var a = new Vector2(MathF.Round(c.X + r.X * s), MathF.Round(c.Y + r.Y * s));
            var b = Vector2.Max(new Vector2(MathF.Round(c.X + r.Right * s), MathF.Round(c.Y + r.Bottom * s)), a + Vector2.One);
            _renderer.FillRect(f.Pixels(a.X, a.Y, b.X - a.X, b.Y - a.Y), ReticleColour);
        }

        var material = Material.Flat(ReticleColour, RenderState.Overlay);
        var screen = Matrix4x4.CreateOrthographicOffCenter(0f, _renderer.CanvasWidth, Renderer.CanvasHeight, 0f, 0f, 1f);
        foreach (var arc in shape.Arcs)
        {
            var rad = arc.Radius * s;
            var steps = Math.Max(8, (int)MathF.Ceiling((arc.To - arc.From) * rad / 3f));
            var vertices = _renderer.Stream(steps * 6, out var mesh, out var first);
            if (vertices.IsEmpty)
            {
                return;
            }

            float inner = (rad - s * 0.5f) * f.Unit, outer = (rad + s * 0.5f) * f.Unit;
            var centre = c * f.Unit;
            Vector3 At(float angle, float r) => new(centre.X + MathF.Cos(angle) * r, centre.Y + MathF.Sin(angle) * r, 0f);
            for (var i = 0; i < steps; i++)
            {
                var a0 = arc.From + (arc.To - arc.From) * i / steps;
                var a1 = arc.From + (arc.To - arc.From) * (i + 1) / steps;
                var q = vertices.Slice(i * 6, 6);
                q[0] = new Vertex(At(a0, inner), Vector2.Zero);
                q[1] = new Vertex(At(a0, outer), Vector2.Zero);
                q[2] = new Vertex(At(a1, outer), Vector2.Zero);
                q[3] = new Vertex(At(a0, inner), Vector2.Zero);
                q[4] = new Vertex(At(a1, outer), Vector2.Zero);
                q[5] = new Vertex(At(a1, inner), Vector2.Zero);
            }

            _renderer.Draw(mesh, first, steps * 6, material, 0, screen);
        }
    }

    private void Blit(HudImage? image, RectangleF target)
    {
        if (image != null)
        {
            _renderer.DrawImage(image.Handle, _art.Palette, image.Size, new RectangleF(0, 0, image.Width, image.Height), target, Vector4.One);
        }
    }

    public void Dispose() => _art.Dispose();
}

/// <summary>The HUD's text for one frame (<see cref="DosHud.Capture"/>), pooled: drawn on another
/// thread while the next tick runs (engine-threads plan B1).</summary>
public sealed class HudText
{
    internal readonly List<string> Messages = [];
    internal readonly List<string> Typed = [];
    internal readonly List<(string Text, byte Colour)> Status = [];
}