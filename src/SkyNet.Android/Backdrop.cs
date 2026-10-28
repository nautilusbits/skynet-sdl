using Android.App;
using Android.Graphics;
using Android.Views;

namespace SkyNet.Android;

/// <summary>The setup screen's backdrop (<see cref="SetupActivity"/>'s content): a dark gunmetal
/// field with faint red scan lines, the app icon in a red glow and the name on the left, the
/// dialogs on the right (<see cref="Place"/>). The project's own art only, no game files.
/// <code>
///   ┌──────────────────────────────────────────┐
///   │ ─ ─ ─ ─ ─ ─ ─  ┌──────────────────────┐  │
///   │   ╭──────╮     │ Title                │  │
///   │   │ icon │     │ message              │  │
///   │   ╰──────╯     │      Secondary [Main]│  │
///   │    SKYNET      └──────────────────────┘  │
///   └──────────────────────────────────────────┘
/// </code></summary>
internal sealed class Backdrop : View
{
    /// <summary>Finds the backdrop in the activity ("SKY").</summary>
    private const int ViewId = 0x534b59;
    /// <summary>The scan lines' spacing (dp) and alpha (0-255).</summary>
    private const float LineStep = 4f;
    private const int LineAlpha = 22;
    /// <summary>The icon's centre (fractions of the view) and side (of the height).</summary>
    private const float IconX = 0.19f;
    private const float IconY = 0.42f;
    private const float IconSide = 0.38f;
    /// <summary>The name's size (of the height) and spacing (ems).</summary>
    private const float NameSize = 0.08f;
    private const float NameSpacing = 0.3f;
    /// <summary>The name's baseline below the icon, in its sizes.</summary>
    private const float NameGap = 1.4f;
    private const string Name = "SKYNET";
    /// <summary>The glow around the icon, its radius of the height.</summary>
    private const float GlowRadius = 0.55f;
    /// <summary>The dialogs' width and right margin, fractions of the screen's width.</summary>
    private const float DialogWidth = 0.6f;
    private const float DialogMargin = 0.025f;

    private static readonly Color FieldTop = Color.ParseColor("#22252B");
    private static readonly Color FieldBottom = Color.ParseColor("#08090B");
    private static readonly Color Glow = Color.ParseColor("#55E0281E");
    private static readonly Color Red = Color.ParseColor("#FF3B2E");

    private readonly Paint _field = new();
    private readonly Paint _glow = new();
    private readonly Paint _line = new();
    private readonly Paint _icon = new(PaintFlags.FilterBitmap | PaintFlags.AntiAlias);
    private readonly Paint _name = new(PaintFlags.AntiAlias);
    private readonly Bitmap? _bitmap;

    public Backdrop(Activity activity) : base(activity)
    {
        Id = ViewId;
        _bitmap = BitmapFactory.DecodeResource(activity.Resources, Resource.Mipmap.ic_launcher);
        _name.Color = Red;
        _name.TextAlign = Paint.Align.Center;
        _name.FakeBoldText = true;
        _name.LetterSpacing = NameSpacing;
        _line.Color = Red;
        _line.Alpha = LineAlpha;
    }

    /// <summary>A shown dialog moves right of the icon, undimmed. UI thread.</summary>
    public static void Place(Activity activity, Dialog dialog)
    {
        if (activity.FindViewById(ViewId) is not Backdrop || dialog.Window is not { } window)
        {
            return;
        }

        // The screen's width: the backdrop may not be laid out yet.
        var width = activity.Window!.DecorView.Width;
        window.ClearFlags(WindowManagerFlags.DimBehind);
        window.SetGravity(GravityFlags.End | GravityFlags.CenterVertical);
        var attributes = window.Attributes!;
        attributes.X = (int)(width * DialogMargin);
        window.Attributes = attributes;
        window.SetLayout((int)(width * DialogWidth), ViewGroup.LayoutParams.WrapContent);
    }

    protected override void OnDetachedFromWindow()
    {
        base.OnDetachedFromWindow();
        _bitmap?.Recycle();
    }

    // The gradients follow the size.
    protected override void OnSizeChanged(int w, int h, int oldw, int oldh)
    {
        base.OnSizeChanged(w, h, oldw, oldh);
        _field.SetShader(new LinearGradient(0, 0, 0, h, FieldTop, FieldBottom, Shader.TileMode.Clamp!));
        _glow.SetShader(new RadialGradient(w * IconX, h * IconY, h * GlowRadius, Glow, Color.Transparent, Shader.TileMode.Clamp!));
        _name.TextSize = h * NameSize;
    }

    protected override void OnDraw(Canvas canvas)
    {
        float w = Width;
        float h = Height;
        canvas.DrawRect(0, 0, w, h, _field);
        canvas.DrawRect(0, 0, w, h, _glow);

        // Scan lines, as on a machine's display.
        var step = LineStep * Resources!.DisplayMetrics!.Density;
        for (var y = 0f; y < h; y += step)
        {
            canvas.DrawLine(0, y, w, y, _line);
        }

        // The icon, the name under it.
        var half = h * IconSide / 2;
        var cx = w * IconX;
        var cy = h * IconY;
        if (_bitmap != null)
        {
            canvas.DrawBitmap(_bitmap, null, new RectF(cx - half, cy - half, cx + half, cy + half), _icon);
        }

        canvas.DrawText(Name, cx, cy + half + _name.TextSize * NameGap, _name);
    }
}
